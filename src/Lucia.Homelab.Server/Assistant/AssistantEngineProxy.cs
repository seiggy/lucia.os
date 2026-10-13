using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using Lucia.Homelab.Server.Domains;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>
/// Puts a reasoning effort on the requests the assistant sends to a model the owner brought. The Copilot runtime can't add
/// fields to a provider's request, so for one turn its provider points here, over loopback, and this forwards the request
/// to the real engine with the effort written in: <c>reasoning_effort</c> for most, and for Disabled
/// <c>chat_template_kwargs.enable_thinking: false</c>, which is how Qwen-style engines turn thinking off.
/// </summary>
public sealed class AssistantEngineProxy(Func<int?> port)
{
    public const string Path = "/assistant-engine";
    /// <summary>The efforts an owner can allow for a model, cheapest first. "none" is Disabled.</summary>
    public static readonly string[] Efforts = ["none", "low", "medium", "high", "xhigh"];
    private const int MaxBody = 16 * 1024 * 1024;
    private static readonly HttpClient Http = new(CloudflareHttp.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, Engine> _engines = new();

    private sealed record Engine(Uri Base, string? Key, string Effort);

    private sealed class Lease(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    /// <summary>Provider settings that route one turn's requests through here, and a lease that ends it. Null when the host can't be reached over loopback.</summary>
    public (ProviderConfig Provider, IDisposable Lease)? Open(ProviderConfig provider, string effort)
    {
        if (port() is not { } open || provider.BaseUrl is null || !Uri.TryCreate(provider.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var target)) return null;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _engines[token] = new(target, provider.ApiKey, effort);
        var routed = new ProviderConfig
        {
            Type = provider.Type, WireApi = provider.WireApi, BaseUrl = $"http://127.0.0.1:{open}{Path}/v1", ApiKey = token,
            MaxOutputTokens = provider.MaxOutputTokens, MaxPromptTokens = provider.MaxPromptTokens,
        };
        return (routed, new Lease(() => _engines.TryRemove(token, out _)));
    }

    /// <summary>Writes the effort into a chat request body. Other requests, and bodies that aren't chat requests, pass through unchanged.</summary>
    internal static byte[] Rewrite(byte[] body, string effort)
    {
        JsonObject? request;
        try { request = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException) { return body; }
        if (request is null || request["messages"] is null) return body;
        request.Remove("reasoning_effort");
        if (effort == "none")
        {
            var kwargs = request["chat_template_kwargs"] as JsonObject ?? new JsonObject();
            kwargs["enable_thinking"] = false;
            request["chat_template_kwargs"] = kwargs;
        }
        else request["reasoning_effort"] = effort;
        return JsonSerializer.SerializeToUtf8Bytes(request);
    }

    /// <summary>The engine's URL for a request that arrived at <paramref name="path"/>, which starts <c>/assistant-engine/v1</c> like the engine's own base does.</summary>
    internal static Uri Target(Uri engineBase, string path, string query)
    {
        var rest = path[(Path.Length + "/v1".Length)..];
        return new Uri(engineBase, rest.TrimStart('/') + query);
    }

    public async Task Forward(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        var auth = context.Request.Headers.Authorization.ToString();
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
            || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || !_engines.TryGetValue(auth["Bearer ".Length..].Trim(), out var engine)
            || !context.Request.Path.StartsWithSegments(Path + "/v1", out _))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        byte[] body = [];
        if (context.Request.ContentLength is not 0 && !HttpMethods.IsGet(context.Request.Method))
        {
            if (context.Request.ContentLength > MaxBody)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = Rewrite(buffer.ToArray(), engine.Effort);
        }
        using var forward = new HttpRequestMessage(new HttpMethod(context.Request.Method), Target(engine.Base, context.Request.Path.Value!, context.Request.QueryString.Value ?? ""));
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            forward.Content = new ByteArrayContent(body);
            if (context.Request.ContentType is { } type) forward.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        }
        if (engine.Key is { Length: > 0 } key) forward.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (context.Request.Headers.Accept.Count > 0) forward.Headers.TryAddWithoutValidation("Accept", context.Request.Headers.Accept.ToString());
        try
        {
            using var response = await Http.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var chunk = new byte[8192];
            int read;
            // Flush each piece so a streamed answer reaches the runtime as it arrives.
            while ((read = await stream.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                await context.Response.Body.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException && !context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }
}
