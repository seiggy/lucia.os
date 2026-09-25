using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;

namespace Lucia.Homelab.Server.Domains;

public static class CloudflareDomainEndpoints
{
    internal const string Route = "/api/host/domains/cloudflare";

    /// <summary>Uses the host's Data Protection keys. No HttpClientFactory logging, redirects, proxy or custom TLS trust.</summary>
    public static void AddCloudflareDomains(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<CloudflareDomainOptions>().BindConfiguration("CloudflareDomains")
            .Validate(o => !string.IsNullOrWhiteSpace(o.CredentialsDirectory) && Path.IsPathFullyQualified(o.CredentialsDirectory),
                "CloudflareDomains:CredentialsDirectory must be an absolute private path.").ValidateOnStart();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<CloudflareConnection>();
        builder.Services.AddSingleton<CloudflareDomainService>(services => new(
            services.GetRequiredService<IOptions<CloudflareDomainOptions>>(),
            services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<CloudflareConnection>().Client));
        builder.Services.PostConfigure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            var previous = options.Filter;
            options.Filter = context => !context.Request.Path.StartsWithSegments(Route) && (previous?.Invoke(context) ?? true);
        });
        builder.Services.AddHttpLoggingInterceptor<CloudflareHttpLoggingInterceptor>();
    }

    /// <summary>Map behind the existing UseHostCsrf and host authentication middleware.</summary>
    public static void MapCloudflareDomains(this WebApplication app)
    {
        var group = app.MapGroup(Route).WithTags("Cloudflare domains").RequireAuthorization("HostOwner")
            .AddEndpointFilter<CloudflareDomainFilter>();
        group.MapGet("/credentials", (CloudflareDomainService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapPut("/credentials", async (HttpContext context, CloudflareDomainService service, CancellationToken ct) =>
        {
            if (context.Request.ContentLength > 8192) throw RequestTooLarge();
            if (!context.Request.HasJsonContentType())
                throw new CloudflareDomainException(415, "json_required", "Send a JSON object with accountId and token strings.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            byte[] body;
            try { body = await CloudflareHttp.ReadBoundedAsync(context.Request.Body, 8192, deadline.Token); }
            catch (CloudflareDomainException) { throw RequestTooLarge(); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new CloudflareDomainException(408, "credential_request_timeout", "The credential request body was not received in time."); }
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || CloudflareHttp.String(root, "accountId") is not { } accountId
                || CloudflareHttp.String(root, "token") is not { } token)
                throw new CloudflareDomainException(400, "invalid_credential_request", "Send exactly one accountId string and one token string.");
            return await service.SaveAsync(new(accountId, token), ct);
        });
        group.MapDelete("/credentials", (CloudflareDomainService service, CancellationToken ct) => service.DisconnectAsync(ct));
        group.MapGet("/zones", (CloudflareDomainService service, CancellationToken ct) => service.ListZonesAsync(ct));
    }

    private static CloudflareDomainException RequestTooLarge() =>
        new(413, "credential_request_too_large", "The credential request exceeds 8192 bytes.");

    private sealed class CloudflareConnection : IDisposable
    {
        internal HttpClient Client { get; } = CloudflareHttp.CreateClient();
        public void Dispose() => Client.Dispose();
    }
}

public sealed class CloudflareDomainFilter(ILogger<CloudflareDomainFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        try
        {
            // A token is accepted only in the bounded JSON body, never a query or a Lucia authorization header.
            if (http.Request.QueryString.HasValue)
                throw new CloudflareDomainException(400, "query_not_allowed", "Cloudflare management does not accept query parameters.");
            return await next(context);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (CloudflareDomainException e)
        {
            logger.Log(e.StatusCode >= 500 ? LogLevel.Error : LogLevel.Warning, "Cloudflare management failed ({Code}).", e.Code);
            if (e.RetryAfterSeconds is { } retry)
                http.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = new { code = e.Code, message = e.Message, retryAfterSeconds = e.RetryAfterSeconds } },
                statusCode: e.StatusCode);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        {
            logger.LogWarning("Cloudflare management rejected invalid input ({ErrorType}).", e.GetType().Name);
            return Results.Json(new { error = new { code = "invalid_request", message = "The request body is invalid.", retryAfterSeconds = (int?)null } },
                statusCode: 400);
        }
        catch (Exception e)
        {
            logger.LogError("Cloudflare management failed ({ErrorType}).", e.GetType().Name);
            return Results.Json(new { error = new { code = "cloudflare_unavailable", message = "Cloudflare management is temporarily unavailable.", retryAfterSeconds = (int?)null } },
                statusCode: 503);
        }
    }
}

internal sealed class CloudflareHttpLoggingInterceptor : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext context)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments(CloudflareDomainEndpoints.Route))
            context.LoggingFields = HttpLoggingFields.None;
        return ValueTask.CompletedTask;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext context) => OnRequestAsync(context);
}
