using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucia.Homelab.Server.Onboarding;

[JsonConverter(typeof(JsonStringEnumConverter<HardwareDevicePhase>))]
public enum HardwareDevicePhase { Discovered, Approved, Installing, AwaitingEnrollment, Managed, Failed, Rejected }

[JsonConverter(typeof(JsonStringEnumConverter<HardwareTaskPhase>))]
public enum HardwareTaskPhase { Approved, GrantIssued, Installing, AwaitingEnrollment, Failed, Invalidated, Managed }

[JsonConverter(typeof(JsonStringEnumConverter<HeartbeatFreshness>))]
public enum HeartbeatFreshness { Unknown, Fresh, Stale }

public sealed record HardwareInterface(string Name, string? MacAddress, string[] Addresses);
public sealed record HardwareDisk(string? Id, string Path, string? Model, string? Serial, long SizeBytes, bool IsRemovable, bool IsReadOnly);
public sealed record HardwareReport(
    string Architecture, string BootMode, bool? SecureBoot, string? Manufacturer, string? Model,
    string? SerialNumber, string? HardwareUuid, string? CpuModel, int LogicalCpuCount, long MemoryBytes,
    HardwareInterface[] Interfaces, HardwareDisk[] Disks)
{
    // Allow an omitted posture without changing the property order used by durable inventory hashes.
    [JsonConstructor]
    public HardwareReport(string architecture, string bootMode, string? manufacturer, string? model,
        string? serialNumber, string? hardwareUuid, string? cpuModel, int logicalCpuCount, long memoryBytes,
        HardwareInterface[] interfaces, HardwareDisk[] disks, bool? secureBoot = null)
        : this(architecture, bootMode, secureBoot, manufacturer, model, serialNumber, hardwareUuid, cpuModel,
            logicalCpuCount, memoryBytes, interfaces, disks) { }
}

public sealed record OpenHardwareWindowRequest(int Minutes);
public sealed record ApproveHardwareInstallRequest(string Hostname, string DiskId, string Confirmation, string? RecoveryPublicKey = null);
public sealed record HardwareAdmissionWindow(bool IsOpen, DateTimeOffset? ExpiresAt);
public sealed record HardwareOnboardingReadiness(bool CanDiscover, bool CanInstall, string[] Reasons);
public sealed record HardwareDevice(
    Guid Id, HardwareDevicePhase Phase, HardwareReport Hardware, long InventoryRevision,
    DateTimeOffset DiscoveredAt, DateTimeOffset UpdatedAt, DateTimeOffset LastSeenAt,
    DateTimeOffset? LastHeartbeatAt, HeartbeatFreshness HeartbeatFreshness, DateTimeOffset DiscoveryExpiresAt,
    Guid? TaskId, string? StatusMessage, string VerificationCode = "", DateTimeOffset? DismissedAt = null);
public sealed record HardwareInstallationTask(
    Guid Id, Guid DeviceId, HardwareTaskPhase Phase, string Hostname, string DiskId, long InventoryRevision,
    string ApprovedBy, DateTimeOffset ApprovedAt, DateTimeOffset AuthorityExpiresAt,
    DateTimeOffset? GrantIssuedAt, DateTimeOffset UpdatedAt, string? StatusMessage, string? RecoveryPublicKey = null,
    DateTimeOffset? ProgressExpiresAt = null);
public sealed record HardwareOnboardingSnapshot(
    HardwareAdmissionWindow Window, HardwareOnboardingReadiness Readiness, HardwareDevice[] Devices,
    HardwareInstallationTask[] Tasks);

// Only a trusted, proof-of-possession transport adapter may call the session methods.
// Neither this receipt nor session credentials are returned by the browser API.
public sealed record DiscoveryReceipt(Guid DeviceId, string? Token, DateTimeOffset ExpiresAt, string VerificationCode);
public sealed record HardwareDeviceSession(Guid DeviceId, string SessionKeyFingerprint, string Capability);
public sealed record HardwareSessionConfiguration(
    Guid DeviceId, long InventoryRevision, Guid? TaskId, DateTimeOffset? AuthorityExpiresAt, bool CanRequestInstallationGrant,
    string? Hostname = null, string? DiskId = null, string? RecoveryPublicKey = null);
public sealed record HardwareInstallationGrant(
    Guid TaskId, Guid DeviceId, Guid RequestId, string Hostname, string DiskId, long InventoryRevision,
    DateTimeOffset ExpiresAt, string OperatingSystem, string? RecoveryPublicKey = null, DateTimeOffset? ProgressExpiresAt = null);

public sealed class HardwareOnboardingException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

internal static class HardwareOnboardingJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        MaxDepth = 24
    };
}
