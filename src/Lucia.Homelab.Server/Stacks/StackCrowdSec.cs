using Lucia.Homelab.Server.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>The installed CrowdSec app as Lucia's CrowdSec page and the gateway see it.</summary>
/// <param name="Address">The server address the gateway and the collector reach it on, which its certificate names.</param>
/// <param name="Settled">Neither moving nor being restored.</param>
/// <param name="Engine">The engine's container in the server's last fresh report.</param>
internal sealed record CrowdSecInstall(string Name, string Node, Guid? NodeId, string? Address, string Desired, bool Settled,
    IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, string> Env, NodeStackStatus? Status, NodeContainer? Engine,
    DateTimeOffset? ReportedAt, string? Image)
{
    public override string ToString() => $"CrowdSecInstall({Name} on {Node})";
}

public sealed partial class StackStore
{
    /// <summary>The CrowdSec app, or null when none is installed.</summary>
    internal async Task<CrowdSecInstall?> CrowdSec(CancellationToken ct)
    {
        if ((await Read(ct)).FirstOrDefault(item => item.Manifest.Template?.Id == CrowdSecApp.AppId) is not { Manifest.Template: { } template } stack)
            return null;
        var facts = (await nodes.Facts(ct)).FirstOrDefault(item => item.Hostname == stack.Assigned);
        var address = stack.Manifest.Address ?? (await nodes.Addresses(ct)).FirstOrDefault(item => item.Hostname == stack.Assigned)?.Address;
        var entry = facts is null ? null : Fresh(facts.NodeId);
        var image = stack.Compose.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("image: crowdsecurity/crowdsec:", StringComparison.Ordinal))?
            ["image: ".Length..];
        return new(stack.Name, stack.Assigned, facts?.NodeId, address, stack.Desired, stack is { Move: null, Restore: null },
            StackCatalog.Settings(StackCatalog.Find(CrowdSecApp.AppId), template.Settings), StackCatalog.ReadEnv(_protector.Unprotect(stack.ProtectedEnv)),
            entry?.Report.Stacks.FirstOrDefault(item => item.Name == stack.Name),
            entry?.Report.Containers.FirstOrDefault(container => container.Project == "lucia-" + stack.Name && container.Service == "crowdsec"),
            entry?.At, image);
    }

    /// <summary>The newest catalog version of this app among the installed stacks, or null when none runs it.</summary>
    internal async Task<int?> TemplateVersion(string app, CancellationToken ct) =>
        (await Read(ct)).Select(stack => stack.Manifest.Template).Where(template => template?.Id == app).Max(template => (int?)template!.Version);

    /// <summary>Whether any stack runs this catalog app, in any state.</summary>
    internal async Task<bool> Installed(string app, CancellationToken ct) => (await Read(ct)).Any(stack => stack.Manifest.Template?.Id == app);

    /// <summary>Replaces the CrowdSec app's own Lucia variables: its certificate, the safe harbor and the Console enroll key.</summary>
    internal Task SetCrowdSec(string name, string lines, CancellationToken ct) => SetLucia(name, CrowdSecPrefix, lines, ct);

    /// <summary>Gives every Observability app the collector config that forwards the gateway's access log to CrowdSec, or takes it away.</summary>
    internal async Task SetCrowdSecCollector(string lines, CancellationToken ct)
    {
        foreach (var stack in (await Read(ct)).Where(item => item.Manifest.Template?.Id == ObservabilityApp.AppId))
            await SetLucia(stack.Name, CrowdSecPrefix, lines, ct);
    }
}
