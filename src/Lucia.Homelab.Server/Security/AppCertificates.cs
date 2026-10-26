using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Security;

/// <summary>What the scoped worker on the Spark signs: one P-256 key, named after the stack, for the app's name under the domain. The address is
/// the server the app runs on, which the worker checks against its own records before it signs.</summary>
internal sealed record AppCertificateRequest(int SchemaVersion, string Stack, Guid NodeId, string Address, string CsrPem, DateTimeOffset ExpiresAt);
internal sealed record AppCertificateResponse(int SchemaVersion, string Stack, string RequestHash, bool Success, string Message,
    DateTimeOffset CheckedAt, string? CertificatePem);
internal sealed record PendingAppCertificate(int SchemaVersion, string Stack, string Address, string ProtectedKey, DateTimeOffset RequestedAt,
    string? Name = null);

/// <summary>A certificate from Lucia's authority for an app's name: the leaf and its intermediate, and the key.</summary>
public sealed record AppCertificate(string ChainPem, string KeyPem, DateTimeOffset NotAfter)
{
    public override string ToString() => $"AppCertificate(<private>, {NotAfter:O})";
}

/// <summary>
/// Certificates from Lucia's own authority for apps the gateway reaches over the LAN, such as CrowdSec. The host holds no CA
/// credentials: it makes the key, writes a signing request to <c>nodes/app-certificate-requests</c>, and the scoped worker on
/// the Spark that already signs node certificates (tools/nodes/enrollment_worker.py) checks it against the stack and the
/// server it runs on, signs it for seven days and answers in <c>app-certificate-responses</c>. A certificate is renewed once
/// fewer than <see cref="RenewBelow"/> remain, and replaced at once when the app's name changes.
/// </summary>
public sealed class AppCertificates(IOptions<HardwareOnboardingOptions> options, IDataProtectionProvider protection, TimeProvider time)
{
    public static readonly TimeSpan RenewBelow = TimeSpan.FromDays(4);
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);
    internal const string Requests = "app-certificate-requests", Responses = "app-certificate-responses";
    private readonly IDataProtector _protector = protection.CreateProtector("Lucia.Homelab.AppCertificateKey.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string Data => Path.GetDirectoryName(options.Value.StateDirectory)!;
    internal string Root => Path.Combine(Data, "nodes");
    private string PendingPath(string stack) => Path.Combine(Data, "security", "app-certificates", stack + ".json");

    /// <summary>The common name the worker requires: the stack's name, so nothing else can be asked for.</summary>
    internal static string CommonName(string stack) => "lucia-app-" + stack;

    /// <summary>
    /// The certificate the app should serve: <paramref name="current"/> while it's valid for <paramref name="name"/> with
    /// time to spare, else a newly signed one once the worker has answered. Otherwise the current one (if still usable) and why
    /// there's no newer one yet. <paramref name="name"/> is the app's name under the domain; <paramref name="address"/> the
    /// server it runs on now, which only authorises the request and isn't in the certificate, so a move keeps it.
    /// </summary>
    public async Task<(AppCertificate? Certificate, string? Problem)> Ensure(string stack, Guid nodeId, string address, string name, AppCertificate? current,
        X509Certificate2 root, Func<bool> workerReady, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var usable = current is not null && Valid(current, name, root, now) ? current : null;
        await _gate.WaitAsync(ct);
        try
        {
            var (requestPath, responsePath, pendingPath) = (Path.Combine(Root, Requests, stack + ".json"), Path.Combine(Root, Responses, stack + ".json"),
                PendingPath(stack));
            foreach (var path in new[] { requestPath, responsePath, pendingPath }) DomainOnboardingStore.RejectLinks(path);
            if (usable is not null && usable.NotAfter - now > RenewBelow)
            {
                Forget(requestPath, responsePath, pendingPath);
                return (usable, null);
            }
            var pending = File.Exists(pendingPath) ? Read<PendingAppCertificate>(pendingPath) : null;
            var request = File.Exists(requestPath) ? Read<AppCertificateRequest>(requestPath) : null;
            if (pending is { SchemaVersion: 1 } && pending.Stack == stack && pending.Address == address && pending.Name == name
                && request is { SchemaVersion: 1 } && request.Stack == stack && request.Address == address && request.NodeId == nodeId)
            {
                if (File.Exists(responsePath))
                {
                    var response = Read<AppCertificateResponse>(responsePath);
                    var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(requestPath, ct)));
                    if (response is { SchemaVersion: 1 } && response.Stack == stack && response.RequestHash == hash)
                    {
                        if (response is { Success: true, CertificatePem: { } chain })
                        {
                            var key = _protector.Unprotect(pending.ProtectedKey);
                            var issued = Issued(chain, key, name, root, now);
                            if (issued is not null)
                            {
                                Forget(requestPath, responsePath, pendingPath);
                                return (issued, null);
                            }
                            Forget(requestPath, responsePath, pendingPath);
                            return (usable, "Lucia's authority returned a certificate that doesn't match this app's request. Lucia will ask again.");
                        }
                        if (request.ExpiresAt <= now) await Write(requestPath, request with { ExpiresAt = now + RequestLifetime }, ct);
                        return (usable, Clean(response.Message));
                    }
                }
                if (request.ExpiresAt <= now)
                    await Write(requestPath, request with { ExpiresAt = now + RequestLifetime }, ct);
                return (usable, workerReady() ? null : "Lucia's certificate service on the Spark isn't running, so the certificate can't be signed yet.");
            }
            using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var builder = new CertificateRequest(new X500DistinguishedName("CN=" + CommonName(stack)), created, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(name);
            builder.CertificateExtensions.Add(names.Build());
            var csr = builder.CreateSigningRequestPem();
            await Write(pendingPath, new PendingAppCertificate(1, stack, address, _protector.Protect(created.ExportPkcs8PrivateKeyPem()), now, name), ct);
            if (File.Exists(responsePath)) File.Delete(responsePath);
            await Write(requestPath, new AppCertificateRequest(1, stack, nodeId, address, csr, now + RequestLifetime), ct);
            return (usable, workerReady() ? null : "Lucia's certificate service on the Spark isn't running, so the certificate can't be signed yet.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drops the request and the waiting key once the app is gone.</summary>
    public async Task Forget(string stack, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { Forget(Path.Combine(Root, Requests, stack + ".json"), Path.Combine(Root, Responses, stack + ".json"), PendingPath(stack)); }
        finally { _gate.Release(); }
    }

    /// <summary>A certificate Lucia's authority signed for exactly this name, still valid, with the matching key.</summary>
    internal static bool Valid(AppCertificate certificate, string name, X509Certificate2 root, DateTimeOffset now) =>
        Issued(certificate.ChainPem, certificate.KeyPem, name, root, now) is not null;

    internal static AppCertificate? Issued(string chainPem, string keyPem, string name, X509Certificate2 root, DateTimeOffset now)
    {
        try
        {
            var chain = new X509Certificate2Collection();
            chain.ImportFromPem(chainPem);
            if (chain.Count is < 2 or > 4) return null;
            using var leaf = X509Certificate2.CreateFromPem(chainPem, keyPem);
            if (!leaf.RawData.AsSpan().SequenceEqual(chain[0].RawData) || leaf.NotAfter.ToUniversalTime() <= now.UtcDateTime
                || leaf.NotBefore.ToUniversalTime() > now.UtcDateTime.AddMinutes(5)
                || leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault() is not { } names
                || !names.EnumerateDnsNames().SequenceEqual([name]) || names.EnumerateIPAddresses().Any())
                return null;
            using var validation = new X509Chain();
            validation.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validation.ChainPolicy.CustomTrustStore.Add(root);
            validation.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            validation.ChainPolicy.VerificationTime = now.UtcDateTime;
            validation.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            foreach (var intermediate in chain.Skip(1)) validation.ChainPolicy.ExtraStore.Add(intermediate);
            return validation.Build(leaf) ? new(chainPem, keyPem, leaf.NotAfter.ToUniversalTime()) : null;
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException) { return null; }
    }

    private static string Clean(string? message) =>
        string.IsNullOrWhiteSpace(message) ? "Lucia's authority refused the certificate request."
            : new string([.. message.Where(c => !char.IsControl(c)).Take(300)]);

    private static void Forget(params string[] paths)
    {
        foreach (var path in paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private static T? Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(CertbotFiles.ReadBounded(path, 65536), DomainOnboardingStore.Json); }
        catch (JsonException) { return default; }
    }

    private static Task Write<T>(string path, T value, CancellationToken ct) => DomainOnboardingStore.WriteJson(path, value, ct);
}
