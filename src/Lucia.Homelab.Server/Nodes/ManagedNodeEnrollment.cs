using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Boot;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Nodes;

public sealed record NodeEnrollmentPayload(Guid NodeId, Guid TaskId, string CsrPem);
public sealed record NodeEnrollmentSubmission(Guid TaskId, SignedDiscovery Proof);
public sealed record NodeSignedSubmission(string CertificatePem, SignedDiscovery Proof);
public sealed record NodeHeartbeat(Guid NodeId, string Hostname, string OsVersion, double UptimeSeconds,
    double? LoadAverage, long MemoryTotalBytes, long MemoryAvailableBytes, long? StorageTotalBytes, long? StorageAvailableBytes);
public sealed record NodeEnrollmentConfiguration(string Hostname, string CertificatePem, string CaPem, string LdapUri,
    string LdapBaseDn, string LdapBindDn, string LdapBindPassword, string OwnerGroupDn)
{
    public override string ToString() => "NodeEnrollmentConfiguration(<private>)";
}
internal sealed record NativeNodeRequest(int SchemaVersion, Guid NodeId, Guid TaskId, string Hostname,
    string PublicKeyFingerprint, string CsrPem, DateTimeOffset ExpiresAt);
internal sealed record NativeNodeResponse(int SchemaVersion, Guid NodeId, Guid? TaskId, string RequestHash,
    bool Success, string Message, DateTimeOffset CheckedAt, NodeEnrollmentConfiguration? Configuration);
internal sealed record ManagedNodeRecord(Guid NodeId, Guid TaskId, string Hostname, string PublicKeyFingerprint,
    string CertificatePem, DateTimeOffset CertificateExpiresAt, DateTimeOffset? LastSeenAt, NodeHeartbeat? Status,
    string? Address = null);
public sealed record ManagedNodeAddress(string Hostname, string Address, Guid NodeId = default);

public sealed class ManagedNodeEnrollment(
    HardwareOnboardingStore onboarding, IOptions<HardwareOnboardingOptions> options, HostAuthenticationOptions authentication)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Root => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "nodes");
    private string NodePath(Guid id) => Path.Combine(Root, "identities", id.ToString("D") + ".json");
    internal string DnsStatePath => Path.Combine(Root, "dns-records.json");

    /// <summary>Raised when a heartbeat arrives from a node address Lucia has not recorded yet.</summary>
    public event Action? AddressChanged;

    public async Task<object> Snapshot(CancellationToken ct)
    {
        var records = await Records(ct);
        return records.Select(node => new { node.NodeId, node.TaskId, node.Hostname, node.CertificateExpiresAt, node.LastSeenAt,
            state = node.LastSeenAt is null ? "AwaitingHeartbeat" : node.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-2) ? "Online" : "Stale",
            node.Address, node.Status }).ToArray();
    }

    public async Task<ManagedNodeAddress[]> Addresses(CancellationToken ct) =>
        (await Records(ct)).Where(node => node.Address is not null)
            .Select(node => new ManagedNodeAddress(node.Hostname, node.Address!, node.NodeId)).ToArray();

    private async Task<ManagedNodeRecord[]> Records(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var directory = Path.Combine(Root, "identities");
            DomainOnboardingStore.RejectLinks(directory);
            if (!Directory.Exists(directory)) return [];
            var paths = Directory.GetFiles(directory, "*.json");
            if (paths.Length > HardwareOnboardingStore.MaximumDevices) throw new InvalidDataException("Managed-node registry is oversized.");
            return paths.Select(Read<ManagedNodeRecord>).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<NodeEnrollmentConfiguration?> Enroll(HardwareDeviceSession session, NodeEnrollmentPayload payload,
        string fingerprint, CancellationToken ct)
    {
        var task = await onboarding.RequireEnrollmentAsync(session, payload.TaskId, ct);
        if (payload.NodeId != session.DeviceId || !fingerprint.Equals(session.SessionKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw Denied();
        return await Prepare(payload, task.Hostname, fingerprint, ct);
    }

    public async Task RequireKnown(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { _ = Read<ManagedNodeRecord>(NodePath(id)); }
        finally { _gate.Release(); }
    }

    public async Task<NodeEnrollmentConfiguration?> Renew(Guid id, NodeEnrollmentPayload payload, string certificatePem,
        string fingerprint, CancellationToken ct)
    {
        ManagedNodeRecord identity;
        await _gate.WaitAsync(ct);
        try { identity = Verify(id, certificatePem, fingerprint, renewal: true); }
        finally { _gate.Release(); }
        if (payload.NodeId != id || payload.TaskId != identity.TaskId) throw Denied();
        return await Prepare(payload, identity.Hostname, fingerprint, ct, renewal: true);
    }

    /// <param name="address">The connection address the signed challenge was bound to.</param>
    public async Task Heartbeat(Guid id, NodeHeartbeat report, string certificatePem, string fingerprint, IPAddress address,
        CancellationToken ct)
    {
        ValidateHeartbeat(id, report);
        var text = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            var identity = Verify(id, certificatePem, fingerprint);
            if (report.Hostname != identity.Hostname) throw Denied();
            await onboarding.CompleteEnrollmentAsync(id, identity.TaskId, fingerprint, ct);
            await onboarding.ManagedHeartbeatAsync(id, fingerprint, ct);
            changed = identity.Address != text;
            await DomainOnboardingStore.WriteJson(NodePath(id),
                identity with { LastSeenAt = DateTimeOffset.UtcNow, Status = report, Address = text }, ct);
        }
        finally { _gate.Release(); }
        if (changed) AddressChanged?.Invoke();
    }

    private async Task<NodeEnrollmentConfiguration?> Prepare(NodeEnrollmentPayload payload, string hostname, string fingerprint,
        CancellationToken ct, bool renewal = false)
    {
        if (payload.NodeId == Guid.Empty || payload.TaskId == Guid.Empty || payload.CsrPem is not { Length: > 0 and <= 8192 })
            throw Denied();
        fingerprint = fingerprint.ToLowerInvariant();
        ValidateCsr(payload, hostname, fingerprint);
        await _gate.WaitAsync(ct);
        try
        {
            DomainOnboardingStore.EnsureDirectory(Root);
            var requests = Path.Combine(Root, "enrollment-requests");
            var responses = Path.Combine(Root, "enrollment-responses");
            DomainOnboardingStore.EnsureDirectory(requests);
            DomainOnboardingStore.EnsureDirectory(responses);
            var requestPath = Path.Combine(requests, payload.NodeId.ToString("D") + ".json");
            var responsePath = Path.Combine(responses, payload.NodeId.ToString("D") + ".json");
            var expected = new NativeNodeRequest(1, payload.NodeId, payload.TaskId, hostname, fingerprint, payload.CsrPem,
                DateTimeOffset.UtcNow.AddMinutes(10));
            NativeNodeRequest? pending = File.Exists(requestPath) ? Read<NativeNodeRequest>(requestPath) : null;
            if (pending is not null && (pending.NodeId != expected.NodeId || pending.TaskId != expected.TaskId
                || pending.Hostname != expected.Hostname || pending.PublicKeyFingerprint != expected.PublicKeyFingerprint
                || pending.CsrPem != expected.CsrPem)) throw Denied();
            if (pending is not null && File.Exists(responsePath))
            {
                var response = Read<NativeNodeResponse>(responsePath);
                var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(requestPath)));
                if (response.SchemaVersion != 1 || response.NodeId != payload.NodeId || response.TaskId != payload.TaskId)
                    throw Denied();
                if (response.RequestHash == hash)
                {
                    if (!response.Success)
                    {
                        if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
                        {
                            if (!WorkerReady()) throw new HardwareOnboardingException(503, "node_worker_unavailable", "The native node-enrollment service is unavailable.");
                            await DomainOnboardingStore.WriteJson(requestPath, expected, ct);
                            return null;
                        }
                        throw new HardwareOnboardingException(503, "node_enrollment_failed",
                            "The native enrollment service could not verify this node's CA or directory setup. Its private receipt was preserved.");
                    }
                    var configuration = response.Configuration ?? throw new InvalidDataException("Enrollment response is empty.");
                    var expiry = ValidateConfiguration(payload.NodeId, hostname, fingerprint, configuration);
                    if (!renewal || expiry > DateTimeOffset.UtcNow.AddHours(6))
                    {
                        var existing = File.Exists(NodePath(payload.NodeId)) ? Read<ManagedNodeRecord>(NodePath(payload.NodeId)) : null;
                        if (existing is not null && (existing.TaskId != payload.TaskId || existing.Hostname != hostname
                            || existing.PublicKeyFingerprint != fingerprint)) throw Denied();
                        var record = new ManagedNodeRecord(payload.NodeId, payload.TaskId, hostname, fingerprint,
                            configuration.CertificatePem, expiry, existing?.LastSeenAt, existing?.Status, existing?.Address);
                        await DomainOnboardingStore.WriteJson(NodePath(payload.NodeId), record, ct);
                        return configuration;
                    }
                    pending = null;
                }
            }
            if (pending is null || pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                if (!WorkerReady()) throw new HardwareOnboardingException(503, "node_worker_unavailable", "The native node-enrollment service is unavailable.");
                await DomainOnboardingStore.WriteJson(requestPath, expected, ct);
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    public bool WorkerReady()
    {
        var path = Path.Combine(Root, "enrollment-worker.json");
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path)) return false;
        using var json = JsonDocument.Parse(CertbotFiles.ReadBounded(path, 4096));
        var value = json.RootElement;
        var time = value.GetProperty("checkedAt").GetDateTimeOffset();
        return value.GetProperty("schemaVersion").GetInt32() == 1 && value.GetProperty("ready").GetBoolean()
            && time <= DateTimeOffset.UtcNow.AddSeconds(5) && time > DateTimeOffset.UtcNow.AddSeconds(-45);
    }

    internal static void ValidateCsr(NodeEnrollmentPayload payload, string hostname, string fingerprint)
    {
        try
        {
            var request = CertificateRequest.LoadSigningRequestPem(payload.CsrPem, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(request.PublicKey.ExportSubjectPublicKeyInfo(), out _);
            if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) != fingerprint
                || request.SubjectName.Name != "CN=" + payload.NodeId.ToString("D")
                || request.CertificateExtensions.Count != 1
                || request.CertificateExtensions[0] is not X509SubjectAlternativeNameExtension san
                || !san.EnumerateDnsNames().SequenceEqual([hostname]) || san.EnumerateIPAddresses().Any()) throw Denied();
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException)
        { throw Denied(); }
    }

    private DateTimeOffset ValidateConfiguration(Guid id, string hostname, string fingerprint, NodeEnrollmentConfiguration configuration)
    {
        if (configuration.Hostname != hostname || configuration.LdapBindPassword is not { Length: >= 32 and <= 256 }
            || configuration.LdapBindPassword.Any(char.IsControl))
            throw Denied();
        using var root = authentication.ReadCertificateAuthority();
        using var returnedRoot = X509Certificate2.CreateFromPem(configuration.CaPem);
        if (!root.RawData.AsSpan().SequenceEqual(returnedRoot.RawData)) throw Denied();
        return ValidateCertificate(id, hostname, fingerprint, configuration.CertificatePem);
    }

    private ManagedNodeRecord Verify(Guid id, string certificatePem, string fingerprint, bool renewal = false)
    {
        var identity = Read<ManagedNodeRecord>(NodePath(id));
        if (identity.NodeId != id || !identity.PublicKeyFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)) throw Denied();
        _ = ValidateCertificate(id, identity.Hostname, identity.PublicKeyFingerprint, certificatePem, renewal);
        return identity;
    }

    internal DateTimeOffset ValidateCertificate(Guid id, string hostname, string fingerprint, string pem, bool renewal = false)
    {
        if (pem is not { Length: > 0 and <= 16384 } || pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw Denied();
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(pem);
            if (certificates.Count is < 1 or > 4) throw Denied();
            var certificate = certificates[0];
            using var key = certificate.GetECDsaPublicKey();
            var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
            if (key is null || Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) != fingerprint
                || certificate.SubjectName.Name != "CN=" + id.ToString("D")
                || certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority)
                || names is null || !names.EnumerateDnsNames().SequenceEqual([hostname]) || names.EnumerateIPAddresses().Any()
                || certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime() > TimeSpan.FromHours(25)
                || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow) throw Denied();
            using var root = authentication.ReadCertificateAuthority();
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.ExtraStore.AddRange(certificates);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            // Offline nodes may renew only with fresh proof of the already-registered
            // key. Normal heartbeats still require a currently valid certificate.
            if (renewal && certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                chain.ChainPolicy.VerificationTime = certificate.NotBefore.AddMinutes(1);
            if (!chain.Build(certificate)) throw Denied();
            return certificate.NotAfter.ToUniversalTime();
        }
        catch (CryptographicException) { throw Denied(); }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(CertbotFiles.ReadBounded(path, 32768),
        DomainOnboardingStore.Json) ?? throw new InvalidDataException("Managed node state is empty.");

    internal static void ValidateHeartbeat(Guid id, NodeHeartbeat report)
    {
        HardwareInventoryValidation.Require(report.NodeId == id && HardwareInventoryValidation.Hostname(report.Hostname)
            && double.IsFinite(report.UptimeSeconds) && report.UptimeSeconds >= 0
            && (report.LoadAverage is null || double.IsFinite(report.LoadAverage.Value) && report.LoadAverage >= 0)
            && report.MemoryTotalBytes > 0 && report.MemoryAvailableBytes >= 0 && report.MemoryAvailableBytes <= report.MemoryTotalBytes
            && (report.StorageTotalBytes is null && report.StorageAvailableBytes is null
                || report.StorageTotalBytes > 0 && report.StorageAvailableBytes >= 0 && report.StorageAvailableBytes <= report.StorageTotalBytes),
            "Managed-node metrics are invalid.");
        HardwareInventoryValidation.Text(report.OsVersion, 256, true);
    }

    private static HardwareOnboardingException Denied() => new(403, "node_identity_invalid",
        "The node identity does not match its approved installation and lab certificate authority.");
}
