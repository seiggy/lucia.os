using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>The owner's skills API under /api/assistant/skills. It sits in the assistant's owner-only group, so CSRF and sign-in apply as they do there.</summary>
public static class SkillEndpoints
{
    private const int MaxBody = 1536 * 1024;

    public static void AddSkills(this IServiceCollection services)
    {
        services.AddSingleton<NodeExec>();
        services.AddSingleton(provider => new SkillStore(provider.GetRequiredService<IOptions<AssistantOptions>>(),
            provider.GetRequiredService<ILogger<SkillStore>>()));
        services.AddSingleton<NodeSkillRunner>();
        services.AddSingleton<SparkSkillRunner>();
        services.AddSingleton<SkillSandboxRuntime>();
        services.AddSingleton<SandboxSkillRunner>();
        services.AddSingleton<SkillPackages>();
        services.AddSingleton<SkillScriptRouter>();
        services.AddSingleton<SkillChecker>();
        services.AddSingleton<AssistantSkills>();
    }

    public static void MapSkills(this RouteGroupBuilder assistant)
    {
        var group = assistant.MapGroup("/skills");
        group.MapGet("", async (SkillStore store, NodeExec exec, CancellationToken ct) =>
            new { skills = await store.ListAsync(ct), kvmNodes = await exec.HostsWith("kvm", ct) });
        group.MapPost("", async (HttpContext context, SkillStore store, CancellationToken ct) =>
            Results.Json(await store.CreateAsync(await Read<SkillCreateRequest>(context, ct), ct), statusCode: 201));
        group.MapPost("/import", async (HttpContext context, SkillStore store, CancellationToken ct) =>
        {
            if (context.Request.ContentLength > SkillStore.MaxZipBytes + 64 * 1024)
                throw new AssistantException(413, "zip_too_large", "Skill zips are at most 5 MiB.");
            if (!context.Request.HasFormContentType) throw new AssistantException(415, "form_required", "Send the zip as multipart form data in a file field.");
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files["file"] ?? throw new AssistantException(400, "invalid_request", "Send the zip in a form field named file.");
            if (file.Length > SkillStore.MaxZipBytes) throw new AssistantException(413, "zip_too_large", "Skill zips are at most 5 MiB.");
            await using var zip = file.OpenReadStream();
            return Results.Json(await store.ImportZipAsync(zip, ct), statusCode: 201);
        });
        group.MapPost("/import-github", async (HttpContext context, SkillStore store, AssistantBroker broker, CancellationToken ct) =>
        {
            var request = await Read<SkillGitHubRequest>(context, ct);
            var sites = (await broker.SettingsAsync(AssistantEndpoints.Owner(context), ct)).Hosts;
            return Results.Json(await store.ImportGitHubAsync(request.Url, sites, ct), statusCode: 201);
        });
        group.MapGet("/{name}", async (string name, SkillStore store, NodeExec exec, CancellationToken ct) =>
        {
            var detail = await store.GetAsync(name, ct);
            SkillNodeState[] nodes = !detail.BuiltIn && detail.Execution is SkillExecution.Node or SkillExecution.Sandbox
                ? [.. (await exec.SkillSyncState(name, detail.Hash, ct)).Select(item => new SkillNodeState(item.Node, item.Synced))] : [];
            return detail with { Nodes = nodes };
        });
        group.MapPut("/{name}", async (string name, HttpContext context, SkillStore store, CancellationToken ct) =>
            await store.UpdateAsync(name, await Read<SkillUpdateRequest>(context, ct), ct));
        group.MapPost("/{name}/enabled", async (string name, HttpContext context, SkillStore store, CancellationToken ct) =>
            await store.SetEnabledAsync(name, (await Read<SkillEnabledRequest>(context, ct)).Enabled, ct));
        group.MapDelete("/{name}", async (string name, SkillStore store, CancellationToken ct) =>
        {
            await store.DeleteAsync(name, ct);
            return Results.NoContent();
        });
        group.MapGet("/{name}/file", (string name, string? path, SkillStore store) => store.ReadFile(name, path));
        group.MapPut("/{name}/file", async (string name, string? path, HttpContext context, SkillStore store, CancellationToken ct) =>
            await store.WriteFileAsync(name, path, (await Read<SkillFileRequest>(context, ct)).Content, ct));
        group.MapDelete("/{name}/file", async (string name, string? path, SkillStore store, CancellationToken ct) =>
        {
            await store.DeleteFileAsync(name, path, ct);
            return Results.NoContent();
        });
        group.MapPost("/{name}/rename", async (string name, HttpContext context, SkillStore store, CancellationToken ct) =>
        {
            var request = await Read<SkillRenameRequest>(context, ct);
            return await store.RenameFileAsync(name, request.From, request.To, ct);
        });
        group.MapPost("/{name}/check", async (string name, HttpContext context, SkillStore store, SkillChecker checker, CancellationToken ct) =>
        {
            var request = await Read<SkillCheckRequest>(context, ct);
            if (await store.FindAsync(name, ct) is null) throw new AssistantException(404, "skill_not_found", "That skill doesn't exist.");
            var path = SkillStore.Normalize(request.Path);
            return await checker.CheckAsync(path, request.Content ?? "", request.Node, ct, name);
        });
        group.MapPost("/{name}/duplicate", async (string name, HttpContext context, SkillStore store, CancellationToken ct) =>
            Results.Json(await store.DuplicateAsync(name, (await Read<SkillDuplicateRequest>(context, ct)).Name, ct), statusCode: 201));
        group.MapGet("/{name}/export", async (string name, SkillStore store, CancellationToken ct) =>
        {
            using var zip = new MemoryStream();
            await store.ExportAsync(name, zip, ct);
            return Results.File(zip.ToArray(), "application/zip", name + ".zip");
        });
    }

    private static async Task<T> Read<T>(HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength > MaxBody) throw new AssistantException(413, "request_too_large", "The request is too large.");
        if (!context.Request.HasJsonContentType()) throw new AssistantException(415, "json_required", "Send a JSON object.");
        return await context.Request.ReadFromJsonAsync<T>(AssistantStream.Json, ct)
            ?? throw new AssistantException(400, "invalid_request", "The request body is invalid.");
    }
}
