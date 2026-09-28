using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public sealed record ModelLoadRequest(int? ContextTokens = null);

/// <summary>Model management and OpenAI-compatible routes shared by the Spark host and the inference worker.</summary>
public static class InferenceEndpoints
{
    /// <param name="prefix">The group's path, used for Accepted locations.</param>
    public static void MapModelManagement(this RouteGroupBuilder host, string prefix)
    {
        host.MapGet("/status", async (InferenceRuntime runtime, CancellationToken ct) =>
        {
            await runtime.Gate.WaitAsync(ct);
            try { return Results.Ok(runtime.Status()); }
            finally { runtime.Gate.Release(); }
        });
        host.MapGet("/model-catalog", () => new[] { ModelPresets.Bundled });
        host.MapGet("/models", (ModelCatalog catalog) => catalog.List());
        host.MapGet("/models/{id:guid}", (Guid id, ModelCatalog catalog) =>
            catalog.Find(id) is { } model ? Results.Ok(model) : Results.NotFound());
        host.MapPost("/models/download", async (ModelDownloadRequest request, ModelCatalog catalog, CancellationToken ct) =>
        {
            var model = await catalog.DownloadAsync(request, ct);
            return Results.Accepted($"{prefix}/models/{model.Id}", model);
        });
        host.MapPost("/models/bundled/download", async (ModelCatalog catalog, CancellationToken ct) =>
        {
            var model = await catalog.DownloadAsync(ModelPresets.Bundled.Source, ct);
            return Results.Accepted($"{prefix}/models/{model.Id}", model);
        });
        host.MapPost("/models/{id:guid}/retry", async (Guid id, ModelCatalog catalog, CancellationToken ct) =>
            Results.Accepted($"{prefix}/models/{id}", await catalog.RetryAsync(id, ct)));
        host.MapPost("/models/{id:guid}/cancel", async (Guid id, ModelCatalog catalog, CancellationToken ct) =>
        {
            await catalog.CancelAsync(id, ct);
            return Results.Accepted($"{prefix}/models/{id}");
        });
        host.MapGet("/models/{id:guid}/context", async (Guid id, int? contextTokens, InferenceRuntime runtime, CancellationToken ct) =>
        {
            await runtime.Gate.WaitAsync(ct);
            try { return Results.Ok(runtime.Inspect(id, contextTokens)); }
            finally { runtime.Gate.Release(); }
        });
        host.MapPost("/models/{id:guid}/load", async (Guid id, ModelLoadRequest request, InferenceRuntime runtime, CancellationToken ct) =>
            Results.Ok(await runtime.LoadAsync(id, request.ContextTokens, ct))).DisableRequestTimeout();
        host.MapPost("/models/{kind}/unload", async (ModelKind kind, InferenceRuntime runtime, CancellationToken ct) =>
        {
            await runtime.UnloadAsync(kind, ct);
            return Results.NoContent();
        });
        host.MapDelete("/models/{id:guid}", async (Guid id, InferenceRuntime runtime, CancellationToken ct) =>
        {
            await runtime.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    public static void MapOpenAIInference(this RouteGroupBuilder openai)
    {
        openai.MapGet("/models", async (InferenceRuntime runtime, CancellationToken ct) =>
        {
            await runtime.Gate.WaitAsync(ct);
            try { return Results.Ok(runtime.OpenAIModels()); }
            finally { runtime.Gate.Release(); }
        });
        openai.MapPost("/chat/completions", (HttpContext context, InferenceRuntime runtime, IOptions<HostPlatformOptions> options) =>
            RunInferenceAsync(context, runtime, options.Value, "chat")).DisableRequestTimeout();
        openai.MapPost("/responses", (HttpContext context, InferenceRuntime runtime, IOptions<HostPlatformOptions> options) =>
            RunInferenceAsync(context, runtime, options.Value, "responses")).DisableRequestTimeout();
        openai.MapPost("/embeddings", (HttpContext context, InferenceRuntime runtime, IOptions<HostPlatformOptions> options) =>
            RunInferenceAsync(context, runtime, options.Value, "embeddings")).DisableRequestTimeout();
        openai.MapGet("/responses/{id}", (HttpContext context, InferenceRuntime runtime, string id) =>
            runtime.ResponsesAdapter.GetResponseAsync(context, id));
    }

    private static async Task RunInferenceAsync(HttpContext context, InferenceRuntime runtime, HostPlatformOptions options, string mode)
    {
        await runtime.Gate.WaitAsync(context.RequestAborted);
        try
        {
            if (mode == "embeddings")
            {
                if (!runtime.HasEmbeddingModel)
                {
                    await Results.Json(new { error = new { message = "No embedding model is loaded.", type = "server_error", code = "model_not_loaded" } },
                        statusCode: 503).ExecuteAsync(context);
                    return;
                }
                await runtime.EmbedAsync(context);
                return;
            }
            if (!runtime.Chat.IsLoaded)
            {
                await Results.Json(new { error = new { message = "No chat model is loaded.", type = "server_error", code = "model_not_loaded" } },
                    statusCode: 503).ExecuteAsync(context);
                return;
            }
            var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false })
                size.MaxRequestBodySize = 2 * 1024 * 1024;
            context.Request.EnableBuffering(bufferThreshold: 65536, bufferLimit: 2 * 1024 * 1024);
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            context.Request.Body.Position = 0;
            var body = document.RootElement;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.String)
                throw new ArgumentException("A string model field is required.");
            runtime.RequireChatModel(model.GetString());
            foreach (var field in new[] { "max_tokens", "max_completion_tokens", "max_output_tokens" })
                if (body.TryGetProperty(field, out var maximum) && maximum.ValueKind != JsonValueKind.Null
                    && (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt32(out var count) || count < 1 || count > options.MaxOutputTokens))
                    throw new ArgumentException($"{field} must be between 1 and {options.MaxOutputTokens}.");
            if (body.TryGetProperty(mode == "chat" ? "messages" : "input", out var messages) && messages.ValueKind == JsonValueKind.Array)
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Messages must be objects.");
                    if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        foreach (var part in content.EnumerateArray())
                            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type)
                                || type.ValueKind != JsonValueKind.String || type.GetString() is not ("text" or "input_text" or "output_text"))
                                throw new NotSupportedException("This host currently accepts text-only messages. Media uploads are not enabled.");
                }
            if (mode == "chat")
                await runtime.ChatAdapter.ChatCompletionsAsync(context);
            else
                await runtime.ResponsesAdapter.CreateResponseAsync(context);
        }
        finally { runtime.Gate.Release(); }
    }
}

public static class SavedModelSelection
{
    /// <summary>Loads the saved chat and embedding models, falling back to configured or bundled ones. Failures are recorded, not thrown, so the management API stays up for recovery.</summary>
    public static async Task LoadSavedSelectionAsync(this ModelCatalog catalog, InferenceRuntime runtime, HostPlatformOptions options,
        ILogger logger, CancellationToken cancellationToken)
    {
        Guid? chat = null;
        try
        {
            var saved = await catalog.ReadSelectionAsync(cancellationToken);
            chat = saved is not null ? saved.ChatId : options.ChatModelId ?? catalog.List()
                .FirstOrDefault(model => model.State == ModelDownloadState.Ready && ModelPresets.Match(model.Source) is not null)?.Id;
            if (chat is { } chatId)
                await runtime.LoadAsync(chatId, saved?.ChatContext, cancellationToken);
            var embedding = saved is not null ? saved.EmbeddingId : options.EmbeddingModelId;
            if (embedding is { } embeddingId)
                await runtime.LoadAsync(embeddingId, saved?.EmbeddingContext, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            runtime.StartupError = $"Startup model loading failed: {exception.Message}";
            logger.LogError(exception, "Startup inference is unavailable; the owner API remains available for recovery");
        }
        if (chat is null)
            logger.LogWarning("No chat model was selected for startup. The management API is available; chat inference is not ready.");
    }
}

public sealed class HostErrorFilter(ILogger<HostErrorFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var status = exception switch
            {
                BadHttpRequestException badRequest => badRequest.StatusCode,
                KeyNotFoundException => 404,
                ArgumentException or JsonException or NotSupportedException or InvalidDataException => 400,
                DllNotFoundException or BadImageFormatException or TypeInitializationException => 503,
                InvalidOperationException => 409,
                _ => 500
            };
            logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning, exception,
                "Host request {Path} failed with {Status}", context.HttpContext.Request.Path, status);
            if (context.HttpContext.Response.HasStarted)
            {
                context.HttpContext.Abort();
                return null;
            }
            var message = status >= 500 ? "The host could not complete this operation. Check host logs and the installed TensorSharp native runtime." : exception.Message;
            return Results.Json(new { error = new { message, type = status >= 500 ? "server_error" : "invalid_request_error" } }, statusCode: status);
        }
    }
}
