using System.Net;
using System.Text.Json;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lucia.Homelab.Server.Boot;

public static class HardwareBootExtensions
{
    public static void AddHardwareBoot(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("Boot").Get<BootOptions>() ?? new();
        settings.Validate();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<DiscoveryChallenges>();
        builder.Services.AddHostedService<BootAdmissionHeartbeat>();
    }

    public static void MapHardwareBoot(this WebApplication app)
    {
        // This group admits discovery only. Installation uses separate session-bound endpoints.
        var boot = app.MapGroup("/api/boot").WithTags("Hardware discovery").AllowAnonymous()
            .AddEndpointFilter<BootProtocolErrorFilter>();
        boot.MapGet("/discovery.cfg", async (HttpContext context, BootOptions options, HardwareOnboardingStore store, CancellationToken ct) =>
        {
            RequireNetwork(context, options);
            await RequireAdmission(store, ct);
            return Results.Text("d-i preseed/early_command string /usr/lib/lucia/discover-and-wait\n", "text/plain");
        });
        boot.MapGet("/challenge", async (HttpContext context, BootOptions options, HardwareOnboardingStore store,
            DiscoveryChallenges challenges, CancellationToken ct) =>
        {
            var address = RequireNetwork(context, options);
            await RequireAdmission(store, ct);
            return Results.Json(challenges.Issue(address));
        });
        boot.MapPost("/discover", async (HttpContext context, BootOptions options, HardwareOnboardingStore store,
            DiscoveryChallenges challenges, CancellationToken ct) =>
        {
            var address = RequireNetwork(context, options);
            await RequireAdmission(store, ct);
            var request = await ReadRequest<SignedDiscovery>(context, ct);
            var verified = challenges.Verify(request, address);
            var report = JsonSerializer.Deserialize<HardwareReport>(verified.ReportJson, HardwareOnboardingJson.Options)
                ?? throw new DiscoveryProtocolException(400, "A hardware inventory object is required.");
            var receipt = await store.RegisterDiscoveryAsync(verified.PublicKeyFingerprint, report,
                isKnownManagedDevice: false, cancellationToken: ct);
            if (receipt.Token is null)
                throw new DiscoveryProtocolException(409, "This device already registered. Resume with its saved local discovery session.");
            return Results.Json(new
            {
                receipt.DeviceId,
                token = DiscoveryCapability.Encode(verified.PublicKeyFingerprint, receipt.Token),
                receipt.ExpiresAt,
                verificationCode = HardwareOnboardingStore.GetVerificationCode(verified.PublicKeyFingerprint)
            });
        });
        boot.MapGet("/devices/{id:guid}", async (Guid id, HttpContext context, BootOptions options,
            HardwareOnboardingStore store, CancellationToken ct) =>
        {
            RequireNetwork(context, options);
            var session = DiscoveryCapability.Read(id, context.Request);
            return Results.Json(await store.GetSessionStatusAsync(session, ct), HardwareOnboardingJson.Options);
        });
    }

    internal static IPAddress RequireNetwork(HttpContext context, BootOptions options)
    {
        if (!options.Enabled)
            throw new DiscoveryProtocolException(503, "Network boot discovery has not been configured.");
        if (context.Connection.RemoteIpAddress is not { } address || !options.Allows(address))
            throw new DiscoveryProtocolException(403, "Discovery is restricted to the configured provisioning network.");
        return address;
    }

    private static async Task RequireAdmission(HardwareOnboardingStore store, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Readiness.CanDiscover)
            throw new DiscoveryProtocolException(503, "Read-only hardware discovery is not ready.");
        if (!snapshot.Window.IsOpen)
            throw new DiscoveryProtocolException(403, "Hardware onboarding is closed. Ask the owner to open Add hardware.");
    }

    internal static async Task<T> ReadRequest<T>(HttpContext context, CancellationToken cancellationToken)
    {
        const int limit = 512 * 1024;
        if (!context.Request.HasJsonContentType() || context.Request.ContentLength > limit)
            throw new DiscoveryProtocolException(400, "Supply a bounded JSON discovery request.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit + 1;
        var buffer = new byte[limit + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) throw new DiscoveryProtocolException(400, "The discovery request exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, count), HardwareOnboardingJson.Options)
            ?? throw new DiscoveryProtocolException(400, "A signed discovery object is required.");
    }
}

internal static class DiscoveryCapability
{
    internal static string Encode(string fingerprint, string capability) => $"d1.{fingerprint}.{capability}";

    internal static HardwareDeviceSession Read(Guid deviceId, HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (deviceId == Guid.Empty || values.Count != 1 || values[0] is not { } header
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        var parts = header[7..].Split('.');
        if (parts.Length != 3 || parts[0] != "d1" || parts[1].Length != 64 || parts[2].Length != 64
            || !parts[1].All(char.IsAsciiHexDigit) || !parts[2].All(char.IsAsciiHexDigit))
            throw Invalid();
        return new(deviceId, parts[1], parts[2]);
    }

    private static DiscoveryProtocolException Invalid() =>
        new(403, "A valid device-specific discovery session is required.");
}

internal sealed class BootAdmissionHeartbeat(BootOptions options, HardwareOnboardingStore store,
    TimeProvider clock, ILogger<BootAdmissionHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var lease = new BootAdmissionLease(options.ControlDirectory, clock);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var snapshot = await store.GetSnapshotAsync(stoppingToken);
                lease.Update(snapshot.Readiness.CanDiscover && snapshot.Window.IsOpen ? snapshot.Window.ExpiresAt : null);
                await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HardwareOnboardingException)
        {
            logger.LogError("Boot admission heartbeat failed ({ErrorType}); boot serving will expire closed.", exception.GetType().Name);
            throw;
        }
    }
}

internal sealed class BootProtocolErrorFilter(ILogger<BootProtocolErrorFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var (status, message) = error switch
            {
                DiscoveryProtocolException known => (known.StatusCode, known.Message),
                HardwareOnboardingException known => (known.StatusCode, known.Message),
                JsonException or BadHttpRequestException => (400, "The discovery request body is invalid."),
                _ => (503, "Hardware discovery is unavailable. Inspect host logs before retrying.")
            };
            logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning,
                "Hardware discovery {Path} failed with {Status} ({ErrorType}).",
                context.HttpContext.Request.Path, status, error.GetType().Name);
            return Results.Json(new { error = new { message, type = "discovery_error" } }, statusCode: status);
        }
    }
}
