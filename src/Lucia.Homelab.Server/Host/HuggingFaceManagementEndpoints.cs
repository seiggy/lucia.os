using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public static class HuggingFaceManagementEndpoints
{
    /// <summary>Uses the host's existing Data Protection provider and key persistence configuration.</summary>
    public static void AddHuggingFaceManagement(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<HuggingFaceManagementOptions>()
            .BindConfiguration("HuggingFaceManagement")
            .Validate(options => !string.IsNullOrWhiteSpace(options.CredentialsDirectory)
                && Path.IsPathFullyQualified(options.CredentialsDirectory), "HuggingFaceManagement:CredentialsDirectory must be an absolute private path.")
            .ValidateOnStart();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<HuggingFaceCredentialService>(services => new(
            services.GetRequiredService<IOptions<HuggingFaceManagementOptions>>(),
            services.GetRequiredService<IOptions<HostPlatformOptions>>(),
            services.GetRequiredService<IDataProtectionProvider>(),
            services.GetRequiredService<HuggingFaceManagementConnection>().Client));
        builder.Services.AddSingleton<HuggingFaceBrowserService>(services => new(
            services.GetRequiredService<HuggingFaceCredentialService>(),
            services.GetRequiredService<HuggingFaceManagementConnection>().Client));
        builder.Services.AddSingleton<HuggingFacePreviewService>(services => new(
            services.GetRequiredService<HuggingFaceBrowserService>(),
            services.GetRequiredService<HuggingFaceCredentialService>(),
            services.GetRequiredService<HuggingFaceManagementConnection>().PreviewClient));
        builder.Services.AddSingleton<HuggingFaceManagementConnection>();
    }

    /// <summary>Map alongside the existing host endpoints, behind UseHostCsrf and host authentication.</summary>
    public static void MapHuggingFaceManagement(this WebApplication app)
    {
        var group = app.MapGroup("/api/host/huggingface").WithTags("Hugging Face")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HuggingFaceManagementFilter>();
        group.MapGet("/credentials", (HuggingFaceCredentialService service, CancellationToken ct) => service.GetStatusAsync(ct));
        group.MapPut("/credentials", async (HttpContext context, HuggingFaceCredentialService service, CancellationToken ct) =>
        {
            // Manual bounded parsing keeps tokens out of model-binding diagnostics and rejects oversized/chunked input.
            if (context.Request.ContentLength > 4096)
                throw new HuggingFaceManagementException(413, "credential_request_too_large", "The credential request is too large.");
            if (!context.Request.HasJsonContentType())
                throw new HuggingFaceManagementException(415, "json_required", "Send a JSON object with a token field.");
            byte[] body;
            try { body = await HuggingFaceManagementHttp.ReadBoundedAsync(context.Request.Body, 4096, ct); }
            catch (HuggingFaceManagementException)
            {
                throw new HuggingFaceManagementException(413, "credential_request_too_large", "The credential request is too large.");
            }
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count() != 1
                || HuggingFaceManagementHttp.String(document.RootElement, "token") is not { } token)
                throw new HuggingFaceManagementException(400, "invalid_credential_request", "Send exactly one string token field.");
            return await service.SaveAsync(token, ct);
        });
        group.MapDelete("/credentials", (HuggingFaceCredentialService service, CancellationToken ct) => service.DisconnectAsync(ct));
        group.MapGet("/search", (string? query, string? kind, string? format, HuggingFaceBrowserService service, CancellationToken ct) =>
            service.SearchAsync(query, ParseKind(kind), ct, ParseFormat(format)));
        group.MapGet("/repository", (string? repository, string? revision, string? kind, string? format, HuggingFaceBrowserService service, CancellationToken ct) =>
            service.GetRepositoryAsync(repository, revision, ParseKind(kind), ct, ParseFormat(format)));
        group.MapGet("/context-preview", async (string? repository, string? revision, string? file, string? kind,
            HuggingFacePreviewService service, [Microsoft.AspNetCore.Mvc.FromServices] InferenceRuntime runtime, CancellationToken ct) =>
        {
            if (repository is null || revision is null || file is null)
                throw new HuggingFaceManagementException(400, "preview_selection_required", "Select a repository, pinned revision, and model file first.");
            var preview = await service.PreviewAsync(repository, revision, file, ParseKind(kind), ct);
            if (preview.Inspection is null)
                return Results.Json(new { available = false, plan = (ContextPlan?)null, reason = PreviewReason(preview.Reason),
                    code = preview.Reason, source = preview.Source, qualification = preview.Qualification });
            await runtime.Gate.WaitAsync(ct);
            try
            {
                var plan = runtime.PreviewContext(preview.Inspection);
                plan = plan with { Qualification = preview.Qualification + " " + plan.Qualification };
                return Results.Json(new { available = true, plan, reason = (string?)null,
                    code = preview.Reason, source = preview.Source, qualification = plan.Qualification });
            }
            finally { runtime.Gate.Release(); }
        });
    }

    private static string PreviewReason(string code) => code switch
    {
        "embedding_requires_local_inspection" => "Embedding-model memory and context are estimated after full local inspection.",
        "provider_access_denied" => "Hugging Face did not allow access to this file. Check your token and any gated-model approval.",
        "provider_rate_limited" => "Hugging Face rate-limited the preview. Try again later or connect your account.",
        "provider_timeout" => "The metadata preview timed out. The full model has not been downloaded.",
        "provider_not_found" => "The selected file is no longer available at this pinned revision.",
        "unsupported_architecture" => "This architecture has no supported pre-download cache estimate. Local format validation is still required.",
        _ => "The bounded GGUF header did not provide a reliable context estimate. Full local inspection is required; no capacity has been guessed."
    };

    private static ModelKind ParseKind(string? kind) => kind switch
    {
        null or "Chat" => ModelKind.Chat,
        "Embedding" => ModelKind.Embedding,
        _ => throw new HuggingFaceManagementException(400, "invalid_kind", "Kind must be Chat or Embedding.")
    };

    private static ModelFormat ParseFormat(string? format) => format switch
    {
        null or "Gguf" => ModelFormat.Gguf,
        "Safetensors" => ModelFormat.Safetensors,
        _ => throw new HuggingFaceManagementException(400, "invalid_format", "Format must be Gguf or Safetensors.")
    };

    private sealed class HuggingFaceManagementConnection : IDisposable
    {
        public HttpClient Client { get; } = HuggingFaceManagementHttp.CreateClient();
        public HttpClient PreviewClient { get; } = HuggingFacePreviewService.CreateClient();
        public void Dispose()
        {
            Client.Dispose();
            PreviewClient.Dispose();
        }
    }
}

public sealed class HuggingFaceManagementFilter(ILogger<HuggingFaceManagementFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        try { return await next(context); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (HuggingFaceManagementException exception)
        {
            logger.Log(exception.StatusCode >= 500 ? LogLevel.Error : LogLevel.Warning,
                "Hugging Face management {Path} failed with {Status} ({Code}).", http.Request.Path, exception.StatusCode, exception.Code);
            if (exception.RetryAfterSeconds is { } retry)
                http.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new { error = new { code = exception.Code, message = exception.Message, retryAfterSeconds = exception.RetryAfterSeconds } },
                statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (exception is JsonException or BadHttpRequestException)
        {
            logger.LogWarning("Hugging Face management {Path} rejected an invalid request ({ErrorType}).",
                http.Request.Path, exception.GetType().Name);
            return Results.Json(new { error = new { code = "invalid_request", message = "The request body is invalid.", retryAfterSeconds = (int?)null } }, statusCode: 400);
        }
        catch (Exception exception)
        {
            logger.LogError("Hugging Face management {Path} failed ({ErrorType}); exception text is excluded to protect credentials.",
                http.Request.Path, exception.GetType().Name);
            return Results.Json(new { error = new { code = "huggingface_unavailable", message = "Hugging Face management is temporarily unavailable.", retryAfterSeconds = (int?)null } },
                statusCode: 503);
        }
    }
}
