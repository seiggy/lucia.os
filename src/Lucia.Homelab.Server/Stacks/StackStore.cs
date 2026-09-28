using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Stacks;

/// <param name="Node">Pin to this node. Leave empty to let Lucia pick an eligible node once, when the stack is created.</param>
/// <param name="Require">Requirements the node must meet; see <see cref="StackRequirements"/>.</param>
public sealed record StackPlacement(string? Node = null, string[]? Require = null);
/// <summary>
/// Lucia's glue around a compose project. Every Lucia-specific behaviour belongs here, never only in UI state, so
/// templates, the onboarding agent and hand edits all produce the same document.
/// </summary>
public sealed record StackManifest(int SchemaVersion, StackPlacement Placement);
public sealed record SaveStackRequest(string Compose, string? Env, StackManifest Manifest, long? ExpectedRevision = null);
public sealed record MoveStackRequest(string Node);
/// <summary>A move in progress: the source stops the stack and streams its data to the target, which then starts it.</summary>
public sealed record StackMove(Guid Id, string From, string To, string Desired, DateTimeOffset StartedAt, string StartedBy);

public sealed record StackServiceStatus(string Service, string State, string? Health, string? Image, int? ExitCode);
/// <param name="Received">The move whose data this node has finished receiving for the stack.</param>
public sealed record NodeStackStatus(string Name, string State, long? AppliedRevision, string? Message, StackServiceStatus[] Services,
    Guid? Received = null);
public sealed record NodeContainer(string Id, string Name, string Image, string State, string? Status, string? Project,
    string? Service, string? Ports);
public sealed record NodeListener(string Protocol, string Address, int Port, string? Process = null, string? ContainerId = null);
/// <summary>What a node reports on each stack sync: its Lucia stacks, every container and every listening socket.</summary>
public sealed record NodeStackReport(NodeStackStatus[] Stacks, NodeContainer[] Containers, NodeListener[] Listeners);
/// <param name="Send">Stream the stack's data for this move once it's stopped.</param>
/// <param name="Receive">Receive the stack's data for this move before applying it.</param>
public sealed record NodeDesiredStack(string Name, long Revision, string Desired, long RestartCount, long PullCount,
    string Compose, string Env, Guid? Send = null, Guid? Receive = null);

/// <param name="Node">The node the stack runs on. Older state kept it only in the manifest's pin.</param>
internal sealed record StoredStack(string Name, string Compose, string ProtectedEnv, StackManifest Manifest, string Desired,
    long Revision, long RestartCount, long PullCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UpdatedBy,
    string? Node = null, StackMove? Move = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Assigned => Node ?? Manifest.Placement.Node!;
}
internal sealed record StackFile(int SchemaVersion, StoredStack[] Stacks);

/// <summary>
/// Compose stacks that Lucia runs on managed nodes. Definitions persist on disk with the environment file encrypted;
/// node reports stay in memory only and reappear within one sync after a restart.
/// </summary>
public sealed partial class StackStore(IOptions<HardwareOnboardingOptions> options, IDataProtectionProvider protection,
    ManagedNodeEnrollment nodes, StackTransfers transfers, TimeProvider time)
{
    public const int MaxStacks = 64, MaxStacksPerNode = 32, MaxComposeBytes = 128 * 1024, MaxEnvBytes = 32 * 1024;
    private const int MaxNodePayloadBytes = 1536 * 1024;
    private static readonly TimeSpan ReportFreshness = TimeSpan.FromSeconds(90);
    private readonly IDataProtector _protector = protection.CreateProtector("Lucia.Homelab.StackEnvironment.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, NodeStackReport Report)> _reports = new();
    private string FilePath => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "stacks", "stacks.json");

    public async Task<object> List(CancellationToken ct)
    {
        var names = await NodeNames(ct);
        var stacks = await Read(ct);
        return new { stacks = stacks.Select(stack => Summary(stack, names)).ToArray() };
    }

    public async Task<object> Get(string name, CancellationToken ct)
    {
        var names = await NodeNames(ct);
        var stack = Find(await Read(ct), name);
        return new { stack = Summary(stack, names), compose = stack.Compose, env = _protector.Unprotect(stack.ProtectedEnv), manifest = stack.Manifest };
    }

    /// <summary>Saves a server's GPU choices. Changing the CUDA line is refused while an app on (or moving to) the server requires the old one.</summary>
    public async Task<NodeGpuSettings> SaveNodeGpu(Guid id, SaveNodeGpuRequest request, CancellationToken ct)
    {
        var hostname = (await nodes.Facts(ct)).FirstOrDefault(item => item.NodeId == id)?.Hostname
            ?? throw new HardwareOnboardingException(404, "unknown_node", "That server isn't managed by Lucia.");
        var wanted = request.CudaLine is { } line ? $"cuda={line}" : null;
        var blocked = (await Read(ct)).Where(stack => stack.Assigned == hostname || stack.Move?.To == hostname)
            .FirstOrDefault(stack => (stack.Manifest.Placement.Require ?? []).Any(item => item.StartsWith("cuda", StringComparison.Ordinal) && item != wanted));
        if (blocked is not null)
            throw new HardwareOnboardingException(409, "cuda_line_in_use",
                $"{blocked.Name} requires {string.Join(", ", blocked.Manifest.Placement.Require!.Where(item => item.StartsWith("cuda", StringComparison.Ordinal)))}. Move it or change its requirement first.");
        return await nodes.SaveGpu(id, request, ct);
    }

    /// <summary>
    /// Creates or updates a stack. A new stack goes to its pinned node or, without a pin, to the eligible online node
    /// running the fewest stacks. An existing stack stays where it is: only a move changes its node, because only a
    /// move takes its data along.
    /// </summary>
    public async Task<object> Save(string name, SaveStackRequest request, string actor, CancellationToken ct)
    {
        ValidateName(name);
        var compose = Normalize(request.Compose, MaxComposeBytes, "compose", required: true);
        var env = Normalize(request.Env, MaxEnvBytes, "environment", required: false);
        ValidateEnv(env);
        if (request.Manifest is not { SchemaVersion: 1, Placement: { } placement })
            throw new HardwareOnboardingException(400, "invalid_manifest", "The stack manifest must be schema version 1 with a placement.");
        var require = StackRequirements.Normalize(placement.Require);
        var manifest = request.Manifest with { Placement = new(string.IsNullOrWhiteSpace(placement.Node) ? null : placement.Node.Trim(),
            require.Length == 0 ? null : require) };
        var facts = await nodes.Facts(ct);
        var names = facts.ToDictionary(item => item.NodeId, item => item.Hostname);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var existing = stacks.FirstOrDefault(stack => stack.Name == name);
            if (request.ExpectedRevision is { } expected && expected != (existing?.Revision ?? 0))
                throw new HardwareOnboardingException(409, "stack_changed", "Someone else changed this stack. Reload it before saving.");
            if (existing is null && stacks.Count >= MaxStacks)
                throw new HardwareOnboardingException(409, "too_many_stacks", $"Lucia runs up to {MaxStacks} stacks. Delete one first.");
            string node;
            if (existing is not null)
            {
                RequireSettled(existing);
                node = existing.Assigned;
                if (manifest.Placement.Node is { } pin && pin != node)
                    throw new HardwareOnboardingException(409, "use_move", $"To run this app on {pin}, move it. Saving doesn't copy its data.");
                if (!require.SequenceEqual(existing.Manifest.Placement.Require ?? [])
                    && facts.FirstOrDefault(item => item.Hostname == node) is var current
                    && StackRequirements.Unmet(require, current?.Status, current?.Gpu) is { } unmet)
                    throw new HardwareOnboardingException(409, "node_not_eligible",
                        $"{node} doesn't meet \"{unmet}\". Change the requirement, or move the app to a server that meets it first.");
            }
            else node = Place(manifest.Placement, require, facts, stacks);
            var now = time.GetUtcNow();
            var saved = new StoredStack(name, compose, _protector.Protect(env), manifest, existing?.Desired ?? "Running",
                (existing?.Revision ?? 0) + 1, existing?.RestartCount ?? 0, existing?.PullCount ?? 0, existing?.CreatedAt ?? now, now, actor, node);
            if (existing is not null) stacks.Remove(existing);
            stacks.Add(saved);
            RequireRoom(stacks, node);
            await Write(stacks, ct);
            return Summary(saved, names);
        }
        finally { _gate.Release(); }
    }

    private static string Place(StackPlacement placement, string[] require, ManagedNodeFacts[] facts, List<StoredStack> stacks)
    {
        if (placement.Node is { } pin)
        {
            var node = facts.FirstOrDefault(item => item.Hostname == pin)
                ?? throw new HardwareOnboardingException(400, "unknown_node", "Choose a managed node for this stack.");
            if (StackRequirements.Unmet(require, node.Status, node.Gpu) is { } unmet)
                throw new HardwareOnboardingException(409, "node_not_eligible", $"{pin} doesn't meet \"{unmet}\".");
            return pin;
        }
        return facts.Where(item => Ready(item) && StackRequirements.Unmet(require, item.Status, item.Gpu) is null)
            .OrderBy(item => stacks.Count(stack => stack.Assigned == item.Hostname)).ThenBy(item => item.Hostname, StringComparer.Ordinal)
            .FirstOrDefault()?.Hostname
            ?? throw new HardwareOnboardingException(409, "no_eligible_node", require.Length == 0
                ? "No server is online with Docker ready."
                : "No online server with Docker ready meets every requirement.");
    }

    private static bool Ready(ManagedNodeFacts node) => node is { Online: true, Status.Runtime.State: "Ready" };

    private static void RequireRoom(List<StoredStack> stacks, string node)
    {
        var onNode = stacks.Where(stack => stack.Assigned == node || stack.Move?.To == node).ToArray();
        if (onNode.Length > MaxStacksPerNode)
            throw new HardwareOnboardingException(409, "node_full", $"A node runs up to {MaxStacksPerNode} stacks. Place this one elsewhere.");
        if (onNode.Sum(stack => stack.Compose.Length + stack.ProtectedEnv.Length) > MaxNodePayloadBytes)
            throw new HardwareOnboardingException(409, "node_full", "This node's stacks are too large to deliver together. Place this one elsewhere.");
    }

    private static void RequireSettled(StoredStack stack)
    {
        if (stack.Move is not null)
            throw new HardwareOnboardingException(409, "stack_moving", "This app is moving. Wait for the move to finish, or cancel it.");
    }

    /// <summary>start, stop, restart, or update (pull newer images, then recreate what changed).</summary>
    public async Task<object> Act(string name, string action, string actor, CancellationToken ct)
    {
        var names = await NodeNames(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var stack = Find(stacks, name);
            if (action is "start" or "stop" or "restart" or "update") RequireSettled(stack);
            var changed = action switch
            {
                "start" => stack with { Desired = "Running" },
                "stop" => stack with { Desired = "Stopped" },
                "restart" when stack.Desired == "Running" => stack with { RestartCount = stack.RestartCount + 1 },
                "update" when stack.Desired == "Running" => stack with { PullCount = stack.PullCount + 1 },
                "restart" or "update" => throw new HardwareOnboardingException(409, "stack_stopped", "Start the stack first."),
                "cancel-move" when stack.Move is { } move => CancelMove(stack, move),
                "cancel-move" => throw new HardwareOnboardingException(409, "not_moving", "This app isn't moving."),
                _ => throw new HardwareOnboardingException(404, "unknown_action", "Use start, stop, restart, update or cancel-move."),
            } with { UpdatedAt = time.GetUtcNow(), UpdatedBy = actor };
            stacks[stacks.IndexOf(stack)] = changed;
            await Write(stacks, ct);
            return Summary(changed, names);
        }
        finally { _gate.Release(); }
    }

    private StoredStack CancelMove(StoredStack stack, StackMove move)
    {
        transfers.Forget(move.Id);
        var placement = stack.Manifest.Placement;
        return stack with
        {
            Move = null, Node = move.From,
            Manifest = stack.Manifest with { Placement = placement.Node == move.To ? placement with { Node = move.From } : placement },
        };
    }

    /// <summary>
    /// Starts moving a stack: its node stops it and streams its directory (volumes and relative bind mounts) to the target,
    /// which starts it with the same desired state. The old copy stays on the source until the owner removes it.
    /// </summary>
    public async Task<object> Move(string name, MoveStackRequest request, string actor, CancellationToken ct)
    {
        var facts = await nodes.Facts(ct);
        var names = facts.ToDictionary(item => item.NodeId, item => item.Hostname);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var stack = Find(stacks, name);
            RequireSettled(stack);
            var target = facts.FirstOrDefault(item => item.Hostname == request.Node)
                ?? throw new HardwareOnboardingException(400, "unknown_node", "Choose a managed server to move this app to.");
            if (target.Hostname == stack.Assigned)
                throw new HardwareOnboardingException(400, "same_node", $"This app already runs on {target.Hostname}.");
            if (facts.FirstOrDefault(item => item.Hostname == stack.Assigned) is not { Online: true })
                throw new HardwareOnboardingException(409, "source_offline", $"{stack.Assigned} must be online to hand over the app's data.");
            if (!Ready(target))
                throw new HardwareOnboardingException(409, "target_not_ready", $"{target.Hostname} must be online with Docker ready.");
            if (StackRequirements.Unmet(stack.Manifest.Placement.Require ?? [], target.Status, target.Gpu) is { } unmet)
                throw new HardwareOnboardingException(409, "node_not_eligible", $"{target.Hostname} doesn't meet \"{unmet}\".");
            var placement = stack.Manifest.Placement;
            var moving = stack with
            {
                Move = new(Guid.NewGuid(), stack.Assigned, target.Hostname, stack.Desired, time.GetUtcNow(), actor),
                Manifest = stack.Manifest with { Placement = placement.Node is null ? placement : placement with { Node = target.Hostname } },
                UpdatedAt = time.GetUtcNow(), UpdatedBy = actor,
            };
            stacks[stacks.IndexOf(stack)] = moving;
            RequireRoom(stacks, target.Hostname);
            await Write(stacks, ct);
            return Summary(moving, names);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Checks that this node is the sending or receiving side of an active move.</summary>
    public async Task RequireTransfer(Guid move, string hostname, bool sending, CancellationToken ct)
    {
        if (!(await Read(ct)).Any(stack => stack.Move is { } active && active.Id == move && (sending ? active.From : active.To) == hostname))
            throw new HardwareOnboardingException(404, "no_transfer", "No move is waiting for this node.");
    }

    /// <summary>Removes the definition. The node takes the containers down and keeps the stack's data directory.</summary>
    public async Task Delete(string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var stack = Find(stacks, name);
            RequireSettled(stack);
            stacks.Remove(stack);
            await Write(stacks, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records a node's report, finishes any move whose data it has received, and returns the stacks for it.</summary>
    public async Task<NodeDesiredStack[]> Sync(Guid nodeId, string hostname, NodeStackReport report, CancellationToken ct)
    {
        ValidateReport(report);
        _reports[nodeId] = (time.GetUtcNow(), report);
        var received = report.Stacks.Where(item => item.Received is not null).GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.First().Received);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked();
            var finished = false;
            for (var i = 0; i < stacks.Length; i++)
                if (stacks[i].Move is { } move && move.To == hostname && received.GetValueOrDefault(stacks[i].Name) == move.Id)
                {
                    stacks[i] = stacks[i] with { Node = move.To, Desired = move.Desired, Move = null, UpdatedAt = time.GetUtcNow() };
                    transfers.Forget(move.Id);
                    finished = true;
                }
            if (finished) await Write(stacks, ct);
            return stacks.Select(stack => Desired(stack, hostname)).OfType<NodeDesiredStack>().ToArray();
        }
        finally { _gate.Release(); }
    }

    private NodeDesiredStack? Desired(StoredStack stack, string hostname)
    {
        (string? Desired, Guid? Send, Guid? Receive) plan = stack.Move switch
        {
            { } move when move.From == hostname => ("Stopped", move.Id, null),
            { } move when move.To == hostname => ("Stopped", null, move.Id),
            null when stack.Assigned == hostname => (stack.Desired, null, null),
            _ => (null, null, null),
        };
        return plan.Desired is null ? null : new(stack.Name, stack.Revision, plan.Desired, stack.RestartCount, stack.PullCount,
            stack.Compose, _protector.Unprotect(stack.ProtectedEnv), plan.Send, plan.Receive);
    }

    /// <summary>Every container and listening socket a node reported in its last sync.</summary>
    public object Inventory(Guid nodeId)
    {
        if (Fresh(nodeId) is not { } entry)
            throw new HardwareOnboardingException(404, "no_node_report",
                "This node hasn't reported its containers recently. It may be offline or running an older agent.");
        return new { reportedAt = entry.At, containers = entry.Report.Containers, listeners = entry.Report.Listeners };
    }

    private (DateTimeOffset At, NodeStackReport Report)? Fresh(Guid nodeId) =>
        _reports.TryGetValue(nodeId, out var entry) && entry.At > time.GetUtcNow() - ReportFreshness ? entry : null;

    private object Summary(StoredStack stack, Dictionary<Guid, string> names)
    {
        var node = NodeOf(stack.Assigned, names);
        var entry = node is { } id ? Fresh(id) : null;
        var move = stack.Move is { } active ? new
        {
            active.From, active.To, active.StartedAt, active.StartedBy, progress = transfers.Status(active.Id),
            target = NodeOf(active.To, names) is { } to ? Fresh(to)?.Report.Stacks.FirstOrDefault(item => item.Name == stack.Name) : null,
        } : null;
        return new
        {
            stack.Name, node = stack.Assigned, nodeId = node, stack.Manifest.Placement, stack.Desired, stack.Revision, stack.CreatedAt,
            stack.UpdatedAt, stack.UpdatedBy, reportedAt = entry?.At,
            status = entry?.Report.Stacks.FirstOrDefault(item => item.Name == stack.Name),
            containers = entry?.Report.Containers.Where(container => container.Project == "lucia-" + stack.Name).ToArray(),
            move,
        };
    }

    private static Guid? NodeOf(string hostname, Dictionary<Guid, string> names) =>
        names.Where(item => item.Value == hostname).Select(item => (Guid?)item.Key).FirstOrDefault();

    private async Task<Dictionary<Guid, string>> NodeNames(CancellationToken ct) =>
        (await nodes.Names(ct)).ToDictionary(item => item.NodeId, item => item.Hostname);

    private static StoredStack Find(IEnumerable<StoredStack> stacks, string name) =>
        stacks.FirstOrDefault(stack => stack.Name == name)
        ?? throw new HardwareOnboardingException(404, "stack_not_found", "That stack no longer exists.");

    internal static void ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name))
            throw new HardwareOnboardingException(400, "invalid_stack_name",
                "Use up to 40 lowercase letters, digits and hyphens, starting with a letter and not ending with a hyphen.");
    }

    internal static string Normalize(string? value, int maximum, string label, bool required)
    {
        var text = (value ?? "").Replace("\r\n", "\n");
        if (System.Text.Encoding.UTF8.GetByteCount(text) > maximum)
            throw new HardwareOnboardingException(400, "stack_too_large", $"The {label} file is larger than {maximum / 1024} KiB.");
        if (text.Any(c => c is not ('\n' or '\t') && char.IsControl(c)))
            throw new HardwareOnboardingException(400, "invalid_stack_text", $"The {label} file contains control characters.");
        if (required && string.IsNullOrWhiteSpace(text))
            throw new HardwareOnboardingException(400, "compose_required", "Paste a compose file.");
        return text;
    }

    internal static void ValidateEnv(string env)
    {
        var line = 0;
        foreach (var entry in env.Split('\n'))
        {
            line++;
            var trimmed = entry.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !EnvPattern().IsMatch(trimmed))
                throw new HardwareOnboardingException(400, "invalid_environment", $"Environment line {line} must look like NAME=value.");
        }
    }

    internal static void ValidateReport(NodeStackReport report)
    {
        static void Text(string? value, int maximum, bool required = false) => HardwareInventoryValidation.Text(value, maximum, required);
        HardwareInventoryValidation.Require(report is { Stacks.Length: <= MaxStacks, Containers.Length: <= 256, Listeners.Length: <= 1024 },
            "The stack report is oversized.");
        foreach (var stack in report.Stacks)
        {
            HardwareInventoryValidation.Require(NamePattern().IsMatch(stack.Name ?? "") && stack.AppliedRevision is null or >= 0
                && stack.State is "Pending" or "Applying" or "Running" or "Degraded" or "Stopped" or "Failed" or "Removing" or "Sending" or "Receiving"
                && stack.Services is { Length: <= 64 }, "The stack report is invalid.");
            Text(stack.Message, 1024);
            foreach (var service in stack.Services)
            {
                Text(service.Service, 128, true);
                Text(service.State, 32, true);
                Text(service.Health, 32);
                Text(service.Image, 512);
            }
        }
        foreach (var container in report.Containers)
        {
            HardwareInventoryValidation.Require(ContainerIdPattern().IsMatch(container.Id ?? ""), "The container report is invalid.");
            Text(container.Name, 256, true);
            Text(container.Image, 512, true);
            Text(container.State, 32, true);
            Text(container.Status, 128);
            Text(container.Project, 128);
            Text(container.Service, 128);
            Text(container.Ports, 1024);
        }
        foreach (var listener in report.Listeners)
        {
            HardwareInventoryValidation.Require(listener.Protocol is "tcp" or "udp" && listener.Port is > 0 and <= 65535
                && IPAddress.TryParse(listener.Address, out _) && (listener.ContainerId is null || ContainerIdPattern().IsMatch(listener.ContainerId)),
                "The listener report is invalid.");
            Text(listener.Process, 64);
        }
    }

    private async Task<StoredStack[]> Read(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return ReadUnlocked(); }
        finally { _gate.Release(); }
    }

    private StoredStack[] ReadUnlocked()
    {
        DomainOnboardingStore.RejectLinks(FilePath);
        if (!File.Exists(FilePath)) return [];
        var file = JsonSerializer.Deserialize<StackFile>(CertbotFiles.ReadBounded(FilePath, 16 * 1024 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Stacks: not null }) throw new InvalidDataException("Stack state is invalid.");
        return file.Stacks;
    }

    private Task Write(IEnumerable<StoredStack> stacks, CancellationToken ct) =>
        DomainOnboardingStore.WriteJson(FilePath, new StackFile(1, stacks.OrderBy(stack => stack.Name, StringComparer.Ordinal).ToArray()), ct);

    [GeneratedRegex(@"\A[a-z](?:[a-z0-9-]{0,38}[a-z0-9])?\z")]
    internal static partial Regex NamePattern();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvPattern();
    [GeneratedRegex(@"\A[0-9a-f]{12,64}\z")]
    private static partial Regex ContainerIdPattern();
}
