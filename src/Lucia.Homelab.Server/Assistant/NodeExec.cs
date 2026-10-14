using System.Net;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>
/// Finds a managed server by the name, address or id a model gives and runs a command on it through its agent. Shared by the
/// assistant's tools and by skill scripts, so both hold a command to the same checks and time limits.
/// </summary>
public sealed partial class NodeExec(ManagedNodeEnrollment nodes, NodeRequests requests)
{
    public async Task<(Guid Id, string Hostname)> Node(string text, CancellationToken ct)
    {
        var wanted = (text ?? "").Trim().TrimEnd('.');
        var names = await nodes.Names(ct);
        if (Guid.TryParse(wanted, out var id))
            foreach (var item in names)
                if (item.NodeId == id) return item;
        if (IPAddress.TryParse(wanted, out var ip) && ip.ToString() == wanted)
            foreach (var address in await nodes.Addresses(ct))
                if (address.Address?.ToString() == wanted)
                    foreach (var item in names)
                        if (string.Equals(item.Hostname, address.Hostname, StringComparison.OrdinalIgnoreCase)) return item;
        foreach (var candidate in new[] { wanted, wanted.Split('.')[0] })
            foreach (var item in names)
                if (string.Equals(item.Hostname, candidate, StringComparison.OrdinalIgnoreCase)) return item;
        throw Unknown(text ?? "");
    }

    /// <summary>The first managed server whose agent can run commands, or null when none can.</summary>
    public async Task<(Guid Id, string Hostname)?> FirstWithExec(CancellationToken ct)
    {
        var facts = await nodes.Facts(ct);
        foreach (var item in await nodes.Names(ct))
            if (facts.FirstOrDefault(fact => fact.NodeId == item.NodeId)?.Status?.Features?.Contains("exec") == true) return item;
        return null;
    }

    /// <summary>Hostnames of the online managed servers whose agent reports <paramref name="feature"/>, such as "kvm".</summary>
    public async Task<string[]> HostsWith(string feature, CancellationToken ct) =>
        [.. (await nodes.Facts(ct)).Where(fact => fact.Online && fact.Status?.Features?.Contains(feature) == true)
            .Select(fact => fact.Hostname).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The online managed servers whose agent reports KVM (and Docker), by hostname.</summary>
    public async Task<(Guid Id, string Hostname)[]> KvmNodes(CancellationToken ct) =>
        [.. (await nodes.Facts(ct)).Where(fact => fact.Online && fact.Status?.Features?.Contains("kvm") == true)
            .OrderBy(fact => fact.Hostname, StringComparer.OrdinalIgnoreCase).Select(fact => (fact.NodeId, fact.Hostname))];

    /// <summary>Each server whose agent syncs skills, and whether it holds <paramref name="hash"/> of <paramref name="skill"/> (a 12-character prefix is what it reports).</summary>
    public async Task<(string Node, bool Synced)[]> SkillSyncState(string skill, string hash, CancellationToken ct) =>
        [.. (await nodes.Facts(ct)).Where(fact => fact.Status?.Features?.Contains("skills") == true).OrderBy(fact => fact.Hostname, StringComparer.OrdinalIgnoreCase)
            .Select(fact => (fact.Hostname, fact.Status!.Skills?.GetValueOrDefault(skill) is { } held && hash.StartsWith(held, StringComparison.Ordinal)))];

    /// <summary>Whether the node's agent downloads skills; an older agent has to be updated first.</summary>
    public async Task<bool> SyncsSkills(Guid id, CancellationToken ct) =>
        (await nodes.Facts(ct)).FirstOrDefault(item => item.NodeId == id)?.Status?.Features?.Contains("skills") == true;

    /// <summary>Asks the node to fetch its skill packages now; its answer says whether the pass finished.</summary>
    public Task<NodeRequestResult> SyncSkills(Guid id, CancellationToken ct) => requests.SyncSkills(id, ct);

    public async Task<(string Hostname, NodeRequestResult Result)> Exec(string node, string command, string interpreter, int seconds, CancellationToken ct)
    {
        var (id, hostname) = await Node(node, ct);
        return (hostname, await Exec(id, hostname, command, interpreter, seconds, ct));
    }

    public async Task<NodeRequestResult> Exec(Guid id, string hostname, string command, string interpreter, int seconds, CancellationToken ct)
    {
        if ((await nodes.Facts(ct)).FirstOrDefault(item => item.NodeId == id)?.Status?.Features?.Contains("exec") != true)
            throw new AssistantException(409, "exec_unsupported",
                $"{hostname}'s agent can't run commands yet. Update it with node_action update-agent, then try again.");
        var job = Guid.NewGuid();
        // The node stops the command at its limit; this only bounds the wait if the node goes quiet.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, NodeRequests.MaxCommandSeconds) + 60);
        try
        {
            var result = await requests.Exec(id, job, command, interpreter, seconds, ct);
            while (result is { Success: true, Running: true } && DateTimeOffset.UtcNow < deadline)
            {
                try { result = await requests.Follow(id, job, stop: false, ct); }
                catch (HardwareOnboardingException ex) when (ex.StatusCode == 504) { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            }
            if (!result.Success) throw new AssistantException(409, "command_refused", result.Message ?? $"{hostname} refused the command.");
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await requests.Follow(id, job, stop: true, brief.Token); }
            catch (Exception ex) when (ex is HardwareOnboardingException or OperationCanceledException) { }
            throw;
        }
    }

    internal static AssistantException Unknown(string text) =>
        new(404, "unknown_node", Spark().IsMatch(text) ? "The Spark isn't a managed server. Use get_spark_health, get_spark_updates, check_spark_updates and install_spark_updates for it."
            : $"No managed server is called \"{(text.Length <= 64 ? text : text[..64] + "…")}\". Use list_nodes to see them.");

    [GeneratedRegex(@"spark|dgx", RegexOptions.IgnoreCase)]
    private static partial Regex Spark();
}
