using System.Security.Cryptography;
using System.Text;
using Lucia.Homelab.Server.Host;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// A GPU server serves models only: no voice, and the OS and other apps live in host RAM, not VRAM.
// Lucia sets HostPlatform__MemoryBudgetGiB to the assigned GPUs' VRAM and both keys when it deploys the worker.
builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?>
    {
        ["HostPlatform:Backend"] = "ggml_cuda",
        ["HostPlatform:ModelDirectory"] = "/models",
        ["HostPlatform:HuggingFaceExecutable"] = "/opt/hf/bin/hf",
        ["HostPlatform:OsReserveGiB"] = "0",
        ["HostPlatform:ServicesReserveGiB"] = "0",
        ["HostPlatform:VoiceReserveGiB"] = "0",
        ["HostPlatform:RuntimeReserveGiB"] = "2",
        ["HostPlatform:WeightMemoryMultiplier"] = "1",
    }
});
builder.Services.AddOptions<HostPlatformOptions>().BindConfiguration("HostPlatform")
    .Validate(o => o.ApiKey.Length >= 32 && o.InferenceApiKey is { Length: >= 32 } && o.ApiKey != o.InferenceApiKey,
        "HostPlatform:ApiKey and HostPlatform:InferenceApiKey must be different keys of at least 32 characters.")
    .Validate(o => o.MemoryBudgetGiB is > 0, "HostPlatform:MemoryBudgetGiB must be the VRAM of the GPUs this worker may use.")
    .Validate(o => o.Backend == "ggml_cuda", "The worker only runs the ggml_cuda backend.")
    .Validate(o => o.RuntimeReserveGiB >= 1, "Keep at least 1 GiB of VRAM for TensorSharp's scratch buffers.")
    .ValidateOnStart();
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddHostedService(services => services.GetRequiredService<ModelCatalog>());
builder.Services.AddSingleton<ModelInspector>();
builder.Services.AddSingleton<InferenceRuntime>();
// Beside vLLM the worker only keeps the model library, and has no GPU to load a saved selection onto.
if (!builder.Configuration.GetValue<bool>("Worker:LibraryOnly")) builder.Services.AddHostedService<SavedModelStartup>();
else builder.Configuration["HostPlatform:RequireTensorSharp"] = "false";
builder.Logging.AddFilter("TensorSharp", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 16 * 1024 * 1024);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGroup("/api/worker").RequireKey(o => o.ApiKey).AddEndpointFilter<HostErrorFilter>().MapModelManagement("/api/worker");
app.MapGroup("/v1").RequireKey(o => o.InferenceApiKey!).AddEndpointFilter<HostErrorFilter>().MapOpenAIInference();
app.Run();

static class WorkerAuthentication
{
    /// <summary>Accepts only "Authorization: Bearer {key}". Both sides are hashed first so the comparison is constant-time.</summary>
    public static RouteGroupBuilder RequireKey(this RouteGroupBuilder group, Func<HostPlatformOptions, string> key)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var expected = key(context.HttpContext.RequestServices.GetRequiredService<IOptions<HostPlatformOptions>>().Value);
            var header = context.HttpContext.Request.Headers.Authorization.ToString();
            var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            return given.Length > 0 && CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)))
                ? await next(context) : Results.Unauthorized();
        });
        return group;
    }
}

sealed class SavedModelStartup(ModelCatalog catalog, InferenceRuntime runtime, IOptions<HostPlatformOptions> options,
    ILogger<SavedModelStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        catalog.LoadSavedSelectionAsync(runtime, options.Value, logger, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
