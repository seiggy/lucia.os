using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

/// <summary>
/// Qwen3.8-Flash-Next on TensorFold, a DGX Spark recipe that needs nearly all of the Spark's memory, so Lucia's own LLM and
/// embedding model are unloaded while it runs and reloaded once it has stopped. Like the Spark runner, the web host holds
/// no Docker access: it writes <c>spark-model/request.json</c>, and the Spark's <c>lucia-spark-model</c> service, which owns
/// the pinned recipe and its settings, runs the container and reports in <c>status.json</c>. Lucia's authenticated
/// <c>/v1</c> forwards to it, since TensorFold has no authentication of its own.
/// </summary>
public sealed class SparkModel(IOptions<HardwareOnboardingOptions> options, IOptions<HostPlatformOptions> platform, InferenceRuntime runtime,
    ModelCatalog catalog, TimeProvider time, ILogger<SparkModel> logger)
{
    public const string Name = "Qwen3.8-Flash-Next";
    // Keep in step with spark_model_worker.py.
    public const int ContextTokens = 200_000, Parallel = 4;
    private const string RunningReason = "Lucia's own LLM and embedding model are off while Qwen3.8-Flash-Next runs on the Spark. Stop it to load them.";
    private const string StoppingReason = "Lucia reloads its own models once Qwen3.8-Flash-Next has stopped.";
    private static readonly HttpClient Client = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Folder => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "spark-model");

    /// <param name="Desired"><c>running</c> or <c>stopped</c>.</param>
    internal sealed record Request(int SchemaVersion, long Generation, string Desired);

    /// <param name="State"><c>starting</c>, <c>running</c>, <c>stopping</c>, <c>stopped</c> or <c>failed</c>.</param>
    /// <param name="Url">Where the model answers, on the Docker bridge the web host shares with the Spark.</param>
    internal sealed record Status(int SchemaVersion, long Generation, string State, DateTimeOffset? Since, string? Message, string? Url,
        DateTimeOffset CheckedAt);

    public object Get()
    {
        var request = ReadRequest();
        var status = ReadStatus();
        var applied = request is not null && status?.Generation == request.Generation ? status : null;
        return new
        {
            model = Name, contextTokens = ContextTokens, parallel = Parallel,
            workerReady = status is not null && time.GetUtcNow() - status.CheckedAt < TimeSpan.FromMinutes(1),
            desired = request?.Desired ?? "stopped",
            state = applied?.State ?? (request is null ? "stopped" : request.Desired == "running" ? "starting" : "stopping"),
            since = applied?.Since, message = applied?.Message,
        };
    }

    public async Task<object> Set(string desired, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var existing = ReadRequest();
            if (desired == "running")
            {
                // Free the memory before the Spark starts loading, and keep it free until Qwen has stopped again.
                runtime.Paused = RunningReason;
                await runtime.UnloadAsync(ModelKind.Chat, ct, keepSelection: true);
                await runtime.UnloadAsync(ModelKind.Embedding, ct, keepSelection: true);
            }
            await DomainOnboardingStore.WriteJson(Path.Combine(Folder, "request.json"), new Request(1, (existing?.Generation ?? 0) + 1, desired), ct);
            if (desired == "stopped") Resume();
        }
        finally { _gate.Release(); }
        return Get();
    }

    /// <summary>At startup: whether Qwen holds, or may still hold, the memory Lucia's own models would load into.</summary>
    public bool HoldsMemory()
    {
        var request = ReadRequest();
        if (request is null || request.Desired == "stopped" && Stopped(request)) return false;
        if (request.Desired == "running") runtime.Paused = RunningReason;
        else Resume();
        return true;
    }

    /// <summary>Where <c>/v1</c> goes while Qwen is wanted: its address once it answers, or why it can't yet.</summary>
    public (Uri? Url, string? Problem)? Serving()
    {
        if (ReadRequest() is not { Desired: "running" } request) return null;
        var status = ReadStatus();
        if (status?.Generation == request.Generation && status.State == "running" && Uri.TryCreate(status.Url, UriKind.Absolute, out var url)
            && time.GetUtcNow() - status.CheckedAt < TimeSpan.FromMinutes(1))
            return (url, null);
        return (null, status?.Generation == request.Generation && status.State == "failed"
            ? $"{Name} failed to start on the Spark: {status.Message}" : $"{Name} is starting on the Spark. The first start downloads about 125 GB.");
    }

    public async Task ProxyAsync(HttpContext context, Uri upstream)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(upstream, context.Request.Path.Value!.TrimStart('/')));
        if (HttpMethods.IsPost(context.Request.Method))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } type) request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            // TensorFold's server reads Content-Length and ignores chunked bodies; Kestrel caps requests at 16 MiB.
            await request.Content.LoadIntoBufferAsync(context.RequestAborted);
        }
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
        context.Response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        if (context.Request.Path.Value!.EndsWith("/responses", StringComparison.Ordinal) && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            await RelayResponsesAsync(body, context.Response.Body, context.RequestAborted);
        else
            await body.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    /// <summary>
    /// TensorFold streams a function call's done events before its last argument delta, so clients that trust them
    /// (Hermes) get cut-off JSON. Hold those events and send them just before the terminal event, with its arguments.
    /// </summary>
    private static async Task RelayResponsesAsync(Stream upstream, Stream client, CancellationToken cancellation)
    {
        using var reader = new StreamReader(upstream);
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), -1, leaveOpen: true) { NewLine = "\n" };
        var held = new List<JsonObject>();
        var frame = new List<string>();
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (line.Length > 0) { frame.Add(line); continue; }
            if (frame.Count == 0) continue;
            var data = frame.FirstOrDefault(part => part.StartsWith("data:", StringComparison.Ordinal));
            var payload = data is null ? null : TryParse(data[5..]);
            var type = (string?)payload?["type"];
            if (type == "response.function_call_arguments.done" || type == "response.output_item.done" && (string?)payload!["item"]?["type"] == "function_call")
                held.Add(payload!);
            else
            {
                if (type is "response.completed" or "response.incomplete" or "response.failed")
                {
                    var final = (payload!["response"]?["output"] as JsonArray ?? [])
                        .Select(item => item as JsonObject).Where(item => (string?)item?["type"] == "function_call")
                        .ToDictionary(item => (string?)item!["id"] ?? "", item => (string?)item!["arguments"]);
                    await WriteHeldAsync(writer, held, final);
                }
                foreach (var part in frame) await writer.WriteLineAsync(part);
                await writer.WriteLineAsync();
                await writer.FlushAsync(cancellation);
            }
            frame.Clear();
        }
        await WriteHeldAsync(writer, held, new Dictionary<string, string?>());
        await writer.FlushAsync(cancellation);
    }

    private static async Task WriteHeldAsync(StreamWriter writer, List<JsonObject> held, Dictionary<string, string?> final)
    {
        foreach (var payload in held)
        {
            var target = payload["item"] as JsonObject ?? payload;
            var id = (string?)(payload["item"] is null ? payload["item_id"] : target["id"]) ?? "";
            if (final.TryGetValue(id, out var arguments) && arguments is not null) target["arguments"] = arguments;
            await writer.WriteLineAsync($"event: {(string?)payload["type"]}\ndata: {payload.ToJsonString()}\n");
        }
        held.Clear();
    }

    private static JsonObject? TryParse(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private bool Stopped(Request request) => ReadStatus() is { } status && status.Generation == request.Generation && status.State == "stopped";

    /// <summary>Reloads the saved models once the Spark reports Qwen stopped, unless the owner starts it again first.</summary>
    private void Resume()
    {
        runtime.Paused = StoppingReason;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    if (ReadRequest() is not { Desired: "stopped" } request) return;
                    if (Stopped(request)) break;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
                {
                    logger.LogWarning(error, "Qwen's state could not be read; retrying.");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), time);
            }
            await _gate.WaitAsync();
            try
            {
                if (ReadRequest() is not { Desired: "stopped" } || runtime.Paused != StoppingReason) return;
                runtime.Paused = null;
            }
            finally { _gate.Release(); }
            await catalog.LoadSavedSelectionAsync(runtime, platform.Value, logger, CancellationToken.None);
        });
    }

    private Request? ReadRequest()
    {
        var path = Path.Combine(Folder, "request.json");
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Request>(CertbotFiles.ReadBounded(path, 4 * 1024), DomainOnboardingStore.Json)
            is { SchemaVersion: 1, Desired: "running" or "stopped" } request ? request : throw new InvalidDataException("Qwen's settings are invalid.");
    }

    private Status? ReadStatus()
    {
        var path = Path.Combine(Folder, "status.json");
        try
        {
            DomainOnboardingStore.RejectLinks(path);
            return File.Exists(path) && JsonSerializer.Deserialize<Status>(CertbotFiles.ReadBounded(path, 16 * 1024), DomainOnboardingStore.Json)
                is { SchemaVersion: 1 } status ? status : null;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException) { return null; }
    }
}
