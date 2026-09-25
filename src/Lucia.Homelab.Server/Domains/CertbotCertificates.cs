using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Domains;

public sealed record CertbotOptions(string StateDirectory, string PublishedCertificatesDirectory);

/// <summary>Only generated Lucia lineage identifiers are accepted, never paths. TermsUrl records the
/// agreement the caller obtained; it is not sent to Certbot. DNS names must already be ASCII.</summary>
public sealed record CertbotCertificateRequest(
    string LineageName, IReadOnlyList<string> DnsNames, string Email,
    bool AcceptedTerms, string TermsUrl, int PropagationSeconds = 30)
{
    public static string NewLineageName() => "lucia-" + Guid.NewGuid().ToString("N");
}

/// <summary>Issuance is not ingress activation. Paths identify an immutable private PEM pair;
/// the caller alone controls gateway configuration/reload. Renewed compares the Certbot leaf
/// before and after the invocation, not whether ingress was activated.</summary>
public sealed record CertbotCertificateReceipt(
    string Lineage, DateTimeOffset NotBefore, DateTimeOffset NotAfter, IReadOnlyList<string> DnsNames,
    string CertificateSha256, string CertificateFile, string KeyFile, bool Renewed);

/// <summary>Safe to expose to the operator. Never attach raw process output or inner exceptions.</summary>
public sealed class CertbotException(string code, string message, int? exitCode = null, string? diagnosticCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? ExitCode { get; } = exitCode;
    public string? DiagnosticCode { get; } = diagnosticCode;
}

/// <summary>
/// Runs the pinned Linux Certbot installation as the current non-root service identity.
/// StageAsync is NOT an offline check: --dry-run contacts ACME staging and changes public DNS TXT
/// records. The caller must obtain explicit approval before invoking it.
/// This helper does not store tokens, activate ingress, install trust roots, or run deployment hooks.
/// </summary>
public sealed class CertbotCertificateService
{
    private const string ProductionServer = "https://acme-v02.api.letsencrypt.org/directory";
    private const string StagingServer = "https://acme-staging-v02.api.letsencrypt.org/directory";
    private readonly string _root;
    private readonly string _published;
    private readonly ICertbotExecutor _executor;
    private readonly X509Certificate2Collection? _testRoots;
    private readonly SemaphoreSlim _operation = new(1, 1);

    public CertbotCertificateService(CertbotOptions options)
        : this(options, new CertbotProcessExecutor(), null) { }

    // The standalone source-linked checks are the only caller of this trust/execution seam.
    internal CertbotCertificateService(CertbotOptions options, ICertbotExecutor executor,
        X509Certificate2Collection? testRoots = null)
    {
        if (!Path.IsPathFullyQualified(options.StateDirectory)
            || !Path.IsPathFullyQualified(options.PublishedCertificatesDirectory))
            throw StorageError();
        _root = Path.Combine(Path.GetFullPath(options.StateDirectory), "certbot");
        _published = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.PublishedCertificatesDirectory));
        if (_published == Path.GetPathRoot(_published) || CertbotFiles.IsWithin(_root, _published)
            || CertbotFiles.IsWithin(_published, _root))
            throw StorageError();
        _executor = executor;
        _testRoots = testRoots;
    }

    /// <summary>Successful staging proves neither production issuance nor ingress activation.</summary>
    public async Task StageAsync(CertbotCertificateRequest request, string cloudflareApiToken,
        CancellationToken ct = default) =>
        _ = await ExecuteAsync(request, cloudflareApiToken, Operation.Stage, ct);

    public async Task<CertbotCertificateReceipt> IssueAsync(CertbotCertificateRequest request,
        string cloudflareApiToken, CancellationToken ct = default) =>
        (await ExecuteAsync(request, cloudflareApiToken, Operation.Issue, ct))!;

    public async Task<CertbotCertificateReceipt> RenewAsync(CertbotCertificateRequest request,
        string cloudflareApiToken, CancellationToken ct = default) =>
        (await ExecuteAsync(request, cloudflareApiToken, Operation.Renew, ct))!;

    private async Task<CertbotCertificateReceipt?> ExecuteAsync(CertbotCertificateRequest input,
        string token, Operation operation, CancellationToken ct)
    {
        var request = Validate(input, token);
        if (!await _operation.WaitAsync(0, ct))
            throw Busy();
        try
        {
            CertbotFiles.EnsureDirectory(_root);
            var leasePath = Path.Combine(_root, "operation.lock");
            CertbotFiles.RejectLinks(leasePath);
            FileStream lease;
            try { lease = CertbotFiles.OpenPrivate(leasePath, FileMode.OpenOrCreate); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33) { throw Busy(); }
            using (lease)
            {
                var config = Path.Combine(_root, "config");
                var work = Path.Combine(_root, "work");
                var logs = Path.Combine(_root, "logs");
                var credentials = Path.Combine(_root, "credentials", "cloudflare.ini");
                foreach (var directory in new[] { config, work, logs, Path.GetDirectoryName(credentials)! })
                    CertbotFiles.EnsureDirectory(directory);
                CertbotFiles.CheckTree(config, config);
                CertbotFiles.CheckTree(work);
                CertbotFiles.CheckTree(logs);
                CheckRenewalHooks(config);
                var cliConfig = Path.Combine(_root, "cli.ini");
                CertbotFiles.RejectLinks(cliConfig);
                // Suppress Certbot's user/system cli.ini, including any inherited hooks.
                using (var file = CertbotFiles.OpenPrivate(cliConfig, FileMode.Create))
                    file.Flush(true);
                var renewal = Path.Combine(config, "renewal", request.LineageName + ".conf");
                if (operation == Operation.Renew && !File.Exists(renewal))
                    throw new CertbotException("certbot_lineage_missing", "Issue this lineage before requesting renewal.");
                CheckRenewalPaths(renewal, config, request.LineageName, credentials);
                var previous = LeafFingerprint(config, request.LineageName,
                    operation == Operation.Renew ? request.DnsNames : null);
                var arguments = Arguments(request, operation, config, work, logs, credentials, cliConfig);
                try
                {
                    CertbotFiles.RejectLinks(credentials);
                    // Remove any credential left by a terminated service before restoring the current token.
                    File.Delete(credentials);
                    var bytes = Encoding.UTF8.GetBytes("dns_cloudflare_api_token = " + token + "\n");
                    try
                    {
                        using var file = CertbotFiles.OpenPrivate(credentials, FileMode.CreateNew);
                        await file.WriteAsync(bytes, ct);
                        file.Flush(true);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    CertbotFiles.PrepareCoreDirectories(_root, config, work);
                    var startedAt = DateTimeOffset.UtcNow;
                    var exitCode = await _executor.ExecuteAsync(arguments,
                        operation == Operation.Stage ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(20), ct);
                    if (exitCode != 0)
                        throw new CertbotException("certbot_failed",
                            "Certbot failed. Lucia will inspect safe diagnostic evidence and explain the next step.", exitCode,
                            CertbotFailureDiagnostics.Read(logs, request.LineageName, startedAt, DateTimeOffset.UtcNow));
                    ct.ThrowIfCancellationRequested();
                }
                finally
                {
                    // Cleanup is deliberately not cancellable. Do not publish if cleanup fails.
                    try
                    {
                        CertbotFiles.RejectLinks(credentials);
                        File.Delete(credentials);
                    }
                    finally { CertbotFiles.SanitizeLogs(logs, token); }
                }
                CertbotFiles.CheckTree(config, config);
                CertbotFiles.CheckTree(work);
                CheckRenewalPaths(renewal, config, request.LineageName, credentials);
                if (operation == Operation.Stage) return null;
                var fullchain = CertbotFiles.ReadLive(config, request.LineageName, "fullchain");
                var key = CertbotFiles.ReadLive(config, request.LineageName, "privkey");
                if (fullchain.Generation != key.Generation) throw InvalidCertificate();
                using var certificate = ValidateCertificate(fullchain.Pem, key.Pem, request.DnsNames, _testRoots);
                var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
                var (certificateFile, keyFile) = Publish(request.LineageName, fingerprint, fullchain.Pem, key.Pem, ct);
                return new(request.LineageName, certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(),
                    DnsNames(certificate), fingerprint, certificateFile, keyFile, previous != fingerprint);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (CertbotException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { throw StorageError(); }
        finally { _operation.Release(); }
    }

    private static CertbotCertificateRequest Validate(CertbotCertificateRequest request, string token)
    {
        if (request is null || !IsLineage(request.LineageName) || request.DnsNames is null
            || request.DnsNames.Count is < 1 or > 20 || request.PropagationSeconds is < 10 or > 600)
            throw InvalidRequest();
        if (!request.AcceptedTerms)
            throw new CertbotException("certbot_consent_required", "Explicit acceptance of the ACME terms is required.");
        if (!Uri.TryCreate(request.TermsUrl, UriKind.Absolute, out var terms) || terms.Scheme != "https"
            || terms.UserInfo.Length != 0 || !terms.IsWellFormedOriginalString())
            throw InvalidRequest();
        if (string.IsNullOrEmpty(request.Email) || request.Email.Length > 254 || request.Email.Any(char.IsWhiteSpace)
            || request.Email.Any(c => c > 127 || char.IsControl(c))
            || !MailAddress.TryCreate(request.Email, out var email) || email.Address != request.Email
            || !email.Host.Contains('.') || IPAddress.TryParse(email.Host, out _))
            throw InvalidRequest();
        var names = request.DnsNames.Select(ValidateDnsName).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw InvalidRequest();
        // Cloudflare tokens are opaque: preserve case and bytes, disallow INI syntax/injection.
        if (string.IsNullOrEmpty(token) || token.Length > 512
            || !Regex.IsMatch(token, @"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant))
            throw new CertbotException("certbot_token_invalid", "Supply a Cloudflare API token without whitespace or INI syntax.");
        return request with { DnsNames = names };
    }

    internal static bool IsLineage(string? value) => value is not null
        && Regex.IsMatch(value, @"\Alucia-[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    internal static string ValidateDnsName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 || value.Any(c => c > 127))
            throw InvalidRequest();
        value = value.ToLowerInvariant();
        var host = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;
        if (!host.Contains('.') || IPAddress.TryParse(host, out _) || host.Split('.').Any(label =>
            !Regex.IsMatch(label, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z", RegexOptions.CultureInvariant)))
            throw InvalidRequest();
        return value;
    }

    private static List<string> Arguments(CertbotCertificateRequest request, Operation operation,
        string config, string work, string logs, string credentials, string cliConfig)
    {
        var args = new List<string>
        {
            operation == Operation.Renew ? "renew" : "certonly",
            "--config", cliConfig, "--config-dir", config, "--work-dir", work, "--logs-dir", logs,
            "--non-interactive", "--strict-permissions", "--no-directory-hooks",
            "--cert-name", request.LineageName, "--email", request.Email, "--agree-tos", "--no-eff-email",
            "--server", operation == Operation.Stage ? StagingServer : ProductionServer,
            "--dns-cloudflare", "--dns-cloudflare-credentials", credentials,
            "--dns-cloudflare-propagation-seconds", request.PropagationSeconds.ToString(CultureInfo.InvariantCulture),
            "--preferred-challenges", "dns-01"
        };
        if (operation == Operation.Renew) args.Add("--no-random-sleep-on-renew");
        else
        {
            args.Add(operation == Operation.Stage ? "--dry-run" : "--keep-until-expiring");
            foreach (var name in request.DnsNames) { args.Add("-d"); args.Add(name); }
        }
        return args;
    }

    private static void CheckRenewalHooks(string config)
    {
        var renewals = Path.Combine(config, "renewal");
        if (!Directory.Exists(renewals)) return;
        foreach (var path in Directory.EnumerateFiles(renewals, "*.conf"))
        {
            var text = CertbotFiles.ReadBounded(path, 128 * 1024);
            if (Regex.IsMatch(text, @"(?im)^\s*(?:pre_hook|post_hook|deploy_hook|renew_hook)\s*=\s*\S"))
                throw new CertbotException("certbot_hooks_forbidden",
                    "Remove saved renewal hooks from this dedicated Certbot state. Ingress activation is managed separately.");
        }
    }

    private static void CheckRenewalPaths(string path, string config, string lineage, string credentials)
    {
        if (!File.Exists(path)) return;
        var text = CertbotFiles.ReadBounded(path, 128 * 1024);
        var expected = new Dictionary<string, string>
        {
            ["archive_dir"] = Path.Combine(config, "archive", lineage),
            ["dns_cloudflare_credentials"] = credentials
        };
        foreach (var stem in new[] { "cert", "privkey", "chain", "fullchain" })
            expected[stem] = Path.Combine(config, "live", lineage, stem + ".pem");
        foreach (var (name, value) in expected)
        {
            var matches = Regex.Matches(text, @"(?m)^\s*" + name + @"\s*=\s*([^\r\n]*)$");
            if (matches.Count != 1) throw StorageError();
            var actual = matches[0].Groups[1].Value.Trim();
            if (actual.Length >= 2 && ((actual[0] == '"' && actual[^1] == '"') || (actual[0] == '\'' && actual[^1] == '\'')))
                actual = actual[1..^1];
            if (!string.Equals(value, actual, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw StorageError();
        }
    }

    private static string? LeafFingerprint(string config, string lineage, IReadOnlyList<string>? expectedNames)
    {
        var path = Path.Combine(config, "live", lineage, "fullchain.pem");
        if (!File.Exists(path) && new FileInfo(path).LinkTarget is null) return null;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(CertbotFiles.ReadLive(config, lineage, "fullchain").Pem);
            if (expectedNames is not null && !NamesMatch(DnsNames(certificate), expectedNames))
                throw InvalidCertificate();
            return certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        }
        catch (CryptographicException) { throw InvalidCertificate(); }
    }

    internal static X509Certificate2 ValidateCertificate(string fullchain, string key,
        IReadOnlyList<string> expectedNames, X509Certificate2Collection? testRoots = null)
    {
        X509Certificate2? leaf = null;
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPem(fullchain);
            if (certificates.Count is < 2 or > 10) throw InvalidCertificate();
            leaf = X509Certificate2.CreateFromPem(fullchain, key);
            var now = DateTimeOffset.UtcNow;
            if (leaf.NotBefore.ToUniversalTime() > now || leaf.NotAfter.ToUniversalTime() <= now.AddDays(7))
                throw InvalidCertificate();
            var names = DnsNames(leaf);
            if (!NamesMatch(names, expectedNames)) throw InvalidCertificate();
            if (certificates.Cast<X509Certificate2>().Any(IsStaging)) throw InvalidCertificate();
            using var chain = new X509Chain();
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            chain.ChainPolicy.ExtraStore.AddRange(certificates);
            if (testRoots is not null)
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(testRoots);
            }
            if (!chain.Build(leaf) || chain.ChainElements.Cast<X509ChainElement>().Any(e => IsStaging(e.Certificate)))
                throw new CertbotException("certbot_chain_untrusted",
                    "The certificate must chain to an existing system-trusted root, not ACME staging. Check the fullchain and host trust store.");
            return leaf;
        }
        catch (CertbotException) { leaf?.Dispose(); throw; }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
        { leaf?.Dispose(); throw InvalidCertificate(); }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static bool IsStaging(X509Certificate2 certificate) =>
        certificate.Subject.Contains("STAGING", StringComparison.OrdinalIgnoreCase)
        || certificate.Issuer.Contains("STAGING", StringComparison.OrdinalIgnoreCase)
        || certificate.Issuer.Contains("Fake LE", StringComparison.OrdinalIgnoreCase);

    private static string[] DnsNames(X509Certificate2 certificate)
    {
        var extensions = certificate.Extensions.Cast<X509Extension>().Where(e => e.Oid?.Value == "2.5.29.17").ToArray();
        if (extensions.Length != 1) throw InvalidCertificate();
        var names = new X509SubjectAlternativeNameExtension(extensions[0].RawData, extensions[0].Critical)
            .EnumerateDnsNames().Select(ValidateDnsName).ToArray();
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static bool Covers(string certificateName, string expected)
    {
        if (certificateName == expected) return true;
        if (!certificateName.StartsWith("*.", StringComparison.Ordinal) || expected.StartsWith("*.", StringComparison.Ordinal))
            return false;
        var suffix = certificateName[1..];
        return expected.EndsWith(suffix, StringComparison.Ordinal)
            && expected.Length > suffix.Length && !expected[..^suffix.Length].Contains('.');
    }

    private static bool NamesMatch(IReadOnlyList<string> names, IReadOnlyList<string> expectedNames) =>
        names.Count != 0 && !expectedNames.Any(expected => !names.Any(actual => Covers(actual, expected)))
        && !names.Any(actual => !expectedNames.Any(expected => Covers(expected, actual)));

    private (string Certificate, string Key) Publish(string lineage, string fingerprint,
        string certificate, string key, CancellationToken ct)
    {
        CertbotFiles.EnsureDirectory(_published);
        var parent = Path.Combine(_published, lineage);
        CertbotFiles.EnsureDirectory(parent);
        var target = Path.Combine(parent, fingerprint);
        CertbotFiles.RejectLinks(target);
        if (!Directory.Exists(target))
        {
            var staging = Path.Combine(parent, ".new-" + Guid.NewGuid().ToString("N"));
            try
            {
                CertbotFiles.EnsureDirectory(staging);
                CertbotFiles.WritePrivate(Path.Combine(staging, "fullchain.pem"), certificate);
                CertbotFiles.WritePrivate(Path.Combine(staging, "privkey.pem"), key);
                ct.ThrowIfCancellationRequested();
                CertbotFiles.RejectLinks(target);
                Directory.Move(staging, target);
            }
            finally
            {
                CertbotFiles.RejectLinks(staging);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        CertbotFiles.CheckTree(target);
        var certificateFile = Path.Combine(target, "fullchain.pem");
        var keyFile = Path.Combine(target, "privkey.pem");
        if (CertbotFiles.ReadBounded(certificateFile, 128 * 1024) != certificate
            || CertbotFiles.ReadBounded(keyFile, 32 * 1024) != key)
            throw StorageError();
        return (certificateFile, keyFile);
    }

    internal static CertbotException StorageError() => new("certbot_storage_unsafe",
        "Certbot storage is unavailable or unsafe. Check private directory permissions, symlinks, and available space.");
    private static CertbotException Busy() => new("certbot_busy", "Another certificate operation owns this Certbot state. Retry after it finishes.");
    private static CertbotException InvalidRequest() => new("certbot_request_invalid",
        "Use a generated lineage, 1–20 distinct ASCII DNS names, a valid email and HTTPS terms URL, and propagation of 10–600 seconds.");
    internal static CertbotException InvalidCertificate() => new("certbot_certificate_invalid",
        "The certificate/key must match, cover precisely the requested DNS names, be currently valid for more than seven days, and not be staging certificates.");
    private enum Operation { Stage, Issue, Renew }
}
