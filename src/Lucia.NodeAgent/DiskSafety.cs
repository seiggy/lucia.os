using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

internal sealed class DiskSafety
{
    private readonly InventoryRoots roots;
    private readonly Func<string, string> blockNumber;
    internal DiskSafety() : this(new("/proc", "/sys", "/dev"), NativeBlockNumber) { }
    internal DiskSafety(InventoryRoots roots, Func<string, string> blockNumber)
    {
        this.roots = roots;
        this.blockNumber = blockNumber;
    }

    internal string BootId()
    {
        var value = Read(Path.Combine(roots.Proc, "sys", "kernel", "random", "boot_id"), 128).Trim();
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw UnsafeDisk();
        return value;
    }

    internal string VerifyUnused(HardwareReport report, string id)
    {
        var (number, members) = Resolve(report, id);
        foreach (var mount in Mounts())
            if (members.Contains(mount.Number)) throw UnsafeDisk();
        var swaps = Read(Path.Combine(roots.Proc, "swaps"), 64 * 1024).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (swaps.Length == 0 || !swaps[0].StartsWith("Filename", StringComparison.Ordinal)) throw UnsafeDisk();
        foreach (var swap in swaps.Skip(1))
        {
            var parts = swap.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || parts[0].Contains('\\')) throw UnsafeDisk();
            // Swap files are already covered by mounted backing filesystems. Raw swap must be a known block node.
            if (parts[1] == "partition" && members.Contains(blockNumber(MapDevice(parts[0])))) throw UnsafeDisk();
            if (parts[1] is not ("partition" or "file")) throw UnsafeDisk();
        }
        return number;
    }

    internal void VerifyInstalledMount(HardwareReport report, InstallPlan plan, string mountPoint)
    {
        InstallationRules.SameDisk(plan, report);
        var (number, members) = Resolve(report, plan.DiskId);
        if (number != plan.DeviceNumber || mountPoint is not ("/target" or "/")) throw UnsafeDisk();
        var mounts = Mounts();
        var root = mounts.SingleOrDefault(m => m.Point == mountPoint) ?? throw UnsafeDisk();
        if (root.Root != "/" || root.Type != "ext4" || root.Number == number || !members.Contains(root.Number)
            || blockNumber(MapDevice(root.Source)) != root.Number) throw UnsafeDisk();
        if (mountPoint == "/target")
        {
            foreach (var nested in mounts.Where(m => m.Point.StartsWith("/target/", StringComparison.Ordinal)))
            {
                if (nested.Point is "/target/boot/efi" && nested.Root == "/" && nested.Type == "vfat"
                    && nested.Number != number && members.Contains(nested.Number)) continue;
                // Fixed d-i bind mounts are outside every path written by stage-managed.
                if (new[] { "/target/dev", "/target/proc", "/target/sys", "/target/run" }
                    .Any(path => nested.Point == path || nested.Point.StartsWith(path + "/", StringComparison.Ordinal))) continue;
                throw UnsafeDisk();
            }
        }
    }

    private (string Number, HashSet<string> Members) Resolve(HardwareReport report, string id)
    {
        var disk = InstallationRules.SelectDisk(report, id);
        var alias = MapDevice(id);
        var target = new FileInfo(alias).ResolveLinkTarget(returnFinalTarget: true);
        var canonical = MapDevice(disk.Path);
        if (target is null || !string.Equals(target.FullName, Path.GetFullPath(canonical),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw UnsafeDisk();
        var number = blockNumber(canonical);
        if (blockNumber(alias) != number) throw UnsafeDisk();
        var sysDisk = Path.Combine(roots.Sys, "class", "block", Path.GetFileName(canonical));
        if (Read(Path.Combine(sysDisk, "dev"), 64).Trim() != number || File.Exists(Path.Combine(sysDisk, "partition")))
            throw UnsafeDisk();
        var realDisk = ResolvedDirectory(sysDisk);
        var members = new HashSet<string>(StringComparer.Ordinal) { number };
        var entries = Directory.EnumerateDirectories(Path.Combine(roots.Sys, "class", "block")).Take(513).ToArray();
        if (entries.Length > 512) throw UnsafeDisk();
        foreach (var entry in entries)
        {
            var real = ResolvedDirectory(entry);
            if (real != realDisk && !real.StartsWith(realDisk + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var member = Read(Path.Combine(entry, "dev"), 64).Trim();
            if (!Regex.IsMatch(member, @"\A[0-9]{1,10}:[0-9]{1,10}\z")) throw UnsafeDisk();
            members.Add(member);
            var holders = Path.Combine(entry, "holders");
            if (!Directory.Exists(holders) || Directory.EnumerateFileSystemEntries(holders).Any()) throw UnsafeDisk();
            if (Read(Path.Combine(entry, "ro"), 16).Trim() != "0") throw UnsafeDisk();
        }
        return (number, members);
    }

    private string MapDevice(string path)
    {
        if (!path.StartsWith("/dev/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)
            || path.Any(char.IsControl) || path.Contains('\\')) throw UnsafeDisk();
        return Path.Combine(roots.Dev, path[5..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ResolvedDirectory(string path) =>
        new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);

    private sealed record Mount(string Number, string Root, string Point, string Type, string Source);
    private Mount[] Mounts()
    {
        var result = new List<Mount>();
        foreach (var line in Read(Path.Combine(roots.Proc, "self", "mountinfo"), 1024 * 1024)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ');
            var separator = Array.IndexOf(parts, "-");
            if (parts.Length < 10 || separator < 6 || separator + 3 >= parts.Length
                || !Regex.IsMatch(parts[2], @"\A[0-9]+:[0-9]+\z")) throw UnsafeDisk();
            result.Add(new(parts[2], Unescape(parts[3]), Unescape(parts[4]), parts[separator + 1], Unescape(parts[separator + 2])));
            if (result.Count > 4096) throw UnsafeDisk();
        }
        if (result.Count == 0) throw UnsafeDisk();
        return result.ToArray();
    }

    private static string Unescape(string value) =>
        value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    internal static string Read(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        using var memory = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = stream.Read(bytes, 0, Math.Min(bytes.Length, limit + 1 - (int)memory.Length))) > 0)
        {
            memory.Write(bytes, 0, count);
            if (memory.Length > limit) throw new NodeAgentException("A required Linux state file exceeds its supported size.");
        }
        return new UTF8Encoding(false, true).GetString(memory.ToArray());
    }

    private static string NativeBlockNumber(string path)
    {
        // statx only: never open a block device, even read-only.
        if (Statx(-100, path, 0, 0x7ff, out var value) != 0 || (value.Mode & 0xf000) != 0x6000)
            throw UnsafeDisk();
        return $"{value.Major}:{value.Minor}";
    }

    private static NodeAgentException UnsafeDisk() =>
        new("The approved disk is absent, changed, mounted, in use, or cannot be verified. Installation is blocked.");

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct BlockStatus
    {
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(128)] public uint Major;
        [FieldOffset(132)] public uint Minor;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out BlockStatus stat);
}
