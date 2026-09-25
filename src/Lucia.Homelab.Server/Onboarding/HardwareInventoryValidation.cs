using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucia.Homelab.Server.Onboarding;

internal static partial class HardwareInventoryValidation
{
    internal static HardwareReport CopyAndValidate(HardwareReport report)
    {
        Require(report is not null, "A hardware report is required.");
        Text(report.Architecture, 32, true);
        Text(report.BootMode, 32, true);
        foreach (var text in new[] { report.Manufacturer, report.Model, report.SerialNumber, report.CpuModel })
            Text(text, 256);
        Text(report.HardwareUuid, 36);
        Require(report.HardwareUuid is null || (Guid.TryParseExact(report.HardwareUuid, "D", out var uuid) && uuid != Guid.Empty),
            "Hardware UUID must be a nonempty UUID or null.");
        Require(report.LogicalCpuCount is >= 1 and <= 4096 && report.MemoryBytes is >= 1 and <= 1_125_899_906_842_624,
            "CPU count or memory size is outside the supported bounds.");
        Require(report.Interfaces is { Length: >= 1 and <= HardwareOnboardingStore.MaximumInterfaces }
            && report.Disks is { Length: <= HardwareOnboardingStore.MaximumDisks },
            "Inventory must contain 1..128 interfaces and 0..128 disks.");
        foreach (var nic in report.Interfaces)
        {
            Require(nic is not null, "An interface cannot be null.");
            Text(nic.Name, 64, true);
            Require(nic.MacAddress is null || MacPattern().IsMatch(nic.MacAddress), "Invalid MAC address.");
            Require(nic.Addresses is { Length: <= HardwareOnboardingStore.MaximumAddressesPerInterface },
                "An interface may contain at most 32 IP addresses.");
            foreach (var address in nic.Addresses)
                Require(address is { Length: > 0 and <= 45 } && !address.Any(char.IsControl) && !address.Contains('%')
                    && IPAddress.TryParse(address, out _), "Invalid IP address.");
            Require(nic.Addresses.Distinct(StringComparer.Ordinal).Count() == nic.Addresses.Length, "Duplicate IP address.");
        }
        Require(report.Interfaces.Select(nic => nic.Name).Distinct(StringComparer.Ordinal).Count() == report.Interfaces.Length,
            "Duplicate interface name.");
        foreach (var disk in report.Disks)
        {
            Require(disk is not null, "A disk cannot be null.");
            Require(disk.Id is null || StableDiskId(disk.Id), "Disk ID must be null or a whole-disk /dev/disk/by-id stable identifier.");
            Require(disk.Path is not null && DiskPathPattern().IsMatch(disk.Path) && !disk.Path.Contains(".."),
                "Disk path must be a simple /dev device path without shell metacharacters.");
            Text(disk.Model, 256);
            Text(disk.Serial, 256);
            Require(disk.SizeBytes is >= 1 and <= 1_152_921_504_606_846_976, "Disk size is outside the supported bounds.");
        }
        var identifiedDisks = report.Disks.Select(disk => disk.Id).OfType<string>().ToArray();
        Require(identifiedDisks.Distinct(StringComparer.Ordinal).Count() == identifiedDisks.Length
            && report.Disks.Select(disk => disk.Path).Distinct(StringComparer.Ordinal).Count() == report.Disks.Length,
            "Duplicate disk ID or path.");
        var copy = report with
        {
            HardwareUuid = report.HardwareUuid?.ToLowerInvariant(),
            Interfaces = report.Interfaces.OrderBy(nic => nic.Name, StringComparer.Ordinal)
                .Select(nic => nic with { MacAddress = nic.MacAddress?.ToUpperInvariant(),
                    Addresses = nic.Addresses.Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            Disks = report.Disks.OrderBy(disk => disk.Id, StringComparer.Ordinal).ThenBy(disk => disk.Path, StringComparer.Ordinal)
                .Select(disk => disk with { }).ToArray()
        };
        Require(JsonSerializer.SerializeToUtf8Bytes(copy, HardwareOnboardingJson.Options).Length <= HardwareOnboardingStore.MaximumHardwareReportBytes,
            "Serialized inventory exceeds 128 KiB.");
        return copy;
    }

    internal static bool StableDiskId(string? value) => value is not null && DiskIdPattern().IsMatch(value)
        && !value.Contains("..") && !PartitionPattern().IsMatch(value);
    internal static bool Hostname(string? value) => value is not null && HostnamePattern().IsMatch(value);
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static void Text(string? value, int maximum, bool required = false) =>
        Require(value is null ? !required : value.Length <= maximum && !value.Any(c => char.IsControl(c) || char.IsSurrogate(c))
            && (!required || !string.IsNullOrWhiteSpace(value)), "Text contains invalid characters or exceeds its length limit.");
    internal static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new HardwareOnboardingException(400, "invalid_onboarding_request", message);
    }

    [GeneratedRegex(@"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\z")]
    private static partial Regex MacPattern();
    [GeneratedRegex(@"\A/dev/disk/by-id/[A-Za-z0-9][A-Za-z0-9._:+-]{0,199}\z")]
    private static partial Regex DiskIdPattern();
    [GeneratedRegex(@"-part[0-9]+\z")]
    private static partial Regex PartitionPattern();
    [GeneratedRegex(@"\A/dev/[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex DiskPathPattern();
    [GeneratedRegex(@"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex HostnamePattern();
}
