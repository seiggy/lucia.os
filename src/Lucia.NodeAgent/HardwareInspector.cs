using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Lucia.NodeAgent;

public sealed record InventoryRoots(string Proc, string Sys, string Dev);

public sealed class HardwareInspector
{
    private readonly InventoryRoots roots;
    private readonly string architecture;
    private readonly Func<IReadOnlyDictionary<string, string[]>> addresses;
    private readonly Action? betweenDiskReads;

    public HardwareInspector()
    {
        if (!OperatingSystem.IsLinux())
            throw new NodeAgentException("Live hardware inspection is supported only on Linux.");
        roots = new("/proc", "/sys", "/dev");
        architecture = ArchitectureName(RuntimeInformation.OSArchitecture);
        addresses = LiveAddresses;
    }

    // Fixture injection is deliberately not exposed as a CLI option.
    internal HardwareInspector(InventoryRoots roots, Architecture architecture,
        Func<IReadOnlyDictionary<string, string[]>> addresses, Action? betweenDiskReads = null)
    {
        this.roots = new(Path.GetFullPath(roots.Proc), Path.GetFullPath(roots.Sys), Path.GetFullPath(roots.Dev));
        this.architecture = ArchitectureName(architecture);
        this.addresses = addresses;
        this.betweenDiskReads = betweenDiskReads;
    }

    public HardwareReport Inspect()
    {
        try
        {
            RequireDirectory(roots.Proc);
            RequireDirectory(roots.Sys);
            RequireDirectory(roots.Dev);
            var firstDisks = ReadDisks();
            var cpu = ReadCpu();
            var uefi = Directory.Exists(Path.Combine(roots.Sys, "firmware", "efi"));
            var report = new HardwareReport(
                architecture, uefi ? "uefi" : "bios", uefi ? ReadSecureBoot() : null,
                Dmi("sys_vendor"), Dmi("product_name"), Dmi("product_serial"), Dmi("product_uuid"),
                cpu.Model, cpu.Count, ReadMemory(), ReadInterfaces(), firstDisks.Select(d => d.Report).ToArray());
            betweenDiskReads?.Invoke();
            if (!firstDisks.SequenceEqual(ReadDisks()))
                throw new NodeAgentException("Block devices changed during inspection. Retry after hardware has settled.");
            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NetworkInformationException
                                   or OverflowException or FormatException or DecoderFallbackException)
        {
            throw new NodeAgentException("Hardware inventory could not be read consistently. Check procfs, sysfs, device nodes and permissions, then retry.");
        }
    }

    private (string? Model, int Count) ReadCpu()
    {
        var cpuInfo = ReadText(Path.Combine(roots.Proc, "cpuinfo"), 4 * 1024 * 1024);
        var ids = new HashSet<int>();
        string? model = null;
        foreach (var line in cpuInfo.Split('\n'))
        {
            var pair = line.Split(':', 2);
            if (pair.Length != 2) continue;
            var field = pair[0].Trim();
            if (field == "processor")
            {
                if (!int.TryParse(pair[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id < 0 || !ids.Add(id) || ids.Count > 4096)
                    throw new NodeAgentException("CPU inventory is malformed or exceeds the 4096 logical CPU limit.");
            }
            if (field is "model name" or "Processor")
                model ??= Clean(pair[1]);
        }
        if (ids.Count == 0)
            throw new NodeAgentException("No logical CPUs were found in /proc/cpuinfo.");
        return (model, ids.Count);
    }

    private long ReadMemory()
    {
        var values = ReadText(Path.Combine(roots.Proc, "meminfo"), 64 * 1024).Split('\n')
            .Where(line => line.StartsWith("MemTotal:", StringComparison.Ordinal)).ToArray();
        if (values.Length != 1)
            throw new NodeAgentException("MemTotal is missing or duplicated in /proc/meminfo.");
        var parts = values[0]["MemTotal:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[1] != "kB"
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib) || kib <= 0)
            throw new NodeAgentException("MemTotal in /proc/meminfo is malformed.");
        if (kib > 1_125_899_906_842_624 / 1024)
            throw new NodeAgentException("Memory exceeds the supported 1125899906842624-byte inventory limit.");
        return checked(kib * 1024);
    }

    private bool? ReadSecureBoot()
    {
        var path = Path.Combine(roots.Sys, "firmware", "efi", "efivars",
            "SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c");
        try
        {
            var data = ReadBytes(path, 5);
            return data.Length == 5 ? data[4] switch { 0 => false, 1 => true, _ => null } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NodeAgentException)
        {
            return null;
        }
    }

    private NetworkInterfaceReport[] ReadInterfaces()
    {
        var networkAddresses = addresses();
        // Docker adds a bridge per network and a veth per container; they're not the server's hardware.
        var entries = Entries(Path.Combine(roots.Sys, "class", "net"), 1024)
            .Where(entry => Path.GetFileName(entry) is var name && name != "lo" && name != "docker0"
                && !name.StartsWith("veth", StringComparison.Ordinal) && !Regex.IsMatch(name, @"\Abr-[0-9a-f]{12}\z", RegexOptions.CultureInvariant))
            .ToArray();
        if (entries.Length is < 1 or > 16)
            throw new NodeAgentException("Inventory requires 1..16 non-loopback interfaces; interfaces will not be omitted to fit the limit.");
        return entries.Select(entry =>
            {
                var name = DeviceName(entry);
                var mac = OptionalText(Path.Combine(entry, "address"));
                if (mac is not null && !Regex.IsMatch(mac, @"\A(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}\z",
                        RegexOptions.CultureInvariant))
                    mac = null;
                var ips = networkAddresses.GetValueOrDefault(name) ?? [];
                if (ips.Length > 8)
                    throw new NodeAgentException("A network interface exceeds the eight-address inventory limit; addresses will not be omitted.");
                // The interface name identifies the scope; preserve address bytes without an IPv6 zone suffix.
                var normalized = ips.Select(ip => IPAddress.TryParse(ip, out var parsed)
                    ? new IPAddress(parsed.GetAddressBytes()).ToString()
                    : throw new NodeAgentException("A network interface address is malformed."))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new NetworkInterfaceReport(name, mac?.ToLowerInvariant(), normalized);
            }).ToArray();
    }

    private sealed record DiskSnapshot(DiskReport Report, string DeviceNumber);

    private DiskSnapshot[] ReadDisks()
    {
        var ids = ReadStableIds();
        var result = new List<DiskSnapshot>();
        foreach (var entry in Entries(Path.Combine(roots.Sys, "class", "block"), 512))
        {
            var name = DeviceName(entry);
            if (Regex.IsMatch(name, @"\A(?:loop|ram|zram|sr|scd|fd)[0-9]", RegexOptions.CultureInvariant)
                || File.Exists(Path.Combine(entry, "partition"))
                || !Directory.Exists(Path.Combine(entry, "device"))
                || OptionalText(Path.Combine(entry, "device", "type")) == "5")
                continue;
            if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)
                || name.Contains("..", StringComparison.Ordinal))
                throw new NodeAgentException("A whole-device path is outside the supported safe 1..64 character component format.");
            if (!File.Exists(Path.Combine(roots.Dev, name)))
                throw new NodeAgentException("A whole-device node disappeared during inspection. Retry after hardware has settled.");
            var size = checked(ReadNonnegative(Path.Combine(entry, "size")) * 512);
            if (size == 0)
                throw new NodeAgentException("A whole device reported no capacity. Retry after hardware has settled.");
            if (size > 1_152_921_504_606_846_976)
                throw new NodeAgentException("A whole device exceeds the supported 1152921504606846976-byte inventory limit.");
            var number = ReadText(Path.Combine(entry, "dev"), 64).Trim();
            if (!Regex.IsMatch(number, @"\A[0-9]{1,10}:[0-9]{1,10}\z", RegexOptions.CultureInvariant))
                throw new NodeAgentException("A block device number is malformed.");
            result.Add(new(new(
                ids.GetValueOrDefault(name), "/dev/" + name,
                OptionalText(Path.Combine(entry, "device", "model")),
                OptionalText(Path.Combine(entry, "device", "serial")), size,
                ReadBoolean(Path.Combine(entry, "removable")), ReadBoolean(Path.Combine(entry, "ro"))), number));
            if (result.Count > 32)
                throw new NodeAgentException("The system exceeds the 32 whole-device inventory limit; devices will not be omitted.");
        }
        return result.ToArray();
    }

    private Dictionary<string, string> ReadStableIds()
    {
        var directory = Path.Combine(roots.Dev, "disk", "by-id");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return result;
        foreach (var alias in Entries(directory, 2048))
        {
            var name = Path.GetFileName(alias);
            if (!Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._:+-]{0,199}\z", RegexOptions.CultureInvariant)
                || name.Contains("..", StringComparison.Ordinal)
                || Regex.IsMatch(name, @"-part[0-9]+\z", RegexOptions.CultureInvariant))
                continue;
            var file = new FileInfo(alias);
            if (file.LinkTarget is null) continue;
            FileSystemInfo? target;
            try { target = file.ResolveLinkTarget(returnFinalTarget: true); }
            catch (IOException) { continue; }
            if (target is null || !target.Exists
                || !string.Equals(Path.GetDirectoryName(target.FullName), roots.Dev, PathComparison))
                continue;
            // Sorted aliases choose one reproducibly; the node path is never substituted for a stable ID.
            result.TryAdd(target.Name, "/dev/disk/by-id/" + name);
        }
        return result;
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private string? Dmi(string name) => OptionalText(Path.Combine(roots.Sys, "class", "dmi", "id", name));

    private static long ReadNonnegative(string path)
    {
        if (!long.TryParse(ReadText(path, 128).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new NodeAgentException("A required numeric hardware field is malformed.");
        return value;
    }

    private static bool ReadBoolean(string path) => ReadNonnegative(path) switch
    {
        0 => false,
        1 => true,
        _ => throw new NodeAgentException("A required hardware flag is malformed.")
    };

    private static string DeviceName(string path)
    {
        var value = Path.GetFileName(path);
        if (value.Length is 0 or > 64 || !Regex.IsMatch(value, @"\A[a-zA-Z0-9_.:-]+\z", RegexOptions.CultureInvariant))
            throw new NodeAgentException("A hardware device name is invalid.");
        return value;
    }

    private static string[] Entries(string path, int limit)
    {
        RequireDirectory(path);
        var entries = Directory.EnumerateFileSystemEntries(path).Take(limit + 1).ToArray();
        if (entries.Length > limit)
            throw new NodeAgentException("A hardware directory exceeds the inventory entry limit.");
        Array.Sort(entries, StringComparer.Ordinal);
        return entries;
    }

    private static void RequireDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new NodeAgentException("Required procfs, sysfs or device directories are missing.");
    }

    private static string? OptionalText(string path)
    {
        try { return Clean(ReadText(path, 4096)); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Clean(string value)
    {
        var clean = string.Concat(value.Select(c => char.IsControl(c) || char.IsSurrogate(c) ? ' ' : c)).Trim();
        if (clean.Length > 256) clean = clean[..256].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    private static string ReadText(string path, int limit) =>
        new UTF8Encoding(false, true).GetString(ReadBytes(path, limit));

    private static byte[] ReadBytes(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 8192)];
        int count;
        while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length))) > 0)
        {
            output.Write(buffer, 0, count);
            if (output.Length > limit)
                throw new NodeAgentException("A hardware file exceeds its inventory size limit.");
        }
        return output.ToArray();
    }

    private static IReadOnlyDictionary<string, string[]> LiveAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces().ToDictionary(nic => nic.Name,
            nic => nic.GetIPProperties().UnicastAddresses.Select(value => value.Address.ToString()).ToArray(),
            StringComparer.Ordinal);

    private static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        _ => throw new NodeAgentException("Only x86_64 and aarch64 hardware reports are supported.")
    };
}
