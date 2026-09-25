using System.Security.Claims;
using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

public static class DomainOnboardingEndpoints
{
    public static void AddDomainOnboarding(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("DomainOnboarding").Get<DomainOnboardingOptions>() ?? new();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<DomainOnboardingStore>();
        builder.Services.AddSingleton<DomainConnectionGate>();
        builder.Services.AddSingleton<DomainSupportService>();
        builder.Services.AddSingleton<DomainOperationsService>();
        builder.Services.AddSingleton(services => new CertbotCertificateService(
            new(options.StateDirectory, Path.Combine(options.StateDirectory, "certificates"))));
        builder.Services.AddSingleton<DomainHttp>();
        builder.Services.AddSingleton(services => new DomainDnsPreflight(services.GetRequiredService<DomainHttp>().Client));
        builder.Services.AddSingleton(services => new DomainOnboardingService(options,
            services.GetRequiredService<DomainOnboardingStore>(), services.GetRequiredService<CloudflareDomainService>(),
            services.GetRequiredService<ILocalDnsProvider>(), services.GetRequiredService<DomainDnsPreflight>(),
            services.GetRequiredService<DomainHttp>().Client, services.GetRequiredService<HostAuthenticationOptions>(),
            services.GetRequiredService<DomainConnectionGate>()));
        builder.Services.AddHostedService<DomainOnboardingWorker>();
    }

    public static void UseDomainConnectionGuard(this WebApplication app) => app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value?.TrimEnd('/');
        var protectedPath = string.Equals(path, "/api/host/connections/adguard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/api/host/domains/cloudflare/credentials", StringComparison.OrdinalIgnoreCase);
        if (!protectedPath || (!HttpMethods.IsPut(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method)))
        {
            await next(context);
            return;
        }
        var gate = context.RequestServices.GetRequiredService<DomainConnectionGate>();
        await gate.Mutex.WaitAsync(context.RequestAborted);
        try
        {
            var job = (await context.RequestServices.GetRequiredService<DomainOnboardingStore>().Read(context.RequestAborted)).Job;
            if (job?.State is "Queued" or "Running" or "Activating" || job?.Phase == "Renewing")
            {
                context.Response.StatusCode = 409;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(new { error = new { message = "Wait for the active DNS or certificate operation before changing its connections." } });
                return;
            }
            await next(context);
        }
        finally { gate.Mutex.Release(); }
    });

    public static void MapDomainOnboarding(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/domains").RequireAuthorization("HostOwner")
            .WithTags("DNS and certificates").AddEndpointFilter<DomainOnboardingErrorFilter>();
        group.MapGet("/status", (DomainOnboardingService service, CancellationToken ct) => service.Status(ct));
        group.MapGet("/overview", (DomainOperationsService service, CancellationToken ct) => service.Read(ct));
        group.MapPost("/plan", async (HttpContext context, DomainOnboardingService service, CancellationToken ct) =>
            await service.Plan(await Read<DomainPlanRequest>(context, ct), ct));
        group.MapPost("/start", async (HttpContext context, DomainOnboardingService service, CancellationToken ct) =>
        {
            var actor = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var job = await service.Start(await Read<StartDomainSetupRequest>(context, ct), actor, ct);
            return Results.Accepted("/api/host/domains/status", new { job.Id, job.State, job.Phase, job.Message });
        });
        group.MapPost("/jobs/{id:guid}/recover", async (Guid id, DomainOnboardingService service, CancellationToken ct) =>
        {
            await service.Recover(id, ct);
            return Results.Accepted("/api/host/domains/status");
        });
        group.MapPost("/jobs/{id:guid}/diagnose", async (Guid id, DomainSupportService support, CancellationToken ct) =>
        {
            await support.Queue(id, ct);
            return Results.Accepted("/api/host/domains/status");
        });
    }

    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType() || context.Request.ContentLength > 32768)
            throw new ArgumentException("Send a bounded JSON domain configuration.");
        var bytes = new byte[32769];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        if (count > 32768) throw new ArgumentException("Domain configuration is too large.");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, count), DomainOnboardingStore.Json)
            ?? throw new ArgumentException("A domain configuration object is required.");
    }

    private sealed class DomainHttp : IDisposable
    {
        public HttpClient Client { get; } = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5)
        }) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 65536 };
        public void Dispose() => Client.Dispose();
    }
}

public sealed class DomainConnectionGate : IDisposable
{
    public SemaphoreSlim Mutex { get; } = new(1, 1);
    public void Dispose() => Mutex.Dispose();
}

internal sealed class DomainOnboardingErrorFilter(ILogger<DomainOnboardingErrorFilter> logger) : IEndpointFilter
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
                CloudflareDomainException known => (known.StatusCode, known.Message),
                AdGuardManagementException known => (known.StatusCode, known.Message),
                DomainProbeException known => (409, known.Message),
                ArgumentException => (400, error.Message),
                InvalidOperationException => (409, error.Message),
                JsonException or BadHttpRequestException => (400, "The DNS configuration is invalid."),
                _ => (503, "DNS onboarding is unavailable. Check connection settings and host logs before retrying.")
            };
            logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning,
                "DNS onboarding {Path} failed with {Status} ({ErrorType}).", context.HttpContext.Request.Path, status, error.GetType().Name);
            return Results.Json(new { error = new { message, code = "domain_setup_failed" } }, statusCode: status);
        }
    }
}
