using System.Text.Json;
using Lucia.Homelab.Server.Domains;

namespace Lucia.Homelab.Server.Packages;

public static class PackageUpdatesEndpoints
{
    public static void AddPackageUpdates(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<PackageUpdatesOptions>().BindConfiguration("PackageUpdates")
            .Validate(options => Path.IsPathFullyQualified(options.Directory), "PackageUpdates:Directory must be absolute.")
            .ValidateOnStart();
        builder.Services.AddSingleton<PackageUpdatesService>();
        builder.Services.AddHostedService<PackageUpdatesScheduler>();
    }

    /// <summary>Owner-only; CSRF comes from the global UseHostCsrf middleware.</summary>
    public static void MapPackageUpdates(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/packages").WithTags("Spark updates")
            .RequireAuthorization("HostOwner").AddEndpointFilter<PackageUpdatesFilter>();
        group.MapGet("", (PackageUpdatesService service, CancellationToken ct) => service.GetAsync(ct));
        group.MapPost("/check", (PackageUpdatesService service, CancellationToken ct) => service.CheckAsync(ct));
        group.MapPost("/repair", (PackageUpdatesService service, CancellationToken ct) => service.RepairAsync(ct));
        group.MapPost("/restart-services", (PackageUpdatesService service, CancellationToken ct) => service.RestartServicesAsync(ct));
        group.MapPost("/restart-spark", async (HttpContext context, PackageUpdatesService service, CancellationToken ct) =>
        {
            var body = await Read<RestartConfirmation>(context, ct);
            if (!body.Confirm)
                throw new PackageUpdatesException(400, "confirmation_required", "Confirm the restart first.");
            return await service.RestartSparkAsync(ct);
        });
        group.MapPost("/install", async (HttpContext context, PackageUpdatesService service, CancellationToken ct) =>
            await service.InstallAsync(await Read<PackageInstallRequest>(context, ct), ct));
        group.MapPost("/changelog", async (HttpContext context, PackageUpdatesService service, CancellationToken ct) =>
            await service.ChangelogAsync((await Read<ChangelogRequest>(context, ct)).Package, ct)).DisableRequestTimeout();
        group.MapPut("/schedule", async (HttpContext context, PackageUpdatesService service, CancellationToken ct) =>
            await service.SaveScheduleAsync(await Read<PackageScheduleRequest>(context, ct), ct));
        group.MapDelete("/schedule", async (PackageUpdatesService service, CancellationToken ct) =>
        {
            await service.CancelScheduleAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength > 64 * 1024)
            throw new PackageUpdatesException(413, "request_too_large", "The request is too large.");
        if (!context.Request.HasJsonContentType())
            throw new PackageUpdatesException(415, "json_required", "Send a JSON object.");
        return await context.Request.ReadFromJsonAsync<T>(DomainOnboardingStore.Json, ct)
            ?? throw new PackageUpdatesException(400, "invalid_request", "The request body is invalid.");
    }

    private sealed record RestartConfirmation(bool Confirm);
    private sealed record ChangelogRequest(string Package);
}

public sealed class PackageUpdatesFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (PackageUpdatesException e)
        { return Results.Json(new { error = new { code = e.Code, message = e.Message } }, statusCode: e.StatusCode); }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        { return Results.Json(new { error = new { code = "invalid_request", message = "The request body is invalid." } }, statusCode: 400); }
        catch (Exception e)
        {
            http.RequestServices.GetRequiredService<ILogger<PackageUpdatesFilter>>()
                .LogError("Spark updates request failed ({ErrorType}).", e.GetType().Name);
            return Results.Json(new { error = new { code = "updates_unavailable", message = "Spark updates are temporarily unavailable." } }, statusCode: 503);
        }
    }
}
