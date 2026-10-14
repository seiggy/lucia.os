using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Boot;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// The routes a node's agent uses to keep its skill packages current. They authenticate exactly like the stack sync: a signed
/// challenge from the node's key and certificate, bound to the request, from the LAN.
/// </summary>
public static partial class SkillNodeEndpoints
{
    public const string SandboxName = "_sandbox";

    public static void MapNodeSkills(this RouteGroupBuilder nodes)
    {
        nodes.MapPost("/skills", async (Guid id, HttpContext context, BootOptions options, DiscoveryChallenges challenges,
            ManagedNodeEnrollment enrollment, SkillPackages packages, CancellationToken ct) =>
        {
            await StackEndpoints.ReadNodeBody(id, "skills", context, options, challenges, enrollment, ct);
            return Results.Json(await packages.ManifestAsync(await ReportsKvm(enrollment, id, ct), ct), HardwareOnboardingJson.Options);
        });
        nodes.MapGet("/skills/{name}/{hash}", async (Guid id, string name, string hash, HttpContext context, BootOptions options,
            DiscoveryChallenges challenges, ManagedNodeEnrollment enrollment, SkillPackages packages, SkillStore store, CancellationToken ct) =>
        {
            if (!HashPattern().IsMatch(hash) || !(name == SandboxName || SkillStore.ValidName(name)))
                throw new DiscoveryProtocolException(400, "Name a skill and its content hash.");
            await StackEndpoints.VerifyProofHeader(id, "skill-download", $"skill:{name}@{hash}", context, options, challenges, enrollment, ct);
            (byte[] Bytes, string Sha256)? package;
            if (name == SandboxName)
            {
                // The runtime is for nodes that can run it; any other node never learns it exists.
                package = await ReportsKvm(enrollment, id, ct) ? await packages.GetSandboxAsync(hash, ct) : null;
                if (package is null) throw new DiscoveryProtocolException(404, "This controller has no such sandbox runtime for this node.");
            }
            else
            {
                package = await packages.GetAsync(name, hash, ct);
                if (package is null)
                    throw await store.FindAsync(name, ct) is { Enabled: true }
                        ? new DiscoveryProtocolException(409, "That skill has changed since the node's manifest.")
                        : new DiscoveryProtocolException(404, "No such skill is available to nodes.");
            }
            if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } rate) rate.MinDataRate = null;
            context.Response.ContentType = "application/gzip";
            context.Response.ContentLength = package.Value.Bytes.Length;
            context.Response.Headers["X-Lucia-Skill-Sha256"] = package.Value.Sha256;
            var bytes = package.Value.Bytes;
            for (var offset = 0; offset < bytes.Length; offset += 64 * 1024)
                await context.Response.Body.WriteAsync(bytes.AsMemory(offset, Math.Min(64 * 1024, bytes.Length - offset)), ct);
        });
    }

    private static async Task<bool> ReportsKvm(ManagedNodeEnrollment enrollment, Guid id, CancellationToken ct) =>
        (await enrollment.Facts(ct)).FirstOrDefault(fact => fact.NodeId == id)?.Status?.Features?.Contains("kvm") == true;

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    internal static partial Regex HashPattern();
}
