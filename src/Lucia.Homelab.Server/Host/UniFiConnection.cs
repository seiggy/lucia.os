using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace Lucia.Homelab.Server.Host;

// Not a record: generated ToString must never include the API key.
public sealed class UniFiConnectionRequest
{
    public string BaseUrl { get; init; } = "";
    [JsonIgnore] public string ApiKey { get; init; } = "";
    public string Site { get; init; } = "default";
    public string? CertificateSha256 { get; init; }
    public override string ToString() => nameof(UniFiConnectionRequest);
}

public sealed record UniFiConnectionStatus(bool Configured, string? BaseUrl, string? Site, string? CertificateSha256,
    string? NetworkVersion, bool ReserveNodeAddresses, DateTimeOffset? LastVerifiedAt);

/// <summary>A UniFi client as seen in the known-client table (<c>rest/user</c>) or the live table (<c>stat/sta</c>).</summary>
public sealed record UniFiClient(string Id, string Mac, string? Address, string? NetworkId, bool UseFixedIp, string? FixedIp);

public sealed class UniFiException(int statusCode, string code, string message, string? certificateSha256 = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? CertificateSha256 { get; } = certificateSha256;
}

/// <summary>DHCP reservation capability used by managed-node automation; UniFi Network is the only implementation.</summary>
public interface IDhcpReservations
{
    event Action? Changed;
    Task<UniFiConnectionStatus> GetStatusAsync(CancellationToken ct = default);
    Task<(UniFiClient[] Known, UniFiClient[] Online)> ListClientsAsync(CancellationToken ct = default);
    /// <summary>Pins <paramref name="address"/> to the client's MAC. Never retried: callers re-read before acting again.</summary>
    Task ReserveAsync(UniFiClient client, string address, string networkId, CancellationToken ct = default);
}

internal sealed record UniFiStoredConnection(int Version, string BaseUrl, string ApiKey, string Site, string? CertificateSha256,
    string NetworkVersion, bool ReserveNodeAddresses, DateTimeOffset LastVerifiedAt)
{
    public override string ToString() => nameof(UniFiStoredConnection);
}

/// <summary>
/// UniFi Network connection. API contract verified against UniFi Network 10.6.106 with an API key (X-API-KEY):
/// GET /proxy/network/integration/v1/info, /proxy/network/api/self/sites, /api/s/{site}/rest/user, /api/s/{site}/stat/sta;
/// PUT /api/s/{site}/rest/user/{id} with use_fixedip, fixed_ip and network_id. The Integration API has no reservation endpoint.
/// </summary>
public sealed class UniFiConnectionService : IDhcpReservations
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly UniFiTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? Changed;

    public UniFiConnectionService(IOptions<AdGuardManagementOptions> options, IDataProtectionProvider protection)
        : this(options.Value.CredentialsDirectory, protection, new UniFiTransport()) { }

    internal UniFiConnectionService(string directory, IDataProtectionProvider protection, UniFiTransport transport)
    {
        if (!Path.IsPathFullyQualified(directory)) throw StorageError();
        _path = Path.Combine(Path.GetFullPath(directory), "unifi.json");
        _protector = protection.CreateProtector("Lucia.Homelab.UniFiCredentials.v1");
        _transport = transport;
    }

    public async Task<UniFiConnectionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return Status(Read()); }
        finally { _gate.Release(); }
    }

    /// <summary>Verifies the gateway before atomically replacing the saved connection; failure keeps the previous one.</summary>
    public async Task<UniFiConnectionStatus> SaveAsync(UniFiConnectionRequest request, CancellationToken ct = default)
    {
        var (origin, pin) = UniFiValidation.Connection(request);
        await _gate.WaitAsync(ct);
        try
        {
            var previous = Read();
            var version = await VerifyAsync(origin, request.ApiKey, request.Site, pin, ct);
            var record = new UniFiStoredConnection(1, origin, request.ApiKey, request.Site, pin, version,
                previous?.ReserveNodeAddresses ?? true, DateTimeOffset.UtcNow);
            await Write(record, ct);
            Changed?.Invoke();
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<UniFiConnectionStatus> VerifyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var record = Require();
            record = record with
            {
                NetworkVersion = await VerifyAsync(record.BaseUrl, record.ApiKey, record.Site, record.CertificateSha256, ct),
                LastVerifiedAt = DateTimeOffset.UtcNow
            };
            await Write(record, ct);
            Changed?.Invoke();
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<UniFiConnectionStatus> SetReservationsAsync(bool enabled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var record = Require() with { ReserveNodeAddresses = enabled };
            await Write(record, ct);
            Changed?.Invoke();
            return Status(record);
        }
        finally { _gate.Release(); }
    }

    public async Task<UniFiConnectionStatus> DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            DomainOnboardingStore.RejectLinks(_path);
            File.Delete(_path);
            Changed?.Invoke();
            return Status(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw StorageError(); }
        finally { _gate.Release(); }
    }

    public async Task<(UniFiClient[] Known, UniFiClient[] Online)> ListClientsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var record = Require();
            return await _transport.RunAsync(record.BaseUrl, record.ApiKey, record.CertificateSha256, async (session, token) =>
                (await session.ClientsAsync(record.Site, "rest/user", token), await session.ClientsAsync(record.Site, "stat/sta", token)), ct);
        }
        finally { _gate.Release(); }
    }

    public async Task ReserveAsync(UniFiClient client, string address, string networkId, CancellationToken ct = default)
    {
        if (!UniFiValidation.Id(client.Id) || !UniFiValidation.Id(networkId)
            || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
            || !AdGuardTransport.IsPrivate(ip) || ip.ToString() != address)
            throw new UniFiException(400, "invalid_unifi_reservation", "A reservation needs a UniFi client, network and private IPv4 address.");
        await _gate.WaitAsync(ct);
        try
        {
            var record = Require();
            await _transport.RunAsync(record.BaseUrl, record.ApiKey, record.CertificateSha256,
                (session, token) => session.ReserveAsync(record.Site, client.Id, address, networkId, token), ct);
        }
        finally { _gate.Release(); }
    }

    public const string PublicForwardName = "Lucia public ingress";

    /// <summary>
    /// Keeps the router's HTTPS port forward (TCP 443 from the internet) pointed at the gateway's public entrypoint, adopting
    /// an existing 443 forward the first time. Off disables Lucia's forward. Returns its state, or null without UniFi.
    /// </summary>
    public async Task<string?> SetPublicForwardAsync(bool enabled, string address, int port, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !AdGuardTransport.IsPrivate(ip))
            throw new UniFiException(400, "invalid_unifi_forward", "The public ingress needs a private IPv4 address.");
        await _gate.WaitAsync(ct);
        try
        {
            if (Read() is not { } record) return null;
            return await _transport.RunAsync(record.BaseUrl, record.ApiKey, record.CertificateSha256, async (session, token) =>
            {
                var forwards = await session.PortForwardsAsync(record.Site, token);
                var ours = forwards.FirstOrDefault(item => UniFiSession.Field(item, "name") == PublicForwardName);
                var https = forwards.Where(item => UniFiSession.Field(item, "dst_port") == "443"
                    && UniFiSession.Field(item, "proto") is "tcp" or "tcp_udp").ToArray();
                if (!enabled)
                {
                    if (ours.ValueKind == JsonValueKind.Object && UniFiSession.Field(ours, "enabled") != "false")
                        await session.SavePortForwardAsync(record.Site, UniFiSession.Field(ours, "_id"), new() { ["enabled"] = false }, token);
                    return "Off";
                }
                var target = ours.ValueKind == JsonValueKind.Object ? ours : https.FirstOrDefault();
                if (target.ValueKind == JsonValueKind.Object && UniFiSession.Field(target, "name") == PublicForwardName
                    && UniFiSession.Field(target, "enabled") == "true" && UniFiSession.Field(target, "fwd") == address
                    && UniFiSession.Field(target, "fwd_port") == port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    && UniFiSession.Field(target, "dst_port") == "443" && UniFiSession.Field(target, "proto") == "tcp")
                    return "Forwarding";
                var body = new Dictionary<string, object>
                {
                    ["name"] = PublicForwardName, ["enabled"] = true, ["proto"] = "tcp", ["dst_port"] = "443", ["fwd"] = address,
                    ["fwd_port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture), ["src"] = "any", ["log"] = false,
                };
                if (target.ValueKind != JsonValueKind.Object)
                {
                    body["pfwd_interface"] = "wan";
                    body["destination_ip"] = "any";
                }
                await session.SavePortForwardAsync(record.Site, target.ValueKind == JsonValueKind.Object ? UniFiSession.Field(target, "_id") : null, body, token);
                return target.ValueKind == JsonValueKind.Object && UniFiSession.Field(target, "name") != PublicForwardName ? "Adopted" : "Forwarding";
            }, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> VerifyAsync(string origin, string key, string site, string? pin, CancellationToken ct) =>
        await _transport.RunAsync(origin, key, pin, async (session, token) =>
        {
            var version = await session.VersionAsync(token);
            if (!(await session.SitesAsync(token)).Contains(site, StringComparer.Ordinal))
                throw new UniFiException(404, "unifi_site_not_found", "This API key cannot see that UniFi site. Use the site's short name, usually “default”.");
            await session.ClientsAsync(site, "stat/sta", token);
            return version;
        }, ct);

    private UniFiStoredConnection Require() => Read()
        ?? throw new UniFiException(409, "unifi_not_configured", "Connect UniFi Network in Settings first.");

    private UniFiStoredConnection? Read()
    {
        try
        {
            DomainOnboardingStore.RejectLinks(_path);
            if (!File.Exists(_path)) return null;
            var envelope = JsonSerializer.Deserialize<ProtectedRecord>(CertbotFiles.ReadBounded(_path, 32768), DomainOnboardingStore.Json);
            if (envelope is not { Version: 1, ProtectedData.Length: > 0 }) throw StorageError();
            var plaintext = _protector.Unprotect(Convert.FromBase64String(envelope.ProtectedData));
            try
            {
                var record = JsonSerializer.Deserialize<UniFiStoredConnection>(plaintext, DomainOnboardingStore.Json);
                if (record is not { Version: 1 }) throw StorageError();
                UniFiValidation.Connection(new()
                {
                    BaseUrl = record.BaseUrl, ApiKey = record.ApiKey, Site = record.Site, CertificateSha256 = record.CertificateSha256
                });
                return record;
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
            or JsonException or FormatException or InvalidDataException or UniFiException)
        { throw StorageError(); }
    }

    private async Task Write(UniFiStoredConnection record, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record, DomainOnboardingStore.Json);
        try
        {
            await DomainOnboardingStore.WriteJson(_path, new ProtectedRecord(1, Convert.ToBase64String(_protector.Protect(plaintext))), ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { throw StorageError(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static UniFiConnectionStatus Status(UniFiStoredConnection? record) => record is null
        ? new(false, null, null, null, null, false, null)
        : new(true, record.BaseUrl, record.Site, record.CertificateSha256, record.NetworkVersion, record.ReserveNodeAddresses, record.LastVerifiedAt);

    private static UniFiException StorageError() => new(503, "unifi_storage_unavailable",
        "UniFi credential storage is unreadable or unsafe. Check private storage permissions and Data Protection keys, then reconnect.");

    private sealed record ProtectedRecord(int Version, string ProtectedData);
}

internal sealed class UniFiTransport
{
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly Func<Uri, IPAddress[], SocketsHttpHandler> _createHandler;

    internal UniFiTransport(Func<Uri, IPAddress[], SocketsHttpHandler>? createHandler = null) =>
        _createHandler = createHandler ?? ((origin, addresses) => AdGuardTransport.CreateHandler(origin, addresses));

    internal async Task<T> RunAsync<T>(string baseUrl, string apiKey, string? pin,
        Func<UniFiSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        // Keep the API key out of HttpClient logging and OpenTelemetry request enrichment.
        using var suppression = SuppressInstrumentationScope.Begin();
        string? observed = null;
        var rejected = false;
        try
        {
            var origin = new Uri(baseUrl);
            var host = origin.IdnHost.Trim('[', ']');
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, deadline.Token);
            if (addresses.Length is < 1 or > 64 || addresses.Any(address => !AdGuardTransport.IsPrivate(address)))
                throw new UniFiException(400, "unifi_private_network_required",
                    "The UniFi gateway must resolve only to private LAN addresses.");
            var handler = _createHandler(origin, addresses);
            // The TLS handshake completes (or fails here) before the API key header is ever written.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null) { rejected = true; return false; }
                observed = Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
                var accepted = pin is null ? errors == SslPolicyErrors.None
                    : CryptographicOperations.FixedTimeEquals(Convert.FromHexString(observed), Convert.FromHexString(pin));
                rejected = !accepted;
                return accepted;
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            return await operation(new UniFiSession(http, baseUrl, apiKey), deadline.Token);
        }
        catch (UniFiException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new UniFiException(504, "unifi_timeout", "UniFi Network did not respond within fifteen seconds."); }
        catch (Exception e) when (rejected && observed is not null && e is HttpRequestException or IOException or AuthenticationException)
        {
            throw pin is null
                ? new UniFiException(409, "unifi_certificate_untrusted",
                    "The gateway's HTTPS certificate is not trusted by this system (UniFi gateways ship a self-signed one). Compare the fingerprint with the certificate shown by your browser, then trust it.", observed)
                : new UniFiException(409, "unifi_certificate_changed",
                    "The gateway presented a different HTTPS certificate than the one you trusted. If you replaced it, reconnect and trust the new fingerprint.", observed);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or AuthenticationException)
        { throw new UniFiException(502, "unifi_unavailable", "Cannot securely reach the UniFi gateway. Check its LAN address."); }
        catch (JsonException) { throw UniFiSession.InvalidResponse(); }
        catch (Exception)
        { throw new UniFiException(502, "unifi_unavailable", "UniFi Network could not complete the operation securely."); }
    }
}

internal sealed class UniFiSession(HttpClient http, string baseUrl, string apiKey)
{
    internal async Task<string> VersionAsync(CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, "/proxy/network/integration/v1/info", null, ct);
        var version = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("applicationVersion", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        return version is { Length: >= 1 and <= 32 } && Regex.IsMatch(version, @"\A[0-9]+(?:\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant)
            ? version : throw InvalidResponse();
    }

    internal async Task<string[]> SitesAsync(CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, "/proxy/network/api/self/sites", null, ct);
        return Data(document).Select(site => Text(site, "name") is { } name && UniFiValidation.Site(name) ? name : throw InvalidResponse()).ToArray();
    }

    internal async Task<UniFiClient[]> ClientsAsync(string site, string table, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, $"/proxy/network/api/s/{site}/{table}", null, ct);
        var clients = new List<UniFiClient>();
        foreach (var item in Data(document))
        {
            var id = Text(item, "_id");
            var mac = Text(item, "mac");
            if (!UniFiValidation.Id(id) || mac is null || !UniFiValidation.Mac(mac)) throw InvalidResponse();
            var fixedIp = item.TryGetProperty("use_fixedip", out var flag) && flag.ValueKind == JsonValueKind.True;
            clients.Add(new(id!, mac.ToLowerInvariant(), Address(Text(item, "ip")), UniFiValidation.Id(Text(item, "network_id")) ? Text(item, "network_id") : null,
                fixedIp, fixedIp ? Address(Text(item, "fixed_ip")) : null));
        }
        return [.. clients];
    }

    internal async Task<bool> ReserveAsync(string site, string id, string address, string networkId, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["use_fixedip"] = true, ["fixed_ip"] = address, ["network_id"] = networkId
        });
        using var document = await SendAsync(HttpMethod.Put, $"/proxy/network/api/s/{site}/rest/user/{id}", body, ct);
        var updated = Data(document).FirstOrDefault();
        if (updated.ValueKind != JsonValueKind.Object || Text(updated, "fixed_ip") != address) throw InvalidResponse();
        return true;
    }

    internal async Task<JsonElement[]> PortForwardsAsync(string site, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Get, $"/proxy/network/api/s/{site}/rest/portforward", null, ct);
        return [.. Data(document).Select(item => UniFiValidation.Id(Text(item, "_id")) ? item.Clone() : throw InvalidResponse())];
    }

    internal async Task SavePortForwardAsync(string site, string? id, Dictionary<string, object> body, CancellationToken ct)
    {
        using var document = await SendAsync(id is null ? HttpMethod.Post : HttpMethod.Put,
            $"/proxy/network/api/s/{site}/rest/portforward" + (id is null ? "" : "/" + id), JsonSerializer.SerializeToUtf8Bytes(body), ct);
        if (!UniFiValidation.Id(Text(Data(document).FirstOrDefault(), "_id"))) throw InvalidResponse();
    }

    internal static string? Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.True => "true", JsonValueKind.False => "false",
                JsonValueKind.Number => value.GetRawText(), _ => null } : null;

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
            throw new UniFiException(502, "unifi_redirect_rejected", "The gateway requested a redirect. Enter its direct HTTPS origin.");
        if (status is 401 or 403)
            throw new UniFiException(403, "unifi_access_denied", "UniFi rejected the API key. Create a new key under Settings → Control Plane → Integrations.");
        if (status == 429) throw new UniFiException(429, "unifi_rate_limited", "UniFi rate-limited this request. Lucia will try again later.");
        if (status == 404) throw new UniFiException(502, "unifi_api_unavailable", "The UniFi Network API was not found at this address. Use a UniFi OS gateway or console.");
        if (response.Content.Headers.ContentLength > UniFiTransport.MaximumResponseBytes)
            throw new UniFiException(502, "unifi_response_too_large", "UniFi returned more than eight MiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = await AdGuardTransport.ReadBoundedAsync(stream, UniFiTransport.MaximumResponseBytes, ct);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (status == 200) return document;
        using (document)
        {
            // UniFi reports rule violations as {"meta":{"rc":"error","msg":"api.err.<Name>"}}.
            var message = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("meta", out var meta)
                ? Text(meta, "msg") : null;
            throw message is not null && Regex.IsMatch(message, @"\Aapi\.err\.[A-Za-z]{1,64}\z", RegexOptions.CultureInvariant)
                ? new UniFiException(409, "unifi_rejected", $"UniFi refused the change ({message}).")
                : new UniFiException(502, "unifi_unavailable", "UniFi Network could not complete the request.");
        }
    }

    private static IEnumerable<JsonElement> Data(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() <= 20000 ? data.EnumerateArray() : throw InvalidResponse();

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? Address(string? value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == value ? value : null;

    internal static UniFiException InvalidResponse() =>
        new(502, "invalid_unifi_response", "The server did not return the expected UniFi Network data.");
}

internal static partial class UniFiValidation
{
    internal static (string Origin, string? Pin) Connection(UniFiConnectionRequest? request)
    {
        if (request is null || request.ApiKey is not { Length: >= 16 and <= 256 } || request.ApiKey.Any(c => c <= ' ' || c > '~'))
            throw new UniFiException(400, "invalid_unifi_key", "Paste the UniFi API key exactly as shown when it was created.");
        if (!Site(request.Site))
            throw new UniFiException(400, "invalid_unifi_site", "Use the UniFi site's short name, usually “default”.");
        if (request.CertificateSha256 is not null && !Regex.IsMatch(request.CertificateSha256, @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant))
            throw new UniFiException(400, "invalid_unifi_certificate", "A trusted certificate must be a lowercase SHA-256 fingerprint.");
        if (request.BaseUrl is not { Length: >= 1 and <= 512 } || !Regex.IsMatch(request.BaseUrl, @"\Ahttps://[^/?#\\%@\s]+/?\z", RegexOptions.CultureInvariant)
            || !Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri)
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
            throw new UniFiException(400, "invalid_unifi_url", "Use the gateway's HTTPS origin, for example https://192.168.1.1, without a path.");
        return (uri.GetLeftPart(UriPartial.Authority), request.CertificateSha256);
    }

    internal static bool Site(string? value) => value is not null && SitePattern().IsMatch(value);
    internal static bool Id(string? value) => value is not null && IdPattern().IsMatch(value);
    internal static bool Mac(string value) => MacPattern().IsMatch(value);

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z")] private static partial Regex SitePattern();
    [GeneratedRegex(@"\A[0-9a-f]{24}\z")] private static partial Regex IdPattern();
    [GeneratedRegex(@"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\z")] private static partial Regex MacPattern();
}

public static class UniFiManagementEndpoints
{
    public static void AddUniFiManagement(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<UniFiConnectionService>();
        builder.Services.AddSingleton<IDhcpReservations>(services => services.GetRequiredService<UniFiConnectionService>());
    }

    /// <summary>Map behind host authentication and the global CSRF middleware.</summary>
    public static void MapUniFiManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/connections/unifi").WithTags("UniFi")
            .RequireAuthorization("HostOwner").AddEndpointFilter<UniFiManagementFilter>();
        group.MapGet("", (UniFiConnectionService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapPut("", async (HttpContext context, UniFiConnectionService service, CancellationToken ct) =>
        {
            const int limit = 16 * 1024;
            if (context.Request.ContentLength > limit || !context.Request.HasJsonContentType())
                throw new UniFiException(400, "invalid_unifi_request", "Send a UniFi connection JSON object under 16 KiB.");
            var body = await AdGuardTransport.ReadBoundedAsync(context.Request.Body, limit, ct);
            try
            {
                // Avoid model binding diagnostics and duplicate or unknown secret fields.
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 2 });
                var root = document.RootElement;
                string[] names = ["baseUrl", "apiKey", "site", "certificateSha256"];
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length
                    || root.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))
                    || AdGuardValidation.String(root, "baseUrl") is not { } baseUrl
                    || AdGuardValidation.String(root, "apiKey") is not { } apiKey
                    || AdGuardValidation.String(root, "site") is not { } site
                    || root.GetProperty("certificateSha256").ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new UniFiException(400, "invalid_unifi_request", "Supply exactly baseUrl, apiKey, site and certificateSha256.");
                return await service.SaveAsync(new()
                {
                    BaseUrl = baseUrl, ApiKey = apiKey, Site = site, CertificateSha256 = AdGuardValidation.String(root, "certificateSha256")
                }, ct);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        });
        group.MapDelete("", (UniFiConnectionService service, CancellationToken ct) => service.DisconnectAsync(ct));
        group.MapPost("/verify", (UniFiConnectionService service, CancellationToken ct) => service.VerifyAsync(ct));
        group.MapPut("/reservations", (UniFiReservationSetting setting, UniFiConnectionService service, CancellationToken ct) =>
            service.SetReservationsAsync(setting.Enabled, ct));
        group.MapGet("/reservations", (Nodes.ManagedNodeDhcp dhcp) => dhcp.Snapshot());
    }
}

public sealed record UniFiReservationSetting(bool Enabled);

public sealed class UniFiManagementFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (UniFiException e)
        {
            return Results.Json(new { error = new { code = e.Code, message = e.Message, certificateSha256 = e.CertificateSha256 } }, statusCode: e.StatusCode);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or AdGuardManagementException)
        { return Results.Json(new { error = new { code = "invalid_unifi_request", message = "The request body is invalid." } }, statusCode: 400); }
        catch (Exception)
        {
            // No exception text, URLs or keys in responses.
            return Results.Json(new { error = new { code = "unifi_unavailable", message = "UniFi management is temporarily unavailable." } }, statusCode: 503);
        }
    }
}
