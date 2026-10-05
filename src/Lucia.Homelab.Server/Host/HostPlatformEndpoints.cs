using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public static class HostPlatformEndpoints
{
    public static void AddHostPlatform(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<HostPlatformOptions>()
            .BindConfiguration("HostPlatform").ValidateDataAnnotations()
            .Validate(o => o.Backend is "cpu" or "ggml_cpu" or "ggml_cuda" or "cuda", "Unsupported inference backend.")
            .Validate(o => o.InferenceApiKey is null || o.InferenceApiKey != o.ApiKey, "Owner and inference API keys must differ.")
            .ValidateOnStart();
        builder.AddHostAuthentication();
        builder.Services.AddSingleton<ModelCatalog>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ModelCatalog>());
        builder.Services.AddSingleton<ModelInspector>();
        builder.Services.AddSingleton<InferenceRuntime>();
        builder.Services.AddSingleton<IChatClient, TensorSharpChatClient>();
        builder.Services.AddSingleton<SparkModel>();
        builder.Services.AddHostedService<HostModelStartup>();
        builder.Logging.AddFilter("TensorSharp", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
    }

    public static void MapHostPlatform(this WebApplication app)
    {
        var host = app.MapGroup("/api/host").WithTags("Host and models").RequireAuthorization("HostOwner").AddEndpointFilter<HostErrorFilter>();
        host.MapModelManagement("/api/host");
        host.MapGet("/spark-model", (SparkModel model) => model.Get());
        host.MapPost("/spark-model/start", (SparkModel model, CancellationToken ct) => model.Set("running", ct));
        host.MapPost("/spark-model/stop", (SparkModel model, CancellationToken ct) => model.Set("stopped", ct));

        var openai = app.MapGroup("/v1").WithTags("OpenAI-compatible inference").RequireAuthorization("HostInference").AddEndpointFilter<HostErrorFilter>();
        // While Qwen3.8-Flash-Next is wanted, it answers models, chat and responses; Lucia's own models are unloaded.
        openai.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var model = http.RequestServices.GetRequiredService<SparkModel>();
            if (http.Request.Path.Value is not ("/v1/models" or "/v1/chat/completions" or "/v1/responses") || model.Serving() is not { } serving) return await next(context);
            var problem = serving.Problem;
            // TensorFold's list lacks the capabilities and context Lucia's clients read, so describe it in Lucia's shape.
            if (serving.Url is not null && http.Request.Path.Value == "/v1/models")
                return Results.Ok(new { @object = "list", data = new[] { new { id = SparkModel.Name, @object = "model", created = 0,
                    owned_by = "local", capabilities = new[] { "chat", "responses" }, backend = "tensorfold", context_length = SparkModel.ContextTokens, max_output_tokens = 32768 } } });
            if (serving.Url is { } url)
                try
                {
                    await model.ProxyAsync(http, url);
                    return Results.Empty;
                }
                catch (HttpRequestException) when (!http.Response.HasStarted) { problem = $"{SparkModel.Name} isn't answering on the Spark."; }
            return Results.Json(new { error = new { message = problem, type = "server_error", code = "model_not_loaded" } }, statusCode: 503);
        });
        openai.MapOpenAIInference();
    }
}

public sealed class HostModelStartup(ModelCatalog catalog, InferenceRuntime runtime, IOptions<HostPlatformOptions> options,
    IOptions<Packages.PackageUpdatesOptions> packages, SparkModel sparkModel, ILogger<HostModelStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (sparkModel.HoldsMemory())
        {
            logger.LogInformation("Startup model loading deferred while {Model} holds the Spark's memory.", SparkModel.Name);
            return;
        }
        if (Packages.PackageUpdatesOptions.ModelsPaused(packages.Value))
        {
            runtime.StartupError = "Local AI is paused while platform updates install. Lucia reloads it when they finish.";
            logger.LogInformation("Startup model loading deferred until platform updates finish.");
            return;
        }
        await catalog.LoadSavedSelectionAsync(runtime, options.Value, logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
