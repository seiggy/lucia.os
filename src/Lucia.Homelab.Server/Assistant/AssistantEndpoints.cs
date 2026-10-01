using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Stacks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

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
        builder.Services.AddSingleton(services => ActivatorUtilities.CreateInstance<AssistantProviders>(services,
            AssistantProviders.CreateClient(),
            (Func<CancellationToken, Task<IReadOnlyList<AppEndpoint>>>)(ct => services.GetRequiredService<StackStore>().AiEndpoints(ct)),
            (Func<SparkInference>)(() => Spark(services))));
        builder.Services.AddSingleton<AssistantRuntime>();
        builder.Services.AddSingleton<AssistantBroker>();
        builder.Services.AddSingleton<AssistantTools>();
        builder.Services.AddSingleton<AssistantRuns>();
    }

    /// <summary>The Spark's model over loopback, which the host proxy lets through with the inference key.</summary>
    private static SparkInference Spark(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<InferenceRuntime>();
        var platform = services.GetRequiredService<IOptions<HostPlatformOptions>>().Value;
        var port = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(BindingAddress.Parse).FirstOrDefault(address => address.Scheme == "http")?.Port;
        var model = runtime.ChatModelName;
        var problem = string.IsNullOrEmpty(platform.InferenceApiKey) ? "Set HostPlatform:InferenceApiKey to use the Spark's model."
            : model is null ? runtime.StartupError ?? "No chat model is loaded on the Spark. Load one on the AI page."
            : port is null ? "The Spark's model needs the host to listen on HTTP." : null;
        return new(port is { } open ? new UriBuilder("http", "127.0.0.1", open, "v1").Uri : null, platform.InferenceApiKey, model,
            runtime.Chat.ContextTokens, platform.MaxOutputTokens, problem);
    }

    /// <summary>Owner-only; CSRF comes from the global UseHostCsrf middleware. Streams speak the AI SDK UI message protocol.</summary>
    public static void MapAssistant(this WebApplication app)
    {
        var group = app.MapGroup("/api/assistant").WithTags("Assistant")
            .RequireAuthorization("HostOwner").AddEndpointFilter<AssistantFilter>();
        group.MapPost("/chat", async (HttpContext context, AssistantRuns runs, CancellationToken ct) =>
        {
            // Owner API keys carry no username: the assistant's changes are then credited to "owner via assistant".
            var user = context.User.FindFirst("preferred_username")?.Value is { Length: > 0 and <= 64 } name ? name : null;
            var actor = (user is { Length: <= 50 } ? user : "owner") + " via assistant";
            var run = await runs.StartAsync(Owner(context), await Read<AssistantChatRequest>(context, ct), actor, user, ct);
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
        group.MapPost("/sessions/{id}/approvals/{approvalId}", async (string id, string approvalId, HttpContext context, AssistantRuns runs,
            CancellationToken ct) =>
        {
            var answer = await Read<AssistantApproval>(context, ct);
            if (answer.Reason?.Length > 500) throw new AssistantException(400, "invalid_reason", "Keep the reason under 500 characters.");
            return runs.Respond(Owner(context), id, approvalId, answer.Approved, string.IsNullOrWhiteSpace(answer.Reason) ? null : answer.Reason.Trim(),
                answer.Always)
                ? Results.NoContent()
                : throw new AssistantException(404, "approval_not_found", "This request was already answered, or its chat has moved on.");
        });
        group.MapPost("/sessions/{id}/answers/{toolCallId}", async (string id, string toolCallId, HttpContext context, AssistantRuns runs,
            CancellationToken ct) =>
        {
            var answer = await Read<AssistantAnswer>(context, ct);
            var text = answer.Answer?.Trim();
            var secret = answer.Secret?.Trim();
            if ((text is not null ? 1 : 0) + (secret is not null ? 1 : 0) + (answer.Declined ? 1 : 0) != 1 || toolCallId.Length > 128)
                throw new AssistantException(400, "invalid_request", "Send an answer, a secret or a decline.");
            if (text is { Length: 0 or > 2000 }) throw new AssistantException(400, "invalid_answer", "Answers are 1 to 2,000 characters.");
            if (secret is not null && (secret.Length is 0 or > 4096 || !StackCatalog.Unquoted(secret)))
                throw new AssistantException(400, "invalid_secret", "Lucia saves it unquoted: up to 4,096 letters, digits and . _ ~ + / = -");
            return runs.AnswerQuestion(Owner(context), id, toolCallId, text is not null ? "answer" : secret is not null ? "secret" : null, text ?? secret)
                ? Results.NoContent()
                : throw new AssistantException(404, "question_not_found", "This question was already answered, or its chat has moved on.");
        });
        group.MapGet("/settings", async (HttpContext context, AssistantBroker broker, AssistantTools tools, CancellationToken ct) =>
            Settings(await broker.SettingsAsync(Owner(context), ct), tools));
        group.MapPut("/settings", async (HttpContext context, AssistantBroker broker, AssistantTools tools, CancellationToken ct) =>
        {
            var changeTools = tools.Create("owner", null).Where(tool => tool.Tier == ToolTier.Change).Select(tool => tool.Name).ToArray();
            return Settings(await broker.SaveSettingsAsync(Owner(context), await Read<AssistantSettings>(context, ct), changeTools, ct), tools);
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

    /// <summary>The owner's settings with every tool, so the settings page can show what each tier covers.</summary>
    private static object Settings(AssistantSettings settings, AssistantTools tools) => new
    {
        autoTools = settings.AutoTools,
        hosts = settings.Hosts,
        tools = tools.Create("owner", null).Select(tool => new { name = tool.Name, tier = tool.Tier, description = tool.Description }),
    };

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

public sealed record AssistantApproval(bool Approved, string? Reason, bool Always = false);

/// <summary>The owner's reply to a question tool: exactly one of an answer, a secret for an app, or a decline.</summary>
public sealed record AssistantAnswer(string? Answer, string? Secret, bool Declined = false);

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
