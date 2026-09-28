using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Boot;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>A node request whose body is bound to the signed challenge by its SHA-256, so it can exceed the proof's size limit.</summary>
public sealed record NodeBoundSubmission(string CertificatePem, SignedDiscovery Proof, string Body);
public sealed record NodeBoundProof(Guid NodeId, string Purpose, string BodySha256);

public static partial class StackEndpoints
{
    public static void AddStacks(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<StackStore>();
        builder.Services.AddSingleton<StackTransfers>();
        builder.Services.AddSingleton<NodeRequests>();
    }

    public static void MapStacks(this WebApplication app)
    {
        var owner = app.MapGroup("/api/host/stacks").WithTags("Stacks")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        var catalog = app.MapGroup("/api/host/catalog").WithTags("Stacks")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        catalog.MapGet("", (StackStore stacks, CancellationToken ct) => stacks.Catalog(ct));
        catalog.MapPost("/{id}/preview", async (string id, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.Preview(id, await ReadOwnerBody<CatalogPreviewRequest>(context, 8 * 1024, "node and settings", ct), ct));
        owner.MapGet("", (StackStore stacks, CancellationToken ct) => stacks.List(ct));
        owner.MapGet("/{name}", (string name, StackStore stacks, CancellationToken ct) => stacks.Get(name, ct));
        owner.MapPut("/{name}", async (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.Save(name, await ReadOwnerBody<SaveStackRequest>(context, 256 * 1024, "compose, env, manifest and optionally expectedRevision", ct), Actor(context), ct));
        owner.MapPost("/{name}/move", async (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.Move(name, await ReadOwnerBody<MoveStackRequest>(context, 4 * 1024, "node", ct), Actor(context), ct));
        owner.MapPost("/{name}/backup", (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            stacks.RunBackup(name, Actor(context), ct));
        owner.MapPut("/{name}/backup", async (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.SaveStackBackup(name, await ReadOwnerBody<StackBackup>(context, 4 * 1024, "enabled and optionally mode", ct), Actor(context), ct));
        owner.MapPut("/{name}/address", async (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.SaveStackAddress(name, await ReadOwnerBody<StackAddressRequest>(context, 1024, "address", ct), Actor(context), ct));
        owner.MapPost("/{name}/restore", async (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.Restore(name, await ReadOwnerBody<RestoreStackRequest>(context, 4 * 1024, "snapshot", ct), Actor(context), ct));
        owner.MapPost("/{name}/{action}", (string name, string action, HttpContext context, StackStore stacks, CancellationToken ct) =>
            stacks.Act(name, action, Actor(context), ct));
        owner.MapDelete("/{name}", async (string name, StackStore stacks, CancellationToken ct) =>
        {
            await stacks.Delete(name, ct);
            return Results.NoContent();
        });

        var nas = app.MapGroup("/api/host/nas").WithTags("Stacks")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        nas.MapGet("", (StackStore stacks, CancellationToken ct) => stacks.NasList(ct));
        nas.MapPut("/{id}", async (string id, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.SaveNas(id, await ReadOwnerBody<SaveNasRequest>(context, 16 * 1024, "kind, host, shares and for SMB username and password", ct),
                Actor(context), ct));
        nas.MapDelete("/{id}", async (string id, StackStore stacks, CancellationToken ct) =>
        {
            await stacks.DeleteNas(id, ct);
            return Results.NoContent();
        });

        var backups = app.MapGroup("/api/host/backups").WithTags("Stacks")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        backups.MapGet("", (StackStore stacks, CancellationToken ct) => stacks.Backups(ct));
        backups.MapPut("/destination", async (HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.SaveBackupDestination(await ReadOwnerBody<SaveBackupDestinationRequest>(context, 4 * 1024,
                "nas, share and optionally folder and timeZone", ct), Actor(context), ct));
        backups.MapGet("/recovery", async (HttpContext context, StackStore stacks, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await stacks.BackupRecovery(ct);
        });

        var inventory = app.MapGroup("/api/host/nodes/{id:guid}").WithTags("Stacks")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        inventory.MapMethods("/ai/{**path}", ["GET", "POST", "DELETE"], ProxyLocalAi);
        inventory.MapGet("/ai-serving", (Guid id, StackStore stacks, CancellationToken ct) => stacks.Serving(id, ct));
        inventory.MapPost("/ai-serving", async (Guid id, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.Serve(id, await ReadOwnerBody<ServeModelRequest>(context, 4 * 1024, "modelId and optionally context", ct), Actor(context), ct));
        inventory.MapDelete("/ai-serving", (Guid id, HttpContext context, StackStore stacks, CancellationToken ct) =>
            stacks.Serve(id, new(null), Actor(context), ct));
        inventory.MapGet("/containers", (Guid id, StackStore stacks) => stacks.Inventory(id));
        inventory.MapPut("/gpu", async (Guid id, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.SaveNodeGpu(id, await ReadOwnerBody<SaveNodeGpuRequest>(context, 4 * 1024, "cudaLine", ct), ct));
        inventory.MapGet("/containers/{container}/logs", async (Guid id, string container, int? tail, NodeRequests requests,
            CancellationToken ct) =>
        {
            var result = await requests.Logs(id, container, tail ?? 200, ct);
            return result.Success
                ? Results.Json(new { container, logs = result.Output ?? "" }, HardwareOnboardingJson.Options)
                : throw new HardwareOnboardingException(404, "logs_unavailable", result.Message ?? "The node couldn't read that container's logs.");
        });

        var nodes = app.MapGroup("/api/nodes/{id:guid}").AllowAnonymous().AddEndpointFilter<BootProtocolErrorFilter>();
        nodes.MapPost("/stacks", async (Guid id, HttpContext context, BootOptions options, DiscoveryChallenges challenges,
            ManagedNodeEnrollment enrollment, StackStore stacks, CancellationToken ct) =>
        {
            var (hostname, body) = await ReadNodeBody(id, "stacks", context, options, challenges, enrollment, ct);
            var report = JsonSerializer.Deserialize<NodeStackReport>(body, HardwareOnboardingJson.Options)
                ?? throw new DiscoveryProtocolException(400, "A stack report is required.");
            return Results.Json(new
            {
                stacks = await stacks.Sync(id, hostname, report, ct), mounts = await stacks.DesiredMounts(ct),
                backup = await stacks.BackupRepository(ct),
            }, HardwareOnboardingJson.Options);
        });
        nodes.MapPost("/requests", async (Guid id, HttpContext context, BootOptions options, DiscoveryChallenges challenges,
            ManagedNodeEnrollment enrollment, NodeRequests requests, CancellationToken ct) =>
        {
            await ReadNodeBody(id, "requests", context, options, challenges, enrollment, ct);
            return Results.Json(new { requests = await requests.Wait(id, ct) }, HardwareOnboardingJson.Options);
        });
        nodes.MapPost("/requests/{requestId:guid}", async (Guid id, Guid requestId, HttpContext context, BootOptions options,
            DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, NodeRequests requests, CancellationToken ct) =>
        {
            var (_, body) = await ReadNodeBody(id, "request-result", context, options, challenges, enrollment, ct);
            var result = JsonSerializer.Deserialize<NodeRequestResult>(body, HardwareOnboardingJson.Options)
                ?? throw new DiscoveryProtocolException(400, "A request result is required.");
            if (result.RequestId != requestId) throw new DiscoveryProtocolException(400, "The result belongs to another request.");
            requests.Complete(id, result);
            return Results.Ok(new { accepted = true });
        });
        nodes.MapGet("/transfers/{move:guid}", async (Guid id, Guid move, HttpContext context, BootOptions options,
            DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, StackStore stacks, StackTransfers transfers,
            CancellationToken ct) =>
        {
            await VerifyTransfer(id, move, "transfer-receive", context, options, challenges, enrollment, stacks, ct);
            context.Response.ContentType = "application/x-tar";
            await context.Response.StartAsync(ct);
            if (!await transfers.Receive(move, context.Response.Body, ct)) context.Abort();
        });
        nodes.MapPost("/transfers/{move:guid}", async (Guid id, Guid move, HttpContext context, BootOptions options,
            DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, StackStore stacks, StackTransfers transfers,
            CancellationToken ct) =>
        {
            await VerifyTransfer(id, move, "transfer-send", context, options, challenges, enrollment, stacks, ct);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
            long? total = long.TryParse(context.Request.Headers["X-Lucia-Transfer-Bytes"], out var size) && size >= 0 ? size : null;
            try { return Results.Ok(new { bytes = await transfers.Send(move, context.Request.Body, total, ct) }); }
            catch (TimeoutException ex) { throw new DiscoveryProtocolException(409, ex.Message); }
        });
    }

    private static async Task<(string Hostname, string Body)> ReadNodeBody(Guid id, string purpose, HttpContext context,
        BootOptions options, DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, CancellationToken ct)
    {
        var address = HardwareBootExtensions.RequireNetwork(context, options);
        var input = await HardwareBootExtensions.ReadRequest<NodeBoundSubmission>(context, ct);
        return (await VerifyNode(id, purpose, input, address, challenges, enrollment, ct), input.Body);
    }

    /// <summary>Streams can't carry the proof in their body, so transfers send the same signed submission in a header.</summary>
    private static async Task<string> VerifyTransfer(Guid id, Guid move, string purpose, HttpContext context, BootOptions options,
        DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, StackStore stacks, CancellationToken ct)
    {
        var address = HardwareBootExtensions.RequireNetwork(context, options);
        var header = context.Request.Headers["X-Lucia-Node-Proof"];
        NodeBoundSubmission? input;
        try
        {
            input = header.Count == 1 && header[0] is { Length: > 0 and <= 16384 } text
                ? JsonSerializer.Deserialize<NodeBoundSubmission>(Convert.FromBase64String(text), HardwareOnboardingJson.Options) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { input = null; }
        if (input is null || input.Body != "move:" + move.ToString("D"))
            throw new DiscoveryProtocolException(400, "A signed transfer proof for this move is required.");
        var hostname = await VerifyNode(id, purpose, input, address, challenges, enrollment, ct);
        try { await stacks.RequireTransfer(move, hostname, purpose == "transfer-send", ct); }
        catch (HardwareOnboardingException) { throw new DiscoveryProtocolException(404, "No move is waiting for this node."); }
        if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } request) request.MinDataRate = null;
        if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } response) response.MinDataRate = null;
        return hostname;
    }

    private static async Task<string> VerifyNode(Guid id, string purpose, NodeBoundSubmission input, System.Net.IPAddress address,
        DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, CancellationToken ct)
    {
        var proof = challenges.Verify(input.Proof, address);
        var bound = JsonSerializer.Deserialize<NodeBoundProof>(proof.ReportJson, HardwareOnboardingJson.Options);
        if (bound is null || bound.NodeId != id || bound.Purpose != purpose || input.Body is null
            || !bound.BodySha256.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Body))), StringComparison.Ordinal))
            throw new DiscoveryProtocolException(400, "The signed request doesn't match its body.");
        return await enrollment.Authenticate(id, input.CertificatePem, proof.PublicKeyFingerprint, ct);
    }

    private static readonly HttpClient LocalAiClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Forwards model management to a server's Local AI with its management key, so the portal manages every server's
    /// models the way it manages the Spark's, and the key never leaves the host.
    /// </summary>
    private static async Task<IResult> ProxyLocalAi(Guid id, string path, HttpContext context, StackStore stacks, CancellationToken ct)
    {
        if (!LocalAiPath().IsMatch(path))
            throw new HardwareOnboardingException(404, "unknown_path", "Local AI has no such endpoint.");
        var (baseUri, key) = await stacks.LocalAi(id, ct);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(baseUri, "/api/worker/" + path + context.Request.QueryString));
        request.Headers.Authorization = new("Bearer", key);
        if (HttpMethods.IsPost(context.Request.Method))
        {
            if (context.Request.ContentLength > 64 * 1024)
                throw new HardwareOnboardingException(400, "invalid_body", "Send JSON of at most 64 KiB.");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = 64 * 1024;
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, ct);
            request.Content = new ByteArrayContent(body.ToArray());
            request.Content.Headers.ContentType = new("application/json");
        }
        try
        {
            using var response = await LocalAiClient.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return Results.Text(text, response.Content.Headers.ContentType?.MediaType ?? "application/json", Encoding.UTF8, (int)response.StatusCode);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new HardwareOnboardingException(502, "local_ai_unreachable",
                "Local AI on that server isn't answering. It may still be starting; check its containers in Apps.");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A[A-Za-z0-9-]{1,64}(?:/[A-Za-z0-9-]{1,64}){0,3}\z")]
    private static partial System.Text.RegularExpressions.Regex LocalAiPath();

    private static async Task<T> ReadOwnerBody<T>(HttpContext context, int limit, string fields, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType() || context.Request.ContentLength > limit)
            throw new HardwareOnboardingException(400, "invalid_body", $"Send JSON of at most {limit / 1024} KiB.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = limit + 1;
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(HardwareOnboardingJson.Options, ct)
                ?? throw new HardwareOnboardingException(400, "invalid_body", "A JSON object is required.");
        }
        catch (JsonException)
        {
            throw new HardwareOnboardingException(400, "invalid_body", $"Send {fields}, with no other fields.");
        }
    }

    private static string Actor(HttpContext context) =>
        context.User.FindFirst("preferred_username")?.Value is { Length: > 0 and <= 64 } name ? name : "owner API key";
}
