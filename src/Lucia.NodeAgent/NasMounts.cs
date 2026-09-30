using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal sealed record NodeMount(string Nas, string Share, string Kind, string Source, string? Username = null, string? Password = null);
internal sealed record NodeMountStatus(string Nas, string Share, string State, string? Message = null);

/// <summary>
/// Keeps every NAS share Lucia knows mounted at <c>/mnt/lucia/nas/&lt;nas&gt;/&lt;share&gt;</c> with plain systemd mount units, so
/// apps can bind-mount them by path. Automount would hand containers the empty trigger directory, and Docker is ordered
/// after remote filesystems so apps don't start against empty folders at boot. Every value is revalidated here because
/// it ends up in root-owned unit files.
/// </summary>
internal static partial class NasMounts
{
    internal const string Root = "/mnt/lucia/nas";
    private const string Units = "/etc/systemd/system", Credentials = "/etc/lucia/nas", Marker = "# Managed by Lucia.";
    private const string DockerDropIn = "/etc/systemd/system/docker.service.d/lucia-nas.conf";
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Error)> attempts = new();
    private static NodeMount[] desired = [];
    private static int running;

    /// <summary>Each desired share's state, read fresh from the kernel's mount table (a dead NFS server can't hang this).</summary>
    internal static NodeMountStatus[] Report()
    {
        var mounted = MountPoints();
        return Volatile.Read(ref desired).Select(mount =>
        {
            if (mounted.Contains(Where(mount))) return new NodeMountStatus(mount.Nas, mount.Share, "Mounted");
            return attempts.TryGetValue(Key(mount), out var attempt) && attempt.Error is { } error
                ? new NodeMountStatus(mount.Nas, mount.Share, "Failed", error)
                : new NodeMountStatus(mount.Nas, mount.Share, "Pending");
        }).ToArray();
    }

    /// <summary>The first NAS share this compose uses that isn't mounted, or null.</summary>
    internal static string? Unmounted(string compose)
    {
        var mounted = MountPoints();
        return PathPattern().Matches(compose).Select(match => match.Value).FirstOrDefault(path => !mounted.Contains(path)) is { } missing
            ? missing[(Root.Length + 1)..] : null;
    }

    internal static void Reconcile(NodeMount[] wanted, CancellationToken token)
    {
        var valid = wanted.Where(Valid).GroupBy(Key).Select(group => group.First()).Take(512).ToArray();
        Volatile.Write(ref desired, valid);
        if (!OperatingSystem.IsLinux() || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await EnsureAsync(valid, token); }
            catch (Exception ex) { Console.Error.WriteLine("NAS mounts could not be updated. " + ex.Message); }
            finally { Volatile.Write(ref running, 0); }
        });
    }

    private static async Task EnsureAsync(NodeMount[] mounts, CancellationToken token)
    {
        string? missing = null;
        foreach (var (kind, package) in new[] { ("nfs", "nfs-common"), ("smb", "cifs-utils") })
            if (mounts.Any(mount => mount.Kind == kind) && await NodeRuntime.InstalledVersionAsync(package, token) is null)
            {
                try
                {
                    Console.Error.WriteLine("Installing " + package + " for NAS shares.");
                    await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "update"], TimeSpan.FromMinutes(10), token);
                    await NodeRuntime.RunAsync("/usr/bin/apt-get", ["-o", "DPkg::Lock::Timeout=600", "-o", "Dpkg::Options::=--force-confold",
                        "install", "-y", "--no-install-recommends", package], TimeSpan.FromMinutes(15), token);
                }
                catch (NodeAgentException ex) { missing = $"Lucia couldn't install {package}. {ex.Message}"; }
            }
        var reload = false;
        if (mounts.Length > 0)
        {
            SecureStateDirectory.MakeReadableDirectory(Path.GetDirectoryName(DockerDropIn)!);
            reload |= NodeRuntime.WriteIfChanged(DockerDropIn, Encoding.UTF8.GetBytes(
                Marker + " Starts Docker after NAS shares so apps don't see empty folders.\n[Unit]\nWants=remote-fs.target\nAfter=remote-fs.target\n"));
            // ifupdown doesn't wait for allow-hotplug interfaces (Debian's default), so without this network-online.target
            // is reached before DHCP and every share fails to mount at boot.
            if (File.Exists("/usr/lib/systemd/system/ifupdown-wait-online.service"))
                await NodeRuntime.TryCaptureAsync("/usr/bin/systemctl", ["enable", "ifupdown-wait-online.service"], token);
        }
        SecureStateDirectory.MakePrivateDirectory(Credentials);
        var changed = new HashSet<string>();
        var rotated = mounts.Where(mount => mount.Kind == "smb").GroupBy(mount => mount.Nas).Where(group => WritePrivate(
            $"{Credentials}/{group.Key}.credentials", $"username={group.First().Username}\npassword={group.First().Password}\n"))
            .Select(group => group.Key).ToHashSet();
        foreach (var mount in mounts)
            if (WriteUnit(UnitName(mount), UnitText(mount)) || rotated.Contains(mount.Nas)) changed.Add(Key(mount));
        reload |= changed.Count > 0;
        var names = mounts.Select(UnitName).ToHashSet();
        foreach (var path in Directory.EnumerateFiles(Units, "mnt-lucia-nas-*.mount"))
        {
            var unit = Path.GetFileName(path);
            if (names.Contains(unit) || File.ReadLines(path).FirstOrDefault()?.StartsWith(Marker, StringComparison.Ordinal) != true) continue;
            await NodeRuntime.TryCaptureAsync("/usr/bin/systemctl", ["disable", "--now", unit], token);
            File.Delete(path);
            reload = true;
        }
        var nases = mounts.Where(mount => mount.Kind == "smb").Select(mount => mount.Nas + ".credentials").ToHashSet();
        foreach (var path in Directory.EnumerateFiles(Credentials, "*.credentials"))
            if (!nases.Contains(Path.GetFileName(path))) SecureStateDirectory.DeleteSystemFile(path);
        if (reload) await NodeRuntime.RunAsync("/usr/bin/systemctl", ["daemon-reload"], TimeSpan.FromMinutes(1), token);
        foreach (var key in attempts.Keys.Except(mounts.Select(Key))) attempts.TryRemove(key, out _);

        var mounted = MountPoints();
        foreach (var mount in mounts)
        {
            var key = Key(mount);
            var fresh = changed.Contains(key);
            if (!fresh && (mounted.Contains(Where(mount))
                || attempts.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last.At < RetryAfterFailure)) continue;
            if (missing is not null && !mounted.Contains(Where(mount)))
            {
                attempts[key] = (DateTimeOffset.UtcNow, missing);
                continue;
            }
            var unit = UnitName(mount);
            try
            {
                if (fresh) await NodeRuntime.RunAsync("/usr/bin/systemctl", ["enable", unit], TimeSpan.FromMinutes(1), token);
                await NodeRuntime.RunAsync("/usr/bin/systemctl", [fresh ? "restart" : "start", unit], TimeSpan.FromSeconds(90), token);
                attempts[key] = (DateTimeOffset.UtcNow, null);
                Console.Error.WriteLine($"NAS share {key} mounted.");
            }
            catch (Exception ex) when (ex is NodeAgentException || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                var log = await NodeRuntime.TryCaptureAsync("/usr/bin/journalctl", ["-u", unit, "-n", "4", "-o", "cat", "--no-pager"], token);
                var detail = (log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => !line.StartsWith("Mounting ", StringComparison.Ordinal) && !line.StartsWith(unit, StringComparison.Ordinal))
                    .LastOrDefault();
                attempts[key] = (DateTimeOffset.UtcNow, NodeRuntime.Bounded(detail ?? ex.Message, 500));
                Console.Error.WriteLine($"NAS share {key} didn't mount. {detail ?? ex.Message}");
            }
        }
    }

    internal static string UnitText(NodeMount mount)
    {
        var (type, options) = mount.Kind == "nfs"
            ? ("nfs", "_netdev,nofail,hard,noatime")
            : ("cifs", $"_netdev,nofail,credentials={Credentials}/{mount.Nas}.credentials,iocharset=utf8,file_mode=0666,dir_mode=0777");
        return $"""
            {Marker} Changes are overwritten.
            [Unit]
            Description=Lucia NAS share {mount.Nas}/{mount.Share}
            Wants=network-online.target
            After=network-online.target

            [Mount]
            What={mount.Source}
            Where={Where(mount)}
            Type={type}
            Options={options}
            TimeoutSec=30

            [Install]
            WantedBy=remote-fs.target

            """.ReplaceLineEndings("\n");
    }

    /// <summary>systemd's name for the mount unit: the path with <c>/</c> as <c>-</c> and a literal <c>-</c> as <c>\x2d</c>.</summary>
    internal static string UnitName(NodeMount mount) =>
        string.Join('-', Where(mount).Trim('/').Split('/').Select(part => part.Replace("-", @"\x2d", StringComparison.Ordinal))) + ".mount";

    internal static bool Valid(NodeMount mount) =>
        NasPattern().IsMatch(mount.Nas ?? "") && SharePattern().IsMatch(mount.Share ?? "") && mount.Kind switch
        {
            "nfs" => NfsSource().IsMatch(mount.Source ?? "") && !mount.Source!.Split('/').Any(part => part is "." or "..")
                && mount.Username is null && mount.Password is null,
            "smb" => SmbSource().IsMatch(mount.Source ?? "") && UserPattern().IsMatch(mount.Username ?? "")
                && mount.Password is { Length: > 0 and <= 256 } password && !password.Any(char.IsControl),
            _ => false,
        };

    private static string Where(NodeMount mount) => $"{Root}/{mount.Nas}/{mount.Share}";
    private static string Key(NodeMount mount) => mount.Nas + "/" + mount.Share;

    internal static bool IsMounted(string nas, string share) => MountPoints().Contains($"{Root}/{nas}/{share}");

    private static HashSet<string> MountPoints()
    {
        try
        {
            // Field 5 is the mount point; Lucia's paths never contain the characters mountinfo escapes.
            return File.ReadLines("/proc/self/mountinfo").Select(line => line.Split(' ')).Where(fields => fields.Length > 4)
                .Select(fields => fields[4]).Where(path => path.StartsWith(Root + "/", StringComparison.Ordinal)).ToHashSet();
        }
        catch (IOException) { return []; }
    }

    private static bool WriteUnit(string name, string text)
    {
        var path = Path.Combine(Units, name);
        if (File.Exists(path) && File.ReadAllText(path) == text) return false;
        var staging = Path.Combine(Units, ".lucia-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(staging, text);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(staging, path, overwrite: true);
        return true;
    }

    private static bool WritePrivate(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            if (SecureStateDirectory.ReadSystemFile(path).AsSpan().SequenceEqual(bytes)) return false;
        }
        catch (Exception ex) when (ex is NodeAgentException or IOException) { }
        SecureStateDirectory.WriteSystemFile(path, bytes);
        return true;
    }

    [GeneratedRegex(@"\A[a-z](?:[a-z0-9-]{0,30}[a-z0-9])?\z")]
    private static partial Regex NasPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex SharePattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.-]{0,252}:/(?:[A-Za-z0-9._-]+/?){0,16}\z")]
    private static partial Regex NfsSource();
    [GeneratedRegex(@"\A//[A-Za-z0-9][A-Za-z0-9.-]{0,252}/[A-Za-z0-9._$-]{1,80}\z")]
    private static partial Regex SmbSource();
    [GeneratedRegex(@"\A[A-Za-z0-9._@-]{1,64}\z")]
    private static partial Regex UserPattern();
    [GeneratedRegex(@"/mnt/lucia/nas/[a-z](?:[a-z0-9-]{0,30}[a-z0-9])?/[A-Za-z0-9][A-Za-z0-9._-]{0,63}(?=[/:""'\s]|\z)")]
    private static partial Regex PathPattern();
}
