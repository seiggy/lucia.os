using System.Text;
using System.Text.Json;

namespace Lucia.NodeAgent;

public sealed record HardwareReport(
    string Architecture, string BootMode, bool? SecureBoot,
    string? Manufacturer, string? Model, string? SerialNumber, string? HardwareUuid,
    string? CpuModel, int LogicalCpuCount, long MemoryBytes,
    NetworkInterfaceReport[] Interfaces, DiskReport[] Disks);

public sealed record NetworkInterfaceReport(string Name, string? MacAddress, string[] Addresses);
public sealed record DiskReport(string? Id, string Path, string? Model, string? Serial,
    long SizeBytes, bool IsRemovable, bool IsReadOnly);
public sealed record DiscoveryChallenge(string ChallengeId, string Nonce, DateTimeOffset ExpiresAt);
public sealed record DiscoveryRequest(string ChallengeId, string PublicKeyPem, string ReportJson, string Signature);
public sealed record DiscoveryRegistration(Guid DeviceId, string Token, DateTimeOffset ExpiresAt, string VerificationCode);
public sealed record DiscoveryCredentials(string Server, Guid DeviceId, string Token,
    DateTimeOffset ExpiresAt, string VerificationCode);
public sealed record DiscoverySummary(Guid DeviceId, DateTimeOffset ExpiresAt, string VerificationCode);

internal sealed record InstallationConfiguration(Guid DeviceId, long InventoryRevision, Guid? TaskId,
    DateTimeOffset? AuthorityExpiresAt, bool CanRequestInstallationGrant, string? Hostname, string? DiskId, string? RecoveryPublicKey);
internal sealed record InstallationGrant(Guid TaskId, Guid DeviceId, Guid RequestId, string Hostname, string DiskId,
    long InventoryRevision, DateTimeOffset ExpiresAt, string OperatingSystem, string RecoveryPublicKey, DateTimeOffset ProgressExpiresAt);
internal sealed record InstallPlan(Guid DeviceId, Guid TaskId, long InventoryRevision, DateTimeOffset AuthorityExpiresAt,
    string Hostname, string DiskId, string RecoveryPublicKey, string BootId, HardwareReport Inventory, string DeviceNumber);
internal sealed record GrantRequest(Guid RequestId, long InventoryRevision, string DiskId, DiscoveryRequest Proof);
internal sealed record EnrollmentRequest(Guid TaskId, DiscoveryRequest Proof);
internal sealed record MachineRequest(string CertificatePem, DiscoveryRequest Proof);
internal sealed record EnrollmentProof(Guid NodeId, Guid TaskId, string CsrPem);
internal sealed record ManagedConfiguration(string Hostname, string CertificatePem, string CaPem, string LdapUri,
    string LdapBaseDn, string LdapBindDn, string LdapBindPassword, string OwnerGroupDn);
internal sealed record ManagedOrigin(string Server);
internal sealed record NodeMetrics(Guid NodeId, string Hostname, string? OsVersion, double? UptimeSeconds,
    double? LoadAverage, long? MemoryTotalBytes, long? MemoryAvailableBytes, long? StorageTotalBytes, long? StorageAvailableBytes,
    RuntimeReport? Runtime = null);

public sealed class NodeAgentException(string message) : Exception(message);

public static class AgentJson
{
    public const int MaxReportBytes = 32 * 1024;
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16
    };

    public static string SerializeReport(HardwareReport report)
    {
        var json = JsonSerializer.Serialize(report, Options);
        ValidateReportSize(json);
        return json;
    }

    internal static void ValidateReportSize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxReportBytes)
            throw new NodeAgentException("The hardware report exceeds the 32 KiB inventory limit. No devices or addresses were omitted to fit it.");
    }
}
