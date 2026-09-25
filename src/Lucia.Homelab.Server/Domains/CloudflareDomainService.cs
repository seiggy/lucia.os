using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Domains;

public sealed class CloudflareDomainOptions
{
    public string CredentialsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "network-credentials");
}

public sealed record CloudflareCredentialStatus(
    bool Configured, string? AccountId, DateTimeOffset? VerifiedAt, DateTimeOffset? ExpiresAt, int ZoneCount);
public sealed record CloudflareZone(string Id, string Name, string Status, string[] NameServers);
public sealed record CloudflareChallengeRecord(string Id, string Name, string Type, string Content);

public sealed class CloudflareCredentialRequest(string? accountId, string? token)
{
    public string? AccountId { get; } = accountId;
    [JsonIgnore] public string? Token { get; } = token;
    public override string ToString() => nameof(CloudflareCredentialRequest);
}

/// <summary>Server only. Pass Token through a private Certbot credentials file, never arguments, logs or a response.</summary>
public sealed class CloudflareToken
{
    internal CloudflareToken(string? accountId, string? token) { AccountId = accountId; Token = token; }
    public string? AccountId { get; }
    [JsonIgnore] public string? Token { get; }
    public override string ToString() => nameof(CloudflareToken);
}

public sealed class CloudflareDomainException(int statusCode, string code, string message, int? retryAfterSeconds = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Read-only Cloudflare account-token verification and zone discovery. Verification is not proof of
/// DNS Edit permission: activation must still pass the parent's Certbot staging DNS-01 gate.
/// </summary>
public sealed class CloudflareDomainService
{
    private readonly CloudflareCredentialStore _store;
    private readonly CloudflareHttp _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CloudflareDomainService(IOptions<CloudflareDomainOptions> options, IDataProtectionProvider protection, HttpClient http)
    {
        _store = new(options.Value.CredentialsDirectory, protection);
        _http = new(http);
    }

    public Task<CloudflareCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(async ct => Status(await _store.ReadAsync(ct)), cancellationToken);

    public Task<CloudflareCredentialStatus> DisconnectAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(async ct =>
        {
            var disconnected = new CloudflareStoredCredential(1, "disconnected", null, null, null, null, 0);
            await _store.WriteAsync(disconnected, ct);
            return Status(disconnected);
        }, cancellationToken);

    public Task<CloudflareToken> GetTokenAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(async ct =>
        {
            var record = await _store.ReadAsync(ct);
            if (record is null || record.State == "disconnected") return new CloudflareToken(null, null);
            CheckExpiry(record);
            return new CloudflareToken(record.AccountId, record.Token);
        }, cancellationToken);

    public Task<CloudflareCredentialStatus> SaveAsync(CloudflareCredentialRequest request, CancellationToken cancellationToken = default)
    {
        if (!CloudflareValidation.Id(request.AccountId))
            throw new CloudflareDomainException(400, "invalid_account_id", "Supply the Cloudflare account ID: exactly 32 hexadecimal characters.");
        if (!CloudflareValidation.Token(request.Token))
            throw new CloudflareDomainException(400, "invalid_token_format", "Supply an account-owned API token of 1–4096 visible ASCII characters, without whitespace.");
        return LockedAsync(async ct =>
        {
            await _store.ReadAsync(ct);
            var account = request.AccountId!.ToLowerInvariant();
            using var verification = await _http.GetAsync($"/accounts/{account}/tokens/verify", request.Token!, ct);
            var result = CloudflareHttp.Result(verification);
            if (CloudflareHttp.String(result, "status") != "active")
                throw new CloudflareDomainException(422, "provider_token_inactive", "Cloudflare reports this account token is not active. Create or enable a token in the selected account; the previous connection was preserved.");
            var notBefore = OptionalDate(result, "not_before");
            var expires = OptionalDate(result, "expires_on");
            var now = DateTimeOffset.UtcNow;
            if (notBefore > now)
                throw new CloudflareDomainException(422, "provider_token_not_yet_valid", "This Cloudflare token is not valid yet. Check its start time and the server clock.");
            if (expires <= now || (notBefore.HasValue && expires <= notBefore))
                throw Expired();
            var zones = await ListZonesAsync(account, request.Token!, ct);
            if (zones.Count == 0)
                throw new CloudflareDomainException(422, "no_accessible_zones", "No active full-setup zones are visible in this account. Check the account ID, Cloudflare authoritative DNS, and Zone / Zone / Read on the specific selected zone. A registrar transfer is not required.");
            // A short-lived candidate must still be valid after discovery, immediately before persistence.
            var record = new CloudflareStoredCredential(1, "managed", account, request.Token, DateTimeOffset.UtcNow, expires, zones.Count);
            CheckExpiry(record);
            await _store.WriteAsync(record, ct);
            return Status(record);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(CancellationToken cancellationToken = default) =>
        LockedAsync(async ct =>
        {
            var record = await RequireCredentialAsync(ct);
            return await ListZonesAsync(record.AccountId!, record.Token!, ct);
        }, cancellationToken);

    public Task<CloudflareZone> GetZoneAsync(string zoneId, CancellationToken cancellationToken = default) =>
        LockedAsync(async ct =>
        {
            var record = await RequireCredentialAsync(ct);
            return await GetZoneAsync(record, zoneId, ct);
        }, cancellationToken);

    /// <summary>
    /// Server-only preflight, exact names only; never adopts, changes or deletes records.
    /// Include the planned challenge names and their relevant ancestors to inspect CNAME/NS delegation.
    /// This is not a substitute for checking actual public DNS authority.
    /// </summary>
    public Task<IReadOnlyList<CloudflareChallengeRecord>> ListChallengeRecordsAsync(string zoneId,
        IReadOnlyCollection<string> names, CancellationToken cancellationToken = default) =>
        LockedAsync<IReadOnlyList<CloudflareChallengeRecord>>(async ct =>
        {
            if (names is null || names.Count is < 1 or > 64)
                throw new CloudflareDomainException(400, "invalid_challenge_names", "Supply between 1 and 64 exact DNS names for preflight.");
            var record = await RequireCredentialAsync(ct);
            var zone = await GetZoneAsync(record, zoneId, ct);
            var normalized = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var canonical = CloudflareValidation.DnsName(name, allowUnderscore: true);
                if (canonical is null || !CloudflareValidation.InZone(canonical, zone.Name))
                    throw new CloudflareDomainException(400, "invalid_challenge_names", "Every exact preflight name must belong to the selected zone.");
                normalized.Add(canonical);
            }
            var records = new List<CloudflareChallengeRecord>();
            foreach (var name in normalized)
            {
                var entries = await _http.ListAsync($"/zones/{zone.Id}/dns_records?name={Uri.EscapeDataString(name)}&match=all",
                    record.Token!, ct);
                foreach (var entry in entries)
                {
                    var id = CloudflareHttp.String(entry, "id");
                    var returnedName = CloudflareValidation.DnsName(CloudflareHttp.String(entry, "name"), allowUnderscore: true);
                    var type = CloudflareHttp.String(entry, "type");
                    var content = CloudflareHttp.String(entry, "content");
                    if (!CloudflareValidation.Id(id) || returnedName != name || type is not { Length: >= 1 and <= 16 }
                        || type.Any(c => c is not (>= 'A' and <= 'Z') && !char.IsAsciiDigit(c))
                        || content is null || content.Length > 4096 || content.Any(char.IsControl))
                        throw CloudflareHttp.InvalidResponse();
                    records.Add(new(id!.ToLowerInvariant(), returnedName!, type, content));
                    if (records.Count > 1000) throw CloudflareHttp.Incomplete();
                }
            }
            if (records.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != records.Count)
                throw CloudflareHttp.Incomplete();
            return records;
        }, cancellationToken);

    private async Task<CloudflareZone> GetZoneAsync(CloudflareStoredCredential record, string zoneId, CancellationToken ct)
    {
        if (!CloudflareValidation.Id(zoneId))
            throw new CloudflareDomainException(400, "invalid_zone_id", "Supply a 32-character hexadecimal Cloudflare zone ID.");
        using var document = await _http.GetAsync($"/zones/{zoneId.ToLowerInvariant()}", record.Token!, ct);
        var zone = ParseZone(CloudflareHttp.Result(document), record.AccountId!);
        if (!zone.Id.Equals(zoneId, StringComparison.OrdinalIgnoreCase)) throw CloudflareHttp.InvalidResponse();
        return zone;
    }

    private async Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(string account, string token, CancellationToken ct)
    {
        var entries = await _http.ListAsync($"/zones?account.id={account}&status=active&type=full&match=all", token, ct);
        var zones = entries.Select(entry => ParseZone(entry, account)).ToArray();
        if (zones.Select(zone => zone.Id).Distinct(StringComparer.Ordinal).Count() != zones.Length
            || zones.Select(zone => zone.Name).Distinct(StringComparer.Ordinal).Count() != zones.Length)
            throw CloudflareHttp.Incomplete();
        return zones;
    }

    private static CloudflareZone ParseZone(JsonElement value, string account)
    {
        var id = CloudflareHttp.String(value, "id");
        var name = CloudflareValidation.DnsName(CloudflareHttp.String(value, "name"));
        if (!CloudflareValidation.Id(id) || name is null) throw CloudflareHttp.InvalidResponse();
        if (!value.TryGetProperty("account", out var owner)
            || !string.Equals(CloudflareHttp.String(owner, "id"), account, StringComparison.OrdinalIgnoreCase))
            throw new CloudflareDomainException(422, "zone_account_mismatch", "Cloudflare returned a zone outside the selected account. Check the account ID and token's specific-zone scope.");
        if (CloudflareHttp.String(value, "status") != "active" || CloudflareHttp.String(value, "type") != "full")
            throw new CloudflareDomainException(422, "zone_not_authoritative", "The selected zone must be active with full Cloudflare DNS setup. Check its authoritative nameservers; a registrar transfer is not required.");
        if (!value.TryGetProperty("name_servers", out var servers) || servers.ValueKind != JsonValueKind.Array
            || servers.GetArrayLength() is < 1 or > 20) throw CloudflareHttp.InvalidResponse();
        var nameservers = servers.EnumerateArray().Select(s =>
            s.ValueKind == JsonValueKind.String ? CloudflareValidation.DnsName(s.GetString()) : null).ToArray();
        if (nameservers.Any(s => s is null) || nameservers.Distinct(StringComparer.Ordinal).Count() != nameservers.Length)
            throw CloudflareHttp.InvalidResponse();
        return new(id!.ToLowerInvariant(), name, "active", nameservers.Select(s => s!).ToArray());
    }

    private async Task<CloudflareStoredCredential> RequireCredentialAsync(CancellationToken ct)
    {
        var record = await _store.ReadAsync(ct);
        if (record is null || record.State == "disconnected")
            throw new CloudflareDomainException(409, "cloudflare_not_connected", "Connect a Cloudflare account-owned token first.");
        CheckExpiry(record);
        return record;
    }

    private static void CheckExpiry(CloudflareStoredCredential record)
    {
        if (record.ExpiresAt <= DateTimeOffset.UtcNow) throw Expired();
    }

    private static CloudflareDomainException Expired() =>
        new(422, "provider_token_expired", "The Cloudflare account token has expired. Connect a new token with Zone / DNS / Edit and Zone / Zone / Read restricted to the specific selected zone.");

    private static CloudflareCredentialStatus Status(CloudflareStoredCredential? record) =>
        record is { State: "managed" } ? new(true, record.AccountId, record.VerifiedAt, record.ExpiresAt, record.ZoneCount)
            : new(false, null, null, null, 0);

    private static DateTimeOffset? OptionalDate(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String || !property.TryGetDateTimeOffset(out var date))
            throw CloudflareHttp.InvalidResponse();
        return date;
    }

    private async Task<T> LockedAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var acquired = false;
        try
        {
            await _gate.WaitAsync(deadline.Token);
            acquired = true;
            return await action(deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw CloudflareHttp.TimeoutError(); }
        finally { if (acquired) _gate.Release(); }
    }
}

internal static class CloudflareValidation
{
    internal static bool Id(string? value) => value is { Length: 32 } && value.All(char.IsAsciiHexDigit);
    internal static bool Token(string? value) => value is { Length: >= 1 and <= 4096 } && value.All(c => c is >= '!' and <= '~');
    internal static bool InZone(string name, string zone) => name == zone || name.EndsWith("." + zone, StringComparison.Ordinal);

    internal static string? DnsName(string? value, bool allowUnderscore = false)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.Any(char.IsWhiteSpace)) return null;
        try
        {
            var dotted = value.Replace('\u3002', '.').Replace('\uff0e', '.').Replace('\uff61', '.');
            var labels = dotted.TrimEnd('.').Split('.');
            if (labels.Length < 2 || dotted.EndsWith("..", StringComparison.Ordinal)) return null;
            var idn = new IdnMapping { UseStd3AsciiRules = true };
            for (var i = 0; i < labels.Length; i++)
            {
                var underscore = allowUnderscore && labels[i].StartsWith('_');
                var label = idn.GetAscii(underscore ? labels[i][1..] : labels[i]).ToLowerInvariant();
                // Round-trip validates malformed A-labels as well as Unicode input.
                if (idn.GetAscii(idn.GetUnicode(label)).ToLowerInvariant() != label) return null;
                labels[i] = (underscore ? "_" : "") + label;
                if (labels[i].Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-')
                    || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
            }
            var name = string.Join('.', labels);
            return name.Length <= 253 && !IPAddress.TryParse(name, out _) ? name : null;
        }
        catch (ArgumentException) { return null; }
    }
}
