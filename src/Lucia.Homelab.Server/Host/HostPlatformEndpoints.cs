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
        builder.Services.AddHostedService<HostModelStartup>();
        builder.Logging.AddFilter("TensorSharp", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
    }

    public static void MapHostPlatform(this WebApplication app)
    {
        var host = app.MapGroup("/api/host").WithTags("Host and models").RequireAuthorization("HostOwner").AddEndpointFilter<HostErrorFilter>();
        host.MapModelManagement("/api/host");

        var openai = app.MapGroup("/v1").WithTags("OpenAI-compatible inference").RequireAuthorization("HostInference").AddEndpointFilter<HostErrorFilter>();
        openai.MapOpenAIInference();
    }
}

public sealed class HostModelStartup(ModelCatalog catalog, InferenceRuntime runtime, IOptions<HostPlatformOptions> options,
    IOptions<Packages.PackageUpdatesOptions> packages, ILogger<HostModelStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
