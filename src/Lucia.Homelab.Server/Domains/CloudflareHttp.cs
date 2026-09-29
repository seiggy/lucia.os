using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenTelemetry;

namespace Lucia.Homelab.Server.Domains;

internal sealed class CloudflareHttp(HttpClient client)
{
    internal const int MaximumResponseBytes = 1_000_000;

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null,
        PreAuthenticate = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 16,
        ActivityHeadersPropagator = null
    };

    internal static HttpClient CreateClient() => new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    // Paths are constructed only from validated IDs and exact canonical DNS names, never a token.
    internal Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct) => SendAsync(HttpMethod.Get, path, token, null, ct);

    internal async Task<JsonDocument> SendAsync(HttpMethod method, string path, string token, object? body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var suppression = SuppressInstrumentationScope.Begin();
        try
        {
            var uri = new Uri("https://api.cloudflare.com/client/v4" + path);
            if ((!path.StartsWith("/accounts/", StringComparison.Ordinal) && !path.StartsWith("/zones", StringComparison.Ordinal))
                || uri.Scheme != "https" || uri.Host != "api.cloudflare.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                throw InvalidResponse();
            using var request = new HttpRequestMessage(method, uri);
            if (body is not null) request.Content = System.Net.Http.Json.JsonContent.Create(body);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw ProviderError(response);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw TooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream(await ReadBoundedAsync(stream, MaximumResponseBytes, deadline.Token));
            var document = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 24 }, deadline.Token);
            try
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success)
                    || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse();
                if (success.ValueKind == JsonValueKind.False) throw TokenRejected();
                if (!root.TryGetProperty("result", out _) || ContainsToken(root, token)) throw InvalidResponse();
                return document;
            }
            catch { document.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw TimeoutError(); }
        catch (Exception e) when (e is HttpRequestException or IOException)
        { throw new CloudflareDomainException(502, "provider_unavailable", "Cannot securely reach Cloudflare. Check network connectivity and operating-system TLS trust."); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    internal async Task<IReadOnlyList<JsonElement>> ListAsync(string path, string token, CancellationToken ct)
    {
        var entries = new List<JsonElement>();
        int? expectedPages = null, expectedTotal = null;
        for (var page = 1; page <= 20; page++)
        {
            using var document = await GetAsync($"{path}&page={page}&per_page=50", token, ct);
            var root = document.RootElement;
            var result = Result(document);
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 50
                || !root.TryGetProperty("result_info", out var info)
                || Number(info, "page") != page || Number(info, "per_page") != 50
                || Number(info, "count") != result.GetArrayLength()) throw Incomplete();
            var pages = Number(info, "total_pages");
            var total = Number(info, "total_count");
            if (pages is < 0 or > 20 || total is < 0 or > 1000
                || (pages == 0 && total != 0) || (pages > 0 && pages != Math.Max(1, (total + 49) / 50))
                || (expectedPages.HasValue && (pages != expectedPages || total != expectedTotal))) throw Incomplete();
            expectedPages = pages;
            expectedTotal = total;
            entries.AddRange(result.EnumerateArray().Select(entry => entry.Clone()));
            if (page >= pages)
            {
                if (entries.Count != total) throw Incomplete();
                return entries;
            }
            if (result.GetArrayLength() != 50) throw Incomplete();
        }
        throw Incomplete();
    }

    internal static JsonElement Result(JsonDocument document) => document.RootElement.GetProperty("result");
    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : -1;

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, maximum + 1 - (int)output.Length)), ct);
            if (read == 0) return output.ToArray();
            output.Write(bytes, 0, read);
            if (output.Length > maximum) throw TooLarge();
        }
    }

    private static bool ContainsToken(JsonElement value, string token) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains(token, StringComparison.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsToken(item, token)),
        JsonValueKind.Object => value.EnumerateObject().Any(item => item.Name.Contains(token, StringComparison.Ordinal)
            || ContainsToken(item.Value, token)),
        _ => false
    };

    internal static CloudflareDomainException InvalidResponse() =>
        new(502, "invalid_provider_response", "Cloudflare returned unexpected metadata. No credential was changed.");
    internal static CloudflareDomainException Incomplete() =>
        new(502, "incomplete_provider_listing", "Cloudflare's listing was incomplete, inconsistent, or exceeded 20 pages / 1000 entries. Narrow the token to the specific selected zone and retry; no partial listing was accepted.");
    internal static CloudflareDomainException TimeoutError() =>
        new(504, "provider_timeout", "Cloudflare did not complete the operation in time. Try again.");
    private static CloudflareDomainException TooLarge() =>
        new(502, "provider_response_too_large", "Cloudflare returned more than the 1,000,000-byte response limit.");
    private static CloudflareDomainException TokenRejected() =>
        new(422, "provider_token_rejected", "Cloudflare rejected this account token or its scope. Check the account ID and use an active account-owned token with Zone / DNS / Edit and Zone / Zone / Read restricted to the specific selected zone. No registrar or account-administration permission is needed. The previous connection was preserved.");

    private static CloudflareDomainException ProviderError(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is 401 or 403) return TokenRejected();
        if (status == 429)
        {
            var retry = response.Headers.RetryAfter;
            var seconds = retry?.Delta?.TotalSeconds ?? (retry?.Date - DateTimeOffset.UtcNow)?.TotalSeconds;
            return new(429, "provider_rate_limited", "Cloudflare rate-limited the request. Wait before retrying.",
                seconds.HasValue ? (int)Math.Clamp(Math.Ceiling(seconds.Value), 1, 3600) : null);
        }
        if (status == 404)
            return new(422, "provider_account_or_zone_not_found", "Cloudflare could not find this account or zone, or the token cannot access it. Check the selected account ID and specific-zone permissions.");
        if (status is >= 300 and < 400)
            return new(502, "provider_redirect_rejected", "Cloudflare requested a redirect. It was not followed to protect the account token.");
        return new(502, "provider_unavailable", "Cloudflare could not complete the request. Try again later.");
    }
}
