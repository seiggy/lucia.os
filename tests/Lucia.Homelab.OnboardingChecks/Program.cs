using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var root = Path.Combine(AppContext.BaseDirectory, "check-state-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
var clock = new CheckClock();
const string fingerprintA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
const string fingerprintB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
const string actor = "synthetic-issuer:owner";
const string diskA = "/dev/disk/by-id/ata-SYNTHETIC_A";
const string diskB = "/dev/disk/by-id/nvme-SYNTHETIC_B";
var recoveryKey = "ssh-ed25519 " + Convert.ToBase64String(
    Convert.FromHexString("0000000B7373682D6564323535313900000020D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A"));
var inventory = new HardwareReport("x86_64", "uefi", false, "Synthetic manufacturer", "Synthetic model",
    "synthetic-serial", "11111111-1111-1111-1111-111111111111", "Synthetic CPU", 8, 32L << 30,
    [new("eth0", "02:00:00:00:00:01", ["192.168.40.11"])],
    [new(diskA, "/dev/sda", "Synthetic A", "A", 500L << 30, false, false),
     new(diskB, "/dev/nvme0n1", "Synthetic B", "B", 1000L << 30, false, false)]);

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
async Task Fails(Func<Task> action, int status, string? code = null)
{
    try { await action(); }
    catch (HardwareOnboardingException exception)
    {
        Check(exception.StatusCode == status && (code is null || exception.Code == code),
            $"Expected {status}/{code}; got {exception.StatusCode}/{exception.Code}: {exception.Message}");
        return;
    }
    throw new InvalidOperationException($"Expected onboarding failure {status}/{code}.");
}
HardwareOnboardingOptions Settings(string directory, bool install = false) => new()
{
    StateDirectory = Path.Combine(root, directory),
    DiscoveryNetworkCidr = "192.168.40.0/24",
    BootBaseUrl = "https://boot.synthetic.invalid",
    DiscoveryAdapterQualified = true,
    BootArtifactsQualified = install,
    EnrollmentQualified = install,
    InstallationEnabled = install
};
HardwareOnboardingStore Store(HardwareOnboardingOptions settings) =>
    new(Options.Create(settings), clock, NullLogger<HardwareOnboardingStore>.Instance);
HardwareDeviceSession Session(DiscoveryReceipt receipt, string fingerprint = fingerprintA) =>
    new(receipt.DeviceId, fingerprint, receipt.Token ?? throw new InvalidOperationException("Missing first-admission capability."));
ApproveHardwareInstallRequest Approval(string hostname = "synthetic-node", string disk = diskB, string confirmation = "ERASE") =>
    new(hostname, disk, confirmation, recoveryKey);

HardwareReport LargeInventory() => inventory with
{
    Interfaces = Enumerable.Range(0, 128).Select(index => new HardwareInterface($"eth{index}", null,
        Enumerable.Range(0, 32).Select(address => $"10.{index}.{address}.1").ToArray())).ToArray(),
    Disks = Enumerable.Range(0, 128).Select(index => new HardwareDisk($"/dev/disk/by-id/SYNTHETIC_{index:D3}",
        $"/dev/synthetic{index:D3}", "", "", 500L << 30, false, false)).ToArray()
};

HardwareReport SizedInventory(int bytes)
{
    var report = LargeInventory();
    var remaining = bytes - JsonSerializer.SerializeToUtf8Bytes(report, HardwareOnboardingJson.Options).Length;
    Check(remaining >= 0, "The bounded-size fixture must begin below its requested byte limit.");
    for (var index = 0; index < report.Disks.Length && remaining > 0; index++)
    {
        var modelLength = Math.Min(remaining, 256);
        remaining -= modelLength;
        var serialLength = Math.Min(remaining, 256);
        remaining -= serialLength;
        report.Disks[index] = report.Disks[index] with { Model = new string('m', modelLength), Serial = new string('s', serialLength) };
    }
    Check(remaining == 0 && JsonSerializer.SerializeToUtf8Bytes(report, HardwareOnboardingJson.Options).Length == bytes,
        "The report fixture must hit the exact UTF-8 byte threshold without exceeding any field bound.");
    return report;
}

try
{
    Console.WriteLine("Checking fail-closed defaults, leases, and admission bounds...");
    Check(RecoverySshKeys.Parse(recoveryKey + " synthetic-test").PublicKey == recoveryKey, "Recovery key comments must not become authorized-key options.");
    foreach (var invalidKey in new[] { "", "-----BEGIN PRIVATE KEY-----", "command=\"id\" " + recoveryKey,
        recoveryKey + "\n" + recoveryKey, "ssh-ed25519 malformed", recoveryKey.Replace("ssh-ed25519", "ssh-rsa", StringComparison.Ordinal) })
        await Fails(() => Task.FromResult(RecoverySshKeys.Parse(invalidKey)), 400, "invalid_recovery_key");
    var defaultStateDirectory = new HardwareOnboardingOptions().StateDirectory;
    Check(defaultStateDirectory == Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "onboarding")
        && Path.IsPathFullyQualified(defaultStateDirectory),
        "Default state must use the current user's local application data, not the application drive root.");
    var missingPostureJson = JsonNode.Parse(JsonSerializer.Serialize(inventory, HardwareOnboardingJson.Options))!.AsObject();
    missingPostureJson.Remove("secureBoot");
    var missingPosture = JsonSerializer.Deserialize<HardwareReport>(missingPostureJson.ToJsonString(), HardwareOnboardingJson.Options)!;
    Check(missingPosture.SecureBoot is null && missingPosture.Architecture == "x86_64" && missingPosture.BootMode == "uefi",
        "Node JSON may omit Secure Boot posture; unknown must remain null, not invented true or false.");
    Check(HardwareOnboardingStore.MaximumHardwareReportBytes == 131072
        && HardwareOnboardingStore.MaximumInterfaces == 128 && HardwareOnboardingStore.MaximumDisks == 128
        && HardwareOnboardingStore.MaximumAddressesPerInterface == 32 && HardwareOnboardingStore.MaximumStateBytes == 33554432
        && HardwareOnboardingStore.DiscoveryLifetime.TotalSeconds == 1800
        && HardwareOnboardingStore.InstallationAuthorityLifetime.TotalSeconds == 900
        && HardwareOnboardingStore.HeartbeatFreshnessLifetime.TotalSeconds == 120,
        "Published transport bounds must exactly match the enforced core limits.");
    using (var store = Store(new() { StateDirectory = Path.Combine(root, "defaults") }))
    {
        await Fails(() => store.GetSnapshotAsync(), 503);
        await Fails(() => store.AssertCanDiscoverAsync(), 503);
        await store.StartAsync(default);
        var snapshot = await store.GetSnapshotAsync();
        Check(!snapshot.Window.IsOpen && snapshot.Window.ExpiresAt is null && !snapshot.Readiness.CanDiscover
            && !snapshot.Readiness.CanInstall && snapshot.Devices.Length == 0 && snapshot.Tasks.Length == 0, "Defaults must be empty and fail-closed.");
        Check(snapshot.Readiness.Reasons.Length == 6, "Every unqualified prerequisite must have an explicit reason.");
        await Fails(() => store.OpenWindowAsync(30, actor), 503, "discovery_not_ready");
        await Fails(() => store.AssertCanDiscoverAsync(), 503, "discovery_not_ready");
        await Fails(() => store.OpenWindowAsync(0, actor), 400);
        await Fails(() => store.OpenWindowAsync(61, actor), 400);
        using var second = Store(new() { StateDirectory = Path.Combine(root, "defaults") });
        await store.StopAsync(default);
        try { await second.StartAsync(default); throw new InvalidOperationException("A second directory lease was granted."); }
        catch (IOException) { checks++; }
    }
    foreach (var configure in new Action<HardwareOnboardingOptions>[]
    {
        settings => settings.DiscoveryNetworkCidr = "0.0.0.0/0",
        settings => settings.DiscoveryNetworkCidr = "172.0.0.0/8",
        settings => settings.BootBaseUrl = "http://boot.synthetic.invalid",
        settings => settings.BootBaseUrl = "https://user:secret@boot.synthetic.invalid",
        settings => settings.StateDirectory = "relative-state"
    })
    {
        var settings = Settings("invalid-options");
        configure(settings);
        try { using var unused = Store(settings); throw new InvalidOperationException("Invalid options were accepted."); }
        catch (ArgumentException) { checks++; }
    }
    using (var store = Store(Settings("discovery")))
    {
        await store.StartAsync(default);
        await Fails(() => store.AssertCanDiscoverAsync(), 409, "admission_closed");
        Check(await store.FindDiscoveryAsync(fingerprintA) is null, "A lookup must not fabricate a discovery.");
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, inventory, false), 409, "admission_closed");
        Check((await store.OpenWindowAsync(1, actor)).ExpiresAt == clock.GetUtcNow().AddMinutes(1), "Window expiry must use the injected clock.");
        Check((await store.AssertCanDiscoverAsync()).ExpiresAt == clock.GetUtcNow().AddMinutes(1),
            "Adapter preflight must return the actual admission deadline.");
        clock.Advance(TimeSpan.FromMinutes(1));
        Check(!(await store.GetSnapshotAsync()).Window.IsOpen, "Admission must expire at the exact boundary.");
        await Fails(() => store.AssertCanDiscoverAsync(), 409, "admission_closed");
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, inventory, false), 409);
        await store.OpenWindowAsync(60, actor);
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, inventory, true), 409, "managed_device");
        await Fails(() => store.RegisterDiscoveryAsync("not-a-key-fingerprint", inventory, false), 400);
        var receipt = await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        var session = Session(receipt);
        Check(receipt.Token is { Length: 64 }, "A new admission must return a 256-bit capability once.");
        Check(receipt.VerificationCode == "AAAA-AAAA-AAAA" && receipt.VerificationCode.Length == 14
            && HardwareOnboardingStore.GetVerificationCode("0123456789abcdef" + new string('0', 48)) == "0123-4567-89AB",
            "The comparison code must be exactly the first 12 normalized fingerprint hex digits grouped by four.");
        var receiptJson = JsonNode.Parse(JsonSerializer.Serialize(receipt, HardwareOnboardingJson.Options))!.AsObject();
        Check(receiptJson.Select(pair => pair.Key).SequenceEqual(["deviceId", "token", "expiresAt", "verificationCode"]),
            "Discovery receipt must have the exact transport contract.");
        var found = await store.FindDiscoveryAsync(fingerprintA.ToLowerInvariant());
        Check(found?.Id == receipt.DeviceId && found.VerificationCode == receipt.VerificationCode,
            "Trusted lookup must use the cryptographic key and return the same public comparison code.");
        var verified = await store.VerifyDiscoveryCapabilityAsync(receipt.DeviceId, fingerprintA, receipt.Token!);
        Check(verified.Id == receipt.DeviceId && verified.VerificationCode == receipt.VerificationCode,
            "Adapter verification must return only the correctly associated device.");
        await Fails(() => store.VerifyDiscoveryCapabilityAsync(receipt.DeviceId, fingerprintA, receipt.VerificationCode), 403);
        await Fails(() => store.VerifyDiscoveryCapabilityAsync(receipt.DeviceId, fingerprintB, receipt.Token!), 403);
        await Fails(() => store.VerifyDiscoveryCapabilityAsync(Guid.NewGuid(), fingerprintA, receipt.Token!), 403);
        await Fails(() => store.FindDiscoveryAsync("not-a-fingerprint"), 400);
        await Fails(() => store.RegisterDiscoveryAsync(new string('A', 12) + new string('B', 52), inventory, false),
            409, "verification_code_collision");
        var duplicate = await store.RegisterDiscoveryAsync(fingerprintA.ToLowerInvariant(), inventory with { Disks = inventory.Disks.Reverse().ToArray() }, false);
        Check(duplicate.DeviceId == receipt.DeviceId && duplicate.Token is null && (await store.GetSessionStatusAsync(session)).InventoryRevision == 1
            && duplicate.ExpiresAt == receipt.ExpiresAt && duplicate.VerificationCode == receipt.VerificationCode,
            "Duplicate registration must neither rotate credentials nor extend authority.");
        var sameMac = await store.RegisterDiscoveryAsync(fingerprintB, inventory, false);
        Check(sameMac.DeviceId != receipt.DeviceId, "A MAC address must not identify or authenticate a discovery.");
        Check((await store.GetSessionStatusAsync(session)).HeartbeatFreshness == HeartbeatFreshness.Unknown, "No heartbeat is not healthy.");
        Check(!(await store.GetSessionConfigurationAsync(session)).CanRequestInstallationGrant, "Discovery alone cannot authorize installation.");
        await Fails(() => store.ApproveInstallAsync(receipt.DeviceId, Approval(), actor), 503, "installation_not_ready");
        await Fails(() => store.GetSessionStatusAsync(session with { Capability = new string('0', 64) }), 403);
        await Fails(() => store.GetSessionStatusAsync(session with { SessionKeyFingerprint = fingerprintB }), 403);
        await Fails(() => store.GetSessionStatusAsync(session with { DeviceId = sameMac.DeviceId }), 403);
        await Fails(() => store.GetSessionStatusAsync(session with { DeviceId = Guid.NewGuid() }), 403);
        await Fails(() => store.RequestInstallationGrantAsync(session, Guid.NewGuid(), diskB, 1), 503);
        Check((await store.HeartbeatAsync(session)).HeartbeatFreshness == HeartbeatFreshness.Fresh, "An authenticated heartbeat must record freshness.");
        clock.Advance(TimeSpan.FromMinutes(2));
        Check((await store.GetSnapshotAsync()).Devices.Single(device => device.Id == receipt.DeviceId).HeartbeatFreshness == HeartbeatFreshness.Stale,
            "Freshness expires without manufacturing a Healthy status.");
        var publicJson = JsonSerializer.Serialize(await store.GetSnapshotAsync(), HardwareOnboardingJson.Options);
        var persistedJson = await File.ReadAllTextAsync(Path.Combine(root, "discovery", "state.json"));
        Check(!publicJson.Contains(receipt.Token!) && !publicJson.Contains(fingerprintA)
            && !publicJson.Contains("capability", StringComparison.OrdinalIgnoreCase)
            && !publicJson.Contains("Hash", StringComparison.OrdinalIgnoreCase), "Public responses must not serialize credentials, hashes, or session keys.");
        Check(!persistedJson.Contains(receipt.Token!) && persistedJson.Contains("capabilityHash"), "Persistence must store only a capability hash.");
        var externalView = await store.GetSnapshotAsync();
        externalView.Devices[0].Hardware.Disks[0] = externalView.Devices[0].Hardware.Disks[0] with { SizeBytes = 1 };
        Check((await store.GetSnapshotAsync()).Devices[0].Hardware.Disks.All(disk => disk.SizeBytes > 1), "Returned arrays must not mutate authority state.");

        var badReports = new List<HardwareReport>
        {
            inventory with { Architecture = null! }, inventory with { LogicalCpuCount = 0 }, inventory with { LogicalCpuCount = 4097 },
            inventory with { MemoryBytes = 0 }, inventory with { Manufacturer = "control\ncharacter" },
            inventory with { CpuModel = new string('x', 257) }, inventory with { HardwareUuid = "bad-uuid" },
            inventory with { Interfaces = [] },
            inventory with { Interfaces = Enumerable.Range(0, 129).Select(index => inventory.Interfaces[0] with { Name = $"eth{index}" }).ToArray() },
            inventory with { Interfaces = [inventory.Interfaces[0] with { MacAddress = "not-a-mac" }] },
            inventory with { Interfaces = [inventory.Interfaces[0] with { Addresses = ["192.168.40.1\n"] }] },
            inventory with { Interfaces = [inventory.Interfaces[0] with { Addresses = ["bad-ip"] }] },
            inventory with { Disks = [inventory.Disks[0] with { Id = "/dev/sda" }] },
            inventory with { Disks = [inventory.Disks[0] with { Id = diskA + "-part1" }] },
            inventory with { Disks = [inventory.Disks[0] with { Path = "/dev/sda;reboot" }] },
            inventory with { Disks = [inventory.Disks[0] with { Path = "/dev/../sda" }] },
            inventory with { Disks = [inventory.Disks[0] with { Path = "/dev/$(reboot)" }] },
            inventory with { Disks = [inventory.Disks[0] with { SizeBytes = 0 }] },
            inventory with { Disks = [inventory.Disks[0], inventory.Disks[0]] },
            inventory with { Disks = [inventory.Disks[0], inventory.Disks[1] with { Path = "/dev/sda" }] }
        };
        foreach (var report in badReports) await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, report, false), 400);
        Check((await store.GetSnapshotAsync()).Devices[0].InventoryRevision == 1, "Rejected inventory must not change state.");
        await store.RejectDiscoveryAsync(sameMac.DeviceId, actor);
        await Fails(() => store.GetSessionStatusAsync(Session(sameMac, fingerprintB)), 403);
        await Fails(() => store.VerifyDiscoveryCapabilityAsync(sameMac.DeviceId, fingerprintB, sameMac.Token!), 403);
        await Fails(() => store.RejectDiscoveryAsync(sameMac.DeviceId, actor), 409);
        clock.Advance(TimeSpan.FromMinutes(28));
        await Fails(() => store.HeartbeatAsync(session), 403);
        await Fails(() => store.VerifyDiscoveryCapabilityAsync(receipt.DeviceId, fingerprintA, receipt.Token!), 403);
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, inventory, false), 409, "discovery_expired");
        await Fails(() => store.ApproveInstallAsync(receipt.DeviceId, Approval(), actor), 409, "discovery_expired");
    }

    Console.WriteLine("Checking reversible dismissal without restoring rejected authority...");
    var dismissalSettings = Settings("dismissal");
    DiscoveryReceipt dismissedReceipt;
    DateTimeOffset dismissedAt;
    using (var store = Store(dismissalSettings))
    {
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        dismissedReceipt = await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        await Fails(() => store.SetDiscoveryDismissedAsync(dismissedReceipt.DeviceId, true, actor), 409, "not_rejected");
        await Fails(() => store.SetDiscoveryDismissedAsync(Guid.NewGuid(), true, actor), 404);
        await store.RejectDiscoveryAsync(dismissedReceipt.DeviceId, actor);
        await store.CloseWindowAsync(actor);
        clock.Advance(TimeSpan.FromSeconds(1));
        var hidden = await store.SetDiscoveryDismissedAsync(dismissedReceipt.DeviceId, true, actor);
        dismissedAt = clock.GetUtcNow();
        Check(hidden.Phase == HardwareDevicePhase.Rejected && hidden.DismissedAt == dismissedAt && hidden.TaskId is null,
            "Dismissal must preserve the rejected phase and absence of installation authority.");
        var path = Path.Combine(dismissalSettings.StateDirectory, "state.json");
        var once = await File.ReadAllTextAsync(path);
        await store.SetDiscoveryDismissedAsync(dismissedReceipt.DeviceId, true, actor);
        Check(await File.ReadAllTextAsync(path) == once, "Duplicate dismissal changed timestamps or audit history.");
        await Fails(() => store.HeartbeatAsync(Session(dismissedReceipt)), 403, "invalid_device_session");
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, inventory, false), 409, "discovery_expired");
        Check((await store.GetSnapshotAsync()).Devices.Single().DismissedAt == dismissedAt, "Owner snapshot lost the dismissed record.");
    }
    using (var restarted = Store(dismissalSettings))
    {
        await restarted.StartAsync(default);
        Check((await restarted.GetSnapshotAsync()).Devices.Single().DismissedAt == dismissedAt, "Restart forgot dismissal.");
        var restored = await restarted.SetDiscoveryDismissedAsync(dismissedReceipt.DeviceId, false, actor);
        Check(restored.DismissedAt is null && restored.Phase == HardwareDevicePhase.Rejected, "Restoring visibility undid rejection.");
        await Fails(() => restarted.GetSessionStatusAsync(Session(dismissedReceipt)), 403, "invalid_device_session");
        await Fails(() => restarted.ApproveInstallAsync(dismissedReceipt.DeviceId, Approval(), actor), 409, "not_pending");
        var once = await File.ReadAllTextAsync(Path.Combine(dismissalSettings.StateDirectory, "state.json"));
        await restarted.SetDiscoveryDismissedAsync(dismissedReceipt.DeviceId, false, actor);
        Check(await File.ReadAllTextAsync(Path.Combine(dismissalSettings.StateDirectory, "state.json")) == once,
            "Duplicate restore modified audit history.");
        var events = JsonNode.Parse(once)!["events"]!.AsArray();
        Check(events.Count(e => e!["kind"]!.GetValue<string>() == "DiscoveryDismissed") == 1
            && events.Count(e => e!["kind"]!.GetValue<string>() == "DiscoveryRestoredToList") == 1,
            "Visibility changes were not recorded in the owner audit history.");
    }
    var legacyDismissal = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dismissalSettings.StateDirectory, "state.json")))!;
    legacyDismissal["devices"]![0]!["device"]!.AsObject().Remove("dismissedAt");
    await File.WriteAllTextAsync(Path.Combine(dismissalSettings.StateDirectory, "state.json"), legacyDismissal.ToJsonString());
    using (var legacy = Store(dismissalSettings))
    {
        await legacy.StartAsync(default);
        Check((await legacy.GetSnapshotAsync()).Devices.Single().DismissedAt is null, "Older records without dismissal metadata were rejected.");
    }

    Console.WriteLine("Checking immutable approval, bounded grants, restart recovery, and reported progress...");
    var qualified = Settings("authority", install: true);
    DiscoveryReceipt admitted;
    HardwareInstallationTask approved;
    using (var store = Store(qualified))
    {
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        admitted = await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        await Fails(() => store.ApproveInstallAsync(admitted.DeviceId, Approval(confirmation: "erase"), actor), 400);
        await Fails(() => store.ApproveInstallAsync(admitted.DeviceId, Approval(confirmation: "ERASE "), actor), 400);
        await Fails(() => store.ApproveInstallAsync(admitted.DeviceId, Approval(hostname: "node;reboot"), actor), 400);
        await Fails(() => store.ApproveInstallAsync(admitted.DeviceId, Approval(disk: "/dev/sda"), actor), 400);
        await Fails(() => store.ApproveInstallAsync(admitted.DeviceId, Approval(disk: diskA + "_missing"), actor), 409);
        approved = await store.ApproveInstallAsync(admitted.DeviceId, Approval(), actor);
        Check(approved.DiskId == diskB && approved.InventoryRevision == 1 && approved.ApprovedBy == actor,
            "Approval must bind the exact selected stable disk, immutable revision, and owner.");
        await Fails(() => store.RejectDiscoveryAsync(admitted.DeviceId, actor), 409);
        await Fails(() => store.SetDiscoveryDismissedAsync(admitted.DeviceId, true, actor), 409, "not_rejected");
        await Fails(() => store.ApproveInstallAsync(admitted.DeviceId, Approval(), actor), 409);
        await Fails(() => store.ReportStatusAsync(Session(admitted), HardwareDevicePhase.Installing, null), 409);
        Check((await store.GetSessionConfigurationAsync(Session(admitted))).CanRequestInstallationGrant, "Approved configuration should expose readiness, not a grant.");
        await store.CloseWindowAsync(actor);
        await Fails(() => store.AssertCanDiscoverAsync(), 409, "admission_closed");
        Check((await store.VerifyDiscoveryCapabilityAsync(admitted.DeviceId, fingerprintA, admitted.Token!)).Id == admitted.DeviceId,
            "Closing new admissions must not reject a previously admitted valid session.");
        Check((await store.GetSnapshotAsync()).Tasks.Single().AuthorityExpiresAt == approved.AuthorityExpiresAt,
            "Closing new admissions must not revoke or extend an approved task.");
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintB, inventory, false), 409);
        await store.OpenWindowAsync(30, actor);
    }
    using (var disabled = Store(Settings("authority")))
    {
        await disabled.StartAsync(default);
        var recovered = await disabled.GetSnapshotAsync();
        Check(!recovered.Window.IsOpen && recovered.Window.ExpiresAt is null && recovered.Tasks.Single() == approved,
            "Restart must close admission but retain approved task state and bounded authority.");
        await Fails(() => disabled.RequestInstallationGrantAsync(Session(admitted), Guid.NewGuid(), diskB, 1), 503, "installation_not_ready");
    }
    var requestId = Guid.NewGuid();
    HardwareInstallationGrant grant;
    string validState;
    using (var store = Store(qualified))
    {
        await store.StartAsync(default);
        await Fails(() => store.RequestInstallationGrantAsync(Session(admitted), Guid.NewGuid(), diskA, 1), 409);
        await Fails(() => store.RequestInstallationGrantAsync(Session(admitted), Guid.NewGuid(), diskB, 2), 409);
        await Fails(() => store.RequestInstallationGrantAsync(Session(admitted), Guid.Empty, diskB, 1), 400);
        grant = await store.RequestInstallationGrantAsync(Session(admitted), requestId, diskB, 1);
        Check(grant.TaskId == approved.Id && grant.DeviceId == admitted.DeviceId && grant.DiskId == diskB
            && grant.ExpiresAt == approved.AuthorityExpiresAt && grant.OperatingSystem == "debian-13.7", "Grant must preserve the exact approval binding.");
        var retries = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            store.RequestInstallationGrantAsync(Session(admitted), requestId, diskB, 1)));
        Check(retries.All(retry => retry == grant), "Exact concurrent retries must return the same grant.");
        Check((await store.GetSnapshotAsync()).Devices.Single().Phase == HardwareDevicePhase.Approved
            && (await store.GetSnapshotAsync()).Tasks.Single().Phase == HardwareTaskPhase.GrantIssued,
            "Issuing a grant must not invent evidence that installation started.");
        Check(!(await store.GetSessionConfigurationAsync(Session(admitted))).CanRequestInstallationGrant, "Consumed authority cannot advertise another grant.");
        await Fails(() => store.RequestInstallationGrantAsync(Session(admitted), Guid.NewGuid(), diskB, 1), 409, "grant_consumed");
        await Fails(() => store.ReportStatusAsync(Session(admitted), HardwareDevicePhase.Managed, "claim managed"), 409);
        await Fails(() => store.ReportStatusAsync(Session(admitted), HardwareDevicePhase.AwaitingEnrollment, null), 409);
        await store.ReportStatusAsync(Session(admitted), HardwareDevicePhase.Installing, "Synthetic progress report");
        await store.ReportStatusAsync(Session(admitted), HardwareDevicePhase.AwaitingEnrollment, "Synthetic installer finished; not enrolled");
        Check((await store.GetSnapshotAsync()).Devices.Single().Phase == HardwareDevicePhase.AwaitingEnrollment, "Device reports must remain distinct from managed identity.");
        validState = await File.ReadAllTextAsync(Path.Combine(qualified.StateDirectory, "state.json"));
    }
    using (var restarted = Store(qualified))
    {
        await restarted.StartAsync(default);
        Check(await restarted.RequestInstallationGrantAsync(Session(admitted), requestId, diskB, 1) == grant,
            "Idempotent grant replay must survive restart.");
        await Fails(() => restarted.RequestInstallationGrantAsync(Session(admitted), Guid.NewGuid(), diskB, 1), 409);
        clock.Advance(TimeSpan.FromMinutes(15));
        await Fails(() => restarted.RequestInstallationGrantAsync(Session(admitted), requestId, diskB, 1), 409);
        await Fails(() => restarted.ReportStatusAsync(Session(admitted), HardwareDevicePhase.Installing, null), 409);
        await restarted.HeartbeatAsync(Session(admitted));
        Check((await restarted.GetSnapshotAsync()).Devices.Single().Phase == HardwareDevicePhase.AwaitingEnrollment,
            "Heartbeat is not enrollment and cannot alter phase.");
    }

    Console.WriteLine("Checking inventory invalidation, hardware eligibility, and concurrent admission...");
    using (var store = Store(Settings("changed", true)))
    {
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.RegisterDiscoveryAsync(fingerprintA, inventory, false)));
        Check(parallel.Select(receipt => receipt.DeviceId).Distinct().Count() == 1 && parallel.Count(receipt => receipt.Token is not null) == 1,
            "Concurrent admissions must deduplicate by key and issue exactly one capability.");
        var receipt = parallel.Single(receipt => receipt.Token is not null);
        var session = Session(receipt);
        var task = await store.ApproveInstallAsync(receipt.DeviceId, Approval(), actor);
        await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        Check((await store.GetSnapshotAsync()).Tasks.Single() == task, "An unchanged duplicate must not invalidate approved inventory.");
        var changed = inventory with { Disks = [inventory.Disks[0], inventory.Disks[1] with { SizeBytes = 900L << 30 }] };
        var updated = await store.RegisterDiscoveryAsync(fingerprintA, changed, false);
        var snapshot = await store.GetSnapshotAsync();
        Check(snapshot.Devices.Single().InventoryRevision == 2 && updated.Token is null
            && updated.VerificationCode == receipt.VerificationCode && snapshot.Devices.Single().Phase == HardwareDevicePhase.Discovered
            && snapshot.Tasks.Single().Phase == HardwareTaskPhase.Invalidated, "Changed inventory must invalidate approval without rotating authority.");
        await Fails(() => store.RequestInstallationGrantAsync(session, Guid.NewGuid(), diskB, 1), 409);
        var newTask = await store.ApproveInstallAsync(receipt.DeviceId, Approval(), actor);
        Check(newTask.InventoryRevision == 2 && newTask.Id != task.Id, "Reapproval must bind the new inventory, not rewrite an earlier job.");
        await store.RequestInstallationGrantAsync(session, Guid.NewGuid(), diskB, 2);
        await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        Check((await store.GetSnapshotAsync()).Devices.Single().Phase == HardwareDevicePhase.Failed,
            "Changed inventory after a grant must fail closed, never enable reinstall.");
        await Fails(() => store.ApproveInstallAsync(receipt.DeviceId, Approval(), actor), 409);
        await Fails(() => store.HeartbeatAsync(session), 403);
    }
    foreach (var (label, report) in new (string, HardwareReport)[]
    {
        ("readonly", inventory with { Disks = [inventory.Disks[1] with { IsReadOnly = true }] }),
        ("removable", inventory with { Disks = [inventory.Disks[1] with { IsRemovable = true }] }),
        ("arm", inventory with { Architecture = "aarch64" }),
        ("bios", inventory with { BootMode = "bios" })
    })
    {
        using var store = Store(Settings("ineligible-" + label, true));
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        var receipt = await store.RegisterDiscoveryAsync(fingerprintA, report, false);
        await Fails(() => store.ApproveInstallAsync(receipt.DeviceId, Approval(), actor), 409);
    }
    Console.WriteLine("Checking nullable identities, expanded inventory bounds, and exact report byte limits...");
    var missingIdentities = inventory with
    {
        Manufacturer = null, Model = null, SerialNumber = null, HardwareUuid = null, CpuModel = null,
        Interfaces = [new("eth0", null, ["192.168.0.222"])],
        Disks = inventory.Disks.Select(disk => disk with { Id = null, Model = null, Serial = null }).ToArray()
    };
    var missingSettings = Settings("missing-identities", true);
    missingSettings.DiscoveryNetworkCidr = "192.168.0.0/23";
    DiscoveryReceipt missingReceipt;
    using (var store = Store(missingSettings))
    {
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        missingReceipt = await store.RegisterDiscoveryAsync(fingerprintA, missingIdentities, false);
        var reported = await store.GetSessionStatusAsync(Session(missingReceipt));
        Check(reported.Hardware.Interfaces.Single().MacAddress is null && reported.Hardware.Disks.Length == 2
            && reported.Hardware.Disks.All(disk => disk.Id is null) && reported.Hardware.Model is null,
            "Unknown MACs, stable IDs, and hardware strings must remain null without omitting the observed devices.");
        var reportJson = JsonNode.Parse(JsonSerializer.Serialize(reported, HardwareOnboardingJson.Options))!;
        Check(reportJson["hardware"]!["interfaces"]![0]!["macAddress"] is null
            && reportJson["hardware"]!["disks"]![0]!["id"] is null, "Public JSON must faithfully expose nullable identities.");
        var duplicate = await store.RegisterDiscoveryAsync(fingerprintA,
            missingIdentities with { Disks = missingIdentities.Disks.Reverse().ToArray() }, false);
        Check(duplicate.Token is null && (await store.GetSessionStatusAsync(Session(missingReceipt))).InventoryRevision == 1,
            "Reordering disks with unknown stable IDs must not create a false inventory revision.");
        await Fails(() => store.ApproveInstallAsync(missingReceipt.DeviceId, Approval(disk: null!), actor), 400);
        await Fails(() => store.ApproveInstallAsync(missingReceipt.DeviceId, Approval(disk: "/dev/sda"), actor), 400);
        await Fails(() => store.ApproveInstallAsync(missingReceipt.DeviceId, Approval(disk: diskA), actor), 409, "ineligible_disk");
        await Fails(() => store.RequestInstallationGrantAsync(Session(missingReceipt), Guid.NewGuid(), diskA, 1), 409, "not_approved");
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA,
            missingIdentities with { Disks = [missingIdentities.Disks[0], missingIdentities.Disks[0]] }, false), 400);
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA,
            missingIdentities with { Disks = [missingIdentities.Disks[0] with { Id = "" }] }, false), 400);
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA,
            missingIdentities with { Interfaces = [missingIdentities.Interfaces[0] with { MacAddress = "" }] }, false), 400);
        var identified = missingIdentities with
        {
            Disks = [missingIdentities.Disks[0] with { Id = diskA }, missingIdentities.Disks[1]]
        };
        await store.RegisterDiscoveryAsync(fingerprintA, identified, false);
        var approvedDisk = await store.ApproveInstallAsync(missingReceipt.DeviceId, Approval(disk: diskA), actor);
        Check(approvedDisk.DiskId == diskA && approvedDisk.InventoryRevision == 2,
            "A known eligible disk can be approved while other observed disks lack stable IDs.");
        await store.RegisterDiscoveryAsync(fingerprintA, missingIdentities, false);
        Check((await store.GetSnapshotAsync()).Tasks.Single().Phase == HardwareTaskPhase.Invalidated,
            "Losing the selected stable ID must invalidate approval, never fall back to the kernel path.");
        await Fails(() => store.RequestInstallationGrantAsync(Session(missingReceipt), Guid.NewGuid(), diskA, 2), 409, "not_approved");
    }
    using (var store = Store(missingSettings))
    {
        await store.StartAsync(default);
        var reported = await store.VerifyDiscoveryCapabilityAsync(missingReceipt.DeviceId, fingerprintA, missingReceipt.Token!);
        Check(reported.Hardware.Disks.All(disk => disk.Id is null) && reported.Hardware.Interfaces.Single().MacAddress is null
            && reported.InventoryRevision == 3 && reported.Phase == HardwareDevicePhase.Discovered,
            "Nullable inventory and invalidated approval must survive restart without renewing authority.");
    }
    var exactLimitReport = SizedInventory(HardwareOnboardingStore.MaximumHardwareReportBytes);
    var oversizedReport = SizedInventory(HardwareOnboardingStore.MaximumHardwareReportBytes + 1);
    var expandedSettings = Settings("expanded-inventory");
    DiscoveryReceipt expandedReceipt;
    using (var store = Store(expandedSettings))
    {
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        expandedReceipt = await store.RegisterDiscoveryAsync(fingerprintA, exactLimitReport, false);
        var reported = (await store.GetSessionStatusAsync(Session(expandedReceipt))).Hardware;
        Check(reported.Disks.Length == 128 && reported.Interfaces.Length == 128
            && reported.Interfaces.All(nic => nic.Addresses.Length == 32),
            "The exact report limit must accept all requested maximum counts without truncation.");
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, oversizedReport, false), 400);
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, inventory with
        {
            Disks = Enumerable.Range(0, 129).Select(index => inventory.Disks[0] with
                { Id = $"/dev/disk/by-id/SYNTHETIC_{index}", Path = $"/dev/synthetic{index}" }).ToArray()
        }, false), 400);
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintA, inventory with
        {
            Interfaces = [inventory.Interfaces[0] with
                { Addresses = Enumerable.Range(1, 33).Select(index => $"192.168.0.{index}").ToArray() }]
        }, false), 400);
        Check((await store.GetSnapshotAsync()).Devices.Single().InventoryRevision == 1,
            "Over-limit reports must not partially mutate or truncate inventory.");
    }
    using (var store = Store(expandedSettings))
    {
        await store.StartAsync(default);
        var reported = await store.GetSessionStatusAsync(Session(expandedReceipt));
        Check(JsonSerializer.SerializeToUtf8Bytes(reported.Hardware, HardwareOnboardingJson.Options).Length == 131072
            && reported.Hardware.Disks.Length == 128 && reported.Hardware.Interfaces.Length == 128,
            "An exact-limit report must persist and restore in full.");
    }
    foreach (var secureBoot in new bool?[] { false, true, null })
    {
        var directory = "boot-posture-" + (secureBoot?.ToString() ?? "unknown");
        DiscoveryReceipt receipt;
        HardwareInstallationTask task;
        using (var disabled = Store(Settings(directory)))
        {
            await disabled.StartAsync(default);
            await disabled.OpenWindowAsync(30, actor);
            receipt = await disabled.RegisterDiscoveryAsync(fingerprintA,
                secureBoot is null ? missingPosture : inventory with { SecureBoot = secureBoot }, false);
            Check((await disabled.GetSnapshotAsync()).Devices.Single().Hardware.SecureBoot == secureBoot,
                "Secure Boot must remain faithfully reported inventory, including unknown posture.");
            await Fails(() => disabled.ApproveInstallAsync(receipt.DeviceId, Approval(), actor), 503, "installation_not_ready");
            await Fails(() => disabled.RequestInstallationGrantAsync(Session(receipt), Guid.NewGuid(), diskB, 1), 503, "installation_not_ready");
        }
        using (var enabled = Store(Settings(directory, true)))
        {
            await enabled.StartAsync(default);
            await Fails(() => enabled.RequestInstallationGrantAsync(Session(receipt), Guid.NewGuid(), diskB, 1), 409, "not_approved");
            await Fails(() => enabled.ApproveInstallAsync(receipt.DeviceId, Approval(confirmation: "erase"), actor), 400);
            task = await enabled.ApproveInstallAsync(receipt.DeviceId, Approval(), actor);
            Check(task.DiskId == diskB && task.InventoryRevision == 1 && task.ApprovedBy == actor,
                "Every Secure Boot posture still requires an explicit Owner approval bound to the disk and inventory.");
        }
        using (var disabled = Store(Settings(directory)))
        {
            await disabled.StartAsync(default);
            await Fails(() => disabled.RequestInstallationGrantAsync(Session(receipt), Guid.NewGuid(), diskB, 1), 503, "installation_not_ready");
        }
        using (var enabled = Store(Settings(directory, true)))
        {
            await enabled.StartAsync(default);
            var snapshot = await enabled.GetSnapshotAsync();
            Check(snapshot.Devices.Single().Hardware.SecureBoot == secureBoot && snapshot.Tasks.Single() == task,
                "Approved work and reported Secure Boot posture must survive restart without an implicit Secure Boot gate.");
            var issued = await enabled.RequestInstallationGrantAsync(Session(receipt), Guid.NewGuid(), diskB, 1);
            Check(issued.TaskId == task.Id && issued.DiskId == diskB,
                "Qualified, Owner-approved x86_64 UEFI may receive a bounded grant regardless of reported Secure Boot posture.");
        }
    }
    foreach (var disable in new Action<HardwareOnboardingOptions>[]
    {
        settings => settings.BootArtifactsQualified = false,
        settings => settings.EnrollmentQualified = false,
        settings => settings.InstallationEnabled = false
    })
    {
        var settings = Settings("readiness-" + Guid.NewGuid().ToString("N"), true);
        disable(settings);
        using var store = Store(settings);
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        var receipt = await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        settings.BootArtifactsQualified = settings.EnrollmentQualified = settings.InstallationEnabled = true;
        await Fails(() => store.ApproveInstallAsync(receipt.DeviceId, Approval(), actor), 503);
    }

    Console.WriteLine("Checking bounded installation progress and managed enrollment...");
    var nodeClock = new CheckClock();
    using (var store = new HardwareOnboardingStore(Options.Create(Settings("managed-enrollment", true)), nodeClock,
        NullLogger<HardwareOnboardingStore>.Instance))
    {
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        var discovered = await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        var approvedNode = await store.ApproveInstallAsync(discovered.DeviceId, Approval("managed-fixture"), actor);
        Check(approvedNode.RecoveryPublicKey == recoveryKey, "Recovery access was not pinned to owner approval.");
        var session = Session(discovered);
        var nodeGrant = await store.RequestInstallationGrantAsync(session, Guid.NewGuid(), diskB, 1);
        Check(nodeGrant.ProgressExpiresAt == nodeClock.GetUtcNow().AddHours(2), "Installation progress did not receive a bounded two-hour lease.");
        nodeClock.Advance(TimeSpan.FromMinutes(16));
        await store.ReportStatusAsync(session, HardwareDevicePhase.Installing, "Synthetic disposable installation");
        await Fails(() => store.RequestInstallationGrantAsync(session, Guid.NewGuid(), diskB, 1), 409, "approval_mismatch");
        nodeClock.Advance(TimeSpan.FromMinutes(20));
        await store.ReportStatusAsync(session, HardwareDevicePhase.AwaitingEnrollment, "Synthetic installed node waiting for enrollment");
        Check((await store.RequireEnrollmentAsync(session, approvedNode.Id, default)).Id == approvedNode.Id,
            "Completed installation was stranded by the shorter discovery lifetime.");
        await Fails(() => store.CompleteEnrollmentAsync(discovered.DeviceId, approvedNode.Id, fingerprintB, default), 409, "enrollment_mismatch");
        var managed = await store.CompleteEnrollmentAsync(discovered.DeviceId, approvedNode.Id, fingerprintA, default);
        Check(managed.Phase == HardwareDevicePhase.Managed
            && (await store.GetSnapshotAsync()).Tasks.Single().Phase == HardwareTaskPhase.Managed,
            "Verified enrollment did not transition device and installation task together.");
        await Fails(() => store.GetSessionStatusAsync(session), 403, "invalid_device_session");
        await Fails(() => store.RegisterDiscoveryAsync(fingerprintB, inventory, false), 409, "managed_device");
        await Fails(() => store.SetDiscoveryDismissedAsync(discovered.DeviceId, true, actor), 409, "not_rejected");
        nodeClock.Advance(TimeSpan.FromMinutes(3));
        Check((await store.ManagedHeartbeatAsync(discovered.DeviceId, fingerprintA, default)).HeartbeatFreshness == HeartbeatFreshness.Fresh,
            "Certificate-authenticated managed heartbeat did not refresh reported state.");
    }

    Console.WriteLine("Checking strict persisted-state validation and no corruption fallback...");
    foreach (var version in new[] { 1, 2 })
    {
        var legacySettings = Settings("legacy-state-" + version, true);
        Directory.CreateDirectory(legacySettings.StateDirectory);
        var legacy = JsonNode.Parse(validState)!;
        legacy["version"] = version;
        foreach (var device in legacy["devices"]!.AsArray())
        {
            if (version == 1)
                device!["device"]!.AsObject().Remove("verificationCode");
            else
            {
                var fingerprint = device!["sessionKeyFingerprint"]!.GetValue<string>();
                device["device"]!["verificationCode"] = HardwareOnboardingStore.GetVerificationCode(fingerprint) + "-" + fingerprint[12..16];
            }
        }
        var legacyTasks = legacy["tasks"]!.DeepClone();
        var legacyCapabilityHash = legacy["devices"]![0]!["capabilityHash"]!.GetValue<string>();
        var legacyExpiry = legacy["devices"]![0]!["device"]!["discoveryExpiresAt"]!.GetValue<string>();
        await File.WriteAllTextAsync(Path.Combine(legacySettings.StateDirectory, "state.json"), legacy.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(legacySettings.StateDirectory, ".onboarding.lease"), "Lucia.Onboarding.v1\n");
        using (var store = Store(legacySettings))
        {
            await store.StartAsync(default);
            var restored = await store.VerifyDiscoveryCapabilityAsync(admitted.DeviceId, fingerprintA, admitted.Token!);
            Check(restored.VerificationCode == admitted.VerificationCode && restored.VerificationCode.Length == 14
                && restored.Phase == HardwareDevicePhase.AwaitingEnrollment,
                "Legacy state must derive the 12-hex comparison code from its bound key without losing device-reported progress.");
            var upgraded = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(legacySettings.StateDirectory, "state.json")))!;
            Check(upgraded["version"]!.GetValue<int>() == 3 && JsonNode.DeepEquals(upgraded["tasks"], legacyTasks)
                && upgraded["devices"]![0]!["capabilityHash"]!.GetValue<string>() == legacyCapabilityHash
                && upgraded["devices"]![0]!["device"]!["discoveryExpiresAt"]!.GetValue<string>() == legacyExpiry,
                "The code migration must preserve jobs, request IDs, deadlines, credentials, and their existing authority.");
            Check(!(await store.GetSnapshotAsync()).Window.IsOpen, "Schema upgrade must never reopen admission.");
        }
        var invalidLegacySettings = Settings("invalid-legacy-state-" + version, true);
        Directory.CreateDirectory(invalidLegacySettings.StateDirectory);
        if (version == 1)
            legacy["devices"]![0]!["device"]!["inventoryRevision"] = 0;
        else
            legacy["devices"]![0]!["device"]!["verificationCode"] = "BBBB-BBBB-BBBB-BBBB";
        var invalidLegacyJson = legacy.ToJsonString();
        await File.WriteAllTextAsync(Path.Combine(invalidLegacySettings.StateDirectory, "state.json"), invalidLegacyJson);
        await File.WriteAllTextAsync(Path.Combine(invalidLegacySettings.StateDirectory, ".onboarding.lease"), "Lucia.Onboarding.v1\n");
        using (var store = Store(invalidLegacySettings))
        {
            try { await store.StartAsync(default); throw new InvalidOperationException("Schema migration masked corrupt legacy authority or comparison code."); }
            catch (InvalidDataException) { checks++; }
            Check(await File.ReadAllTextAsync(Path.Combine(invalidLegacySettings.StateDirectory, "state.json")) == invalidLegacyJson,
                "Corrupt legacy data must be preserved, not migrated or reset.");
        }
    }
    var mutations = new Action<JsonNode>[]
    {
        state => state["version"] = 4,
        state => state.AsObject().Remove("version"),
        state => state["devices"] = null,
        state => state["devices"]![0]!["device"]!["inventoryRevision"] = 0,
        state => state["devices"]![0]!["inventoryHash"] = new string('0', 64),
        state => state["devices"]![0]!["capabilityHash"] = "malformed",
        state => state["devices"]![0]!["device"]!["phase"] = "Managed",
        state => state["devices"]![0]!["device"]!["verificationCode"] = "BBBB-BBBB-BBBB",
        state => state["devices"]![0]!["device"]!.AsObject().Remove("verificationCode"),
        state => state["devices"]![0]!["device"]!["updatedAt"] = clock.GetUtcNow().AddDays(1).ToString("O"),
        state => state["devices"]![0]!["device"]!["dismissedAt"] = clock.GetUtcNow().ToString("O"),
        state => state["devices"]![0]!["scope"] = 77,
        state => state["devices"]![0]!["device"]!["taskId"] = Guid.NewGuid().ToString(),
        state => state["devices"]![0]!["device"]!["hardware"]!["disks"]![0]!["path"] = "/dev/sda;reboot",
        state => state["tasks"]![0]!["sessionKeyFingerprint"] = fingerprintB,
        state => state["tasks"]![0]!["task"]!["authorityExpiresAt"] = clock.GetUtcNow().AddDays(1).ToString("O"),
        state => state["tasks"]![0]!["grantRequestId"] = null,
        state => state["tasks"]![0]!["task"]!["phase"] = "Invalidated",
        state => state["tasks"]![0]!["task"]!["diskId"] = diskB + "_missing",
        state => state["events"]![0]!["deviceId"] = Guid.NewGuid().ToString(),
        state => state["events"]![0]!["sequence"] = 0,
        state => state["events"]![0]!["at"] = clock.GetUtcNow().AddDays(1).ToString("O"),
        state => state["password"] = "must-not-be-accepted"
    };
    var malformed = new List<string> { "", "{", "null", validState.Replace("\"version\":3", "\"version\":3,\"version\":3", StringComparison.Ordinal) };
    foreach (var mutate in mutations)
    {
        var node = JsonNode.Parse(validState)!;
        mutate(node);
        malformed.Add(node.ToJsonString());
    }
    for (var index = 0; index < malformed.Count; index++)
    {
        var settings = Settings("corrupt-" + index, true);
        Directory.CreateDirectory(settings.StateDirectory);
        var file = Path.Combine(settings.StateDirectory, "state.json");
        await File.WriteAllTextAsync(file, malformed[index]);
        await File.WriteAllTextAsync(Path.Combine(settings.StateDirectory, ".onboarding.lease"), "Lucia.Onboarding.v1\n");
        using var store = Store(settings);
        try { await store.StartAsync(default); throw new InvalidOperationException($"Corrupt state {index} was accepted."); }
        catch (InvalidDataException) { checks++; }
        Check(await File.ReadAllTextAsync(file) == malformed[index], "Startup must not overwrite corrupted state.");
        await Fails(() => store.GetSnapshotAsync(), 503);
    }
    var orphan = Settings("orphan");
    Directory.CreateDirectory(orphan.StateDirectory);
    await File.WriteAllTextAsync(Path.Combine(orphan.StateDirectory, "state.synthetic.pending"), "{}");
    using (var store = Store(orphan))
    {
        try { await store.StartAsync(default); throw new InvalidOperationException("Orphan snapshot was silently discarded."); }
        catch (InvalidDataException) { checks++; }
    }
    var oversizedState = Settings("oversized-state");
    Directory.CreateDirectory(oversizedState.StateDirectory);
    using (var file = File.Create(Path.Combine(oversizedState.StateDirectory, "state.json")))
        file.SetLength(HardwareOnboardingStore.MaximumStateBytes + 1L);
    await File.WriteAllTextAsync(Path.Combine(oversizedState.StateDirectory, ".onboarding.lease"), "Lucia.Onboarding.v1\n");
    using (var store = Store(oversizedState))
    {
        try { await store.StartAsync(default); throw new InvalidOperationException("Oversized state was loaded."); }
        catch (InvalidDataException) { checks++; }
        Check(new FileInfo(Path.Combine(oversizedState.StateDirectory, "state.json")).Length == 33554433,
            "Oversized persisted state must be rejected before parsing and preserved unchanged.");
    }
    var missing = Settings("missing");
    using (var store = Store(missing)) { await store.StartAsync(default); }
    File.Delete(Path.Combine(missing.StateDirectory, "state.json"));
    using (var store = Store(missing))
    {
        try { await store.StartAsync(default); throw new InvalidOperationException("A lost manifest silently reset installation authority."); }
        catch (InvalidDataException) { checks++; }
    }
    var persistence = Settings("write-failure", true);
    DiscoveryReceipt failureReceipt;
    using (var store = Store(persistence))
    {
        await store.StartAsync(default);
        await store.OpenWindowAsync(30, actor);
        failureReceipt = await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        await store.ApproveInstallAsync(failureReceipt.DeviceId, Approval(), actor);
        var file = Path.Combine(persistence.StateDirectory, "state.json");
        File.Move(file, file + ".saved");
        Directory.CreateDirectory(file);
        await Fails(() => store.RequestInstallationGrantAsync(Session(failureReceipt), Guid.NewGuid(), diskB, 1), 503, "persistence_failed");
        Directory.Delete(file);
        File.Move(file + ".saved", file);
        await Fails(() => store.GetSnapshotAsync(), 503, "onboarding_unavailable");
        Check(!Directory.EnumerateFiles(persistence.StateDirectory, "*.pending").Any(), "Failed writes should remove only their own incomplete snapshot.");
    }
    using (var store = Store(persistence))
    {
        await store.StartAsync(default);
        Check((await store.GetSnapshotAsync()).Tasks.Single().Phase == HardwareTaskPhase.Approved,
            "A failed persistence operation must never return or commit installation authority.");
        await store.RequestInstallationGrantAsync(Session(failureReceipt), Guid.NewGuid(), diskB, 1);
    }

    Console.WriteLine("Checking real ASP.NET authorization, browser contracts, and global CSRF integration...");
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production", ContentRootPath = root });
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["HardwareOnboarding:StateDirectory"] = Path.Combine(root, "http"),
        ["HardwareOnboarding:DiscoveryNetworkCidr"] = "192.168.40.0/24",
        ["HardwareOnboarding:BootBaseUrl"] = "https://boot.synthetic.invalid",
        ["HardwareOnboarding:DiscoveryAdapterQualified"] = "true"
    });
    builder.Services.AddSingleton<TimeProvider>(clock);
    builder.Services.AddAuthentication("TestSelect")
        .AddPolicyScheme("TestSelect", null, options => options.ForwardDefaultSelector = context =>
            context.Request.Headers.ContainsKey("X-Test-Role") ? "TestHeader" : "TestCookie")
        .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("TestHeader", null)
        .AddCookie("TestCookie", options =>
        {
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
    builder.Services.AddAuthorizationBuilder().AddPolicy("HostOwner", policy => policy.RequireAuthenticatedUser().RequireRole("Owner"));
    Directory.CreateDirectory(Path.Combine(root, "keys"));
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
    builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
    builder.AddHardwareOnboarding();
    await using (var app = builder.Build())
    {
        app.UseAuthentication();
        app.UseAuthorization();
        // Mirrors the existing global host CSRF placement; no endpoint-specific exemption is added.
        app.Use(async (context, next) =>
        {
            if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE")
                && (await context.AuthenticateAsync()).Ticket?.AuthenticationScheme == "TestCookie")
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException) { context.Response.StatusCode = 403; return; }
            }
            await next(context);
        });
        app.MapHardwareOnboarding();
        app.MapPost("/test/login", async (HttpContext context) =>
        {
            await context.SignInAsync("TestCookie", new ClaimsPrincipal(new ClaimsIdentity(
                [new(ClaimTypes.NameIdentifier, "synthetic-cookie-owner"), new(ClaimTypes.Role, "Owner")], "TestCookie")));
            return Results.Ok();
        });
        app.MapGet("/test/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Json(new { token = antiforgery.GetAndStoreTokens(context).RequestToken })).RequireAuthorization("HostOwner");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        var paths = new[] { (HttpMethod.Get, "/api/host/onboarding"), (HttpMethod.Get, "/api/host/ssh-keys/github/invalid--name"),
            (HttpMethod.Post, "/api/host/onboarding/window"),
            (HttpMethod.Delete, "/api/host/onboarding/window"), (HttpMethod.Post, $"/api/host/devices/{Guid.NewGuid():D}/approve-install"),
            (HttpMethod.Post, $"/api/host/devices/{Guid.NewGuid():D}/reject"),
            (HttpMethod.Post, $"/api/host/devices/{Guid.NewGuid():D}/dismiss"),
            (HttpMethod.Post, $"/api/host/devices/{Guid.NewGuid():D}/restore") };
        foreach (var role in new string?[] { null, "Inference" })
            foreach (var (method, path) in paths)
            {
                using var request = new HttpRequestMessage(method, path) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
                if (role is not null) request.Headers.Add("X-Test-Role", role);
                using var response = await client.SendAsync(request);
                Check((int)response.StatusCode == (role is null ? 401 : 403), "Non-owner must be denied before body validation or mutation.");
            }
        client.DefaultRequestHeaders.Add("X-Test-Role", "Owner");
        using (var response = await client.GetAsync("/api/host/ssh-keys/github/invalid--name"))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "GitHub key imports accepted an invalid username.");
        using (var response = await client.GetAsync("/api/host/onboarding"))
        {
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Owner snapshot must be noncacheable.");
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Check(json.AsObject().Select(pair => pair.Key).SequenceEqual(["window", "readiness", "devices", "tasks"]), "Browser snapshot contract must be exact camelCase.");
            Check(json["readiness"]!["canDiscover"]!.GetValue<bool>() && !json["readiness"]!["canInstall"]!.GetValue<bool>(), "HTTP install readiness must fail closed.");
        }
        foreach (var body in new[] { "null", "{", "{}", "{\"minutes\":0}", "{\"minutes\":61}", "{\"minutes\":30,\"installationEnabled\":true}",
            "{\"minutes\":30,\"minutes\":1}", "{\"minutes\":30,\"secret\":\"" + new string('x', 4096) + "\"}" })
        {
            using var response = await client.PostAsync("/api/host/onboarding/window", new StringContent(body, Encoding.UTF8, "application/json"));
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed or oversized browser body must be rejected.");
            var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!;
            Check(error["code"] is not null && error["message"] is not null && error["type"] is not null, "Errors must have a structured code, message, and type.");
        }
        using (var response = await client.PostAsJsonAsync("/api/host/onboarding/window", new { minutes = 30 }))
            Check(response.StatusCode == HttpStatusCode.OK, "Owner should be able to open a qualified discovery window.");
        var store = app.Services.GetRequiredService<HardwareOnboardingStore>();
        var receipt = await store.RegisterDiscoveryAsync(fingerprintA, inventory, false);
        using (var response = await client.PostAsJsonAsync($"/api/host/devices/{receipt.DeviceId:D}/approve-install", Approval()))
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "HTTP cannot bypass disabled installation readiness.");
        using (var response = await client.PostAsJsonAsync($"/api/host/devices/{Guid.NewGuid():D}/approve-install", Approval()))
            Check(response.StatusCode == HttpStatusCode.NotFound, "Unknown device should have a structured 404.");
        using (var response = await client.PostAsync("/api/host/devices/not-a-uuid/reject", null))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid IDs should have a structured 400.");
        var payload = await client.GetStringAsync("/api/host/onboarding");
        Check(!payload.Contains(receipt.Token!) && !payload.Contains(fingerprintA) && !payload.Contains("capability", StringComparison.OrdinalIgnoreCase),
            "Browser wire response must not contain session credentials.");
        Check(JsonNode.Parse(payload)!["devices"]![0]!["verificationCode"]!.GetValue<string>() == receipt.VerificationCode,
            "Browser and physical device must receive the same fingerprint comparison code, without exposing the capability.");
        using (var response = await client.DeleteAsync("/api/host/onboarding/window"))
            Check(response.StatusCode == HttpStatusCode.OK && !(await store.GetSnapshotAsync()).Window.IsOpen, "Owner close must persist.");
        using (var response = await client.PostAsync($"/api/host/devices/{receipt.DeviceId:D}/reject", null))
            Check(response.StatusCode == HttpStatusCode.OK, "Owner may reject an existing pending discovery after closing admissions.");
        using (var response = await client.PostAsync($"/api/host/devices/{receipt.DeviceId:D}/reject", null))
            Check(response.StatusCode == HttpStatusCode.Conflict, "Repeated rejection must be a conflict.");
        using (var response = await client.PostAsync($"/api/host/devices/{receipt.DeviceId:D}/dismiss", null))
            Check(response.StatusCode == HttpStatusCode.OK && (await store.GetSnapshotAsync()).Devices.Single().DismissedAt is not null,
                "Owner dismissal did not persist through the HTTP endpoint.");
        using (var response = await client.PostAsync("/api/host/devices/not-a-uuid/restore", null))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid restore IDs were not rejected.");
        using (var response = await client.PostAsync("/api/host/onboarding/discovery", null))
            Check(response.StatusCode == HttpStatusCode.NotFound, "No insecure discovery endpoint may be exposed.");
        using var cookieClient = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
            { BaseAddress = client.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
        using (var response = await cookieClient.PostAsync("/test/login", null))
            Check(response.StatusCode == HttpStatusCode.OK, "Synthetic owner cookie sign-in should work.");
        using (var response = await cookieClient.PostAsJsonAsync("/api/host/onboarding/window", new { minutes = 30 }))
            Check(response.StatusCode == HttpStatusCode.Forbidden, "Cookie mutations must require the existing global CSRF check.");
        foreach (var operation in new[] { "dismiss", "restore" })
        {
            using var response = await cookieClient.PostAsync($"/api/host/devices/{receipt.DeviceId:D}/{operation}", null);
            Check(response.StatusCode == HttpStatusCode.Forbidden, "Discovery visibility mutation bypassed cookie CSRF.");
        }
        var token = (await cookieClient.GetFromJsonAsync<JsonObject>("/test/csrf"))!["token"]!.GetValue<string>();
        cookieClient.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        using (var response = await cookieClient.PostAsJsonAsync("/api/host/onboarding/window", new { minutes = 30 }))
            Check(response.StatusCode == HttpStatusCode.OK, "An owner cookie with valid CSRF may mutate.");
        using (var response = await cookieClient.PostAsync($"/api/host/devices/{receipt.DeviceId:D}/restore", null))
            Check(response.StatusCode == HttpStatusCode.OK && (await store.GetSnapshotAsync()).Devices.Single().DismissedAt is null,
                "An owner cookie with CSRF could not restore list visibility.");
        await app.StopAsync();
    }
    Console.WriteLine($"PASS: {checks} hardware onboarding checks (synthetic local data; no disks, PXE, enrollment, GPU, or live services used).");
}
finally
{
    Directory.Delete(root, recursive: true);
}

sealed class CheckClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}

sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (role is not ("Owner" or "Inference")) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "synthetic-" + role), new(ClaimTypes.Role, role)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
