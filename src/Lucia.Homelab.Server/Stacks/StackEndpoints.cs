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

public static class StackEndpoints
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
        owner.MapGet("", (StackStore stacks, CancellationToken ct) => stacks.List(ct));
        owner.MapGet("/{name}", (string name, StackStore stacks, CancellationToken ct) => stacks.Get(name, ct));
        owner.MapPut("/{name}", async (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.Save(name, await ReadOwnerBody<SaveStackRequest>(context, 256 * 1024, "compose, env, manifest and optionally expectedRevision", ct), Actor(context), ct));
        owner.MapPost("/{name}/move", async (string name, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.Move(name, await ReadOwnerBody<MoveStackRequest>(context, 4 * 1024, "node", ct), Actor(context), ct));
        owner.MapPost("/{name}/{action}", (string name, string action, HttpContext context, StackStore stacks, CancellationToken ct) =>
            stacks.Act(name, action, Actor(context), ct));
        owner.MapDelete("/{name}", async (string name, StackStore stacks, CancellationToken ct) =>
        {
            await stacks.Delete(name, ct);
            return Results.NoContent();
        });

        var inventory = app.MapGroup("/api/host/nodes/{id:guid}").WithTags("Stacks")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        inventory.MapGet("/containers", (Guid id, StackStore stacks) => stacks.Inventory(id));
        inventory.MapPut("/gpu", async (Guid id, HttpContext context, StackStore stacks, CancellationToken ct) =>
            await stacks.SaveNodeGpu(id, await ReadOwnerBody<SaveNodeGpuRequest>(context, 4 * 1024, "cudaLine, inference and inferenceGpus", ct), ct));
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
            return Results.Json(new { stacks = await stacks.Sync(id, hostname, report, ct) }, HardwareOnboardingJson.Options);
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
