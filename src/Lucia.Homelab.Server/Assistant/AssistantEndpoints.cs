using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Lucia.Homelab.Server.Assistant;

public static class AssistantEndpoints
{
    public static void AddAssistant(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AssistantOptions>().BindConfiguration("Assistant")
            .Validate(options => Path.IsPathFullyQualified(options.Directory), "Assistant:Directory must be absolute.")
            .ValidateOnStart();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton(services =>
            ActivatorUtilities.CreateInstance<GitHubSignIn>(services, GitHubSignIn.CreateClient(), TimeProvider.System));
        builder.Services.AddSingleton<AssistantRuntime>();
        builder.Services.AddSingleton<AssistantRuns>();
    }

    /// <summary>Owner-only; CSRF comes from the global UseHostCsrf middleware. Streams speak the AI SDK UI message protocol.</summary>
    public static void MapAssistant(this WebApplication app)
    {
        var group = app.MapGroup("/api/assistant").WithTags("Assistant")
            .RequireAuthorization("HostOwner").AddEndpointFilter<AssistantFilter>();
        group.MapPost("/chat", async (HttpContext context, AssistantRuns runs, CancellationToken ct) =>
        {
            var run = await runs.StartAsync(Owner(context), await Read<AssistantChatRequest>(context, ct), ct);
            await StreamAsync(context, run, ct);
            return Results.Empty;
        }).DisableRequestTimeout();
        group.MapGet("/sessions", (HttpContext context, AssistantRuns runs) => runs.List(Owner(context)));
        group.MapGet("/sessions/{id}", (string id, HttpContext context, AssistantRuns runs, CancellationToken ct) =>
            runs.GetAsync(Owner(context), id, ct));
        group.MapGet("/sessions/{id}/stream", async (string id, HttpContext context, AssistantRuns runs, CancellationToken ct) =>
        {
            if (runs.Find(Owner(context), id) is not { } run) return Results.NoContent();
            await StreamAsync(context, run, ct);
            return Results.Empty;
        }).DisableRequestTimeout();
        group.MapPost("/sessions/{id}/stop", (string id, HttpContext context, AssistantRuns runs) =>
        {
            runs.Stop(Owner(context), id);
            return Results.NoContent();
        });
        group.MapDelete("/sessions/{id}", async (string id, HttpContext context, AssistantRuns runs, CancellationToken ct) =>
        {
            await runs.DeleteAsync(Owner(context), id, ct);
            return Results.NoContent();
        });
        group.MapGet("/models", (HttpContext context, AssistantRuntime runtime, CancellationToken ct) => runtime.ModelsAsync(Owner(context), ct));
        // GitHub sign-in: the host keeps the tokens; the browser only ever sees the code to type on GitHub.
        group.MapGet("/github", (HttpContext context, GitHubSignIn github) => github.Status(Owner(context)));
        group.MapPost("/github/device", (HttpContext context, GitHubSignIn github, CancellationToken ct) => github.StartAsync(Owner(context), ct));
        group.MapDelete("/github/device", async (HttpContext context, GitHubSignIn github) =>
        {
            await github.CancelAsync(Owner(context));
            return Results.NoContent();
        });
        group.MapDelete("/github", async (HttpContext context, GitHubSignIn github) =>
        {
            await github.DisconnectAsync(Owner(context));
            return Results.NoContent();
        });
    }

    /// <summary>Replays the turn from its first chunk, then follows it until it ends or the browser leaves.</summary>
    private static async Task StreamAsync(HttpContext context, AssistantRun run, CancellationToken ct)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var next = 0;
        while (true)
        {
            var (chunks, done, changed) = run.Read(next);
            next += chunks.Count;
            foreach (var chunk in chunks) await response.WriteAsync("data: " + chunk + "\n\n", ct);
            await response.Body.FlushAsync(ct);
            if (done) return;
            try { await changed.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            catch (TimeoutException) { await response.WriteAsync(": keep-alive\n\n", ct); }
        }
    }

    /// <summary>A path-safe folder name per owner, stable across browser sign-ins.</summary>
    private static string Owner(HttpContext context)
    {
        var subject = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
            throw new AssistantException(403, "actor_required", "A stable signed-in owner is required.");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))[..16];
    }

    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength > 256 * 1024)
            throw new AssistantException(413, "request_too_large", "The message is too large.");
        if (!context.Request.HasJsonContentType())
            throw new AssistantException(415, "json_required", "Send a JSON object.");
        return await context.Request.ReadFromJsonAsync<T>(AssistantStream.Json, ct)
            ?? throw new AssistantException(400, "invalid_request", "The request body is invalid.");
    }
}

public sealed class AssistantException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class AssistantFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception e) when (http.Response.HasStarted)
        {
            Logger(http).LogWarning("An assistant stream broke ({ErrorType}).", e.GetType().Name);
            http.Abort();
            return Results.Empty;
        }
        catch (AssistantException e)
        { return Results.Json(new { error = new { code = e.Code, message = e.Message } }, statusCode: e.StatusCode); }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        { return Results.Json(new { error = new { code = "invalid_request", message = "The request body is invalid." } }, statusCode: 400); }
        catch (Exception e)
        {
            Logger(http).LogError("An assistant request failed ({ErrorType}).", e.GetType().Name);
            return Results.Json(new { error = new { code = "assistant_unavailable", message = "The assistant is temporarily unavailable." } }, statusCode: 503);
        }
    }

    private static ILogger Logger(HttpContext http) => http.RequestServices.GetRequiredService<ILogger<AssistantFilter>>();
}
