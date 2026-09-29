using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal sealed record StackServiceStatus(string Service, string State, string? Health, string? Image, int? ExitCode);
internal sealed record NodeStackStatus(string Name, string State, long? AppliedRevision, string? Message, StackServiceStatus[] Services,
    Guid? Received = null, NodeBackupStatus? Backup = null, Guid? Restored = null);
internal sealed record NodeContainer(string Id, string Name, string Image, string State, string? Status, string? Project,
    string? Service, string? Ports, [property: System.Text.Json.Serialization.JsonIgnore] string[]? Awaits = null);
internal sealed record NodeListener(string Protocol, string Address, int Port, string? Process = null, string? ContainerId = null);
internal sealed record SocketOwner(string Process, string? ContainerId);
internal sealed record NodeStackReport(NodeStackStatus[] Stacks, NodeContainer[] Containers, NodeListener[] Listeners,
    NodeMountStatus[] Mounts, NodeSnapshot[]? Snapshots = null, string? RepositoryError = null, NodeAddressStatus[]? Addresses = null);
internal sealed record NodeDesiredStack(string Name, long Revision, string Desired, long RestartCount, long PullCount,
    string Compose, string Env, Guid? Send = null, Guid? Receive = null, NodeBackupRequest? Backup = null, NodeRestoreRequest? Restore = null,
    string? Address = null);
internal sealed record AppliedStack(long Revision, string Desired, long RestartCount, long PullCount, string? Files = null)
{
    /// <summary>A hash of the compose file and env, since Compose doesn't recreate a container when only its inline configs change.</summary>
    public static string Hash(NodeDesiredStack stack) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(stack.Compose + "\0" + stack.Env)));
}
internal sealed record NodeLink(DiscoveryClient Client, Guid Node, Func<string> Certificate, System.Security.Cryptography.ECDsa Key);

/// <summary>
/// Runs the compose stacks the owner placed on this node. Each stack lives in <c>/srv/lucia/stacks/&lt;name&gt;</c>;
/// named volumes become bind mounts under its <c>volumes/</c> directory so the data is visible and easy to back up.
/// Removing a stack takes its containers down and keeps that data. Moving one stops it here and streams its directory,
/// through Lucia, to the node it's moving to.
/// </summary>
internal static partial class StackRunner
{
    internal const string Root = "/srv/lucia/stacks";
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, string> busy = new();
    private static readonly ConcurrentDictionary<string, (string Key, DateTimeOffset At, string Message)> failures = new();
    private static string[] known = [];
    private const string ReceivedMarker = ".lucia-received", SentMarker = ".lucia-sent";
    internal const string RestoredMarker = ".lucia-restored";
    // Lucia's own files are rewritten from the definition on the receiving node; only the app's data travels.
    private static readonly string[] NotMoved = ["compose.yaml", ".env", "compose.lucia.json", ".lucia-applied.json", ReceivedMarker, SentMarker,
        ".lucia-backup.json", RestoredMarker];

    internal static async Task RunAsync(DiscoveryClient client, Guid node, Func<string> certificate, System.Security.Cryptography.ECDsa key,
        CancellationToken token)
    {
        var link = new NodeLink(client, node, certificate, key);
        string? lastError = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            if (NodeRuntime.Current.State != "Ready") continue;
            using var sync = AgentTelemetry.Source.StartActivity("stack sync");
            try
            {
                var containers = await ContainersAsync(token);
                var desired = await client.StacksAsync(node, certificate(), Report(containers), key, token);
                // Older servers send no mounts; leave existing units alone rather than removing them.
                if (desired.Mounts is { } mounts) NasMounts.Reconcile(mounts, token);
                ResticBackups.Configure(desired.Backup, token);
                try
                {
                    await StackAddresses.ReconcileAsync((desired.Stacks ?? []).Where(stack => stack.Desired == "Running" && stack.Send is null)
                        .Select(stack => stack.Address).OfType<string>(), token);
                }
                // Apps with an address wait for it; the rest carry on.
                catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException or JsonException)
                {
                    Console.Error.WriteLine("Couldn't reconcile app addresses. " + ex.Message);
                }
                Reconcile(desired.Stacks ?? [], link, token);
                lastError = null;
            }
            catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException or JsonException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                sync?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                if (ex.Message != lastError) Console.Error.WriteLine("Stack sync failed; retrying. " + ex.Message);
                lastError = ex.Message;
            }
            sync?.Stop();
        } while (await timer.WaitForNextTickAsync(token));
    }

    private static NodeStackReport Report(NodeContainer[] containers)
    {
        var stacks = known.Union(busy.Keys).Distinct().Take(64).Select(name =>
        {
            var project = containers.Where(item => item.Project == Project(name)).Take(64).ToArray();
            var services = project.Select(item =>
                new StackServiceStatus(item.Service ?? item.Name, item.State, Health(item.Status), item.Image, ExitCode(item.Status))).ToArray();
            // A one-shot service other services wait on, such as a setup step, is done once it exits cleanly.
            var oneShots = project.SelectMany(item => item.Awaits ?? []).ToHashSet(StringComparer.Ordinal);
            var applied = ReadApplied(name);
            var received = ReadMarker(name, ReceivedMarker);
            var backup = ResticBackups.Status(name);
            var restored = ReadMarker(name, RestoredMarker);
            // A backup doesn't change what the app is doing, so it reports its usual state.
            if (busy.TryGetValue(name, out var working) && working != BackingUp)
                return new NodeStackStatus(name, working, applied?.Revision, null, services, received, backup, restored);
            if (failures.TryGetValue(name, out var failure))
                return new NodeStackStatus(name, "Failed", applied?.Revision, failure.Message, services, received, backup, restored);
            var running = services.Count(item => item.State == "running" && item.Health != "unhealthy"
                || item.State == "exited" && item.ExitCode == 0 && oneShots.Contains(item.Service));
            var state = applied is null ? "Pending"
                : applied.Desired == "Stopped" ? "Stopped"
                : services.Length > 0 && running == services.Length ? "Running" : "Degraded";
            return new NodeStackStatus(name, state, applied?.Revision, null, services, received, backup, restored);
        }).ToArray();
        return new(stacks, containers, Listeners(), NasMounts.Report(), ResticBackups.Snapshots, ResticBackups.Error, StackAddresses.Report());
    }

    private static void Reconcile(NodeDesiredStack[] desired, NodeLink link, CancellationToken token)
    {
        var valid = desired.Where(stack => NamePattern().IsMatch(stack.Name) && stack.Desired is "Running" or "Stopped")
            .GroupBy(stack => stack.Name).Select(group => group.First()).Take(64).ToArray();
        known = valid.Select(stack => stack.Name).ToArray();
        foreach (var name in failures.Keys.Except(known))
            if (failures.TryGetValue(name, out var stale) && stale.Key != "remove") failures.TryRemove(name, out _);
        foreach (var stack in valid)
        {
            var target = new AppliedStack(stack.Revision, stack.Desired, stack.RestartCount, stack.PullCount, AppliedStack.Hash(stack));
            var applied = ReadApplied(stack.Name);
            // A receiving node waits for the data before anything else; Lucia hands it the stack once it reports the marker.
            if (stack.Receive is { } incoming)
            {
                if (ReadMarker(stack.Name, ReceivedMarker) != incoming && Due(stack.Name, "receive:" + incoming))
                    Start(stack.Name, "Receiving", () => ReceiveAsync(link, stack.Name, incoming, token), "receive:" + incoming);
                continue;
            }
            // A restore swaps the snapshot's data in first; the stack then applies onto it like a fresh one.
            if (stack.Restore is { } restore && ReadMarker(stack.Name, RestoredMarker) != restore.Id)
            {
                if (Due(stack.Name, "restore:" + restore.Id))
                    Start(stack.Name, "Restoring", () => RestoreAsync(stack.Name, restore, token), "restore:" + restore.Id);
                continue;
            }
            if (applied == target)
            {
                // A sending node stops the stack first (the target below says Stopped), then streams it once.
                if (stack.Send is { } outgoing && ReadMarker(stack.Name, SentMarker) != outgoing)
                {
                    if (Due(stack.Name, "send:" + outgoing))
                        Start(stack.Name, "Sending", () => SendAsync(link, stack.Name, outgoing, token), "send:" + outgoing);
                }
                else
                {
                    failures.TryRemove(stack.Name, out _);
                    if (stack.Backup is { } backup && ResticBackups.Status(stack.Name)?.Id != backup.Id) StartBackup(stack.Name, backup, applied, token);
                }
                continue;
            }
            var key = JsonSerializer.Serialize(target);
            // Waiting on an address shouldn't cost the usual backoff once it's taken; DNS is down until the app starts.
            if (stack.Address is { } address && StackAddresses.Held(address) && failures.TryGetValue(stack.Name, out var waiting)
                && waiting.Message.StartsWith(StackAddresses.Waiting, StringComparison.Ordinal))
                failures.TryRemove(stack.Name, out _);
            if (Due(stack.Name, key)) Start(stack.Name, "Applying", () => ApplyAsync(stack, applied, token), key);
        }
        if (Directory.Exists(Root))
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(directory);
                if (NamePattern().IsMatch(name) && !known.Contains(name) && File.Exists(Path.Combine(directory, "compose.yaml"))
                    && !(failures.TryGetValue(name, out var failure) && DateTimeOffset.UtcNow - failure.At < RetryAfterFailure))
                    Start(name, "Removing", () => RemoveAsync(name, token), "remove");
            }
    }

    private static bool Due(string name, string key) =>
        !(failures.TryGetValue(name, out var failure) && failure.Key == key && DateTimeOffset.UtcNow - failure.At < RetryAfterFailure);

    private const string BackingUp = "BackingUp";

    private static void StartBackup(string name, NodeBackupRequest backup, AppliedStack applied, CancellationToken token)
    {
        if (!busy.TryAdd(name, BackingUp)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await ResticBackups.BackupAsync(name, backup, arguments => ComposeAsync(name, arguments, TimeSpan.FromMinutes(5), token),
                    stop: backup.Mode == "stop" && applied.Desired == "Running", token);
            }
            catch (Exception ex) { Console.Error.WriteLine($"Backup of {name} failed. {ex.Message}"); }
            finally { busy.TryRemove(name, out _); }
        });
    }

    private static void Start(string name, string state, Func<Task> work, string key)
    {
        if (!busy.TryAdd(name, state)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
                failures.TryRemove(name, out _);
                Console.Error.WriteLine($"Stack {name}: " + state switch
                    { "Removing" => "removed.", "Sending" => "sent.", "Receiving" => "received.", "Restoring" => "restored.", _ => "applied." });
            }
            // Any failure is recorded so it's reported and retried on the backoff; an unobserved fault would retry at once, silently.
            catch (Exception ex)
            {
                failures[name] = (key, DateTimeOffset.UtcNow, Bounded(ex.Message, 1000) ?? "The stack could not be applied.");
                Console.Error.WriteLine($"Stack {name} failed. {ex.Message}");
            }
            finally { busy.TryRemove(name, out _); }
        });
    }

    private static async Task ApplyAsync(NodeDesiredStack stack, AppliedStack? applied, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new NodeAgentException("Stacks run only on Linux nodes.");
        var directory = Path.Combine(Root, stack.Name);
        Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WritePrivate(Path.Combine(directory, "compose.yaml"), stack.Compose);
        WritePrivate(Path.Combine(directory, ".env"), stack.Env);
        var overridePath = Path.Combine(directory, "compose.lucia.json");
        File.Delete(overridePath);
        var config = await ComposeAsync(stack.Name, ["config", "--format", "json"], TimeSpan.FromMinutes(1), token);
        var redirect = VolumeOverride(config, directory);
        if (redirect is not null)
        {
            foreach (var path in redirect.Value.Paths) Directory.CreateDirectory(path);
            WritePrivate(overridePath, redirect.Value.Json);
        }
        var files = AppliedStack.Hash(stack);
        if (stack.Desired == "Stopped")
            await ComposeAsync(stack.Name, ["down", "--remove-orphans"], TimeSpan.FromMinutes(5), token);
        else
        {
            // Docker would bind the empty mount point, and the app wouldn't see the share once it mounted.
            if (NasMounts.Unmounted(stack.Compose) is { } share)
                throw new NodeAgentException($"Waiting for the NAS share {share} to mount on this server.");
            if (stack.Address is { } address && !StackAddresses.Held(address)) throw new NodeAgentException(StackAddresses.Problem(address));
            if (applied is not null && stack.PullCount != applied.PullCount)
                await ComposeAsync(stack.Name, ["pull", "--quiet"], TimeSpan.FromMinutes(30), token);
            // A saved change to the compose file or env recreates every container, as a restart does; stacks applied before
            // the hash was recorded just catch up without a restart.
            string[] up = applied is not null && (stack.RestartCount != applied.RestartCount || applied.Files is not null && applied.Files != files)
                ? ["up", "-d", "--remove-orphans", "--quiet-pull", "--force-recreate"]
                : ["up", "-d", "--remove-orphans", "--quiet-pull"];
            await ComposeAsync(stack.Name, up, TimeSpan.FromMinutes(30), token);
        }
        WritePrivate(Path.Combine(directory, ".lucia-applied.json"),
            JsonSerializer.Serialize(new AppliedStack(stack.Revision, stack.Desired, stack.RestartCount, stack.PullCount, files), AgentJson.Options));
    }

    private static async Task RemoveAsync(string name, CancellationToken token)
    {
        var directory = Path.Combine(Root, name);
        await ComposeAsync(name, ["down", "--remove-orphans"], TimeSpan.FromMinutes(5), token);
        foreach (var file in new[] { ".lucia-applied.json", "compose.lucia.json", ".env", "compose.yaml" })
            File.Delete(Path.Combine(directory, file));
    }

    /// <summary>
    /// Streams the stopped stack's directory as a tar archive. tar failing mid-way fails the upload, so the receiver never
    /// mistakes a truncated archive for a whole one.
    /// </summary>
    private static async Task SendAsync(NodeLink link, string name, Guid move, CancellationToken token)
    {
        var directory = Path.Combine(Root, name);
        if (!Directory.Exists(directory)) throw new NodeAgentException("This app's data directory is missing.");
        long total = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            total += file.Length;
        string[] arguments = ["--create", "--file=-", "--directory=" + directory, "--numeric-owner", "--xattrs", "--acls", "--sparse",
            "--anchored", .. NotMoved.Select(item => "--exclude=./" + item), "."];
        await link.Client.SendTransferAsync(link.Node, link.Certificate(), move, link.Key, total,
            (stream, ct) => Commands.PipeAsync("/usr/bin/tar", arguments, output: true, stream, ct), token);
        WritePrivate(Path.Combine(directory, SentMarker), move.ToString("D"));
    }

    /// <summary>
    /// Unpacks the incoming archive beside the stacks, then swaps it into place. Anything already at that path is kept
    /// aside rather than merged, so a move never mixes two copies of an app's data.
    /// </summary>
    private static async Task ReceiveAsync(NodeLink link, string name, Guid move, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new NodeAgentException("Stacks run only on Linux nodes.");
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(Root, Private);
        var directory = Path.Combine(Root, name);
        var incoming = Path.Combine(Root, ".incoming-" + name);
        if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
        Directory.CreateDirectory(incoming, Private);
        try
        {
            await link.Client.ReceiveTransferAsync(link.Node, link.Certificate(), move, link.Key, (stream, ct) =>
                Commands.PipeAsync("/usr/bin/tar", ["--extract", "--file=-", "--directory=" + incoming, "--numeric-owner", "--same-owner",
                    "--same-permissions", "--xattrs", "--xattrs-include=*", "--acls"], output: false, stream, ct), token);
        }
        catch
        {
            // A cancelled or broken transfer leaves a partial copy that could be as large as the app's data.
            try { Directory.Delete(incoming, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        if (Directory.Exists(directory))
        {
            if (File.Exists(Path.Combine(directory, "compose.yaml")))
                await ComposeAsync(name, ["down", "--remove-orphans"], TimeSpan.FromMinutes(5), token);
            Directory.Move(directory, Path.Combine(Root, $".replaced-{name}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}"));
        }
        File.SetUnixFileMode(incoming, Private);
        Directory.Move(incoming, directory);
        WritePrivate(Path.Combine(directory, ReceivedMarker), move.ToString("D"));
    }

    /// <summary>
    /// Restores the snapshot beside the stack, then swaps it in the way a move does: the current data is kept aside as
    /// <c>.replaced-&lt;name&gt;-&lt;time&gt;</c>, never merged or deleted.
    /// </summary>
    private static async Task RestoreAsync(string name, NodeRestoreRequest restore, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new NodeAgentException("Stacks run only on Linux nodes.");
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(Root, Private);
        var directory = Path.Combine(Root, name);
        var incoming = Path.Combine(Root, ".restore-" + name);
        if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
        try { await ResticBackups.RestoreAsync(name, restore.Snapshot, incoming, token); }
        catch
        {
            try { if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        if (!Directory.Exists(incoming)) Directory.CreateDirectory(incoming, Private);
        if (Directory.Exists(directory))
        {
            if (File.Exists(Path.Combine(directory, "compose.yaml")))
                await ComposeAsync(name, ["down", "--remove-orphans"], TimeSpan.FromMinutes(5), token);
            // Skipped paths, such as downloaded models, aren't in the snapshot; keep the current copies instead of losing them.
            foreach (var path in ResticBackups.Declared(restore.Keep ?? []))
            {
                var from = Path.Combine(directory, path);
                var to = Path.Combine(incoming, path);
                if (!Path.Exists(from) || Path.Exists(to) || ThroughLink(incoming, path) || ThroughLink(directory, path)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
            }
            Directory.Move(directory, Path.Combine(Root, $".replaced-{name}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}"));
        }
        File.SetUnixFileMode(incoming, Private);
        Directory.Move(incoming, directory);
        // Nothing of the old apply state came back, so the stack applies from scratch onto the restored data.
        File.Delete(Path.Combine(directory, ".lucia-applied.json"));
        WritePrivate(Path.Combine(directory, RestoredMarker), restore.Id.ToString("D"));
    }

    /// <summary>Whether a restored folder on the way to <paramref name="path"/> is a symlink, which could point outside the app.</summary>
    private static bool ThroughLink(string root, string path)
    {
        var current = root;
        foreach (var part in path.Split('/')[..^1])
        {
            current = Path.Combine(current, part);
            if (new DirectoryInfo(current) is { Exists: true, LinkTarget: not null } || File.Exists(current)) return true;
        }
        return false;
    }

    private static Guid? ReadMarker(string name, string marker)
    {
        try { return Guid.TryParse(File.ReadAllText(Path.Combine(Root, name, marker)).Trim(), out var id) ? id : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Task<string> ComposeAsync(string name, string[] arguments, TimeSpan limit, CancellationToken token)
    {
        var directory = Path.Combine(Root, name);
        List<string> command = ["compose", "-p", Project(name), "--project-directory", directory,
            "--env-file", Path.Combine(directory, ".env"), "-f", Path.Combine(directory, "compose.yaml")];
        if (File.Exists(Path.Combine(directory, "compose.lucia.json"))) command.AddRange(["-f", Path.Combine(directory, "compose.lucia.json")]);
        return Commands.RunAsync("/usr/bin/docker", [.. command, .. arguments], limit, token);
    }

    /// <summary>
    /// Points each plain named volume at a bind directory under the stack. External volumes and volumes with a custom
    /// driver or options are the owner's choice and stay as written.
    /// </summary>
    internal static (string Json, string[] Paths)? VolumeOverride(string configJson, string directory)
    {
        if (JsonNode.Parse(configJson)?["volumes"] is not JsonObject volumes) return null;
        var result = new JsonObject();
        var paths = new List<string>();
        foreach (var (key, value) in volumes)
        {
            if (!VolumeKeyPattern().IsMatch(key) || value is not JsonObject volume) continue;
            if (volume["external"]?.GetValueKind() == JsonValueKind.True || volume["driver_opts"] is not null
                || volume["driver"] is JsonValue driver && driver.GetValue<string>() != "local")
                continue;
            var path = directory + "/volumes/" + key;
            paths.Add(path);
            result[key] = new JsonObject
            {
                ["driver"] = "local",
                ["driver_opts"] = new JsonObject { ["type"] = "none", ["o"] = "bind", ["device"] = path }
            };
        }
        return result.Count == 0 ? null : (new JsonObject { ["volumes"] = result }.ToJsonString(), paths.ToArray());
    }

    internal static async Task<NodeContainer[]> ContainersAsync(CancellationToken token) =>
        ParseContainers(await Commands.RunAsync("/usr/bin/docker", ["ps", "-a", "--no-trunc", "--format", "{{json .}}"],
            TimeSpan.FromSeconds(30), token, keep: 2 * 1024 * 1024));

    /// <summary>Parses <c>docker ps -a --no-trunc --format '{{json .}}'</c>.</summary>
    internal static NodeContainer[] ParseContainers(string output)
    {
        var containers = new List<NodeContainer>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (containers.Count == 256) break;
            JsonElement item;
            try { item = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { continue; }
            string? Field(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var id = Field("ID");
            var name = Bounded(Field("Names")?.Split(',')[0], 256);
            if (id is null || !ContainerIdPattern().IsMatch(id) || name is null) continue;
            var labels = Field("Labels") ?? "";
            string? Label(string key) => Bounded(Regex.Match(labels, @"(?:^|,)" + Regex.Escape(key) + "=([^,]*)").Groups[1].Value, 128);
            containers.Add(new(id, name, Bounded(Field("Image"), 512) ?? "unknown", Bounded(Field("State"), 32) ?? "unknown",
                Bounded(Field("Status"), 128), Label("com.docker.compose.project"), Label("com.docker.compose.service"),
                Bounded(Field("Ports"), 1024),
                // Compose's depends_on label is itself comma-separated (service:condition:restart), so match its entries anywhere.
                [.. CompletedDependency().Matches(labels).Select(match => match.Groups[1].Value).Where(service => service.Length <= 128).Distinct().Take(16)]));
        }
        return containers.ToArray();
    }

    internal static string? Health(string? status) =>
        status is null ? null : status.Contains("(unhealthy)") ? "unhealthy" : status.Contains("(healthy)") ? "healthy"
            : status.Contains("(health: starting)") ? "starting" : null;

    internal static int? ExitCode(string? status) =>
        status is not null && ExitPattern().Match(status) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var code) ? code : null;

    private static NodeListener[] Listeners()
    {
        var owners = SocketOwners();
        var listeners = new List<NodeListener>();
        foreach (var (file, protocol) in new[] { ("tcp", "tcp"), ("tcp6", "tcp"), ("udp", "udp"), ("udp6", "udp") })
        {
            try { listeners.AddRange(ParseListeners(DiskSafety.Read("/proc/net/" + file, 4 * 1024 * 1024), protocol, owners)); }
            catch (Exception ex) when (ex is NodeAgentException or IOException) { }
        }
        return listeners.Distinct().Take(1024).ToArray();
    }

    /// <summary>Maps socket inodes to the process holding them and, when that process runs in a container, the container id.</summary>
    private static Dictionary<long, SocketOwner> SocketOwners()
    {
        var owners = new Dictionary<long, SocketOwner>();
        if (!OperatingSystem.IsLinux()) return owners;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(directory);
            if (!pid.All(char.IsAsciiDigit)) continue;
            try
            {
                SocketOwner? owner = null;
                foreach (var fd in Directory.EnumerateFileSystemEntries(directory + "/fd"))
                {
                    if (new FileInfo(fd).LinkTarget is not { } target || !target.StartsWith("socket:[", StringComparison.Ordinal)
                        || !long.TryParse(target.AsSpan(8, target.Length - 9), CultureInfo.InvariantCulture, out var inode) || owners.ContainsKey(inode)) continue;
                    owner ??= new(File.ReadAllText(directory + "/comm").Trim(), CgroupContainer(File.ReadAllText(directory + "/cgroup")));
                    owners[inode] = owner;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return owners;
    }

    internal static string? CgroupContainer(string cgroup) => CgroupPattern().Match(cgroup) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"(?:docker-|/docker/)([0-9a-f]{64})")]
    private static partial Regex CgroupPattern();

    /// <summary>Parses <c>/proc/net/{tcp,udp}[6]</c>: listening TCP sockets and unconnected, bound UDP sockets.
    /// Inode 0 is a socket the kernel opened itself, such as NFS and its lock manager.</summary>
    internal static IEnumerable<NodeListener> ParseListeners(string table, string protocol, IReadOnlyDictionary<long, SocketOwner>? owners = null)
    {
        foreach (var line in table.Split('\n').Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[3] != (protocol == "tcp" ? "0A" : "07")) continue;
            if (protocol == "udp" && fields[2].TrimEnd('0', ':').Length != 0) continue;
            var local = fields[1].Split(':');
            if (local.Length != 2 || !int.TryParse(local[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port) || port == 0
                || ParseAddress(local[0]) is not { } address) continue;
            if (fields.Length > 9 && long.TryParse(fields[9], CultureInfo.InvariantCulture, out var inode))
            {
                if (inode == 0) { yield return new(protocol, address.ToString(), port, "kernel"); continue; }
                if (owners?.GetValueOrDefault(inode) is { } owner)
                {
                    yield return new(protocol, address.ToString(), port, Bounded(owner.Process, 64), owner.ContainerId);
                    continue;
                }
                // No process holds it: the kernel's own sockets, such as the NFS lock manager.
                if (owners is { Count: > 0 }) { yield return new(protocol, address.ToString(), port, "kernel"); continue; }
            }
            yield return new(protocol, address.ToString(), port);
        }
    }

    private static IPAddress? ParseAddress(string hex)
    {
        if (hex.Length is not (8 or 32) || !hex.All(char.IsAsciiHexDigit)) return null;
        var bytes = Convert.FromHexString(hex);
        // The kernel prints each 32-bit word in host (little-endian) order.
        for (var word = 0; word < bytes.Length; word += 4) Array.Reverse(bytes, word, 4);
        return new IPAddress(bytes);
    }

    private static AppliedStack? ReadApplied(string name)
    {
        try
        {
            var path = Path.Combine(Root, name, ".lucia-applied.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<AppliedStack>(File.ReadAllText(path), AgentJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal static void WritePrivate(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    private static string Project(string name) => "lucia-" + name;

    internal static string? Bounded(string? value, int maximum)
    {
        var clean = new string((value ?? "").Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean.Length > maximum ? clean[..maximum] : clean;
    }

    [GeneratedRegex(@"\A[a-z](?:[a-z0-9-]{0,38}[a-z0-9])?\z")]
    internal static partial Regex NamePattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z")]
    private static partial Regex VolumeKeyPattern();
    [GeneratedRegex(@"\A[0-9a-f]{12,64}\z")]
    private static partial Regex ContainerIdPattern();
    [GeneratedRegex(@"\AExited \((-?\d{1,4})\)")]
    private static partial Regex ExitPattern();
    [GeneratedRegex(@"[=,]([A-Za-z0-9][A-Za-z0-9_.-]*):service_completed_successfully:(?:true|false)(?=,|\z)")]
    private static partial Regex CompletedDependency();
}

/// <summary>Runs fixed executables with argument arrays, bounded time and bounded output.</summary>
internal static class Commands
{
    internal static async Task<string> RunAsync(string executable, string[] arguments, TimeSpan limit, CancellationToken token,
        int keep = 256 * 1024) =>
        (await CaptureAsync(executable, arguments, limit, token, keep, failOnError: true)).Stdout;

    internal static async Task<(int Exit, string Stdout, string Stderr)> CaptureAsync(string executable, string[] arguments,
        TimeSpan limit, CancellationToken token, int keep, bool failOnError, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["HOME"] = "/root";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new NodeAgentException($"'{Path.GetFileName(executable)}' could not start.");
        process.StandardInput.Close();
        try
        {
            var stdout = TailAsync(process.StandardOutput, keep, timeout.Token);
            var stderr = TailAsync(process.StandardError, keep, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (failOnError && process.ExitCode != 0)
            {
                var detail = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                throw new NodeAgentException($"'{Path.GetFileName(executable)} {string.Join(' ', arguments.TakeLast(2))}' exited with code "
                    + $"{process.ExitCode}." + (StackRunner.Bounded(detail, 600) is { } text ? " " + text : ""));
            }
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new NodeAgentException($"'{Path.GetFileName(executable)} {arguments.LastOrDefault()}' timed out.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// Runs a fixed executable with one side of it streamed: <paramref name="output"/> copies its stdout into
    /// <paramref name="stream"/>, otherwise <paramref name="stream"/> is fed to its stdin. Fails if it exits non-zero.
    /// </summary>
    internal static async Task PipeAsync(string executable, string[] arguments, bool output, Stream stream, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = output, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["HOME"] = "/root";
        using var process = Process.Start(start) ?? throw new NodeAgentException($"'{Path.GetFileName(executable)}' could not start.");
        try
        {
            var stderr = TailAsync(process.StandardError, 16 * 1024, token);
            try
            {
                if (output)
                {
                    process.StandardInput.Close();
                    await process.StandardOutput.BaseStream.CopyToAsync(stream, 256 * 1024, token);
                }
                else
                {
                    await stream.CopyToAsync(process.StandardInput.BaseStream, 256 * 1024, token);
                    process.StandardInput.Close();
                }
            }
            catch (IOException) when (!output && process.WaitForExit(TimeSpan.FromSeconds(5)) && process.ExitCode != 0)
            {
                // tar quit and broke the pipe; its own error explains why.
            }
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
            {
                var detail = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                throw new NodeAgentException($"'{Path.GetFileName(executable)}' exited with code {process.ExitCode}."
                    + (StackRunner.Bounded(detail, 600) is { } text ? " " + text : ""));
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Reads a stream to the end, keeping only its last <paramref name="keep"/> characters.</summary>
    private static async Task<string> TailAsync(StreamReader reader, int keep, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer, token)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 2 * keep) text.Remove(0, text.Length - keep);
        }
        return text.Length > keep ? text.ToString(text.Length - keep, keep) : text.ToString();
    }
}
