using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <param name="Mode"><c>live</c> backs the app up while it runs; <c>stop</c> stops it for the backup, which some databases
/// need to be consistent. Null uses the catalog app's choice, or <c>live</c>.</param>
public sealed record StackBackup(bool Enabled = true, string? Mode = null);
/// <summary>A restore in progress: the node stops the app, swaps the snapshot's data in, and starts it again.</summary>
public sealed record StackRestore(Guid Id, string Snapshot, DateTimeOffset StartedAt, string StartedBy);
/// <param name="TimeZone">The IANA zone the 03:00 schedule follows, normally the owner's browser zone.</param>
public sealed record SaveBackupDestinationRequest(string Nas, string Share, string? Folder, string? TimeZone);
public sealed record RestoreStackRequest(string Snapshot);

/// <param name="Exclude">Paths under the app's directory to skip.</param>
public sealed record NodeBackupRequest(Guid Id, string Mode, string[] Exclude);
public sealed record NodeRestoreRequest(Guid Id, string Snapshot, string[] Keep);
/// <summary>Where every node's backups go, and the key that opens the repository.</summary>
public sealed record NodeBackupRepository(string Nas, string Share, string Path, string Password, int KeepDaily, int KeepWeekly, int KeepMonthly);
/// <param name="State">Running, Succeeded or Failed.</param>
/// <param name="Added">Bytes this backup added to the repository after deduplication.</param>
/// <param name="Total">Bytes of app data the backup read.</param>
public sealed record NodeBackupStatus(Guid Id, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt = null,
    string? Snapshot = null, long? Added = null, long? Total = null, string? Message = null);
public sealed record NodeSnapshot(string Id, string Stack, string Host, DateTimeOffset Time, long? Size = null);

internal sealed record BackupDestination(string Nas, string Share, string Folder, string ProtectedPassword, string TimeZone,
    DateTimeOffset UpdatedAt, string UpdatedBy);
/// <param name="RequestedAt">When the latest backup was asked for; the schedule counts from it.</param>
internal sealed record BackupRecord(string Stack, Guid? Requested = null, DateTimeOffset? RequestedAt = null, string? RequestedBy = null,
    NodeBackupStatus? Last = null, string? LastNode = null, NodeBackupStatus? LastSuccess = null);
internal sealed record BackupFile(int SchemaVersion, BackupDestination? Destination, BackupRecord[] Stacks);

/// <summary>
/// Backs every app up with restic into one repository on a NAS share. Lucia keeps the schedule: when an app is due, its
/// node's next sync carries a backup request, and the node's report closes it. Snapshots are listed by the nodes, which
/// are the only machines that mount the repository.
/// </summary>
public sealed partial class StackStore
{
    public const int KeepDaily = 7, KeepWeekly = 4, KeepMonthly = 6;
    private static readonly TimeSpan BackupAt = new(3, 0, 0), BackupTimeout = TimeSpan.FromHours(12);
    private readonly IDataProtector _backupProtector = protection.CreateProtector("Lucia.Homelab.BackupRepository.v1");
    private string BackupPath => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "stacks", "backups.json");

    /// <summary>The destination, the schedule, and each app's last backup, next run and snapshots.</summary>
    public async Task<object> Backups(CancellationToken ct)
    {
        var facts = await nodes.Facts(ct);
        BackupFile file;
        StoredStack[] stacks;
        StoredNas[] servers;
        await _gate.WaitAsync(ct);
        try { (file, stacks, servers) = (ReadBackupsUnlocked(), ReadUnlocked(), ReadNasUnlocked()); }
        finally { _gate.Release(); }
        var snapshots = Snapshots();
        var zone = Zone(file.Destination?.TimeZone);
        var records = file.Stacks.ToDictionary(record => record.Stack);
        return new
        {
            destination = file.Destination is { } destination ? new
            {
                destination.Nas, destination.Share, destination.Folder, path = RepositoryPath(destination),
                source = Source(servers, destination), destination.TimeZone, destination.UpdatedAt, destination.UpdatedBy,
                mounts = facts.Select(node => new
                {
                    node = node.Hostname,
                    status = Fresh(node.NodeId)?.Report.Mounts?.FirstOrDefault(item => item.Nas == destination.Nas && item.Share == destination.Share),
                    repositoryError = Fresh(node.NodeId)?.Report.RepositoryError,
                }).ToArray(),
            } : null,
            schedule = new { time = "03:00", keepDaily = KeepDaily, keepWeekly = KeepWeekly, keepMonthly = KeepMonthly },
            shares = servers.SelectMany(nas => nas.Shares.Select(share => new { nas = nas.Id, share = share.Name, mountPath = $"{NasRoot}/{nas.Id}/{share.Name}" })).ToArray(),
            apps = stacks.Select(stack =>
            {
                var record = records.GetValueOrDefault(stack.Name);
                var (enabled, mode, exclude) = Policy(stack);
                var node = facts.FirstOrDefault(item => item.Hostname == stack.Assigned);
                var reported = node is null ? null : Fresh(node.NodeId)?.Report.Stacks.FirstOrDefault(item => item.Name == stack.Name)?.Backup;
                return new
                {
                    stack.Name, node = stack.Assigned, enabled, mode, exclude,
                    requested = record?.Requested is null ? null : new { at = record.RequestedAt, by = record.RequestedBy },
                    running = reported is { State: "Running" } && reported.Id == record?.Requested ? reported : null,
                    last = record?.Last, lastNode = record?.LastNode, lastSuccess = record?.LastSuccess,
                    nextRun = file.Destination is null || !enabled || record?.Requested is not null ? (DateTimeOffset?)null
                        : Due(record, stack, file.Destination, zone),
                    snapshots = snapshots.Where(item => item.Stack == stack.Name).OrderByDescending(item => item.Time).Take(100).ToArray(),
                };
            }).ToArray(),
        };
    }

    /// <summary>Chooses where backups go. The repository key is generated once and kept when the destination changes.</summary>
    public async Task<object> SaveBackupDestination(SaveBackupDestinationRequest request, string actor, CancellationToken ct)
    {
        var folder = (request.Folder ?? "").Trim().Trim('/');
        if (folder.Length > 0 && (!BackupFolderPattern().IsMatch(folder) || folder.Split('/').Any(part => part is "." or "..")))
            throw new HardwareOnboardingException(400, "invalid_backup_folder",
                "Use letters, digits, dots, dashes and underscores for the folder, with / between levels, such as lucia.");
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone.Trim();
        if (zoneId.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out _))
            throw new HardwareOnboardingException(400, "invalid_time_zone", $"Lucia doesn't know the time zone \"{Short(zoneId)}\".");
        await _gate.WaitAsync(ct);
        try
        {
            if (!ReadNasUnlocked().Any(nas => nas.Id == request.Nas && nas.Shares.Any(share => share.Name == request.Share)))
                throw new HardwareOnboardingException(400, "unknown_nas_share", "Choose a share from a connected NAS.");
            var file = ReadBackupsUnlocked();
            // Groups of letters and digits that can't be confused on paper, about 190 bits.
            var password = file.Destination?.ProtectedPassword ?? _backupProtector.Protect(string.Join('-',
                Enumerable.Range(0, 8).Select(_ => RandomNumberGenerator.GetString("abcdefghjkmnpqrstuvwxyz23456789", 5))));
            await WriteBackups(file with
            {
                Destination = new(request.Nas, request.Share, folder, password, zoneId, time.GetUtcNow(), actor),
            }, ct);
            return new { saved = true };
        }
        finally { _gate.Release(); }
    }

    /// <summary>What someone needs to open the repository without Lucia: where it is and its key.</summary>
    public async Task<object> BackupRecovery(CancellationToken ct)
    {
        BackupFile file;
        StoredNas[] servers;
        await _gate.WaitAsync(ct);
        try { (file, servers) = (ReadBackupsUnlocked(), ReadNasUnlocked()); }
        finally { _gate.Release(); }
        var destination = file.Destination
            ?? throw new HardwareOnboardingException(404, "no_backup_destination", "Choose where backups go first.");
        return new
        {
            repository = RepositoryPath(destination), source = Source(servers, destination),
            kind = servers.FirstOrDefault(nas => nas.Id == destination.Nas)?.Kind,
            password = _backupProtector.Unprotect(destination.ProtectedPassword), destination.UpdatedAt,
        };
    }

    /// <summary>Asks the app's server to back it up at its next sync.</summary>
    public async Task<object> RunBackup(string name, string actor, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stack = Find(ReadUnlocked(), name);
            RequireSettled(stack);
            var file = ReadBackupsUnlocked();
            if (file.Destination is null) throw new HardwareOnboardingException(409, "no_backup_destination", "Choose where backups go first.");
            var record = file.Stacks.FirstOrDefault(item => item.Stack == name) ?? new BackupRecord(name);
            if (record.Requested is not null)
                throw new HardwareOnboardingException(409, "backup_pending", $"A backup of {name} is already waiting or running.");
            await WriteBackups(file with
            {
                Stacks = [.. file.Stacks.Where(item => item.Stack != name),
                    record with { Requested = Guid.NewGuid(), RequestedAt = time.GetUtcNow(), RequestedBy = actor }],
            }, ct);
            return new { requested = true };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Turns an app's backups on or off and chooses live or stopped. Nodes get this with each request, so the stack isn't reapplied.</summary>
    public async Task<object> SaveStackBackup(string name, StackBackup request, string actor, CancellationToken ct)
    {
        var backup = ValidBackup(request);
        var names = await NodeNames(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var stack = Find(stacks, name);
            var changed = stack with { Manifest = stack.Manifest with { Backup = backup }, UpdatedAt = time.GetUtcNow(), UpdatedBy = actor };
            stacks[stacks.IndexOf(stack)] = changed;
            await Write(stacks, ct);
            return Summary(changed, names);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Replaces an app's data with a snapshot on the server it runs on. The node keeps the current data beside it as
    /// <c>.replaced-&lt;app&gt;-&lt;time&gt;</c>, and a pending backup is dropped so it can't capture half-restored data.
    /// </summary>
    public async Task<object> Restore(string name, RestoreStackRequest request, string actor, CancellationToken ct)
    {
        var snapshot = (request.Snapshot ?? "").Trim();
        if (!SnapshotPattern().IsMatch(snapshot))
            throw new HardwareOnboardingException(400, "invalid_snapshot", "Choose one of the app's snapshots.");
        if (!Snapshots().Any(item => item.Id == snapshot && item.Stack == name))
            throw new HardwareOnboardingException(404, "unknown_snapshot", $"That snapshot isn't a backup of {name}, or no server has listed it yet.");
        var names = await NodeNames(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var stack = Find(stacks, name);
            RequireSettled(stack);
            var changed = stack with
            {
                Restore = new(Guid.NewGuid(), snapshot, time.GetUtcNow(), actor), UpdatedAt = time.GetUtcNow(), UpdatedBy = actor,
            };
            stacks[stacks.IndexOf(stack)] = changed;
            await Write(stacks, ct);
            var file = ReadBackupsUnlocked();
            if (file.Stacks.FirstOrDefault(item => item.Stack == name) is { Requested: not null } pending)
                await WriteBackups(file with { Stacks = [.. file.Stacks.Where(item => item != pending), pending with { Requested = null }] }, ct);
            return Summary(changed, names);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The repository for a node's sync, or null until the owner chooses a destination.</summary>
    internal async Task<NodeBackupRepository?> BackupRepository(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ReadBackupsUnlocked().Destination is { } destination
                ? new(destination.Nas, destination.Share, RepositoryPath(destination), _backupProtector.Unprotect(destination.ProtectedPassword),
                    KeepDaily, KeepWeekly, KeepMonthly)
                : null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Closes backups the node has finished, fails ones it never finished, and asks for the ones that are due. Runs under
    /// the gate from <see cref="Sync"/>. Returns the request each of this node's stacks should carry.
    /// </summary>
    private async Task<Dictionary<string, NodeBackupRequest>> SyncBackups(StoredStack[] stacks, string hostname, NodeStackReport report,
        CancellationToken ct)
    {
        var file = ReadBackupsUnlocked();
        var records = file.Stacks.ToDictionary(record => record.Stack);
        var now = time.GetUtcNow();
        var zone = Zone(file.Destination?.TimeZone);
        var changed = false;
        var requests = new Dictionary<string, NodeBackupRequest>();
        foreach (var stack in stacks.Where(item => item.Assigned == hostname && item.Move is null && item.Restore is null))
        {
            var before = records.GetValueOrDefault(stack.Name) ?? new BackupRecord(stack.Name);
            var record = before;
            var status = report.Stacks.FirstOrDefault(item => item.Name == stack.Name)?.Backup;
            if (record.Requested is { } id)
            {
                if (status is { State: "Succeeded" or "Failed" } done && done.Id == id)
                    record = record with { Requested = null, Last = done, LastNode = hostname, LastSuccess = done.State == "Succeeded" ? done : record.LastSuccess };
                else if (!(status is { State: "Running" } && status.Id == id) && now - record.RequestedAt > BackupTimeout)
                    record = record with
                    {
                        Requested = null, LastNode = hostname,
                        Last = new(id, "Failed", record.RequestedAt!.Value, now, Message: $"{hostname} didn't finish this backup within 12 hours."),
                    };
            }
            var (enabled, mode, exclude) = Policy(stack);
            if (record.Requested is null && file.Destination is not null && enabled && now >= Due(record, stack, file.Destination, zone))
                record = record with { Requested = Guid.NewGuid(), RequestedAt = now, RequestedBy = "schedule" };
            if (record != before) { records[stack.Name] = record; changed = true; }
            if (record.Requested is { } pending && file.Destination is not null) requests[stack.Name] = new(pending, mode, exclude);
        }
        if (changed) await WriteBackups(file with { Stacks = records.Values.ToArray() }, ct);
        return requests;
    }

    /// <summary>A never-backed-up app waits for the first night after the destination is saved, as the setup screen promises.</summary>
    private static DateTimeOffset Due(BackupRecord? record, StoredStack stack, BackupDestination destination, TimeZoneInfo zone) =>
        NextRun(new[] { record?.RequestedAt ?? stack.CreatedAt, destination.UpdatedAt }.Max(), zone);

    /// <summary>The first 03:00 in <paramref name="zone"/> after <paramref name="after"/>. A day whose 03:00 doesn't exist runs at 04:00.</summary>
    internal static DateTimeOffset NextRun(DateTimeOffset after, TimeZoneInfo zone)
    {
        var day = TimeZoneInfo.ConvertTime(after, zone).Date;
        while (true)
        {
            var at = day + BackupAt;
            if (zone.IsInvalidTime(at)) at = at.AddHours(1);
            var candidate = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(at, zone), TimeSpan.Zero);
            if (candidate > after) return candidate;
            day = day.AddDays(1);
        }
    }

    private static TimeZoneInfo Zone(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    private static (bool Enabled, string Mode, string[] Exclude) Policy(StoredStack stack)
    {
        var app = stack.Manifest.Template is { } template ? StackCatalog.Apps.FirstOrDefault(item => item.Id == template.Id) : null;
        return (stack.Manifest.Backup?.Enabled ?? true, stack.Manifest.Backup?.Mode ?? app?.BackupMode ?? "live", app?.BackupExclude ?? []);
    }

    internal static StackBackup ValidBackup(StackBackup backup) => backup.Mode is null or "live" or "stop" ? backup
        : throw new HardwareOnboardingException(400, "invalid_backup_mode", "Choose live or stop for how the app is backed up.");

    /// <summary>Every Lucia snapshot the nodes listed in their last fresh reports.</summary>
    private NodeSnapshot[] Snapshots() => _reports.Keys.Select(Fresh).OfType<(DateTimeOffset At, NodeStackReport Report)>()
        .OrderByDescending(entry => entry.At).SelectMany(entry => entry.Report.Snapshots ?? []).DistinctBy(item => item.Id).ToArray();

    private static string RepositoryPath(BackupDestination destination) =>
        $"{NasRoot}/{destination.Nas}/{destination.Share}" + (destination.Folder.Length > 0 ? "/" + destination.Folder : "");

    /// <summary>The repository as the NAS names it, for opening it from any machine.</summary>
    private static string? Source(StoredNas[] servers, BackupDestination destination)
    {
        if (servers.FirstOrDefault(nas => nas.Id == destination.Nas) is not { } nas
            || nas.Shares.FirstOrDefault(share => share.Name == destination.Share) is not { } share) return null;
        var folder = destination.Folder.Length > 0 ? "/" + destination.Folder : "";
        return nas.Kind == "nfs" ? $"{nas.Host}:{share.Path.TrimEnd('/')}{folder}" : $"//{nas.Host}/{share.Path}{folder}";
    }

    /// <summary>True while the share holds the backup repository. Call under the gate.</summary>
    private bool HoldsBackups(string nas, string? share = null) =>
        ReadBackupsUnlocked().Destination is { } destination && destination.Nas == nas && (share is null || destination.Share == share);

    internal static void ValidateBackupStatus(NodeBackupStatus? status)
    {
        if (status is null) return;
        HardwareInventoryValidation.Require(status.State is "Running" or "Succeeded" or "Failed"
            && (status.Snapshot is null || SnapshotPattern().IsMatch(status.Snapshot)) && status.Added is null or >= 0 && status.Total is null or >= 0,
            "The backup report is invalid.");
        HardwareInventoryValidation.Text(status.Message, 1024);
    }

    internal static void ValidateSnapshots(NodeSnapshot[]? snapshots)
    {
        if (snapshots is null) return;
        HardwareInventoryValidation.Require(snapshots.Length <= 4096, "The snapshot report is oversized.");
        foreach (var snapshot in snapshots)
        {
            HardwareInventoryValidation.Require(SnapshotPattern().IsMatch(snapshot.Id ?? "") && NamePattern().IsMatch(snapshot.Stack ?? "")
                && snapshot.Size is null or >= 0, "The snapshot report is invalid.");
            HardwareInventoryValidation.Text(snapshot.Host, 253, true);
        }
    }

    private BackupFile ReadBackupsUnlocked()
    {
        DomainOnboardingStore.RejectLinks(BackupPath);
        if (!File.Exists(BackupPath)) return new(1, null, []);
        var file = JsonSerializer.Deserialize<BackupFile>(CertbotFiles.ReadBounded(BackupPath, 4 * 1024 * 1024), DomainOnboardingStore.Json);
        if (file is not { SchemaVersion: 1, Stacks: not null }) throw new InvalidDataException("Backup state is invalid.");
        return file;
    }

    private Task WriteBackups(BackupFile file, CancellationToken ct) =>
        DomainOnboardingStore.WriteJson(BackupPath, file with { Stacks = file.Stacks.OrderBy(item => item.Stack, StringComparer.Ordinal).ToArray() }, ct);

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    internal static partial Regex SnapshotPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}(?:/[A-Za-z0-9._-]{1,64}){0,3}\z")]
    private static partial Regex BackupFolderPattern();
}
