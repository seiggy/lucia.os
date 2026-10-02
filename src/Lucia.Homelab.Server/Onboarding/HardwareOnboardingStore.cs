using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Onboarding;

public sealed class HardwareOnboardingStore : IHostedService, IDisposable
{
    public const int MaximumDevices = 128;
    public const int MaximumTasks = 256;
    public const int MaximumEvents = 2048;
    public const int MaximumHardwareReportBytes = 131072;
    public const int MaximumInterfaces = 128;
    public const int MaximumDisks = 128;
    public const int MaximumAddressesPerInterface = 32;
    public const int MaximumStateBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan InstallationAuthorityLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan InstallationProgressLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan HeartbeatFreshnessLifetime = TimeSpan.FromMinutes(2);
    private static readonly byte[] LeaseMarker = "Lucia.Onboarding.v1\n"u8.ToArray();
    private readonly HardwareOnboardingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<HardwareOnboardingStore> _logger;
    // ponytail: one serialized, bounded snapshot; use a transactional database beyond 128 devices.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _lease;
    private State? _state;
    private bool _faulted;
    private bool _disposed;

    public HardwareOnboardingStore(IOptions<HardwareOnboardingOptions> options, TimeProvider clock,
        ILogger<HardwareOnboardingStore> logger)
    {
        _options = JsonSerializer.Deserialize<HardwareOnboardingOptions>(
            JsonSerializer.Serialize(options.Value, HardwareOnboardingJson.Options), HardwareOnboardingJson.Options)!;
        _options.Validate();
        _clock = clock;
        _logger = logger;
    }

    private string StatePath => Path.Combine(_options.StateDirectory, "state.json");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not null || _lease is not null) throw new InvalidOperationException("Onboarding is already started.");
            RejectLinks(_options.StateDirectory);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_options.StateDirectory);
            else Directory.CreateDirectory(_options.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _lease = AcquireLease();
            try
            {
                RejectLinks(StatePath);
                var initialized = _lease.Length > 0;
                if (initialized)
                {
                    var marker = new byte[LeaseMarker.Length];
                    if (_lease.Length != marker.Length) throw new InvalidDataException("The onboarding lease marker is invalid.");
                    _lease.ReadExactly(marker);
                    if (!marker.AsSpan().SequenceEqual(LeaseMarker)) throw new InvalidDataException("The onboarding lease marker is invalid.");
                }
                State loaded;
                if (File.Exists(StatePath))
                {
                    if (!initialized) throw new InvalidDataException("Onboarding state has no initialization marker; operator recovery is required.");
                    using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length is <= 0 or > MaximumStateBytes) throw new InvalidDataException("Onboarding state size is invalid.");
                    try
                    {
                        loaded = await JsonSerializer.DeserializeAsync<State>(stream, HardwareOnboardingJson.Options, cancellationToken)
                            ?? throw new InvalidDataException("Onboarding state is empty.");
                        ValidateState(loaded, _clock.GetUtcNow());
                        if (loaded.Version < 3)
                            loaded = loaded with { Version = 3, Devices = loaded.Devices.Select(device => device with
                                { Device = device.Device with { VerificationCode = GetVerificationCode(device.SessionKeyFingerprint) } }).ToList() };
                    }
                    catch (Exception exception) when (exception is JsonException or HardwareOnboardingException or ArgumentException
                        or NullReferenceException or InvalidOperationException or OverflowException)
                    {
                        throw new InvalidDataException("Onboarding state is invalid; preserve it and repair it before restarting.", exception);
                    }
                }
                else
                {
                    if (initialized || Directory.Exists(StatePath) || Directory.EnumerateFiles(_options.StateDirectory, "state.*.pending").Any())
                        throw new InvalidDataException("Onboarding state is missing or incomplete; operator recovery is required.");
                    loaded = new(3, null, 0, [], [], []);
                    // Mark first, so a crash or later deletion cannot silently recreate an empty authority registry.
                    _lease.Write(LeaseMarker);
                    _lease.Flush(flushToDisk: true);
                }
                loaded = loaded with { WindowExpiresAt = null };
                loaded = Event(loaded, _clock.GetUtcNow(), "AdmissionClosedOnStartup", "host", null, null);
                await PersistAsync(loaded, cancellationToken);
                _state = loaded;
            }
            catch
            {
                _lease.Dispose();
                _lease = null;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    // The directory lease intentionally lasts until disposal, not merely StopAsync.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<HardwareOnboardingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        ReadAsync((state, now) => new HardwareOnboardingSnapshot(Window(state, now), _options.Readiness(),
            state.Devices.Select(device => PublicDevice(device, now)).ToArray(),
            state.Tasks.Select(task => task.Task with { }).ToArray()), cancellationToken);

    /// <summary>Preflight for a new-admission challenge. Registration rechecks admission atomically.</summary>
    public Task<HardwareAdmissionWindow> AssertCanDiscoverAsync(CancellationToken cancellationToken = default) =>
        ReadAsync((state, now) =>
        {
            RequireDiscovery();
            var window = Window(state, now);
            if (!window.IsOpen) throw Conflict("admission_closed", "The Add hardware window is closed.");
            return window;
        }, cancellationToken);

    /// <summary>Trusted adapter lookup only; finding a device is not authentication or renewed authority.</summary>
    public Task<HardwareDevice?> FindDiscoveryAsync(string publicKeyFingerprint, CancellationToken cancellationToken = default)
    {
        var fingerprint = Fingerprint(publicKeyFingerprint);
        return ReadAsync((state, now) => state.Devices.SingleOrDefault(device => device.SessionKeyFingerprint == fingerprint)
            is { } found ? PublicDevice(found, now) : null, cancellationToken);
    }

    public Task<HardwareDevice> VerifyDiscoveryCapabilityAsync(Guid deviceId, string publicKeyFingerprint, string token,
        CancellationToken cancellationToken = default) =>
        GetSessionStatusAsync(new(deviceId, publicKeyFingerprint, token), cancellationToken);

    public static string GetVerificationCode(string publicKeyFingerprint)
    {
        var fingerprint = Fingerprint(publicKeyFingerprint);
        return $"{fingerprint[..4]}-{fingerprint[4..8]}-{fingerprint[8..12]}";
    }

    public Task<HardwareAdmissionWindow> OpenWindowAsync(int minutes, string actor, CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            Actor(actor);
            HardwareInventoryValidation.Require(minutes is >= 1 and <= 60, "Window minutes must be between 1 and 60.");
            RequireDiscovery();
            state = state with { WindowExpiresAt = now.AddMinutes(minutes) };
            return (Event(state, now, "AdmissionOpened", actor, null, null), Window(state, now));
        }, cancellationToken);

    public Task<HardwareAdmissionWindow> CloseWindowAsync(string actor, CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            Actor(actor);
            state = state with { WindowExpiresAt = null };
            return (Event(state, now, "AdmissionClosed", actor, null, null), Window(state, now));
        }, cancellationToken);

    /// <summary>
    /// The adapter must verify possession of the session public key, source-network eligibility, and the
    /// authoritative managed-node registry before calling. The fingerprint is SHA-256 of the session SPKI.
    /// Token is returned only on first admission; duplicate registration cannot rotate or recover it.
    /// </summary>
    public Task<DiscoveryReceipt> RegisterDiscoveryAsync(string publicKeyFingerprint, HardwareReport inventory,
        bool isKnownManagedDevice, CancellationToken cancellationToken = default)
    {
        var fingerprint = Fingerprint(publicKeyFingerprint);
        var report = HardwareInventoryValidation.CopyAndValidate(inventory);
        var inventoryHash = InventoryHash(report);
        return MutateAsync((state, now) =>
        {
            RequireDiscovery();
            if (isKnownManagedDevice || report.HardwareUuid is not null && state.Devices.Any(entry =>
                entry.Device.Phase == HardwareDevicePhase.Managed && entry.Device.Hardware.HardwareUuid == report.HardwareUuid))
                throw Conflict("managed_device", "Managed devices cannot enter onboarding or be reinstalled.");
            var index = state.Devices.FindIndex(device => device.SessionKeyFingerprint == fingerprint);
            if (index >= 0)
            {
                var existing = state.Devices[index];
                var device = existing.Device;
                if (device.DiscoveryExpiresAt <= now || existing.Scope == CapabilityScope.Revoked)
                    throw Conflict("discovery_expired", "This discovery session is expired or closed; it cannot be renewed.");
                var changed = existing.InventoryHash != inventoryHash && StableInventory(existing.Device.Hardware) != StableInventory(report);
                if (changed)
                {
                    var hadGrant = false;
                    if (device.TaskId is { } taskId)
                    {
                        var jobIndex = state.Tasks.FindIndex(task => task.Task.Id == taskId);
                        var job = state.Tasks[jobIndex];
                        hadGrant = job.GrantRequestId is not null;
                        state.Tasks[jobIndex] = job with { Task = job.Task with
                        {
                            Phase = HardwareTaskPhase.Invalidated, UpdatedAt = now, StatusMessage = "Inventory changed; approval invalidated."
                        } };
                    }
                    device = device with
                    {
                        Hardware = report, InventoryRevision = checked(device.InventoryRevision + 1),
                        Phase = hadGrant ? HardwareDevicePhase.Failed : HardwareDevicePhase.Discovered,
                        TaskId = hadGrant ? device.TaskId : null, StatusMessage = "Inventory changed; approval invalidated."
                    };
                    existing = existing with { InventoryHash = inventoryHash,
                        Scope = hadGrant ? CapabilityScope.Revoked : CapabilityScope.Discovery };
                    state = Event(state, now, "InventoryChanged", "device-session", device.Id, device.TaskId);
                }
                device = device with { LastSeenAt = now, UpdatedAt = now };
                state.Devices[index] = existing with { Device = device };
                return (state, new DiscoveryReceipt(device.Id, null, device.DiscoveryExpiresAt, device.VerificationCode));
            }
            if (!Window(state, now).IsOpen) throw Conflict("admission_closed", "The Add hardware window is closed.");
            if (state.Devices.Count >= MaximumDevices) throw Unavailable("registry_full", "The bounded onboarding device registry is full.");
            var verificationCode = GetVerificationCode(fingerprint);
            if (state.Devices.Any(device => device.Device.VerificationCode == verificationCode))
                throw Conflict("verification_code_collision", "This key's comparison code is already associated with another device. Generate a new session key.");
            var capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var admitted = new HardwareDevice(Guid.NewGuid(), HardwareDevicePhase.Discovered, report, 1, now, now, now,
                null, HeartbeatFreshness.Unknown, now + DiscoveryLifetime, null, null, verificationCode);
            state.Devices.Add(new(admitted, fingerprint, inventoryHash, SecretHash(capability), CapabilityScope.Discovery));
            return (Event(state, now, "Discovered", "device-session", admitted.Id, null),
                new DiscoveryReceipt(admitted.Id, capability, admitted.DiscoveryExpiresAt, verificationCode));
        }, cancellationToken);
    }

    public Task<HardwareInstallationTask> ApproveInstallAsync(Guid deviceId, ApproveHardwareInstallRequest request,
        string actor, CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            Actor(actor);
            HardwareInventoryValidation.Require(request is not null && request.Confirmation == "ERASE", "Confirmation must be the literal ERASE.");
            HardwareInventoryValidation.Require(HardwareInventoryValidation.Hostname(request.Hostname),
                "Hostname must be a lowercase DNS label of 1..63 characters.");
            HardwareInventoryValidation.Require(request.Hostname[0] is >= 'a' and <= 'z' && request.Hostname != "localhost"
                && (_options.BootBaseUrl is null || !new Uri(_options.BootBaseUrl).Host.Equals(request.Hostname, StringComparison.OrdinalIgnoreCase)),
                "Choose a hostname beginning with a lowercase letter, distinct from localhost and the Lucia controller.");
            HardwareInventoryValidation.Require(HardwareInventoryValidation.StableDiskId(request.DiskId), "A stable disk ID is required.");
            var index = DeviceIndex(state, deviceId);
            var stored = state.Devices[index];
            var device = stored.Device;
            if (device.Phase != HardwareDevicePhase.Discovered || stored.Scope != CapabilityScope.Discovery
                || state.Tasks.Any(task => task.Task.DeviceId == deviceId && task.GrantRequestId is not null))
                throw Conflict("not_pending", "Only pending discoveries can be approved; reinstall is not supported.");
            if (device.DiscoveryExpiresAt <= now) throw Conflict("discovery_expired", "The discovery session has expired.");
            RequireInstallation();
            var recoveryKey = RecoverySshKeys.Parse(request.RecoveryPublicKey ?? "").PublicKey;
            if (device.Hardware.Architecture != "x86_64" || device.Hardware.BootMode != "uefi")
                throw Conflict("unsupported_boot", "Installation requires x86_64 UEFI.");
            var disk = device.Hardware.Disks.SingleOrDefault(disk => disk.Id == request.DiskId);
            if (disk is null || disk.IsReadOnly || disk.IsRemovable)
                throw Conflict("ineligible_disk", "Choose an exact inventory stable ID for a nonremovable, writable disk.");
            // An install that stopped reporting before enrolling no longer holds its hostname.
            if (state.Tasks.Any(task => task.Task.Hostname == request.Hostname && (task.Task.Phase == HardwareTaskPhase.Managed
                || task.Task.Phase is not (HardwareTaskPhase.Failed or HardwareTaskPhase.Invalidated)
                    && (task.Task.ProgressExpiresAt ?? task.Task.AuthorityExpiresAt) > now)))
                throw Conflict("hostname_in_use", "An existing installation task already reserves this hostname.");
            if (state.Tasks.Count >= MaximumTasks) throw Unavailable("registry_full", "The bounded onboarding task registry is full.");
            var job = new HardwareInstallationTask(Guid.NewGuid(), device.Id, HardwareTaskPhase.Approved, request.Hostname,
                request.DiskId, device.InventoryRevision, actor, now, Min(now + InstallationAuthorityLifetime, device.DiscoveryExpiresAt),
                null, now, null, recoveryKey);
            state.Tasks.Add(new(job, stored.SessionKeyFingerprint, stored.InventoryHash, null));
            state.Devices[index] = stored with { Scope = CapabilityScope.Installation, Device = device with
                { Phase = HardwareDevicePhase.Approved, TaskId = job.Id, UpdatedAt = now, StatusMessage = null } };
            return (Event(state, now, "InstallationApproved", actor, device.Id, job.Id), job);
        }, cancellationToken);

    public Task<HardwareDevice> RejectDiscoveryAsync(Guid deviceId, string actor, CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            Actor(actor);
            var index = DeviceIndex(state, deviceId);
            var stored = state.Devices[index];
            if (stored.Device.Phase != HardwareDevicePhase.Discovered)
                throw Conflict("not_pending", "Only pending discoveries can be rejected.");
            stored = stored with { Scope = CapabilityScope.Revoked, Device = stored.Device with
                { Phase = HardwareDevicePhase.Rejected, UpdatedAt = now } };
            state.Devices[index] = stored;
            return (Event(state, now, "DiscoveryRejected", actor, deviceId, null), PublicDevice(stored, now));
        }, cancellationToken);

    public Task<HardwareDevice> GetSessionStatusAsync(HardwareDeviceSession session, CancellationToken cancellationToken = default) =>
        ReadAsync((state, now) => PublicDevice(Authenticate(state, session, now), now), cancellationToken);

    public Task<HardwareDevice> SetDiscoveryDismissedAsync(Guid deviceId, bool dismissed, string actor,
        CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            Actor(actor);
            var index = DeviceIndex(state, deviceId);
            var stored = state.Devices[index];
            if (stored.Device.Phase != HardwareDevicePhase.Rejected)
                throw Conflict("not_rejected", "Only rejected discoveries can be dismissed or restored to the list.");
            if ((stored.Device.DismissedAt is not null) == dismissed)
                return (state, PublicDevice(stored, now));
            stored = stored with { Device = stored.Device with { DismissedAt = dismissed ? now : null, UpdatedAt = now } };
            state.Devices[index] = stored;
            return (Event(state, now, dismissed ? "DiscoveryDismissed" : "DiscoveryRestoredToList", actor, deviceId, null),
                PublicDevice(stored, now));
        }, cancellationToken);

    // Polling configuration never grants installation authority or exposes credentials.
    public Task<HardwareSessionConfiguration> GetSessionConfigurationAsync(HardwareDeviceSession session,
        CancellationToken cancellationToken = default) =>
        ReadAsync((state, now) =>
        {
            var stored = Authenticate(state, session, now);
            var task = state.Tasks.SingleOrDefault(task => task.Task.Id == stored.Device.TaskId);
            return new HardwareSessionConfiguration(stored.Device.Id, stored.Device.InventoryRevision, task?.Task.Id,
                task?.Task.AuthorityExpiresAt, _options.Readiness().CanInstall && stored.Scope == CapabilityScope.Installation
                && task is { GrantRequestId: null } && task.Task.Phase == HardwareTaskPhase.Approved && task.Task.AuthorityExpiresAt > now,
                task?.Task.Hostname, task?.Task.DiskId, task?.Task.RecoveryPublicKey);
        }, cancellationToken);

    public Task<HardwareDevice> HeartbeatAsync(HardwareDeviceSession session, CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            var stored = Authenticate(state, session, now);
            stored = stored with { Device = stored.Device with { LastHeartbeatAt = now, LastSeenAt = now, UpdatedAt = now } };
            state.Devices[DeviceIndex(state, stored.Device.Id)] = stored;
            return (state, PublicDevice(stored, now));
        }, cancellationToken);

    public Task<HardwareInstallationGrant> RequestInstallationGrantAsync(HardwareDeviceSession session, Guid requestId,
        string diskId, long inventoryRevision, CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            HardwareInventoryValidation.Require(requestId != Guid.Empty, "An installation request UUID is required.");
            var stored = Authenticate(state, session, now);
            RequireInstallation();
            var taskIndex = state.Tasks.FindIndex(task => task.Task.Id == stored.Device.TaskId);
            if (stored.Scope != CapabilityScope.Installation || taskIndex < 0)
                throw Conflict("not_approved", "This session has no installation approval.");
            var job = state.Tasks[taskIndex];
            if (job.Task.Phase is HardwareTaskPhase.Failed or HardwareTaskPhase.Invalidated
                || job.Task.AuthorityExpiresAt <= now || job.Task.DiskId != diskId
                || job.Task.InventoryRevision != inventoryRevision || stored.Device.InventoryRevision != inventoryRevision
                || job.InventoryHash != stored.InventoryHash || job.SessionKeyFingerprint != stored.SessionKeyFingerprint)
                throw Conflict("approval_mismatch", "The bounded installation approval is expired or does not match this disk, inventory, or session.");
            if (job.GrantRequestId is { } priorRequest && priorRequest != requestId)
                throw Conflict("grant_consumed", "The installation grant was already consumed by another request.");
            if (state.Tasks.Any(task => task.Task.Id != job.Task.Id && task.GrantRequestId == requestId))
                throw Conflict("request_reused", "The request UUID has already been used for a different installation.");
            if (job.GrantRequestId is null)
            {
                job = job with { GrantRequestId = requestId, Task = job.Task with
                    { Phase = HardwareTaskPhase.GrantIssued, GrantIssuedAt = now, UpdatedAt = now,
                        ProgressExpiresAt = now + InstallationProgressLifetime } };
                state.Tasks[taskIndex] = job;
                state = Event(state, now, "InstallationGrantIssued", "device-session", stored.Device.Id, job.Task.Id);
            }
            return (state, new HardwareInstallationGrant(job.Task.Id, job.Task.DeviceId, requestId, job.Task.Hostname,
                job.Task.DiskId, job.Task.InventoryRevision, job.Task.AuthorityExpiresAt, "debian-13.7",
                job.Task.RecoveryPublicKey, job.Task.ProgressExpiresAt));
        }, cancellationToken);

    /// <summary>Records device-reported progress, not verified health or enrollment. Managed is never accepted here.</summary>
    public Task<HardwareDevice> ReportStatusAsync(HardwareDeviceSession session, HardwareDevicePhase phase, string? message,
        CancellationToken cancellationToken = default) =>
        MutateAsync((state, now) =>
        {
            HardwareInventoryValidation.Text(message, 512);
            var stored = Authenticate(state, session, now);
            var index = state.Tasks.FindIndex(task => task.Task.Id == stored.Device.TaskId);
            if (index < 0 || stored.Scope != CapabilityScope.Installation)
                throw Conflict("not_approved", "Only a granted installation session can report progress.");
            var job = state.Tasks[index];
            if (job.GrantRequestId is null || (job.Task.ProgressExpiresAt ?? job.Task.AuthorityExpiresAt) <= now
                || job.Task.Phase is HardwareTaskPhase.Failed or HardwareTaskPhase.Invalidated
                || job.InventoryHash != stored.InventoryHash)
                throw Conflict("not_granted", "Installation progress requires a current, matching grant.");
            var current = stored.Device.Phase;
            if (!(phase == HardwareDevicePhase.Failed
                || (phase == HardwareDevicePhase.Installing && current is HardwareDevicePhase.Approved or HardwareDevicePhase.Installing)
                || (phase == HardwareDevicePhase.AwaitingEnrollment && current is HardwareDevicePhase.Installing or HardwareDevicePhase.AwaitingEnrollment)))
                throw Conflict("invalid_transition", "This device-reported transition is not permitted; managed identity requires verified enrollment.");
            var taskPhase = phase switch
            {
                HardwareDevicePhase.Installing => HardwareTaskPhase.Installing,
                HardwareDevicePhase.AwaitingEnrollment => HardwareTaskPhase.AwaitingEnrollment,
                _ => HardwareTaskPhase.Failed
            };
            stored = stored with { Scope = phase == HardwareDevicePhase.Failed ? CapabilityScope.Revoked : stored.Scope,
                Device = stored.Device with { Phase = phase, StatusMessage = message, LastHeartbeatAt = now, LastSeenAt = now, UpdatedAt = now } };
            state.Devices[DeviceIndex(state, stored.Device.Id)] = stored;
            state.Tasks[index] = job with { Task = job.Task with { Phase = taskPhase, UpdatedAt = now, StatusMessage = message } };
            return (Event(state, now, "DeviceReported" + phase, "device-session", stored.Device.Id, job.Task.Id), PublicDevice(stored, now));
        }, cancellationToken);

    internal Task<HardwareInstallationTask> RequireEnrollmentAsync(HardwareDeviceSession session, Guid taskId, CancellationToken ct) =>
        ReadAsync((state, now) =>
        {
            var device = Authenticate(state, session, now);
            var task = state.Tasks.SingleOrDefault(item => item.Task.Id == taskId && item.Task.DeviceId == device.Device.Id);
            if (device.Device.Phase != HardwareDevicePhase.AwaitingEnrollment || task?.Task.Phase != HardwareTaskPhase.AwaitingEnrollment
                || task.GrantRequestId is null || task.Task.ProgressExpiresAt is null || task.Task.ProgressExpiresAt <= now)
                throw Conflict("enrollment_not_ready", "Only this granted installation can request managed enrollment.");
            return task.Task;
        }, ct);

    internal Task<HardwareDevice> CompleteEnrollmentAsync(Guid deviceId, Guid taskId, string fingerprint, CancellationToken ct) =>
        MutateAsync((state, now) =>
        {
            var index = DeviceIndex(state, deviceId);
            var stored = state.Devices[index];
            var taskIndex = state.Tasks.FindIndex(item => item.Task.Id == taskId && item.Task.DeviceId == deviceId);
            if (taskIndex < 0 || stored.SessionKeyFingerprint != Fingerprint(fingerprint) || stored.Device.TaskId != taskId
                || stored.Device.Phase is not (HardwareDevicePhase.AwaitingEnrollment or HardwareDevicePhase.Managed))
                throw Conflict("enrollment_mismatch", "The certificate does not match the approved installation.");
            if (stored.Device.Phase == HardwareDevicePhase.Managed) return (state, PublicDevice(stored, now));
            var task = state.Tasks[taskIndex];
            if (task.GrantRequestId is null || task.Task.ProgressExpiresAt is null || task.Task.ProgressExpiresAt <= now)
                throw Conflict("enrollment_expired", "The managed enrollment window expired.");
            stored = stored with { Scope = CapabilityScope.Revoked, Device = stored.Device with
            {
                Phase = HardwareDevicePhase.Managed, UpdatedAt = now, LastSeenAt = now, LastHeartbeatAt = now,
                StatusMessage = "Managed node identity verified."
            } };
            state.Devices[index] = stored;
            state.Tasks[taskIndex] = task with { Task = task.Task with { Phase = HardwareTaskPhase.Managed, UpdatedAt = now,
                StatusMessage = "Managed node identity verified." } };
            return (Event(state, now, "NodeEnrolled", "node-certificate", deviceId, taskId), PublicDevice(stored, now));
        }, ct);

    internal Task<HardwareDevice> ManagedHeartbeatAsync(Guid deviceId, string fingerprint, CancellationToken ct) =>
        MutateAsync((state, now) =>
        {
            var index = DeviceIndex(state, deviceId);
            var stored = state.Devices[index];
            if (stored.Device.Phase != HardwareDevicePhase.Managed || stored.SessionKeyFingerprint != Fingerprint(fingerprint))
                throw Conflict("not_managed", "A verified managed-node identity is required.");
            stored = stored with { Device = stored.Device with { UpdatedAt = now, LastSeenAt = now, LastHeartbeatAt = now } };
            state.Devices[index] = stored;
            return (state, PublicDevice(stored, now));
        }, ct);

    /// <summary>
    /// Forgets a device and its installation tasks, for retired, failed or stuck machines, cancelling any installation in
    /// progress. Earlier journal events stay, detached from the device. Returns false when it isn't known.
    /// </summary>
    public Task<bool> RemoveDeviceAsync(Guid deviceId, string actor, CancellationToken ct = default) =>
        MutateAsync((state, now) =>
        {
            Actor(actor);
            var index = state.Devices.FindIndex(item => item.Device.Id == deviceId);
            if (index < 0) return (state, false);
            // An install in progress is cancelled too: its session and task go, so the installer's next call is refused.
            state.Devices.RemoveAt(index);
            state.Tasks.RemoveAll(item => item.Task.DeviceId == deviceId);
            for (var i = 0; i < state.Events.Count; i++)
                if (state.Events[i].DeviceId == deviceId) state.Events[i] = state.Events[i] with { DeviceId = null, TaskId = null };
            return (Event(state, now, "DeviceRemoved", actor, null, null), true);
        }, ct);

    private async Task<T> ReadAsync<T>(Func<State, DateTimeOffset, T> read, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return read(RequireState(), _clock.GetUtcNow()); }
        finally { _gate.Release(); }
    }

    private async Task<T> MutateAsync<T>(Func<State, DateTimeOffset, (State State, T Result)> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var copy = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(RequireState(), HardwareOnboardingJson.Options),
                HardwareOnboardingJson.Options)!;
            var (next, result) = mutation(copy, _clock.GetUtcNow());
            await PersistAsync(next, cancellationToken);
            _state = next;
            return result;
        }
        finally { _gate.Release(); }
    }

    private State RequireState()
    {
        if (_disposed || _lease is null || _state is null || _faulted)
            throw Unavailable("onboarding_unavailable", "Onboarding state is unavailable; inspect host logs before restarting.");
        return _state;
    }

    private async Task PersistAsync(State state, CancellationToken cancellationToken)
    {
        ValidateState(state, _clock.GetUtcNow());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, HardwareOnboardingJson.Options);
        if (bytes.Length > MaximumStateBytes) throw Unavailable("registry_full", "Onboarding state exceeds its bounded storage limit.");
        var pending = Path.Combine(_options.StateDirectory, $"state.{Guid.NewGuid():N}.pending");
        try
        {
            RejectLinks(StatePath);
            await using (var stream = new FileStream(pending, PrivateFileOptions(FileMode.CreateNew, FileOptions.Asynchronous | FileOptions.WriteThrough)))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, StatePath, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _faulted = true;
            _logger.LogError(exception, "Onboarding persistence failed. No further authority will be issued by this store.");
            throw Unavailable("persistence_failed", "Onboarding persistence failed; inspect host logs and repair storage.");
        }
        finally
        {
            if (File.Exists(pending))
            {
                try { File.Delete(pending); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { _logger.LogWarning("An incomplete onboarding snapshot could not be removed."); }
            }
        }
    }

    private FileStream AcquireLease()
    {
        var path = Path.Combine(_options.StateDirectory, ".onboarding.lease");
        RejectLinks(path);
        var lease = new FileStream(path, PrivateFileOptions(FileMode.OpenOrCreate, FileOptions.None));
        try
        {
            RejectLinks(path);
            try { using var probe = new FileStream(path, PrivateFileOptions(FileMode.Open, FileOptions.None)); }
            catch (IOException exception) when (OperatingSystem.IsWindows() ? (exception.HResult & 0xffff) == 32
                : exception.HResult == (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 35 : 11))
            { return lease; }
            throw new IOException("Onboarding requires working exclusive file locks. Do not disable .NET file locking.");
        }
        catch { lease.Dispose(); throw; }
    }

    private static FileStreamOptions PrivateFileOptions(FileMode mode, FileOptions options)
    {
        var result = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = options };
        if (!OperatingSystem.IsWindows() && mode != FileMode.Open)
            result.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return result;
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var entry = new FileInfo(current);
            if (entry.LinkTarget is not null || ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("Onboarding storage paths must not contain symbolic links or reparse points.");
        }
    }

    private static HardwareAdmissionWindow Window(State state, DateTimeOffset now) =>
        new(state.WindowExpiresAt > now, state.WindowExpiresAt > now ? state.WindowExpiresAt : null);

    private static HardwareDevice PublicDevice(DeviceRecord stored, DateTimeOffset now) => stored.Device with
    {
        Hardware = HardwareInventoryValidation.CopyAndValidate(stored.Device.Hardware),
        HeartbeatFreshness = stored.Device.LastHeartbeatAt is not { } last ? HeartbeatFreshness.Unknown
            : now >= last && now - last < HeartbeatFreshnessLifetime ? HeartbeatFreshness.Fresh : HeartbeatFreshness.Stale
    };

    private static int DeviceIndex(State state, Guid id)
    {
        var index = state.Devices.FindIndex(device => device.Device.Id == id);
        return index >= 0 ? index : throw new HardwareOnboardingException(404, "device_not_found", "The onboarding device was not found.");
    }

    private static DeviceRecord Authenticate(State state, HardwareDeviceSession session, DateTimeOffset now)
    {
        // All authentication failures are indistinguishable; do not disclose whether a node ID exists.
        var fingerprint = session?.SessionKeyFingerprint;
        var capability = session?.Capability;
        var stored = state.Devices.SingleOrDefault(device => device.Device.Id == session?.DeviceId);
        var progressDeadline = stored?.Scope == CapabilityScope.Installation
            ? state.Tasks.SingleOrDefault(task => task.Task.Id == stored.Device.TaskId && task.GrantRequestId is not null)?.Task.ProgressExpiresAt
            : null;
        var hash = SecretHash(HardwareInventoryValidation.Hash(capability) ? capability! : "");
        var expected = stored?.CapabilityHash ?? new string('0', 64);
        var tokenMatches = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(expected));
        if (stored is null || !HardwareInventoryValidation.Hash(fingerprint) || !HardwareInventoryValidation.Hash(capability)
            || stored.SessionKeyFingerprint != fingerprint!.ToUpperInvariant() || !tokenMatches
            || (progressDeadline ?? stored.Device.DiscoveryExpiresAt) <= now || stored.Scope == CapabilityScope.Revoked)
            throw new HardwareOnboardingException(403, "invalid_device_session", "A current, correctly scoped device session is required.");
        return stored;
    }

    private void RequireDiscovery()
    {
        if (!_options.Readiness().CanDiscover) throw Unavailable("discovery_not_ready", "Discovery prerequisites have not been qualified.");
    }
    private void RequireInstallation()
    {
        if (!_options.Readiness().CanInstall) throw Unavailable("installation_not_ready", "Installation is disabled until boot and enrollment prerequisites are qualified.");
    }
    private static string Fingerprint(string value)
    {
        HardwareInventoryValidation.Require(HardwareInventoryValidation.Hash(value), "Session key fingerprint must be a SHA-256 hex digest.");
        return value.ToUpperInvariant();
    }
    private static string InventoryHash(HardwareReport report) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report, HardwareOnboardingJson.Options)));
    // IP addresses come and go (DHCP, IPv6 router advertisements, the installer's own netcfg); they aren't hardware.
    private static string StableInventory(HardwareReport report) => JsonSerializer.Serialize(report with
        { Interfaces = report.Interfaces.Select(nic => nic with { Addresses = [] }).ToArray() }, HardwareOnboardingJson.Options);
    private static string SecretHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Actor(string actor) => HardwareInventoryValidation.Text(actor, 512, true);
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
    private static HardwareOnboardingException Conflict(string code, string message) => new(409, code, message);
    private static HardwareOnboardingException Unavailable(string code, string message) => new(503, code, message);

    private static State Event(State state, DateTimeOffset at, string kind, string actor, Guid? deviceId, Guid? taskId)
    {
        var sequence = checked(state.LastEventId + 1);
        state.Events.Add(new(sequence, at, kind, actor, deviceId, taskId));
        if (state.Events.Count > MaximumEvents) state.Events.RemoveAt(0);
        return state with { LastEventId = sequence };
    }

    private static void ValidateState(State state, DateTimeOffset now)
    {
        static void Valid([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition)
        {
            if (!condition) throw new InvalidDataException("Onboarding state violates its schema or authority invariants.");
        }
        static bool Timestamp(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value >= DateTimeOffset.UnixEpoch;
        Valid(state.Version is 1 or 2 or 3 && state.Devices is not null && state.Tasks is not null && state.Events is not null
            && state.Devices.Count <= MaximumDevices && state.Tasks.Count <= MaximumTasks && state.Events.Count <= MaximumEvents
            && state.LastEventId >= 0 && (state.WindowExpiresAt is null || Timestamp(state.WindowExpiresAt.Value)));
        Valid(state.Devices.Select(device => device.Device.Id).Distinct().Count() == state.Devices.Count
            && state.Devices.Select(device => device.SessionKeyFingerprint).Distinct().Count() == state.Devices.Count
            && state.Tasks.Select(task => task.Task.Id).Distinct().Count() == state.Tasks.Count);
        var requestIds = state.Tasks.Where(task => task.GrantRequestId is not null).Select(task => task.GrantRequestId).ToArray();
        Valid(requestIds.Distinct().Count() == requestIds.Length);
        foreach (var stored in state.Devices)
        {
            var device = stored.Device;
            Valid(device.Id != Guid.Empty && Enum.IsDefined(device.Phase)
                && Enum.IsDefined(stored.Scope) && device.HeartbeatFreshness == HeartbeatFreshness.Unknown && device.InventoryRevision >= 1
                && HardwareInventoryValidation.Hash(stored.SessionKeyFingerprint) && stored.SessionKeyFingerprint == stored.SessionKeyFingerprint.ToUpperInvariant()
                && HardwareInventoryValidation.Hash(stored.CapabilityHash) && HardwareInventoryValidation.Hash(stored.InventoryHash)
                && Timestamp(device.DiscoveredAt) && Timestamp(device.UpdatedAt) && Timestamp(device.LastSeenAt) && device.UpdatedAt <= now
                && device.UpdatedAt >= device.LastSeenAt && device.LastSeenAt >= device.DiscoveredAt
                && Timestamp(device.DiscoveryExpiresAt) && device.DiscoveryExpiresAt > device.DiscoveredAt
                && device.DiscoveryExpiresAt <= device.DiscoveredAt + DiscoveryLifetime
                && (device.LastHeartbeatAt is null || Timestamp(device.LastHeartbeatAt.Value)
                    && device.LastHeartbeatAt >= device.DiscoveredAt && device.LastHeartbeatAt <= device.LastSeenAt));
            Valid(InventoryHash(HardwareInventoryValidation.CopyAndValidate(device.Hardware)) == stored.InventoryHash);
            var verificationCode = GetVerificationCode(stored.SessionKeyFingerprint);
            var legacyCode = verificationCode + "-" + stored.SessionKeyFingerprint[12..16];
            Valid(state.Version switch
            {
                1 => device.VerificationCode == "" || device.VerificationCode == legacyCode,
                2 => device.VerificationCode == legacyCode,
                3 => device.VerificationCode == verificationCode,
                _ => false
            });
            HardwareInventoryValidation.Text(device.StatusMessage, 512);
            Valid(device.DismissedAt is null || device.Phase == HardwareDevicePhase.Rejected
                && Timestamp(device.DismissedAt.Value) && device.DismissedAt >= device.DiscoveredAt && device.DismissedAt <= device.UpdatedAt);
            Valid(device.Phase switch
            {
                HardwareDevicePhase.Discovered => stored.Scope == CapabilityScope.Discovery && device.TaskId is null,
                HardwareDevicePhase.Rejected => stored.Scope == CapabilityScope.Revoked && device.TaskId is null,
                HardwareDevicePhase.Failed => stored.Scope == CapabilityScope.Revoked && device.TaskId is not null,
                HardwareDevicePhase.Managed => stored.Scope == CapabilityScope.Revoked && device.TaskId is not null,
                _ => stored.Scope == CapabilityScope.Installation && device.TaskId is not null
            });
            if (device.TaskId is { } taskId)
                Valid(state.Tasks.Any(task => task.Task.Id == taskId && task.Task.DeviceId == device.Id));
            var granted = state.Tasks.Where(task => task.Task.DeviceId == device.Id && task.GrantRequestId is not null).ToArray();
            Valid(granted.Length <= 1 && (granted.Length == 0 || device.TaskId == granted[0].Task.Id
                && device.Phase is not (HardwareDevicePhase.Discovered or HardwareDevicePhase.Rejected)));
        }
        foreach (var stored in state.Tasks)
        {
            var job = stored.Task;
            var device = state.Devices.SingleOrDefault(device => device.Device.Id == job.DeviceId);
            Valid(device is not null && job.Id != Guid.Empty && Enum.IsDefined(job.Phase)
                && HardwareInventoryValidation.Hostname(job.Hostname) && HardwareInventoryValidation.StableDiskId(job.DiskId)
                && job.InventoryRevision >= 1 && job.InventoryRevision <= device!.Device.InventoryRevision
                && HardwareInventoryValidation.Hash(stored.InventoryHash) && stored.SessionKeyFingerprint == device.SessionKeyFingerprint
                && Timestamp(job.ApprovedAt) && Timestamp(job.AuthorityExpiresAt) && Timestamp(job.UpdatedAt)
                && job.ApprovedAt >= device.Device.DiscoveredAt && job.AuthorityExpiresAt > job.ApprovedAt
                && job.AuthorityExpiresAt <= job.ApprovedAt + InstallationAuthorityLifetime
                && job.AuthorityExpiresAt <= device.Device.DiscoveryExpiresAt && job.UpdatedAt >= job.ApprovedAt && job.UpdatedAt <= now
                && (stored.GrantRequestId is null) == (job.GrantIssuedAt is null)
                && (stored.GrantRequestId is null || stored.GrantRequestId != Guid.Empty && Timestamp(job.GrantIssuedAt!.Value)
                    && job.GrantIssuedAt >= job.ApprovedAt && job.GrantIssuedAt < job.AuthorityExpiresAt && job.UpdatedAt >= job.GrantIssuedAt));
            Actor(job.ApprovedBy);
            if (job.RecoveryPublicKey is not null) Valid(RecoverySshKeys.Parse(job.RecoveryPublicKey).PublicKey == job.RecoveryPublicKey);
            Valid(job.ProgressExpiresAt is null || job.GrantIssuedAt is not null && Timestamp(job.ProgressExpiresAt.Value)
                && job.ProgressExpiresAt == job.GrantIssuedAt + InstallationProgressLifetime);
            HardwareInventoryValidation.Text(job.StatusMessage, 512);
            if (job.Phase is HardwareTaskPhase.Failed or HardwareTaskPhase.Invalidated)
            {
                Valid(stored.GrantRequestId is null
                    ? job.Phase == HardwareTaskPhase.Invalidated && device!.Device.TaskId != job.Id
                    : device!.Device.TaskId == job.Id && device.Device.Phase == HardwareDevicePhase.Failed);
                continue;
            }
            Valid(device!.Device.TaskId == job.Id && device.InventoryHash == stored.InventoryHash
                && device.Device.InventoryRevision == job.InventoryRevision
                && device.Device.Hardware.Architecture == "x86_64" && device.Device.Hardware.BootMode == "uefi"
                && device.Device.Hardware.Disks.Any(disk => disk.Id == job.DiskId && !disk.IsReadOnly && !disk.IsRemovable)
                && (job.Phase switch
                {
                    HardwareTaskPhase.Approved => stored.GrantRequestId is null && device.Device.Phase == HardwareDevicePhase.Approved,
                    HardwareTaskPhase.GrantIssued => stored.GrantRequestId is not null && device.Device.Phase == HardwareDevicePhase.Approved,
                    HardwareTaskPhase.Installing => stored.GrantRequestId is not null && device.Device.Phase == HardwareDevicePhase.Installing,
                    HardwareTaskPhase.AwaitingEnrollment => stored.GrantRequestId is not null && device.Device.Phase == HardwareDevicePhase.AwaitingEnrollment,
                    HardwareTaskPhase.Managed => stored.GrantRequestId is not null && device.Device.Phase == HardwareDevicePhase.Managed,
                    _ => false
                }));
        }
        var activeNames = state.Tasks.Where(task => task.Task.Phase is not (HardwareTaskPhase.Failed or HardwareTaskPhase.Invalidated))
            .Select(task => task.Task.Hostname).ToArray();
        Valid(state.Devices.Select(device => GetVerificationCode(device.SessionKeyFingerprint)).Distinct(StringComparer.Ordinal).Count()
            == state.Devices.Count);
        Valid(activeNames.Distinct(StringComparer.Ordinal).Count() == activeNames.Length);
        Valid(state.Events.Count > 0 && state.Events[^1].Sequence == state.LastEventId
            && state.Events.Count == Math.Min(state.LastEventId, MaximumEvents));
        for (var index = 0; index < state.Events.Count; index++)
        {
            var item = state.Events[index];
            Valid(item.Sequence > 0 && Timestamp(item.At) && item.At <= now && (index == 0 || item.Sequence == state.Events[index - 1].Sequence + 1)
                && (item.DeviceId is null || state.Devices.Any(device => device.Device.Id == item.DeviceId))
                && (item.TaskId is null || state.Tasks.Any(task => task.Task.Id == item.TaskId && task.Task.DeviceId == item.DeviceId)));
            Actor(item.Actor);
            HardwareInventoryValidation.Text(item.Kind, 64, true);
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; _lease?.Dispose(); _lease = null; }
        finally { _gate.Release(); }
    }

    private enum CapabilityScope { Discovery, Installation, Revoked }
    private sealed record DeviceRecord(HardwareDevice Device, string SessionKeyFingerprint, string InventoryHash, string CapabilityHash, CapabilityScope Scope);
    private sealed record TaskRecord(HardwareInstallationTask Task, string SessionKeyFingerprint, string InventoryHash, Guid? GrantRequestId);
    private sealed record EventRecord(long Sequence, DateTimeOffset At, string Kind, string Actor, Guid? DeviceId, Guid? TaskId);
    private sealed record State(int Version, DateTimeOffset? WindowExpiresAt, long LastEventId, List<DeviceRecord> Devices,
        List<TaskRecord> Tasks, List<EventRecord> Events);
}
