using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTelemetry;

[assembly: InternalsVisibleTo("Lucia.Homelab.AdGuardChecks")]

namespace Lucia.Homelab.Server.Host;

/// <summary>
/// API contract verified against AdguardTeam/AdGuardHome openapi/openapi.yaml at
/// v0.107.79, 05ba17b282da1c4393d6a4ba4db0cf519194a362: /control, HTTP Basic authentication;
/// GET /profile, /status, /rewrite/list, /rewrite/settings, /querylog (read-only, for the lab map's internet names);
/// POST /rewrite/add, /rewrite/delete.
/// No login/session, protection toggles, list replacement or rewrite update calls.
/// </summary>
internal sealed class AdGuardTransport
{
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<Uri, IPAddress[], HttpMessageHandler> _createHandler;
    private readonly TimeSpan _timeout;

    internal AdGuardTransport(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<Uri, IPAddress[], HttpMessageHandler>? createHandler = null, TimeSpan? timeout = null)
    {
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _createHandler = createHandler ?? ((origin, addresses) => CreateHandler(origin, addresses));
        _timeout = timeout ?? OperationTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > OperationTimeout) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <param name="pin">Connect here instead of resolving the origin's host; TLS still validates the host name.</param>
    internal async Task<T> RunAsync<T>(AdGuardStoredConnection record,
        Func<AdGuardSession, CancellationToken, Task<T>> operation, CancellationToken ct, IPAddress? pin = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        // Do not send credentials through HttpClientFactory logging or OpenTelemetry request enrichment.
        using var suppression = SuppressInstrumentationScope.Begin();
        try
        {
            var origin = new Uri(record.BaseUrl!);
            var host = origin.IdnHost.Trim('[', ']');
            var addresses = pin is not null ? [pin] : IPAddress.TryParse(host, out var literal) ? [literal] : await _resolve(host, deadline.Token);
            if (addresses.Length is < 1 or > 64 || addresses.Any(address => !IsPrivate(address)))
                throw new AdGuardManagementException(400, "adguard_private_network_required",
                    "AdGuard must resolve exclusively to private RFC1918 or IPv6 ULA addresses. Public, loopback, link-local, metadata and multicast endpoints are not allowed.");
            // One fresh handler per operation: reuse the validated address set for every request,
            // never re-resolve a hostname in the socket layer or reuse a stale cross-origin pool.
            using var http = new HttpClient(_createHandler(origin, addresses.ToArray())) { Timeout = Timeout.InfiniteTimeSpan };
            var session = new AdGuardSession(http, record);
            // Userless AdGuard returns HTTP 200 even for arbitrary Basic credentials. Require a
            // named authenticated profile on every operation, including after a server changes.
            await session.VerifyProfileAsync(deadline.Token);
            return await operation(session, deadline.Token);
        }
        catch (AdGuardManagementException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new AdGuardManagementException(504, "adguard_timeout", "AdGuard did not complete the operation within ten seconds. No automatic retry was attempted."); }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException)
        { throw new AdGuardManagementException(502, "adguard_unavailable", "Cannot securely reach AdGuard. Check its LAN address, port and operating-system certificate trust."); }
        catch (JsonException) { throw InvalidResponse(); }
        catch (Exception)
        { throw new AdGuardManagementException(502, "adguard_unavailable", "AdGuard could not complete the operation securely."); }
    }

    /// <summary>
    /// Finishes a fresh AdGuard's first-run wizard on port 3000: web on port 80, DNS on 53, with these credentials. Returns
    /// false when the wizard isn't there (not started yet, or already set up). This crosses the LAN in plain HTTP once.
    /// </summary>
    internal async Task<bool> InstallAsync(IPAddress address, string username, string password, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        using var suppression = SuppressInstrumentationScope.Begin();
        var origin = new Uri($"http://{address}:3000");
        using var http = new HttpClient(_createHandler(origin, [address])) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using (var probe = await http.GetAsync(origin + "control/install/get_addresses", deadline.Token))
                if (probe.StatusCode != HttpStatusCode.OK) return false;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            web = new { ip = "0.0.0.0", port = 80 }, dns = new { ip = "0.0.0.0", port = 53 }, username, password,
        });
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.PostAsync(origin + "control/install/configure", content, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new AdGuardManagementException(502, "adguard_install_failed", "AdGuard's first-run setup refused Lucia's settings.");
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        { throw new AdGuardManagementException(502, "adguard_install_failed", "AdGuard's first-run setup didn't finish."); }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && address.ScopeId == 0 && (bytes[0] & 0xfe) == 0xfc;
    }

    internal static SocketsHttpHandler CreateHandler(Uri origin, IPAddress[] addresses,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>>? connect = null)
    {
        if (addresses.Length == 0 || addresses.Any(address => !IsPrivate(address)))
            throw new ArgumentException("A validated private address is required.");
        var pinned = new IPAddress(addresses[0].GetAddressBytes());
        return new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            PreAuthenticate = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 16,
            ActivityHeadersPropagator = null,
            // TLS still authenticates the original URI hostname with native OS trust. No TLS callback.
            ConnectCallback = async (context, ct) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host.Trim('[', ']'), origin.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
                    || context.DnsEndPoint.Port != origin.Port)
                    throw new HttpRequestException("The pinned AdGuard destination changed.");
                var endpoint = new IPEndPoint(pinned, origin.Port);
                if (connect is not null) return await connect(endpoint, ct);
                var socket = new Socket(pinned.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(endpoint, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length)), ct);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > maximum)
                throw new AdGuardManagementException(502, "adguard_response_too_large", "AdGuard returned more than the bounded response limit.");
        }
    }

    internal static AdGuardManagementException InvalidResponse() =>
        new(502, "invalid_adguard_response", "The server did not return the expected AdGuard Home profile, status or rewrite schema.");
}

internal sealed class AdGuardSession(HttpClient http, AdGuardStoredConnection record)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task VerifyProfileAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/control/profile", ct);
        var name = AdGuardValidation.String(document.RootElement, "name");
        if (name is null) throw AdGuardTransport.InvalidResponse();
        if (string.IsNullOrWhiteSpace(name))
            throw new AdGuardManagementException(403, "adguard_authentication_required",
                "AdGuard did not identify an authenticated user. Configure authentication in AdGuard before connecting; an HTTP 200 response alone does not verify credentials.");
        if (!string.Equals(name, record.Username, StringComparison.Ordinal))
            throw new AdGuardManagementException(403, "adguard_identity_mismatch",
                "AdGuard did not confirm the configured username. Check authentication and any reverse proxy; the previous connection was preserved.");
    }

    internal async Task<(string Version, bool Running, bool ProtectionEnabled, int DnsPort,
        IReadOnlyList<string> DnsAddresses)> StatusAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/control/status", ct);
        var root = document.RootElement;
        var version = AdGuardValidation.String(root, "version");
        if (!AdGuardValidation.Version(version) || AdGuardValidation.SensitiveMetadata(version!, record.Username!, record.Password!)
            || !Port(root, "dns_port") || !Port(root, "http_port")
            || !root.TryGetProperty("dns_addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array
            || addresses.GetArrayLength() > 64 || addresses.EnumerateArray().Any(item =>
                item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: >= 1 and <= 2048 } address
                || address.Any(char.IsControl)
                || AdGuardValidation.SensitiveMetadata(item.GetString()!, record.Username!, record.Password!))
            // Empty until someone picks a language in AdGuard's web interface, as after Lucia's own first-run setup.
            || AdGuardValidation.String(root, "language") is not { Length: <= 32 }
            || !root.TryGetProperty("protection_disabled_duration", out var duration)
            || duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt64(out _))
            throw AdGuardTransport.InvalidResponse();
        // AdGuard advertises IPs, IP:port pairs and encrypted-DNS URLs here, not connection targets.
        return (version!, Boolean(root, "running"), Boolean(root, "protection_enabled"),
            root.GetProperty("dns_port").GetInt32(), Array.AsReadOnly(addresses.EnumerateArray().Select(item => item.GetString()!).ToArray()));
    }

    internal async Task<bool> RewritesEnabledAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/control/rewrite/settings", ct);
        return Boolean(document.RootElement, "enabled");
    }

    internal async Task<IReadOnlyList<AdGuardRewrite>> ListAsync(CancellationToken ct)
    {
        using var document = await GetAsync("/control/rewrite/list", ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw AdGuardTransport.InvalidResponse();
        var entries = new List<AdGuardRewrite>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var domain = AdGuardValidation.String(item, "domain");
            var answer = AdGuardValidation.String(item, "answer");
            // Existing rules can use public IPs or CNAMEs; keep them visible for collision detection.
            if (!AdGuardValidation.Domain(domain) || answer is not { Length: >= 1 and <= 253 }
                || !(IPAddress.TryParse(answer, out _) || AdGuardValidation.Domain(answer))
                || AdGuardValidation.SensitiveMetadata(domain!, record.Username!, record.Password!)
                || AdGuardValidation.SensitiveMetadata(answer, record.Username!, record.Password!))
                throw AdGuardTransport.InvalidResponse();
            entries.Add(new(domain!, answer) { Enabled = !item.TryGetProperty("enabled", out _) || Boolean(item, "enabled") });
        }
        return entries;
    }

    internal async Task MutateAsync(AdGuardRewrite rewrite, bool add, CancellationToken ct)
    {
        // A timeout/cancellation/error after dispatch may follow a committed mutation. Never report
        // success, retry, or guess rollback: the workflow must reconcile its journal against List.
        ct.ThrowIfCancellationRequested();
        try
        {
            await SendAsync(add ? "/control/rewrite/add" : "/control/rewrite/delete",
                JsonSerializer.SerializeToUtf8Bytes(new { domain = rewrite.Domain, answer = rewrite.Answer }, Json), ct);
        }
        catch (Exception)
        {
            throw new AdGuardManagementException(502, "adguard_mutation_indeterminate",
                "The rewrite request did not complete reliably and may already have applied. Refresh the rewrite list and reconcile the DNS journal before retrying or rolling back.");
        }
    }

    /// <summary>AdGuard redacts a saved key and base64-encodes stored certificate data.</summary>
    internal async Task<System.Text.Json.Nodes.JsonObject> TlsStatusAsync(CancellationToken ct) =>
        System.Text.Json.Nodes.JsonNode.Parse(await SendAsync("/control/tls/status", null, ct)) as System.Text.Json.Nodes.JsonObject
            ?? throw AdGuardTransport.InvalidResponse();

    /// <summary>AdGuard answers, then restarts its HTTPS and encrypted-DNS listeners.</summary>
    internal Task ConfigureTlsAsync(System.Text.Json.Nodes.JsonObject settings, CancellationToken ct) =>
        SendAsync("/control/tls/configure", JsonSerializer.SerializeToUtf8Bytes(settings), ct);

    /// <summary>The newest query log entries, or those older than an entry's time; read only for the lab map's internet names.</summary>
    internal Task<byte[]> QueryLogAsync(int limit, string? olderThan, CancellationToken ct) =>
        SendAsync($"/control/querylog?limit={limit}" + (olderThan is null ? "" : "&older_than=" + Uri.EscapeDataString(olderThan)), null, ct);

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var bytes = await SendAsync(path, null, ct);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    }

    private async Task<byte[]> SendAsync(string path, byte[]? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, record.BaseUrl + path);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var credentials = Encoding.UTF8.GetBytes(record.Username + ":" + record.Password);
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials)); }
        finally { CryptographicOperations.ZeroMemory(credentials); }
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
            throw new AdGuardManagementException(502, "adguard_redirect_rejected", "AdGuard requested a redirect. Enter its direct origin; Lucia never follows credential-bearing redirects.");
        if (status is 401 or 403)
            throw new AdGuardManagementException(403, "adguard_access_denied", "AdGuard rejected access. Check the username and password; the previous connection was preserved.");
        if (status == 429)
            throw new AdGuardManagementException(429, "adguard_rate_limited", "AdGuard rate-limited this request. Wait before retrying; Lucia will not retry automatically.");
        if (status == 404)
            throw new AdGuardManagementException(502, "adguard_api_unavailable", "The required AdGuard API is unavailable. Check the direct origin and AdGuard version.");
        if (status != 200)
            throw new AdGuardManagementException(502, "adguard_unavailable", "AdGuard could not complete the request.");
        if (response.Content.Headers.ContentLength > AdGuardTransport.MaximumResponseBytes)
            throw new AdGuardManagementException(502, "adguard_response_too_large", "AdGuard returned more than one MiB.");
        if (body is null && response.Content.Headers.ContentType?.MediaType != "application/json")
            throw AdGuardTransport.InvalidResponse();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await AdGuardTransport.ReadBoundedAsync(stream, AdGuardTransport.MaximumResponseBytes, ct);
    }

    private static bool Boolean(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
            : throw AdGuardTransport.InvalidResponse();

    private static bool Port(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var port) && port is >= 1 and <= 65535;

}
