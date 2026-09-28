using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal sealed record NodeBackupRequest(Guid Id, string Mode, string[] Exclude);
/// <param name="Keep">Paths the backups skip; a restore carries them over from the current folder.</param>
internal sealed record NodeRestoreRequest(Guid Id, string Snapshot, string[]? Keep = null);
internal sealed record NodeBackupRepository(string Nas, string Share, string Path, string Password, int KeepDaily, int KeepWeekly, int KeepMonthly);
internal sealed record NodeBackupStatus(Guid Id, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null,
    string? Snapshot = null, long? Added = null, long? Total = null, string? Message = null);
internal sealed record NodeSnapshot(string Id, string Stack, string Host, DateTimeOffset Time, long? Size = null);

/// <summary>
/// Backs stacks up with restic into the repository Lucia chose on a NAS share. Every node writes to the same repository;
/// each snapshot is tagged with its stack so retention and restores follow the app wherever it runs.
/// </summary>
internal static partial class ResticBackups
{
    private const string Restic = "/usr/bin/restic", Cache = "/var/cache/lucia/restic", StatusFile = ".lucia-backup.json";
    internal const string StackTag = "lucia:stack=";
    private static readonly TimeSpan ListEvery = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim gate = new(1, 1), install = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NodeBackupStatus> running = new();
    private static NodeBackupRepository? repository;
    private static NodeSnapshot[] snapshots = [];
    private static string? error;
    private static DateTimeOffset listedAt = DateTimeOffset.MinValue;
    private static int listing;

    internal static NodeSnapshot[]? Snapshots => Volatile.Read(ref repository) is null ? null : Volatile.Read(ref snapshots);
    internal static string? Error => Volatile.Read(ref repository) is null ? null : Volatile.Read(ref error);

    /// <summary>Takes the repository from the latest sync and relists its snapshots when they're stale.</summary>
    internal static void Configure(NodeBackupRepository? wanted, CancellationToken token)
    {
        if (wanted is not null && !Valid(wanted)) wanted = null;
        var previous = Volatile.Read(ref repository);
        if (previous != wanted)
        {
            Volatile.Write(ref repository, wanted);
            Volatile.Write(ref snapshots, []);
            Volatile.Write(ref error, null);
            listedAt = DateTimeOffset.MinValue;
        }
        if (wanted is null || DateTimeOffset.UtcNow - listedAt < ListEvery || Interlocked.CompareExchange(ref listing, 1, 0) != 0) return;
        listedAt = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await ListAsync(wanted, token); }
            finally { Volatile.Write(ref listing, 0); }
        });
    }

    /// <summary>The backup this node is running for the stack, or the last one it finished.</summary>
    internal static NodeBackupStatus? Status(string name)
    {
        if (running.TryGetValue(name, out var current)) return current;
        try
        {
            var path = Path.Combine(StackRunner.Root, name, StatusFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<NodeBackupStatus>(File.ReadAllText(path), AgentJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Runs one requested backup and records how it went beside the stack. A failed backup is reported to Lucia, which
    /// schedules the next one; it never marks the app itself as failed.
    /// </summary>
    internal static async Task BackupAsync(string name, NodeBackupRequest request, Func<string[], Task> compose, bool stop, CancellationToken token)
    {
        var status = new NodeBackupStatus(request.Id, "Running", DateTimeOffset.UtcNow);
        running[name] = status;
        try
        {
            status = await RunBackupAsync(name, request, compose, stop, status, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            status = status with { State = "Failed", Message = StackRunner.Bounded(ex.Message, 1000) ?? "The backup failed." };
        }
        finally
        {
            status = status.State == "Running" ? status with { State = "Failed", Message = "The agent stopped during the backup." } : status;
            status = status with { FinishedAt = DateTimeOffset.UtcNow };
            try { StackRunner.WritePrivate(Path.Combine(StackRunner.Root, name, StatusFile), JsonSerializer.Serialize(status, AgentJson.Options)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            running.TryRemove(name, out _);
            Console.Error.WriteLine($"Backup of {name}: {status.State.ToLowerInvariant()}." + (status.Message is { } message ? " " + message : ""));
        }
    }

    private static async Task<NodeBackupStatus> RunBackupAsync(string name, NodeBackupRequest request, Func<string[], Task> compose, bool stop,
        NodeBackupStatus status, CancellationToken token)
    {
        var repo = Volatile.Read(ref repository) ?? throw new NodeAgentException("This server hasn't received the backup destination yet.");
        var directory = Path.Combine(StackRunner.Root, name);
        if (!Directory.Exists(directory)) throw new NodeAgentException("This app's data directory is missing.");
        await gate.WaitAsync(token);
        try
        {
            await ReadyAsync(repo, token);
            if (stop) await compose(["stop"]);
            (int Exit, string Stdout, string Stderr) result;
            try
            {
                result = await RunAsync(repo, ["backup", directory, "--json", "--quiet", "--host", Environment.MachineName,
                    "--tag", StackTag + name, "--tag", "lucia:version=1", "--retry-lock", "30m",
                    .. Excludes(directory, request.Exclude).SelectMany(path => new[] { "--exclude", path })], TimeSpan.FromHours(10), token);
            }
            finally
            {
                if (stop) await compose(["start"]);
            }
            // 3: the snapshot was made, but some files couldn't be read.
            if (result.Exit is not (0 or 3)) throw Failure("backup", result);
            var summary = ParseSummary(result.Stdout) ?? throw new NodeAgentException("restic didn't report the snapshot it made.");
            status = status with
            {
                State = "Succeeded", Snapshot = summary.Snapshot, Added = summary.Added, Total = summary.Total,
                Message = result.Exit == 3 ? "Some files couldn't be read and were left out. " + LastLine(result.Stderr) : null,
            };
            var forget = await RunAsync(repo, ["forget", "--tag", StackTag + name, "--group-by", "", "--keep-daily", Keep(repo.KeepDaily),
                "--keep-weekly", Keep(repo.KeepWeekly), "--keep-monthly", Keep(repo.KeepMonthly), "--prune", "--retry-lock", "30m"],
                TimeSpan.FromHours(4), token);
            if (forget.Exit != 0)
                status = status with { Message = ((status.Message ?? "") + " Old snapshots weren't pruned: " + LastLine(forget.Stderr)).Trim() };
        }
        finally { gate.Release(); }
        listedAt = DateTimeOffset.MinValue;
        return status;
    }

    /// <summary>Restores a snapshot of the stack into <paramref name="target"/>, which must not exist yet.</summary>
    internal static async Task RestoreAsync(string name, string snapshot, string target, CancellationToken token)
    {
        if (!SnapshotPattern().IsMatch(snapshot)) throw new NodeAgentException("The snapshot id is invalid.");
        var repo = Volatile.Read(ref repository) ?? throw new NodeAgentException("This server hasn't received the backup destination yet.");
        await gate.WaitAsync(token);
        try
        {
            await ReadyAsync(repo, token);
            var listed = await RunAsync(repo, ["snapshots", "--json", "--no-lock", snapshot], TimeSpan.FromMinutes(5), token);
            if (listed.Exit != 0) throw Failure("snapshots", listed);
            var found = ParseSnapshots(listed.Stdout).FirstOrDefault(item => item.Id == snapshot);
            if (found?.Stack != name) throw new NodeAgentException($"Snapshot {snapshot[..8]} isn't a backup of {name}.");
            var restored = await RunAsync(repo, ["restore", $"{snapshot}:{StackRunner.Root}/{name}", "--target", target], TimeSpan.FromHours(10), token);
            if (restored.Exit != 0) throw Failure("restore", restored);
        }
        finally { gate.Release(); }
    }

    private static async Task ListAsync(NodeBackupRepository repo, CancellationToken token)
    {
        try
        {
            if (!NasMounts.IsMounted(repo.Nas, repo.Share)) throw new NodeAgentException(NotMounted(repo));
            await InstallAsync(token);
            var result = await RunAsync(repo, ["snapshots", "--json", "--no-lock"], TimeSpan.FromMinutes(5), token);
            // 10: there's no repository there yet; the first backup creates it.
            if (result.Exit == 10) Publish(repo, [], null);
            else if (result.Exit != 0) throw Failure("snapshots", result);
            else Publish(repo, ParseSnapshots(result.Stdout), null);
        }
        catch (Exception ex) when (ex is NodeAgentException or IOException or UnauthorizedAccessException or JsonException
            || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            Publish(repo, Volatile.Read(ref snapshots), StackRunner.Bounded(ex.Message, 1000));
        }
    }

    private static void Publish(NodeBackupRepository repo, NodeSnapshot[] list, string? problem)
    {
        if (Volatile.Read(ref repository) != repo) return;
        Volatile.Write(ref snapshots, list);
        Volatile.Write(ref error, problem);
    }

    /// <summary>Installs restic, checks the share is really mounted, and creates the repository the first time.</summary>
    private static async Task ReadyAsync(NodeBackupRepository repo, CancellationToken token)
    {
        // Writing into an unmounted mount point would fill this server's disk with a repository nobody else sees.
        if (!NasMounts.IsMounted(repo.Nas, repo.Share)) throw new NodeAgentException(NotMounted(repo));
        await InstallAsync(token);
        var config = await RunAsync(repo, ["cat", "config"], TimeSpan.FromMinutes(2), token);
        if (config.Exit == 0) return;
        if (config.Exit != 10) throw Failure("cat config", config);
        var created = await RunAsync(repo, ["init"], TimeSpan.FromMinutes(5), token);
        if (created.Exit != 0) throw Failure("init", created);
        Console.Error.WriteLine("Created the backup repository at " + repo.Path + ".");
    }

    private static async Task InstallAsync(CancellationToken token)
    {
        if (File.Exists(Restic)) return;
        await install.WaitAsync(token);
        try
        {
            if (File.Exists(Restic)) return;
            Console.Error.WriteLine("Installing restic for backups.");
            await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "update"], TimeSpan.FromMinutes(10), token);
            await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "-o", "Dpkg::Options::=--force-confold",
                "install", "-y", "--no-install-recommends", "restic"], TimeSpan.FromMinutes(15), token);
        }
        finally { install.Release(); }
    }

    private static Task<(int Exit, string Stdout, string Stderr)> RunAsync(NodeBackupRepository repo, string[] arguments, TimeSpan limit,
        CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new NodeAgentException("Backups run only on Linux nodes.");
        Directory.CreateDirectory(Cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The password travels in the environment, never on the command line where any user can read it.
        return Commands.CaptureAsync(Restic, arguments, limit, token, 4 * 1024 * 1024, failOnError: false,
            new Dictionary<string, string>
            {
                ["RESTIC_REPOSITORY"] = repo.Path, ["RESTIC_PASSWORD"] = repo.Password, ["RESTIC_CACHE_DIR"] = Cache,
            });
    }

    /// <summary>Lucia's own bookkeeping, and paths the app declares rebuildable, such as downloaded models.</summary>
    internal static string[] Excludes(string directory, string[] extra) =>
    [
        .. new[] { ".lucia-applied.json", ".lucia-received", ".lucia-sent", StatusFile, StackRunner.RestoredMarker }.Select(file => directory + "/" + file),
        .. Declared(extra).Select(path => directory + "/" + path),
    ];

    internal static IEnumerable<string> Declared(string[] extra) =>
        extra.Where(path => ExcludePattern().IsMatch(path) && !path.Split('/').Any(part => part is "." or "..")).Take(32);

    /// <summary>Reads the <c>summary</c> line of <c>restic backup --json</c>.</summary>
    internal static (string Snapshot, long? Added, long? Total)? ParseSummary(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                var item = JsonDocument.Parse(line).RootElement;
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("message_type", out var type) || type.ValueKind != JsonValueKind.String
                    || type.GetString() != "summary") continue;
                if (!item.TryGetProperty("snapshot_id", out var id) || id.ValueKind != JsonValueKind.String || !SnapshotPattern().IsMatch(id.GetString()!))
                    return null;
                return (id.GetString()!, Number(item, "data_added"), Number(item, "total_bytes_processed"));
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>Reads <c>restic snapshots --json</c>, keeping only snapshots Lucia made.</summary>
    internal static NodeSnapshot[] ParseSnapshots(string output)
    {
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var list = new List<NodeSnapshot>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (list.Count == 4096) break;
            if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { } id || !SnapshotPattern().IsMatch(id)
                || Text(item, "time") is not { } time || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                || !item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) continue;
            var stack = tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString()!)
                .FirstOrDefault(tag => tag.StartsWith(StackTag, StringComparison.Ordinal))?[StackTag.Length..];
            if (stack is null || !StackRunner.NamePattern().IsMatch(stack)) continue;
            long? size = item.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object
                ? Number(summary, "total_bytes_processed") : null;
            list.Add(new(id, stack, StackRunner.Bounded(Text(item, "hostname"), 253) ?? "unknown", at.ToUniversalTime(), size));
        }
        return list.ToArray();
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static bool Valid(NodeBackupRepository repo) =>
        NasPartPattern().IsMatch(repo.Nas ?? "") && NasPartPattern().IsMatch(repo.Share ?? "") && repo.Password is { Length: >= 16 and <= 256 }
        && repo.Path is { } path && (path == $"{NasMounts.Root}/{repo.Nas}/{repo.Share}" || path.StartsWith($"{NasMounts.Root}/{repo.Nas}/{repo.Share}/", StringComparison.Ordinal))
        && PathPattern().IsMatch(path) && !path.Split('/').Any(part => part is "." or "..")
        && repo.KeepDaily is >= 0 and <= 365 && repo.KeepWeekly is >= 0 and <= 520 && repo.KeepMonthly is >= 0 and <= 240;

    private static string NotMounted(NodeBackupRepository repo) => $"The backup share {repo.Nas}/{repo.Share} isn't mounted on this server.";
    private static string Keep(int count) => count.ToString(CultureInfo.InvariantCulture);
    private static string LastLine(string text) =>
        StackRunner.Bounded(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(), 600) ?? "";
    private static NodeAgentException Failure(string command, (int Exit, string Stdout, string Stderr) result) =>
        new($"'restic {command}' exited with code {result.Exit}. {LastLine(result.Stderr)}".Trim());

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex SnapshotPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex NasPartPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9/._-]{1,512}\z")]
    private static partial Regex PathPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,128}(?:/[A-Za-z0-9._-]{1,128}){0,7}\z")]
    private static partial Regex ExcludePattern();
}
