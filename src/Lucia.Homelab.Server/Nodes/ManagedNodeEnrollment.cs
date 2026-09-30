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
    double? LoadAverage, long MemoryTotalBytes, long MemoryAvailableBytes, long? StorageTotalBytes, long? StorageAvailableBytes,
    NodeRuntime? Runtime = null, NodeUpdateStatus? Updates = null, string? AgentRelease = null,
    double? CpuPercent = null, double? CpuTemperatureCelsius = null, double? GpuPercent = null, double? GpuTemperatureCelsius = null);
/// <summary>One heartbeat's utilization, kept for the node's last hour. Percents are 0–100; temperatures are °C.</summary>
public sealed record NodeUsageSample(DateTimeOffset At, double? Cpu, double Memory, double? Gpu, double? CpuTemperature, double? GpuTemperature);
/// <summary>A pending Debian package upgrade, from <c>apt-get -s upgrade</c>.</summary>
public sealed record NodePackageUpdate(string Name, string? Current, string Candidate, bool Security);
/// <summary>The node's own view of its Debian updates. <c>State</c> is Idle, Checking, Installing or Failed; the package list
/// is the last successful check, capped, with <c>Count</c> the full number.</summary>
public sealed record NodeUpdateStatus(string State, DateTimeOffset? CheckedAt, int Count, int SecurityCount, NodePackageUpdate[] Packages,
    bool RestartRequired, string? Message = null);
public static class NodeUpdateLimits
{
    /// <summary>Keeps a heartbeat within its 32 KiB signed-report limit.</summary>
    public const int Packages = 50, Text = 100;
}
public sealed record NodeGpu(string Vendor, string Model, long? MemoryBytes, string? ComputeCapability, string? Uuid = null);
/// <summary>The node's container host: Docker, Compose and whether containers can use its GPUs. <c>CudaVersion</c> is the
/// newest CUDA runtime the NVIDIA driver supports.</summary>
public sealed record NodeRuntime(string State, string? DockerVersion, string? ComposeVersion, bool GpuContainers, NodeGpu[] Gpus,
    string? Message = null, string? DriverVersion = null, string? CudaVersion = null);
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
    string? Address = null, NodeGpuSettings? Gpu = null);
public sealed record ManagedNodeAddress(string Hostname, string Address, Guid NodeId = default);
public sealed record ManagedNodeFacts(Guid NodeId, string Hostname, bool Online, NodeHeartbeat? Status, NodeGpuSettings? Gpu = null);

public sealed class ManagedNodeEnrollment(
    HardwareOnboardingStore onboarding, IOptions<HardwareOnboardingOptions> options, HostAuthenticationOptions authentication)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // The last hour of heartbeats per node, in memory only: it starts over when the host restarts.
    private readonly Dictionary<Guid, Queue<NodeUsageSample>> _usage = [];
    private string Root => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "nodes");
    private string NodePath(Guid id) => Path.Combine(Root, "identities", id.ToString("D") + ".json");
    internal string DnsStatePath => Path.Combine(Root, "dns-records.json");

    /// <summary>Raised when a heartbeat arrives from a node address Lucia has not recorded yet.</summary>
    public event Action? AddressChanged;

    /// <param name="naming">The active domain's naming, which gives each node the DNS name <see cref="ManagedNodeDns"/> publishes.</param>
    public async Task<object> Snapshot(DomainNamingPlan? naming, CancellationToken ct)
    {
        var records = await Records(ct);
        Dictionary<Guid, NodeUsageSample[]> usage;
        lock (_usage) usage = _usage.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
        var names = naming is null ? [] : ManagedNodeDns.Wanted(naming, records.Where(node => node.Address is not null)
            .Select(node => new ManagedNodeAddress(node.Hostname, node.Address!, node.NodeId))).Select(record => record.Domain).ToHashSet();
        string? DnsName(string hostname) =>
            naming is null ? null : names.FirstOrDefault(name => name == $"{hostname}.{naming.Namespace}".ToLowerInvariant());
        return records.Select(node => new { node.NodeId, node.TaskId, node.Hostname, node.CertificateExpiresAt, node.LastSeenAt,
            state = node.LastSeenAt is null ? "AwaitingHeartbeat" : node.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-2) ? "Online" : "Stale",
            node.Address, dnsName = DnsName(node.Hostname),
            node.Status, gpu = node.Gpu ?? NodeGpuSettings.None,
            // A driver change can drop support for the pinned line; the owner decides what to do about it.
            gpuWarning = node.Gpu?.CudaLine is { } line && node.Status?.Runtime is { } runtime ? CudaLines.Unsupported(line, runtime) : null,
            // Null when the node's agent predates reporting its release, and so can't update itself.
            agentUpdateAvailable = node.Status?.AgentRelease is { } release && NodeAgentRelease.Latest is { } latest ? release != latest : (bool?)null,
            history = usage.GetValueOrDefault(node.NodeId, []) }).ToArray();
    }

    /// <summary>Adds a heartbeat's utilization to the node's hour, dropping anything older.</summary>
    internal void RecordUsage(Guid id, NodeHeartbeat report, DateTimeOffset at)
    {
        var sample = new NodeUsageSample(at, Round(report.CpuPercent),
            Math.Round(100.0 * (report.MemoryTotalBytes - report.MemoryAvailableBytes) / report.MemoryTotalBytes, 1),
            Round(report.GpuPercent), report.CpuTemperatureCelsius, report.GpuTemperatureCelsius);
        lock (_usage)
        {
            if (!_usage.TryGetValue(id, out var samples)) _usage[id] = samples = new();
            samples.Enqueue(sample);
            while (samples.Count > 180 || samples.Peek().At < at.AddHours(-1)) samples.Dequeue();
        }
        static double? Round(double? value) => value is { } number ? Math.Round(number, 1) : null;
    }

    public async Task<ManagedNodeAddress[]> Addresses(CancellationToken ct) =>
        (await Records(ct)).Where(node => node.Address is not null)
            .Select(node => new ManagedNodeAddress(node.Hostname, node.Address!, node.NodeId)).ToArray();

    public async Task<(Guid NodeId, string Hostname)[]> Names(CancellationToken ct) =>
        (await Records(ct)).Select(node => (node.NodeId, node.Hostname)).ToArray();

    /// <summary>What placement needs to know about each node: its last report and whether it's checking in.</summary>
    public async Task<ManagedNodeFacts[]> Facts(CancellationToken ct) =>
        (await Records(ct)).Select(node => new ManagedNodeFacts(node.NodeId, node.Hostname,
            node.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-2), node.Status, node.Gpu)).ToArray();

    public async Task<NodeGpuSettings> SaveGpu(Guid id, SaveNodeGpuRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = NodePath(id);
            DomainOnboardingStore.RejectLinks(path);
            if (!File.Exists(path)) throw new HardwareOnboardingException(404, "unknown_node", "That server isn't managed by Lucia.");
            var node = Read<ManagedNodeRecord>(path);
            var settings = CudaLines.Validate(request, node.Status?.Runtime);
            await DomainOnboardingStore.WriteJson(path, node with { Gpu = settings }, ct);
            return settings;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Checks a node's current certificate and signing key; returns its approved hostname.</summary>
    public async Task<string> Authenticate(Guid id, string certificatePem, string fingerprint, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return Verify(id, certificatePem, fingerprint).Hostname; }
        finally { _gate.Release(); }
    }

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
        try
        {
            if (!File.Exists(NodePath(id))) throw Denied();
            _ = Read<ManagedNodeRecord>(NodePath(id));
        }
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
            var now = DateTimeOffset.UtcNow;
            await DomainOnboardingStore.WriteJson(NodePath(id),
                identity with { LastSeenAt = now, Status = report, Address = text }, ct);
            RecordUsage(id, report, now);
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
                            configuration.CertificatePem, expiry, existing?.LastSeenAt, existing?.Status, existing?.Address, existing?.Gpu);
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
        // A removed node keeps calling until someone turns it off; it gets the same answer as any unknown identity.
        if (!File.Exists(NodePath(id))) throw Denied();
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

    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(CertbotFiles.ReadBounded(path, 65536),
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
        if (report.Runtime is { } runtime)
        {
            HardwareInventoryValidation.Require(runtime.State is "Preparing" or "Ready" or "Failed" && runtime.Gpus is { Length: <= 16 }
                && runtime.Gpus.All(gpu => gpu is { Vendor: "nvidia" } && (gpu.MemoryBytes is null or > 0)
                    && (gpu.Uuid is null || System.Text.RegularExpressions.Regex.IsMatch(gpu.Uuid,
                        @"\AGPU-[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}\z")))
                && runtime.Gpus.Select(gpu => gpu.Uuid).OfType<string>().CountBy(uuid => uuid).All(entry => entry.Value == 1)
                && (runtime.CudaVersion is null || System.Text.RegularExpressions.Regex.IsMatch(runtime.CudaVersion, @"\A[0-9]{1,2}\.[0-9]{1,2}\z")),
                "Managed-node runtime is invalid.");
            HardwareInventoryValidation.Text(runtime.DockerVersion, 64);
            HardwareInventoryValidation.Text(runtime.ComposeVersion, 64);
            HardwareInventoryValidation.Text(runtime.Message, 512);
            HardwareInventoryValidation.Text(runtime.DriverVersion, 32);
            foreach (var gpu in runtime.Gpus)
            {
                HardwareInventoryValidation.Text(gpu.Model, 128, true);
                HardwareInventoryValidation.Text(gpu.ComputeCapability, 16);
            }
        }
        if (report.Updates is { } updates)
        {
            HardwareInventoryValidation.Require(updates.State is "Idle" or "Checking" or "Installing" or "Failed"
                && updates.Packages is { Length: <= NodeUpdateLimits.Packages } && updates.Count >= updates.Packages.Length
                && updates.Count <= 100_000 && updates.SecurityCount >= 0 && updates.SecurityCount <= updates.Count
                && (updates.CheckedAt is null || updates.CheckedAt.Value.Offset == TimeSpan.Zero),
                "Managed-node updates are invalid.");
            HardwareInventoryValidation.Text(updates.Message, 512);
            foreach (var package in updates.Packages)
            {
                HardwareInventoryValidation.Text(package.Name, NodeUpdateLimits.Text, true);
                HardwareInventoryValidation.Text(package.Current, NodeUpdateLimits.Text);
                HardwareInventoryValidation.Text(package.Candidate, NodeUpdateLimits.Text, true);
            }
        }
        HardwareInventoryValidation.Require(report.AgentRelease is null || NodeAgentRelease.IsId(report.AgentRelease),
            "Managed-node agent release is invalid.");
        static bool Percent(double? value) => value is null || double.IsFinite(value.Value) && value >= 0 && value <= 100;
        static bool Celsius(double? value) => value is null || double.IsFinite(value.Value) && value > -40 && value < 150;
        HardwareInventoryValidation.Require(Percent(report.CpuPercent) && Percent(report.GpuPercent)
            && Celsius(report.CpuTemperatureCelsius) && Celsius(report.GpuTemperatureCelsius), "Managed-node utilization is invalid.");
    }

    /// <summary>
    /// Forgets a node: its identity, enrollment files and address. Its DNS and DHCP entries go on the next sync, and the
    /// enrollment worker deletes its directory reader. Returns the hostname it had ("" if it never enrolled), or null when
    /// Lucia had no files for it.
    /// </summary>
    public async Task<string?> Remove(Guid id, CancellationToken ct)
    {
        string? hostname = null;
        await _gate.WaitAsync(ct);
        try
        {
            var files = new[] { NodePath(id), Path.Combine(Root, "enrollment-requests", id.ToString("D") + ".json"),
                Path.Combine(Root, "enrollment-responses", id.ToString("D") + ".json") };
            foreach (var file in files) DomainOnboardingStore.RejectLinks(file);
            if (!files.Any(File.Exists)) return null;
            if (File.Exists(files[0])) hostname = Read<ManagedNodeRecord>(files[0]).Hostname;
            var removals = Path.Combine(Root, "removals");
            DomainOnboardingStore.EnsureDirectory(removals);
            // Written first, so the worker still learns of the node if a delete below fails.
            await DomainOnboardingStore.WriteJson(Path.Combine(removals, id.ToString("D") + ".json"), new { schemaVersion = 1, nodeId = id }, ct);
            foreach (var file in files) File.Delete(file);
        }
        finally { _gate.Release(); }
        lock (_usage) _usage.Remove(id);
        AddressChanged?.Invoke();
        return hostname ?? "";
    }

    private static HardwareOnboardingException Denied() => new(403, "node_identity_invalid",
        "The node identity does not match its approved installation and lab certificate authority.");
}
