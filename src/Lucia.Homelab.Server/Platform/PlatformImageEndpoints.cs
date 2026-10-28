using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Packages;

namespace Lucia.Homelab.Server.Platform;

public static class PlatformImageEndpoints
{
    public static void AddPlatformImages(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<PlatformImageUpdates>();
        builder.Services.AddHostedService<PlatformImageChecks>();
    }

    /// <summary>Owner-only, beside the Spark's package updates; CSRF comes from the global UseHostCsrf middleware.</summary>
    public static void MapPlatformImages(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/platform-images").WithTags("Spark updates")
            .RequireAuthorization("HostOwner").AddEndpointFilter<PackageUpdatesFilter>();
        // The page warns, but doesn't refuse, while Qwen runs; the Assistant's tool refuses.
        group.MapGet("", (PlatformImageUpdates updates, IServiceProvider services) =>
        {
            var listed = JsonSerializer.SerializeToNode(updates.Get(), JsonSerializerOptions.Web) as JsonObject ?? [];
            listed["qwenRunning"] = QwenRunning(services.GetService<SparkModel>());
            return Results.Json(listed);
        });
        group.MapPost("/check", (PlatformImageUpdates updates) =>
        {
            updates.Forget();
            return Results.Accepted();
        });
        group.MapPost("/update", async (HttpContext context, PlatformImageUpdates updates, CancellationToken ct) =>
            await updates.Apply(await PackageUpdatesEndpoints.Read<PlatformUpdateRequest>(context, ct), ct));
    }

    private static bool QwenRunning(SparkModel? model)
    {
        try { return model?.Serving() is not null; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { return false; }
    }
}