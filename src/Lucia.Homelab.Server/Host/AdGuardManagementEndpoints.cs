using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace Lucia.Homelab.Server.Host;

public static class AdGuardManagementEndpoints
{
    /// <summary>Uses the host's persisted Data Protection keys; one connection per host instance.</summary>
    public static void AddAdGuardManagement(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AdGuardManagementOptions>().BindConfiguration("AdGuardManagement")
            .Validate(options => !string.IsNullOrWhiteSpace(options.CredentialsDirectory)
                && Path.IsPathFullyQualified(options.CredentialsDirectory),
                "AdGuardManagement:CredentialsDirectory must be an absolute private path.")
            .ValidateOnStart();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<AdGuardConnectionService>();
        builder.Services.AddSingleton<ILocalDnsProvider>(services => services.GetRequiredService<AdGuardConnectionService>());
    }

    /// <summary>Map behind the host authentication and global UseHostCsrf middleware. No browser rewrite CRUD.</summary>
    public static void MapAdGuardManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/connections/adguard").WithTags("AdGuard")
            .RequireAuthorization("HostOwner").AddEndpointFilter<AdGuardManagementFilter>();
        group.MapGet("", (AdGuardConnectionService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapPut("", async (HttpContext context, AdGuardConnectionService service, CancellationToken ct) =>
        {
            const int limit = 16 * 1024;
            if (context.Request.ContentLength > limit) throw TooLarge();
            if (!context.Request.HasJsonContentType())
                throw new AdGuardManagementException(415, "json_required", "Send an AdGuard connection JSON object.");
            byte[] body;
            try { body = await AdGuardTransport.ReadBoundedAsync(context.Request.Body, limit, ct); }
            catch (AdGuardManagementException) { throw TooLarge(); }
            try
            {
                // Avoid model binding diagnostics, record ToString and duplicate/unknown secret fields.
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 2 });
                var root = document.RootElement;
                string[] names = ["baseUrl", "username", "password", "allowInsecureHttp"];
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length
                    || root.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))
                    || AdGuardValidation.String(root, "baseUrl") is not { } baseUrl
                    || AdGuardValidation.String(root, "username") is not { } username
                    || AdGuardValidation.String(root, "password") is not { } password
                    || !root.TryGetProperty("allowInsecureHttp", out var consent)
                    || consent.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new AdGuardManagementException(400, "invalid_adguard_request",
                        "Supply exactly baseUrl, username, password and allowInsecureHttp.");
                return await service.SaveAsync(new()
                {
                    BaseUrl = baseUrl, Username = username, Password = password, AllowInsecureHttp = consent.GetBoolean()
                }, ct);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        });
        group.MapDelete("", (AdGuardConnectionService service, CancellationToken ct) => service.DisconnectAsync(ct));
        group.MapPost("/verify", (AdGuardConnectionService service, CancellationToken ct) => service.VerifyAsync(ct));
        group.MapGet("/certificate", ([FromServices] Domains.AdGuardCertificateWorker worker, CancellationToken ct) => worker.GetStatusAsync(ct));
        group.MapPut("/certificate", ([FromBody] Domains.AdGuardCertificateToggle toggle, [FromServices] Domains.AdGuardCertificateWorker worker,
            CancellationToken ct) => worker.SetEnabledAsync(toggle.Enabled, toggle.Name, ct));
    }

    private static AdGuardManagementException TooLarge() =>
        new(413, "adguard_request_too_large", "The connection request exceeds 16 KiB.");
}

public sealed class AdGuardManagementFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        try { return await next(context); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (AdGuardManagementException e)
        { return Results.Json(new { error = new { code = e.Code, message = e.Message } }, statusCode: e.StatusCode); }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        { return Results.Json(new { error = new { code = "invalid_adguard_request", message = "The request body is invalid." } }, statusCode: 400); }
        catch (Exception)
        {
            // No exception objects/text, response bodies, credentials or URLs in diagnostics.
            return Results.Json(new { error = new { code = "adguard_unavailable", message = "AdGuard management is temporarily unavailable." } }, statusCode: 503);
        }
    }
}
