using System.Collections.Concurrent;
using System.Globalization;
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
/// <param name="Address">
/// An IPv4 address of its own on your network, taken by the server it runs on. The compose binds ports to <c>${LUCIA_ADDRESS}</c>.
/// Saving without one keeps the current address; an empty one removes it.
/// </param>
/// <param name="Routes">Web addresses under the active domain; see <see cref="StackRoute"/>. Catalog apps set their own; saving a custom app
/// without them keeps the current ones, and an empty list removes them.</param>
public sealed record StackManifest(int SchemaVersion, StackPlacement Placement, StackTemplate? Template = null, StackBackup? Backup = null,
    string? Address = null, StackRoute[]? Routes = null);
/// <param name="Compose">Ignored for catalog apps: Lucia renders their compose and environment.</param>
public sealed record SaveStackRequest(string? Compose, string? Env, StackManifest Manifest, long? ExpectedRevision = null);
public sealed record CatalogPreviewRequest(string Node, Dictionary<string, string>? Settings);
public sealed record MoveStackRequest(string Node);
/// <param name="ModelId">A downloaded safetensors model for vLLM to serve, or null to serve nothing.</param>
/// <param name="Context"><c>auto</c> (the most the GPUs fit) or a token count.</param>
public sealed record ServeModelRequest(Guid? ModelId, string? Context = null);
/// <summary>A move in progress: the source stops the stack and streams its data to the target, which then starts it.</summary>
public sealed record StackMove(Guid Id, string From, string To, string Desired, DateTimeOffset StartedAt, string StartedBy);

public sealed record StackServiceStatus(string Service, string State, string? Health, string? Image, int? ExitCode);
/// <param name="Received">The move whose data this node has finished receiving for the stack.</param>
/// <param name="Restored">The restore this node has finished for the stack.</param>
public sealed record NodeStackStatus(string Name, string State, long? AppliedRevision, string? Message, StackServiceStatus[] Services,
    Guid? Received = null, NodeBackupStatus? Backup = null, Guid? Restored = null);
public sealed record NodeContainer(string Id, string Name, string Image, string State, string? Status, string? Project,
    string? Service, string? Ports);
public sealed record NodeListener(string Protocol, string Address, int Port, string? Process = null, string? ContainerId = null);
/// <summary>What a node reports on each stack sync: its Lucia stacks, every container, every listening socket and its NAS mounts.</summary>
/// <param name="Snapshots">Every Lucia snapshot in the backup repository, as this node last listed it.</param>
/// <param name="RepositoryError">Why this node couldn't list the backup repository.</param>
public sealed record NodeStackReport(NodeStackStatus[] Stacks, NodeContainer[] Containers, NodeListener[] Listeners,
    NodeMountStatus[]? Mounts = null, NodeSnapshot[]? Snapshots = null, string? RepositoryError = null, NodeAddressStatus[]? Addresses = null);
/// <param name="Send">Stream the stack's data for this move once it's stopped.</param>
/// <param name="Receive">Receive the stack's data for this move before applying it.</param>
/// <param name="Backup">Back the stack up once for this request.</param>
/// <param name="Restore">Replace the stack's data with this snapshot before applying it.</param>
/// <param name="Address">Take this address while the stack runs. Sent only to the node the stack has settled on.</param>
public sealed record NodeDesiredStack(string Name, long Revision, string Desired, long RestartCount, long PullCount,
    string Compose, string Env, Guid? Send = null, Guid? Receive = null, NodeBackupRequest? Backup = null, NodeRestoreRequest? Restore = null,
    string? Address = null);

/// <param name="Node">The node the stack runs on. Older state kept it only in the manifest's pin.</param>
internal sealed record StoredStack(string Name, string Compose, string ProtectedEnv, StackManifest Manifest, string Desired,
    long Revision, long RestartCount, long PullCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UpdatedBy,
    string? Node = null, StackMove? Move = null, StackRestore? Restore = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Assigned => Node ?? Manifest.Placement.Node!;
}
internal sealed record StackFile(int SchemaVersion, StoredStack[] Stacks);

/// <summary>
/// Compose stacks that Lucia runs on managed nodes. Definitions persist on disk with the environment file encrypted;
/// node reports stay in memory only and reappear within one sync after a restart.
/// </summary>
public sealed partial class StackStore(IOptions<HardwareOnboardingOptions> options, IDataProtectionProvider protection,
    ManagedNodeEnrollment nodes, StackTransfers transfers, DomainOnboardingStore domains, TimeProvider time)
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
        var address = (await nodes.Addresses(ct)).FirstOrDefault(item => item.Hostname == stack.Assigned)?.Address;
        var ns = (await ManagedNodeDns.ActiveNaming(domains, ct))?.Namespace;
        var urls = ns is null ? null : (stack.Manifest.Routes ?? []).ToDictionary(route => route.Host, route => $"https://{route.Host}.{ns}");
        var publicZone = await ManagedNodeDns.PublicZone(domains, ct);
        var publicUrls = publicZone is null ? null : (stack.Manifest.Routes ?? []).Where(route => route.Public is not null)
            .ToDictionary(route => route.Host, route => $"https://{route.Public}.{publicZone}");
        return new { stack = Summary(stack, names), compose = stack.Compose, env = _protector.Unprotect(stack.ProtectedEnv), manifest = stack.Manifest, address, urls,
            zone = (await ManagedNodeDns.ActiveNaming(domains, ct))?.Domain, publicAccess = publicZone is not null, publicUrls };
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
        if (request.Manifest is not { SchemaVersion: 1, Placement: { } placement })
            throw new HardwareOnboardingException(400, "invalid_manifest", "The stack manifest must be schema version 1 with a placement.");
        var app = request.Manifest.Template is { } template ? StackCatalog.Find(template.Id) : null;
        var settings = app is null ? null : StackCatalog.Settings(app, request.Manifest.Template!.Settings);
        var compose = app is null ? Normalize(request.Compose, MaxComposeBytes, "compose", required: true) : "";
        var env = app is null ? Normalize(request.Env, MaxEnvBytes, "environment", required: false) : "";
        ValidateEnv(env);
        var require = StackRequirements.Normalize(app is null
            ? [.. (placement.Require ?? []).Where(item => !item.StartsWith("nas=", StringComparison.Ordinal)),
                .. NasRequirements(compose, await KnownShares(ct))] : app.Require);
        var manifest = new StackManifest(1, new(string.IsNullOrWhiteSpace(placement.Node) ? null : placement.Node.Trim(),
            require.Length == 0 ? null : require), app is null ? null : new(app.Id, app.Version, settings));
        var facts = await nodes.Facts(ct);
        var names = facts.ToDictionary(item => item.NodeId, item => item.Hostname);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var existing = stacks.FirstOrDefault(stack => stack.Name == name);
            if (request.ExpectedRevision is { } expected && expected != (existing?.Revision ?? 0))
                throw new HardwareOnboardingException(409, "stack_changed", "Someone else changed this stack. Reload it before saving.");
            manifest = manifest with
            {
                Backup = request.Manifest.Backup is { } backup ? ValidBackup(backup) : existing?.Manifest.Backup,
                Address = request.Manifest.Address is null ? existing?.Manifest.Address : await ValidAddress(request.Manifest.Address, name, stacks, ct),
                Routes = app is not null ? null
                    : request.Manifest.Routes is null ? existing?.Manifest.Routes : await ValidRoutes(request.Manifest.Routes, name, stacks, ct),
            };
            if (app is { UsesAddress: true } && manifest.Address is null)
                throw new HardwareOnboardingException(400, "address_required", $"{app.Name} needs an address of its own on your network.");
            if (existing is null && stacks.Count >= MaxStacks)
                throw new HardwareOnboardingException(409, "too_many_stacks", $"Lucia runs up to {MaxStacks} stacks. Delete one first.");
            string node;
            if (existing is not null)
            {
                RequireSettled(existing);
                node = existing.Assigned;
                if (manifest.Placement.Node is { } pin && pin != node)
                    throw new HardwareOnboardingException(409, "use_move", $"To run this app on {pin}, move it. Saving doesn't copy its data.");
                if (app is not null && existing.Manifest.Template?.Id != app.Id)
                    throw new HardwareOnboardingException(409, "stack_exists", existing.Manifest.Template is null
                        ? $"{name} is a custom app. Choose another name for the catalog app."
                        : $"{name} is a different catalog app. Choose another name.");
            }
            else node = Place(manifest.Placement, require, facts, stacks, Mounted);
            if (app is not null)
            {
                var target = facts.FirstOrDefault(item => item.Hostname == node)
                    ?? throw new HardwareOnboardingException(409, "unknown_node", $"{node} isn't managed by Lucia anymore.");
                if (app.ServerBound && stacks.FirstOrDefault(stack => stack.Name != name && stack.Manifest.Template?.Id == app.Id
                    && (stack.Assigned == node || stack.Move?.To == node)) is { } other)
                    throw new HardwareOnboardingException(409, "already_installed", $"{node} already runs {app.Name} as {other.Name}.");
                var previous = existing is null ? "" : _protector.Unprotect(existing.ProtectedEnv);
                var output = app.Render(settings!, target, StackCatalog.KeepSecrets(app, settings!, StackCatalog.ReadEnv(previous)));
                (compose, env, require) = (output.Compose, output.Env + LuciaLines(previous), StackRequirements.Normalize(output.Require));
                manifest = manifest with { Placement = manifest.Placement with { Require = require },
                    Routes = await ValidRoutes(output.Routes?.Select(route => route with
                    {
                        Public = (request.Manifest.Routes ?? existing?.Manifest.Routes)?.FirstOrDefault(item => item.Host == route.Host)?.Public,
                    }).ToArray(), name, stacks, ct) };
            }
            if (existing is not null && !require.SequenceEqual(existing.Manifest.Placement.Require ?? [])
                && facts.FirstOrDefault(item => item.Hostname == node) is var current
                && StackRequirements.Unmet(require, current?.Status, current?.Gpu, Mounted(current?.NodeId)) is { } unmet)
                throw new HardwareOnboardingException(409, "node_not_eligible",
                    $"{node} doesn't meet \"{unmet}\". Change the requirement, or move the app to a server that meets it first.");
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

    private static string Place(StackPlacement placement, string[] require, ManagedNodeFacts[] facts, List<StoredStack> stacks,
        Func<Guid?, IReadOnlySet<string>> mounted)
    {
        if (placement.Node is { } pin)
        {
            var node = facts.FirstOrDefault(item => item.Hostname == pin)
                ?? throw new HardwareOnboardingException(400, "unknown_node", "Choose a managed node for this stack.");
            if (StackRequirements.Unmet(require, node.Status, node.Gpu, mounted(node.NodeId)) is { } unmet)
                throw new HardwareOnboardingException(409, "node_not_eligible", $"{pin} doesn't meet \"{unmet}\".");
            return pin;
        }
        return facts.Where(item => Ready(item) && StackRequirements.Unmet(require, item.Status, item.Gpu, mounted(item.NodeId)) is null)
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
        if (stack.Restore is not null)
            throw new HardwareOnboardingException(409, "stack_restoring", "This app is being restored. Wait for the restore to finish, or cancel it.");
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
                "cancel-restore" when stack.Restore is not null => stack with { Restore = null },
                "cancel-restore" => throw new HardwareOnboardingException(409, "not_restoring", "This app isn't being restored."),
                _ => throw new HardwareOnboardingException(404, "unknown_action", "Use start, stop, restart, update, cancel-move or cancel-restore."),
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
            if (stack.Manifest.Template is { } bound && StackCatalog.Apps.FirstOrDefault(app => app.Id == bound.Id) is { ServerBound: true } fixedApp)
                throw new HardwareOnboardingException(409, "server_bound",
                    $"{fixedApp.Name} uses {stack.Assigned}'s own GPUs, so it can't move. Install it on the other server instead.");
            var target = facts.FirstOrDefault(item => item.Hostname == request.Node)
                ?? throw new HardwareOnboardingException(400, "unknown_node", "Choose a managed server to move this app to.");
            if (target.Hostname == stack.Assigned)
                throw new HardwareOnboardingException(400, "same_node", $"This app already runs on {target.Hostname}.");
            if (facts.FirstOrDefault(item => item.Hostname == stack.Assigned) is not { Online: true })
                throw new HardwareOnboardingException(409, "source_offline", $"{stack.Assigned} must be online to hand over the app's data.");
            if (!Ready(target))
                throw new HardwareOnboardingException(409, "target_not_ready", $"{target.Hostname} must be online with Docker ready.");
            if (StackRequirements.Unmet(stack.Manifest.Placement.Require ?? [], target.Status, target.Gpu, Mounted(target.NodeId)) is { } unmet)
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

    /// <summary>
    /// Records a node's report, finishes any move or restore it has completed, closes and schedules its backups, and
    /// returns the stacks for it.
    /// </summary>
    public async Task<NodeDesiredStack[]> Sync(Guid nodeId, string hostname, NodeStackReport report, CancellationToken ct)
    {
        ValidateReport(report);
        _reports[nodeId] = (time.GetUtcNow(), report);
        var received = report.Stacks.Where(item => item.Received is not null).GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.First().Received);
        var restored = report.Stacks.Where(item => item.Restored is not null).GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.First().Restored);
        await _gate.WaitAsync(ct);
        StoredStack[] stacks;
        NodeDesiredStack[] desired;
        try
        {
            stacks = ReadUnlocked();
            var finished = false;
            for (var i = 0; i < stacks.Length; i++)
            {
                if (stacks[i].Move is { } move && move.To == hostname && received.GetValueOrDefault(stacks[i].Name) == move.Id)
                {
                    stacks[i] = stacks[i] with { Node = move.To, Desired = move.Desired, Move = null, UpdatedAt = time.GetUtcNow() };
                    transfers.Forget(move.Id);
                    finished = true;
                }
                if (stacks[i].Restore is { } restore && stacks[i].Assigned == hostname && restored.GetValueOrDefault(stacks[i].Name) == restore.Id)
                {
                    stacks[i] = stacks[i] with { Restore = null, UpdatedAt = time.GetUtcNow() };
                    finished = true;
                }
            }
            if (finished) await Write(stacks, ct);
            var backups = await SyncBackups(stacks, hostname, report, ct);
            var ns = (await ManagedNodeDns.ActiveNaming(domains, ct))?.Namespace;
            desired = stacks.Select(stack => Desired(stack, hostname, ns, backups.GetValueOrDefault(stack.Name))).OfType<NodeDesiredStack>().ToArray();
        }
        finally { _gate.Release(); }
        return await Relay(nodeId, hostname, stacks, ct) is { } relay ? [.. desired, relay] : desired;
    }

    private NodeDesiredStack? Desired(StoredStack stack, string hostname, string? ns, NodeBackupRequest? backup = null)
    {
        (string? Desired, Guid? Send, Guid? Receive) plan = stack.Move switch
        {
            { } move when move.From == hostname => ("Stopped", move.Id, null),
            { } move when move.To == hostname => ("Stopped", null, move.Id),
            null when stack.Assigned == hostname => (stack.Desired, null, null),
            _ => (null, null, null),
        };
        var settled = stack.Move is null && stack.Assigned == hostname;
        return plan.Desired is null ? null : new(stack.Name, stack.Revision, plan.Desired, stack.RestartCount, stack.PullCount,
            stack.Compose, NodeEnv(_protector.Unprotect(stack.ProtectedEnv), stack.Manifest, ns), plan.Send, plan.Receive,
            settled && stack.Restore is null ? backup : null,
            settled && stack.Restore is { } restore ? new NodeRestoreRequest(restore.Id, restore.Snapshot, Policy(stack).Exclude) : null,
            settled ? stack.Manifest.Address : null);
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
            restore = stack.Restore,
            appAddress = stack.Manifest.Address is { } ip
                ? new { ip, status = entry?.Report.Addresses?.FirstOrDefault(item => item.Address == ip) } : null,
            template = stack.Manifest.Template is { } template && StackCatalog.Apps.FirstOrDefault(app => app.Id == template.Id) is var app
                ? new { template.Id, template.Version, latest = app?.Version, name = app?.Name, template.Settings, serverBound = app?.ServerBound ?? false }
                : null,
        };
    }

    /// <summary>Every catalog app, with each server's eligibility so the portal can explain it.</summary>
    public async Task<object> Catalog(CancellationToken ct)
    {
        var facts = await nodes.Facts(ct);
        return new
        {
            apps = StackCatalog.Apps.Select(app => new
            {
                app.Id, app.Version, app.Name, app.Summary, app.Needs, app.Require, app.Fields, app.ServerBound, app.UsesAddress,
                servers = facts.Select(node => StackCatalog.Server(app, node)).ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>The compose Lucia would run for these settings. Generated secrets aren't shown or kept.</summary>
    public async Task<object> Preview(string id, CatalogPreviewRequest request, CancellationToken ct)
    {
        var app = StackCatalog.Find(id);
        var node = (await nodes.Facts(ct)).FirstOrDefault(item => item.Hostname == request.Node)
            ?? throw new HardwareOnboardingException(400, "unknown_node", "Choose a managed server.");
        var settings = StackCatalog.Settings(app, request.Settings);
        var output = app.Render(settings, node, StackCatalog.KeepSecrets(app, settings, []));
        return new { output.Compose, require = output.Require };
    }

    /// <summary>Where the host reaches a server's Local AI, and the key that manages it.</summary>
    public async Task<(Uri BaseUri, string Key)> LocalAi(Guid nodeId, CancellationToken ct)
    {
        var (stack, address) = await LocalAiStack(nodeId, ct);
        if (stack.Move is not null || stack.Desired != "Running")
            throw new HardwareOnboardingException(409, "local_ai_stopped", $"Local AI on {stack.Assigned} is stopped. Start it first.");
        var settings = stack.Manifest.Template!.Settings!;
        var port = int.Parse(settings[(settings.GetValueOrDefault("engine") ?? "lucia") != "lucia" ? "library-port" : "port"], CultureInfo.InvariantCulture);
        var key = StackCatalog.ReadEnv(_protector.Unprotect(stack.ProtectedEnv)).GetValueOrDefault("LUCIA_WORKER_KEY")
            ?? throw new HardwareOnboardingException(409, "local_ai_key_missing", "Local AI's management key is missing. Save the app again.");
        return (new UriBuilder("http", address, port).Uri, key);
    }

    private async Task<(StoredStack Stack, string Address)> LocalAiStack(Guid nodeId, CancellationToken ct)
    {
        var address = (await nodes.Addresses(ct)).FirstOrDefault(item => item.NodeId == nodeId)
            ?? throw new HardwareOnboardingException(404, "unknown_node", "That server isn't managed by Lucia, or hasn't checked in yet.");
        var stack = (await Read(ct)).FirstOrDefault(item => item.Manifest.Template?.Id == "local-ai" && item.Assigned == address.Hostname)
            ?? throw new HardwareOnboardingException(404, "local_ai_not_installed", $"Local AI isn't installed on {address.Hostname}.");
        return (stack, IPAddress.Parse(address.Address).ToString());
    }

    private static readonly HttpClient ServingClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>What Local AI's engine serves. <c>ready</c> means vLLM or llama.cpp answered with the inference key; for llama.cpp,
    /// <c>models</c> is its router's list, which reading also makes it reread the worker's preset file.</summary>
    public async Task<object> Serving(Guid nodeId, CancellationToken ct)
    {
        var (stack, address) = await LocalAiStack(nodeId, ct);
        var settings = stack.Manifest.Template!.Settings!;
        var engine = settings.GetValueOrDefault("engine") ?? "lucia";
        var model = engine == "vllm" && settings.GetValueOrDefault("vllm-model") is { Length: > 0 } id ? id : null;
        var service = Fresh(nodeId)?.Report.Stacks.FirstOrDefault(item => item.Name == stack.Name)?.Services
            .FirstOrDefault(item => item.Service == (engine == "llamacpp" ? "llama" : "vllm"));
        var ready = false;
        List<object>? models = engine == "llamacpp" ? [] : null;
        if ((model is not null || engine == "llamacpp") && stack is { Desired: "Running", Move: null })
        {
            var path = engine == "llamacpp" ? "/models" : "/v1/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder("http", address, int.Parse(settings["port"], CultureInfo.InvariantCulture), path)
                { Query = engine == "llamacpp" ? "reload=1" : "" }.Uri);
            request.Headers.Authorization = new("Bearer", StackCatalog.ReadEnv(_protector.Unprotect(stack.ProtectedEnv)).GetValueOrDefault("LUCIA_INFERENCE_KEY") ?? "");
            try
            {
                using var response = await ServingClient.SendAsync(request, ct);
                ready = response.IsSuccessStatusCode;
                if (ready && models is not null
                    && (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var item in data.EnumerateArray().Take(200))
                        if (item.TryGetProperty("id", out var name) && name.ValueKind == JsonValueKind.String)
                            models.Add(new
                            {
                                id = name.GetString(),
                                status = item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
                                    && status.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "unknown",
                            });
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { }
        }
        return new
        {
            stack = stack.Name, engine, model, name = model is null ? "" : settings.GetValueOrDefault("vllm-name"), context = settings.GetValueOrDefault("vllm-context"),
            stack.Desired, stack.Revision, ready, service, models,
        };
    }

    /// <summary>Points vLLM at a downloaded safetensors model, or at nothing, and redeploys Local AI.</summary>
    public async Task<object> Serve(Guid nodeId, ServeModelRequest request, string actor, CancellationToken ct)
    {
        var (stack, _) = await LocalAiStack(nodeId, ct);
        var settings = new Dictionary<string, string>(stack.Manifest.Template!.Settings!, StringComparer.Ordinal);
        var engine = settings.GetValueOrDefault("engine") ?? "lucia";
        if (engine != "vllm")
            throw new HardwareOnboardingException(409, "not_serving_engine", engine == "llamacpp"
                ? "llama.cpp serves every downloaded GGUF LLM already. Apps choose one by its name."
                : "Local AI on this server runs Lucia Inference, which loads models itself. Switch its engine in Apps first.");
        if (request.ModelId is { } id)
        {
            var (baseUri, key) = await LocalAi(nodeId, ct);
            using var lookup = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"/api/worker/models/{id}"));
            lookup.Headers.Authorization = new("Bearer", key);
            LocalModel? model;
            try
            {
                using var response = await ServingClient.SendAsync(lookup, ct);
                model = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LocalModel>(ct) : null;
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new HardwareOnboardingException(502, "local_ai_unreachable", "Local AI's model library isn't answering. Check its containers in Apps.");
            }
            if (model is not { State: ModelDownloadState.Ready, Source.Kind: ModelKind.Chat, Source.Format: ModelFormat.Safetensors })
                throw new HardwareOnboardingException(409, "model_not_servable", "vLLM serves downloaded safetensors models. Choose one that has finished downloading.");
            (settings["vllm-model"], settings["vllm-name"], settings["vllm-context"], settings["vllm-file"]) = (id.ToString(), model.Source.Repository,
                string.IsNullOrWhiteSpace(request.Context) ? "auto" : request.Context.Trim(), "");
        }
        else (settings["vllm-model"], settings["vllm-name"], settings["vllm-file"]) = ("", "", "");
        return await Save(stack.Name, new SaveStackRequest(null, null,
            stack.Manifest with { Template = stack.Manifest.Template with { Settings = settings } }, stack.Revision), actor, ct);
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
        // The portal uses these as page addresses under #/apps/.
        if (name is "new" or "containers" or "catalog" or "install" or "backups" or RelayName)
            throw new HardwareOnboardingException(400, "invalid_stack_name", $"\"{name}\" is reserved. Choose another name.");
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
            if (trimmed.StartsWith(AddressVariable + "=", StringComparison.Ordinal))
                throw new HardwareOnboardingException(400, "invalid_environment", $"Lucia sets {AddressVariable} from the app's address. Remove line {line}.");
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
                    or "Restoring"
                && stack.Services is { Length: <= 64 }, "The stack report is invalid.");
            Text(stack.Message, 1024);
            ValidateBackupStatus(stack.Backup);
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
        ValidateMounts(report.Mounts);
        ValidateSnapshots(report.Snapshots);
        ValidateAddresses(report.Addresses);
        Text(report.RepositoryError, 1024);
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

    private async Task Write(IEnumerable<StoredStack> stacks, CancellationToken ct)
    {
        var sorted = stacks.OrderBy(stack => stack.Name, StringComparer.Ordinal).ToArray();
        await DomainOnboardingStore.WriteJson(FilePath, new StackFile(1, sorted), ct);
        _destination = default;
        NoticeRoutes(sorted);
    }

    [GeneratedRegex(@"\A[a-z](?:[a-z0-9-]{0,38}[a-z0-9])?\z")]
    internal static partial Regex NamePattern();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvPattern();
    [GeneratedRegex(@"\A[0-9a-f]{12,64}\z")]
    private static partial Regex ContainerIdPattern();
}
