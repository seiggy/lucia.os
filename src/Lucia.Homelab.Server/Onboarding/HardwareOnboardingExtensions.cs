using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lucia.Homelab.Server.Onboarding;

public static class HardwareOnboardingExtensions
{
    public static void AddHardwareOnboarding(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<HardwareOnboardingOptions>().BindConfiguration("HardwareOnboarding");
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<HardwareOnboardingStore>();
        builder.Services.AddSingleton<GithubRecoveryKeys>();
        builder.Services.AddHostedService(services => services.GetRequiredService<HardwareOnboardingStore>());
    }

    // Requires AddHostAuthentication/UseAuthorization and the existing global UseHostCsrf middleware.
    // Deliberately maps no discovery, capability, enrollment, or installer endpoint.
    public static void MapHardwareOnboarding(this WebApplication app)
    {
        var group = app.MapGroup("/api/host").WithTags("Hardware onboarding")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        group.MapGet("/onboarding", async (HardwareOnboardingStore store, CancellationToken ct) =>
            Json(await store.GetSnapshotAsync(ct)));
        group.MapGet("/ssh-keys/github/{username}", (string username, GithubRecoveryKeys keys, CancellationToken ct) =>
            keys.Read(username, ct));
        group.MapPost("/onboarding/window", async (HttpContext context, HardwareOnboardingStore store, CancellationToken ct) =>
        {
            var request = await ReadAsync<OpenHardwareWindowRequest>(context, ct);
            return Json(await store.OpenWindowAsync(request.Minutes, Actor(context), ct));
        });
        group.MapDelete("/onboarding/window", async (HttpContext context, HardwareOnboardingStore store, CancellationToken ct) =>
            Json(await store.CloseWindowAsync(Actor(context), ct)));
        group.MapPost("/devices/{id}/approve-install", async (string id, HttpContext context, HardwareOnboardingStore store, CancellationToken ct) =>
        {
            var request = await ReadAsync<ApproveHardwareInstallRequest>(context, ct);
            return Json(await store.ApproveInstallAsync(DeviceId(id), request, Actor(context), ct));
        });
        group.MapPost("/devices/{id}/reject", async (string id, HttpContext context, HardwareOnboardingStore store, CancellationToken ct) =>
            Json(await store.RejectDiscoveryAsync(DeviceId(id), Actor(context), ct)));
        group.MapPost("/devices/{id}/dismiss", async (string id, HttpContext context, HardwareOnboardingStore store, CancellationToken ct) =>
            Json(await store.SetDiscoveryDismissedAsync(DeviceId(id), true, Actor(context), ct)));
        group.MapPost("/devices/{id}/restore", async (string id, HttpContext context, HardwareOnboardingStore store, CancellationToken ct) =>
            Json(await store.SetDiscoveryDismissedAsync(DeviceId(id), false, Actor(context), ct)));
    }

    private static IResult Json<T>(T value) => Results.Json(value, HardwareOnboardingJson.Options);
    private static Guid DeviceId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? id : throw new HardwareOnboardingException(400, "invalid_device_id", "Device ID must be a nonempty UUID.");

    private static string Actor(HttpContext context)
    {
        var identity = context.User.Identities.FirstOrDefault(identity => identity.IsAuthenticated);
        var subject = identity?.FindFirst("sub")?.Value ?? identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            throw new HardwareOnboardingException(403, "actor_required", "A stable authenticated owner identity is required.");
        return (identity?.FindFirst("iss")?.Value ?? identity?.AuthenticationType ?? "owner") + ":" + subject;
    }

    private static async Task<T> ReadAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        const int limit = 4096;
        if (!context.Request.HasJsonContentType() || context.Request.ContentLength > limit)
            throw new HardwareOnboardingException(400, "invalid_body", "Supply a JSON object of at most 4096 bytes.");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit + 1;
        var bytes = new byte[limit + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) throw new HardwareOnboardingException(400, "invalid_body", "Supply a JSON object of at most 4096 bytes.");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, count), HardwareOnboardingJson.Options)
            ?? throw new HardwareOnboardingException(400, "invalid_body", "A JSON object is required.");
    }
}

internal sealed class HardwareOnboardingErrorFilter(ILogger<HardwareOnboardingErrorFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var (status, code, message) = exception switch
            {
                HardwareOnboardingException known => (known.StatusCode, known.Code, known.Message),
                JsonException or BadHttpRequestException => (400, "invalid_body", "The onboarding request body is invalid."),
                _ => (503, "onboarding_unavailable", "Onboarding could not complete this operation. Inspect host logs before retrying.")
            };
            logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning,
                "Hardware onboarding request {Path} failed with {Status} ({Code}, {ExceptionType})",
                context.HttpContext.Request.Path, status, code, exception.GetType().Name);
            return Results.Json(new { error = new { message, type = status >= 500 ? "server_error" : "invalid_request_error", code } },
                HardwareOnboardingJson.Options, statusCode: status);
        }
    }
}
