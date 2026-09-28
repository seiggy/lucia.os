using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public sealed class AdGuardManagementOptions
{
    public string CredentialsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "network-credentials");
}

// Not a record: generated ToString must never include the password.
public sealed class AdGuardConnectionRequest
{
    public string BaseUrl { get; init; } = "";
    public string Username { get; init; } = "";
    [JsonIgnore] public string Password { get; init; } = "";
    public bool AllowInsecureHttp { get; init; }
    public override string ToString() => nameof(AdGuardConnectionRequest);
}

public sealed record AdGuardConnectionStatus(bool Configured, string? BaseUrl, string? Username,
    bool AllowInsecureHttp, string? Version, DateTimeOffset? LastVerifiedAt);

public sealed record AdGuardRewrite(string Domain, string Answer)
{
    // Older AdGuard versions omit this field and treat every rule as enabled.
    public bool Enabled { get; init; } = true;
}

public sealed record AdGuardHealth(bool Running, bool ProtectionEnabled, bool RewritesEnabled)
{
    public string? Version { get; init; }
    public int DnsPort { get; init; }
    public IReadOnlyList<string> DnsAddresses { get; init; } = [];
}

/// <summary>
/// Local DNS capability, independent of who deploys AdGuard. List returns existing A/AAAA/CNAME
/// entries, including disabled rules. Writes accept only hostnames and private IP answers.
/// Add never replaces a domain already present (including a disabled rule); delete targets the
/// exact domain/answer pair (all duplicates, regardless of enabled), never a whole domain or list.
/// AdGuard add appends duplicates; this connector refuses existing domains before adding.
/// The caller owns durable intent,
/// cross-client conflicts, idempotency and rollback; AdGuard offers no conditional-write API.
/// </summary>
public interface ILocalDnsProvider
{
    Task<AdGuardConnectionStatus> GetConnectionAsync(CancellationToken cancellationToken = default);
    Task<AdGuardHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdGuardRewrite>> ListRewritesAsync(CancellationToken cancellationToken = default);
    Task AddRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default);
    Task DeleteRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default);
}

public sealed class AdGuardManagementException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class AdGuardConnectionService : ILocalDnsProvider
{
    private readonly AdGuardCredentialStore _store;
    private readonly AdGuardTransport _transport;
    // Single host instance; serialize replacement/disconnect against every DNS operation.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AdGuardConnectionService(IOptions<AdGuardManagementOptions> options, IDataProtectionProvider protection)
        : this(options, protection, new AdGuardTransport()) { }

    internal AdGuardConnectionService(IOptions<AdGuardManagementOptions> options,
        IDataProtectionProvider protection, AdGuardTransport transport)
    {
        _store = new(options.Value.CredentialsDirectory, protection);
        _transport = transport;
    }

    public Task<AdGuardConnectionStatus> GetConnectionAsync(CancellationToken cancellationToken = default) =>
        GetStatusAsync(cancellationToken);

    public async Task<AdGuardConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return Status(await _store.ReadAsync(cancellationToken)); }
        finally { _gate.Release(); }
    }

    /// <summary>Read-only remote validation before atomic credential replacement; failure preserves prior state.</summary>
    public async Task<AdGuardConnectionStatus> SaveAsync(AdGuardConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var origin = AdGuardValidation.Connection(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _store.ReadAsync(cancellationToken);
            var record = new AdGuardStoredConnection
            {
                State = "configured", BaseUrl = origin, Username = request.Username,
                Password = request.Password, AllowInsecureHttp = request.AllowInsecureHttp
            };
            await VerifyRecordAsync(record, cancellationToken);
            await _store.WriteAsync(record, cancellationToken);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<AdGuardConnectionStatus> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireConnectionAsync(cancellationToken);
            await VerifyRecordAsync(record, cancellationToken);
            await _store.WriteAsync(record, cancellationToken);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<AdGuardConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = new AdGuardStoredConnection { State = "disconnected" };
            await _store.WriteAsync(record, cancellationToken);
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<AdGuardHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireConnectionAsync(cancellationToken);
            return await _transport.RunAsync(record, async (session, ct) =>
            {
                var status = await session.StatusAsync(ct);
                return new AdGuardHealth(status.Running, status.ProtectionEnabled, await session.RewritesEnabledAsync(ct))
                {
                    Version = status.Version, DnsPort = status.DnsPort, DnsAddresses = status.DnsAddresses
                };
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AdGuardRewrite>> ListRewritesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireConnectionAsync(cancellationToken);
            return await _transport.RunAsync(record, (session, ct) => session.ListAsync(ct), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public Task AddRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default) =>
        MutateAsync(rewrite, add: true, cancellationToken);

    public Task DeleteRewriteAsync(AdGuardRewrite rewrite, CancellationToken cancellationToken = default) =>
        MutateAsync(rewrite, add: false, cancellationToken);

    private async Task MutateAsync(AdGuardRewrite rewrite, bool add, CancellationToken cancellationToken)
    {
        AdGuardValidation.Rewrite(rewrite, add);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireConnectionAsync(cancellationToken);
            await _transport.RunAsync(record, async (session, ct) =>
            {
                var health = await session.StatusAsync(ct);
                if (!health.Running || !health.ProtectionEnabled || !await session.RewritesEnabledAsync(ct))
                    throw new AdGuardManagementException(409, "adguard_rewrites_disabled",
                        "Start AdGuard DNS and enable protection and DNS rewrites in AdGuard before applying records. Lucia will not change these global settings.");
                var entries = await session.ListAsync(ct);
                if (add && entries.Any(item => string.Equals(item.Domain.TrimEnd('.'), rewrite.Domain.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
                    throw new AdGuardManagementException(409, "adguard_rewrite_conflict",
                        "A rewrite for this domain already exists. Review the existing rule; Lucia did not replace it.");
                if (!add && !entries.Any(item => item.Domain == rewrite.Domain && item.Answer == rewrite.Answer))
                    throw new AdGuardManagementException(409, "adguard_rewrite_not_found",
                        "The exact domain and answer pair no longer exists. Refresh and reconcile the DNS operation.");
                await session.MutateAsync(rewrite, add, ct);
                return true;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The connection's own host name, which is the name AdGuard serves HTTPS and DoT under.</summary>
    public async Task<string> CertificateNameAsync(CancellationToken cancellationToken = default) =>
        new Uri((await RequireConnectionAsync(cancellationToken)).BaseUrl!).IdnHost;

    /// <summary>Serves the chain as names[0]. Returns false when AdGuard already serves it.</summary>
    public async Task<bool> PushCertificateAsync(IReadOnlyList<string> names, string chainPem, string keyPem, CancellationToken cancellationToken = default)
    {
        var name = names[0];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var record = await RequireConnectionAsync(cancellationToken);
            if (!names.Contains(new Uri(record.BaseUrl!).IdnHost))
                throw new AdGuardManagementException(409, "adguard_connection_changed", "The AdGuard connection changed; the certificate will be reissued.");
            var chain = Convert.ToBase64String(Encoding.UTF8.GetBytes(chainPem));
            return await _transport.RunAsync(record, async (session, ct) =>
            {
                var settings = await session.TlsStatusAsync(ct);
                if (settings["enabled"]?.GetValue<bool>() == true && settings["server_name"]?.GetValue<string>() == name
                    && settings["certificate_chain"]?.GetValue<string>() == chain)
                    return false;
                settings["enabled"] = true;
                settings["server_name"] = name;
                settings["certificate_chain"] = chain;
                settings["private_key"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyPem));
                settings["certificate_path"] = "";
                settings["private_key_path"] = "";
                settings["private_key_saved"] = false;
                await session.ConfigureTlsAsync(settings, ct);
                return true;
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task VerifyRecordAsync(AdGuardStoredConnection record, CancellationToken cancellationToken)
    {
        var version = await _transport.RunAsync(record, async (session, ct) =>
        {
            var status = await session.StatusAsync(ct);
            if (!status.Running)
                throw new AdGuardManagementException(409, "adguard_dns_not_running",
                    "AdGuard DNS is not running. Start DNS in AdGuard before verifying this connection; the previous configuration was preserved.");
            await session.ListAsync(ct);
            await session.RewritesEnabledAsync(ct);
            return status.Version;
        }, cancellationToken);
        record.ServerVersion = version;
        record.LastVerifiedAt = DateTimeOffset.UtcNow;
    }

    private async Task<AdGuardStoredConnection> RequireConnectionAsync(CancellationToken cancellationToken)
    {
        var record = await _store.ReadAsync(cancellationToken);
        return record?.State == "configured" ? record
            : throw new AdGuardManagementException(409, "adguard_not_configured", "Connect AdGuard Home in Settings first.");
    }

    private static AdGuardConnectionStatus Status(AdGuardStoredConnection? record) => record?.State == "configured"
        ? new(true, record.BaseUrl, record.Username, record.AllowInsecureHttp, record.ServerVersion, record.LastVerifiedAt)
        : new(false, null, null, false, null, null);
}

internal static class AdGuardValidation
{
    internal static string Connection(AdGuardConnectionRequest? request)
    {
        if (request is null || request.Username is not { Length: >= 1 and <= 128 }
            || request.Username.Any(c => c == ':' || char.IsControl(c))
            || request.Password is not { Length: >= 1 and <= 1024 }
            || request.Password.Contains('\r') || request.Password.Contains('\n'))
            throw new AdGuardManagementException(400, "invalid_adguard_credentials",
                "Supply a username of 1–128 characters without colons or controls and a password of 1–1024 characters without CR/LF. Whitespace is preserved.");
        try { new UTF8Encoding(false, true).GetByteCount(request.Username + request.Password); }
        catch (EncoderFallbackException)
        { throw new AdGuardManagementException(400, "invalid_adguard_credentials", "Use valid Unicode text for the username and password."); }
        if (request.BaseUrl is not { Length: >= 1 and <= 512 }
            || request.BaseUrl.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Regex.IsMatch(request.BaseUrl, @"\Ahttps?://[^/?#\\%]+/?\z", RegexOptions.CultureInvariant)
            || !Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || uri.Port is < 1 or > 65535 || uri.Host.Length > 253)
            throw new AdGuardManagementException(400, "invalid_adguard_url",
                "Use an HTTP or HTTPS origin, optionally with an explicit port; paths, user information, query strings and fragments are not supported.");
        if (uri.Scheme == "http" && !request.AllowInsecureHttp)
            throw new AdGuardManagementException(400, "adguard_http_consent_required",
                "HTTP exposes the AdGuard username and password on the LAN. Explicitly allow insecure HTTP or use HTTPS.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    internal static bool Domain(string? value)
    {
        if (value is not { Length: >= 1 and <= 253 }) return false;
        var name = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;
        if (name.EndsWith('.')) name = name[..^1];
        return name.Length > 0 && !System.Net.IPAddress.TryParse(name, out _)
            && name.Split('.').All(label => Regex.IsMatch(label,
                @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant));
    }

    internal static void Rewrite(AdGuardRewrite? rewrite, bool add)
    {
        if (rewrite is null || !Domain(rewrite.Domain) || (add && !rewrite.Enabled)
            || !System.Net.IPAddress.TryParse(rewrite.Answer, out var ip)
            || !AdGuardTransport.IsPrivate(ip) || rewrite.Answer != ip.ToString())
            throw new AdGuardManagementException(400, "invalid_adguard_rewrite",
                "Use an ASCII hostname (optional leftmost wildcard) and a canonical private RFC1918 or IPv6 ULA address. Only enabled IP rewrites can be written.");
    }

    internal static bool Version(string? value) => value is { Length: >= 1 and <= 64 }
        && Regex.IsMatch(value, @"\Av[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.+-]+)?\z", RegexOptions.CultureInvariant);

    internal static bool SensitiveMetadata(string value, string username, string password) =>
        value == password || (password.Length >= 8 && value.Contains(password, StringComparison.Ordinal))
        || value.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)), StringComparison.Ordinal);

    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
