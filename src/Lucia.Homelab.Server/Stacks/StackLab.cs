using System.Text;
using Lucia.Homelab.Server.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>An installed app as the lab map draws it, with its server's last report of it.</summary>
/// <param name="UpdateCount">Image updates plus one when its catalog app has a newer version.</param>
internal sealed record LabStack(string Name, string Node, string Desired, NodeStackStatus? Status, string? Address, int UpdateCount,
    NodeContainer[] Containers);
internal sealed record LabNas(string Id, string Host, string[] Shares);
/// <param name="Reports">Each server's last fresh report, by hostname.</param>
internal sealed record LabStacks(LabStack[] Stacks, IReadOnlyDictionary<string, NodeStackReport> Reports, LabNas[] Nas);

public sealed partial class StackStore
{
    /// <summary>Every app, every server's fresh report and every NAS, for the lab map.</summary>
    internal async Task<LabStacks> LabStacks(CancellationToken ct)
    {
        var facts = await nodes.Facts(ct);
        StoredStack[] stacks;
        StoredNas[] servers;
        await _gate.WaitAsync(ct);
        try { (stacks, servers) = (ReadUnlocked(), ReadNasUnlocked()); }
        finally { _gate.Release(); }
        var reports = new Dictionary<string, NodeStackReport>(StringComparer.Ordinal);
        foreach (var node in facts)
            if (Fresh(node.NodeId) is { } entry) reports[node.Hostname] = entry.Report;
        return new([.. stacks.Select(stack =>
        {
            var report = reports.GetValueOrDefault(stack.Assigned);
            var behind = stack.Manifest.Template is { } template && StackCatalog.Apps.FirstOrDefault(app => app.Id == template.Id) is { } app
                && template.Version < app.Version;
            return new LabStack(stack.Name, stack.Assigned, stack.Desired, report?.Stacks.FirstOrDefault(item => item.Name == stack.Name),
                stack.Manifest.Address, Updates(stack).Length + (behind ? 1 : 0),
                report?.Containers.Where(container => container.Project == "lucia-" + stack.Name).ToArray() ?? []);
        })], reports, [.. servers.Select(nas => new LabNas(nas.Id, nas.Host, [.. nas.Shares.Select(share => share.Name)]))]);
    }

    /// <summary>
    /// The running Observability app's Grafana and its admin credentials, which the lab map uses to query Prometheus through
    /// Grafana's data source proxy: Prometheus itself isn't published. Null when no Observability app runs.
    /// </summary>
    internal async Task<(Uri Endpoint, string Authorization)?> GrafanaEndpoint(CancellationToken ct)
    {
        var stack = (await Read(ct)).FirstOrDefault(item => item.Manifest.Template?.Id == "observability" && item is { Desired: "Running", Move: null });
        if (stack?.Manifest.Routes?.FirstOrDefault(route => route.GrpcPort is null) is not { } route) return null;
        if (StackCatalog.ReadEnv(_protector.Unprotect(stack.ProtectedEnv)).GetValueOrDefault("GRAFANA_ADMIN_PASSWORD") is not { Length: > 0 } password)
            return null;
        var ns = (await ManagedNodeDns.ActiveNaming(domains, ct))?.Namespace;
        var address = stack.Manifest.Address ?? (await nodes.Addresses(ct)).FirstOrDefault(item => item.Hostname == stack.Assigned)?.Address;
        var endpoint = ns is not null ? new Uri($"https://{route.Host}.{ns}") : address is not null ? new UriBuilder("http", address, route.Port).Uri : null;
        return endpoint is null ? null : (endpoint, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{password}")));
    }

    /// <summary>Gives the running Observability app the lab map's extra collector config and SNMP credentials, or takes them away.</summary>
    internal async Task SetLabCollector(string lines, CancellationToken ct)
    {
        foreach (var stack in (await Read(ct)).Where(item => item.Manifest.Template?.Id == "observability"))
            await SetLucia(stack.Name, LabPrefix, lines, ct);
    }
}
