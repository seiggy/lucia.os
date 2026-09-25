# Durable hardware onboarding: first control-plane slice

This project links only `src\Lucia.Homelab.Server\Onboarding\*.cs` and the
ASP.NET Core shared framework. No server project reference, GPU packages, new
NuGet packages, boot tooling, shell execution, disk operations, LDAP calls, or
network discovery are involved. Its Kestrel checks use synthetic authentication
and loopback only, including real authorization, cookies, and antiforgery.

Run from the repository root, entirely offline:

```powershell
dotnet restore .\tests\Lucia.Homelab.OnboardingChecks\Lucia.Homelab.OnboardingChecks.csproj --source .\tests\Lucia.Homelab.OnboardingChecks -p:NuGetAudit=false
dotnet run --project .\tests\Lucia.Homelab.OnboardingChecks\Lucia.Homelab.OnboardingChecks.csproj --no-restore
```

All synthetic state and data-protection keys are created beneath this project's
build output and removed on completion.

Verified: **316 checks passed in Release**, with warnings treated as errors.
The full GPU-backed server was not built or started.

### Files added

Under `src\Lucia.Homelab.Server\Onboarding\`:

- `HardwareOnboardingContracts.cs`
- `HardwareOnboardingOptions.cs`
- `HardwareInventoryValidation.cs`
- `HardwareOnboardingStore.cs`
- `HardwareOnboardingExtensions.cs`

Under `tests\Lucia.Homelab.OnboardingChecks\`:

- `Lucia.Homelab.OnboardingChecks.csproj`
- `Program.cs`
- `README.md` (this contract and integration report)

No existing application file, project manifest, frontend, boot tooling, or
identity AppHost was changed. No commits, branch operations, or live/remote
commands were performed.

## Required integration, intentionally not applied

1. Import `Lucia.Homelab.Server.Onboarding`; call
   `builder.AddHardwareOnboarding()` before `Build()` and
   `app.MapHardwareOnboarding()` alongside the host routes.
2. Retain existing `AddHostAuthentication`, authentication/authorization
   middleware, the `HostOwner` policy, and **global `UseHostCsrf`**. No onboarding
   route opts out of CSRF. An Owner must have `sub` or `ClaimTypes.NameIdentifier`.
   Audit actor is `issuer:subject`, falling back to authentication-scheme name
   when an issuer claim is absent.
3. The development default is the current user's local application data:
   `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "onboarding")`
   (`%LOCALAPPDATA%\Lucia\onboarding` on Windows). Managed containers must
   explicitly configure `HardwareOnboarding:StateDirectory` as `/data/onboarding`
   on their persistent private volume. Absolute-path validation remains
   mandatory; if the platform cannot resolve local application data, configure
   an explicit absolute path instead of falling back to a drive root.
   No prior root-level data is automatically adopted, moved, or removed.
   Use an isolated local filesystem supporting exclusive locks and atomic
   same-directory rename, with restrictive service account ACLs. Existing
   directory permissions are not silently changed.
4. Configure and qualify the separate **trusted discovery adapter**. Before
   calling any session method it must verify TLS/private-CA requirements,
   session-key proof of possession, scope/route binding, network eligibility,
   and the authoritative managed-node registry. The fingerprint is uppercase or
   lowercase 64-hex SHA-256 of the session public key's DER SubjectPublicKeyInfo, **not** a MAC,
   device-supplied identity claim, or arbitrary HTTP header. The adapter supplies
   the required `isKnownManagedDevice` argument; it must also refuse a node that
   becomes known managed before releasing an installation grant. Onboarding
   records are not managed identities.
5. Deliver the first-admission capability once as receipt `token` over that
   authenticated transport, never in browser responses, URLs, logs, or
   telemetry. Display **only `verificationCode`**, not the token, on the node's
   physical console so the Owner can compare it with the browser device card.
   It is the first 12 uppercase hex characters of the full fingerprint grouped
   `XXXX-XXXX-XXXX` (48 comparison bits, 14 display characters); for example,
   a fingerprint beginning `0123456789abcdef` displays `0123-4567-89AB`. This is
   not a password, authentication proof, certificate, or installation grant.
   Different keys with the same comparison code are refused with 409
   `verification_code_collision`; generate a new key instead of presenting
   ambiguous codes. Bound adapter request bodies before deserialization.
   There is deliberately **no HTTP discovery, session, grant, or enrollment
   endpoint** in this slice. Signed challenge/replay handling belongs to the
   parent's separate `Server\Boot` implementation, which this slice does not edit.
6. Leave installation disabled until the coordinator has qualified actual
   Debian **13.7 x86-64 UEFI** boot artifacts, installer verification, disk
   identity checks, private CA trust, LDAP/directory-backed identity enrollment,
   and the transport/installer's durable single-use request handling. Configuration
   flags are administrator assertions, not successful live probes. This slice
   does not claim these prerequisites are available.
7. A future installer adapter must revalidate the approved stable disk and
   inventory immediately before destructive work, honor grant expiry, and
   durably deduplicate the exact request UUID. Returning the same grant is
   transport retry support, not permission to erase again. Do not interpret a
   reported `AwaitingEnrollment` phase as a verified managed/healthy node.

The confirmed provisioning CIDR `192.168.0.0/23` is accepted by the options
validator. The core does not hardcode that network or Spark's reported
`192.168.0.222` address and has made no live connection to either. Transport
allowlists, advertised origins, signed challenges, and the optional short-lived
boot admission file remain parent-owned configuration/implementation.

### Configuration section: `HardwareOnboarding`

| Property | Type/default | Meaning |
|---|---|---|
| `StateDirectory` | string, user-local application-data path above | Persistent single-host state; explicitly `/data/onboarding` in managed containers |
| `DiscoveryNetworkCidr` | nullable string, null | Explicit RFC1918 IPv4 subnet, `/8` or narrower and wholly inside its private range |
| `BootBaseUrl` | nullable string, null | HTTPS origin, no credentials/path/query/fragment |
| `DiscoveryAdapterQualified` | bool, false | Coordinator qualified read-only, authenticated discovery |
| `BootArtifactsQualified` | bool, false | Coordinator qualified the actual intended boot/installer chain |
| `EnrollmentQualified` | bool, false | Coordinator qualified private CA and directory-backed enrollment |
| `InstallationEnabled` | bool, false | Explicit final installation enable |

Missing prerequisites produce explicit readiness reasons. Malformed configured
paths/CIDRs/origins fail startup. `canDiscover` requires the two network/boot
settings and qualified discovery adapter. `canInstall` additionally requires
all three installation flags. Both approval and every grant retrieval enforce
readiness in the store, not just the UI. Options are snapshotted at startup;
changes require restart. No window auto-opens from configuration.

## Exact browser API

All routes require `HostOwner`. JSON is camelCase; enum values are the
PascalCase strings listed below. Successful operations return **200** and
`Cache-Control: no-store`. Use 30 minutes as the UI suggestion, not a default
that silently opens admission.

| Method/path | Request body | Response |
|---|---|---|
| `GET /api/host/onboarding` | none | `HardwareOnboardingSnapshot` |
| `POST /api/host/onboarding/window` | `{ "minutes": 30 }`, integer 1..60 | `HardwareAdmissionWindow` |
| `DELETE /api/host/onboarding/window` | none | `HardwareAdmissionWindow` |
| `POST /api/host/devices/{id}/approve-install` | `{ "hostname": "node-1", "diskId": "/dev/disk/by-id/…", "confirmation": "ERASE" }` | `HardwareInstallationTask` |
| `POST /api/host/devices/{id}/reject` | none | `HardwareDevice` |

IDs in paths are nonempty `D`-format UUIDs. JSON bodies are limited to 4096
bytes, require all constructor fields, and reject duplicate/unknown fields,
wrong types, and null required values. Hostnames are lowercase DNS labels,
1..63 characters; active tasks reserve their hostname. Confirmation is
case-sensitive literal `ERASE`. Disk selection must exactly match a reported
nonnull stable ID for a nonremovable, writable disk, never its transient `/dev`
path. Disks whose stable ID is unknown remain in inventory with `id: null`;
display them as ineligible, not absent. A missing MAC also remains null.
Only pending, unexpired discovery may be approved or rejected. Admission
closing/expiry does not cancel previously admitted discoveries or jobs.

Endpoint failures are logged without request bodies and use:

```json
{
  "error": {
    "message": "Safe explanatory text.",
    "type": "invalid_request_error",
    "code": "stable_machine_code"
  }
}
```

Statuses are 400 (invalid body/input, including nonliteral confirmation),
404 (missing device), 409 (phase/approval binding/eligibility/expiry/conflict), and 503 (readiness or
storage unavailable); `type` is `server_error` for 503. Authentication/Owner
denials remain the existing host 401/403 contract. A missing stable owner
subject yields 403 `actor_required`. Store session failures yield 403
`invalid_device_session`, identically for unknown ID, wrong key, wrong
capability, revoked scope, or expired capability.

### Public DTOs

Notation: `uuid` is a GUID string, `timestamp` is a UTC ISO-8601
`DateTimeOffset`, `?` permits null, and arrays are JSON arrays. Public browser
responses contain no capability, capability hash, full session fingerprint, grant
request UUID, CA private key, LDAP credential, or other enrollment secret.

```text
HardwareAdmissionWindow {
  isOpen: bool, expiresAt: timestamp?
}
HardwareOnboardingReadiness {
  canDiscover: bool, canInstall: bool, reasons: string[]
}
HardwareOnboardingSnapshot {
  window: HardwareAdmissionWindow, readiness: HardwareOnboardingReadiness,
  devices: HardwareDevice[], tasks: HardwareInstallationTask[]
}
HardwareDevice {
  id: uuid, phase: HardwareDevicePhase, hardware: HardwareReport,
  inventoryRevision: int64, discoveredAt: timestamp, updatedAt: timestamp,
  lastSeenAt: timestamp, lastHeartbeatAt: timestamp?,
  heartbeatFreshness: "Unknown" | "Fresh" | "Stale",
  discoveryExpiresAt: timestamp, taskId: uuid?, statusMessage: string?,
  verificationCode: string
}
HardwareInstallationTask {
  id: uuid, deviceId: uuid, phase: HardwareTaskPhase,
  hostname: string, diskId: string, inventoryRevision: int64,
  approvedBy: string, approvedAt: timestamp, authorityExpiresAt: timestamp,
  grantIssuedAt: timestamp?, updatedAt: timestamp, statusMessage: string?
}
HardwareReport {
  architecture: string, bootMode: string, secureBoot: bool?,
  manufacturer: string?, model: string?, serialNumber: string?,
  hardwareUuid: string?, cpuModel: string?, logicalCpuCount: int,
  memoryBytes: int64, interfaces: HardwareInterface[], disks: HardwareDisk[]
}
HardwareInterface { name: string, macAddress: string?, addresses: string[] }
HardwareDisk {
  id: string?, path: string, model: string?, serial: string?, sizeBytes: int64,
  isRemovable: bool, isReadOnly: bool
}
OpenHardwareWindowRequest { minutes: int }
ApproveHardwareInstallRequest { hostname: string, diskId: string, confirmation: string }
```

`HardwareDevicePhase`: `Discovered`, `Approved`, `Installing`,
`AwaitingEnrollment`, `Managed`, `Failed`, `Rejected`.
`Managed` is reserved: this slice never accepts, restores, or produces a
managed identity.

`HardwareTaskPhase`: `Approved`, `GrantIssued`, `Installing`,
`AwaitingEnrollment`, `Failed`, `Invalidated`.

Adapter-only DTOs (not browser routes):

```text
DiscoveryReceipt {
  deviceId: uuid, token: string?, expiresAt: timestamp, verificationCode: string
}
HardwareDeviceSession {
  deviceId: uuid, sessionKeyFingerprint: string, capability: string
}
HardwareSessionConfiguration {
  deviceId: uuid, inventoryRevision: int64, taskId: uuid?,
  authorityExpiresAt: timestamp?, canRequestInstallationGrant: bool
}
HardwareInstallationGrant {
  taskId: uuid, deviceId: uuid, requestId: uuid, hostname: string,
  diskId: string, inventoryRevision: int64, expiresAt: timestamp,
  operatingSystem: "debian-13.7"
}
```

## Public service/method contract

All store methods are asynchronous. Except `StartAsync`/`StopAsync`, they
accept an optional final `CancellationToken cancellationToken = default`.
Constructor:
`HardwareOnboardingStore(IOptions<HardwareOnboardingOptions>, TimeProvider, ILogger<HardwareOnboardingStore>)`.
`TimeProvider.System` is registered only if the host has not supplied one.

```text
AddHardwareOnboarding(WebApplicationBuilder builder) -> void [extension]
MapHardwareOnboarding(WebApplication app) -> void [extension]
StartAsync(CancellationToken) -> Task
StopAsync(CancellationToken) -> Task
Dispose() -> void
GetSnapshotAsync(...) -> Task<HardwareOnboardingSnapshot>
AssertCanDiscoverAsync(...) -> Task<HardwareAdmissionWindow>
FindDiscoveryAsync(string publicKeyFingerprint, ...) -> Task<HardwareDevice?>
VerifyDiscoveryCapabilityAsync(Guid deviceId, string publicKeyFingerprint,
                               string token, ...) -> Task<HardwareDevice>
GetVerificationCode(string publicKeyFingerprint) -> string [static]
OpenWindowAsync(int minutes, string actor, ...) -> Task<HardwareAdmissionWindow>
CloseWindowAsync(string actor, ...) -> Task<HardwareAdmissionWindow>
ApproveInstallAsync(Guid deviceId, ApproveHardwareInstallRequest request,
                    string actor, ...) -> Task<HardwareInstallationTask>
RejectDiscoveryAsync(Guid deviceId, string actor, ...) -> Task<HardwareDevice>
RegisterDiscoveryAsync(string publicKeyFingerprint, HardwareReport inventory,
                       bool isKnownManagedDevice, ...) -> Task<DiscoveryReceipt>
GetSessionStatusAsync(HardwareDeviceSession session, ...) -> Task<HardwareDevice>
GetSessionConfigurationAsync(HardwareDeviceSession session, ...)
    -> Task<HardwareSessionConfiguration>
HeartbeatAsync(HardwareDeviceSession session, ...) -> Task<HardwareDevice>
RequestInstallationGrantAsync(HardwareDeviceSession session, Guid requestId,
                             string diskId, long inventoryRevision, ...)
    -> Task<HardwareInstallationGrant>
ReportStatusAsync(HardwareDeviceSession session, HardwareDevicePhase phase,
                  string? message, ...) -> Task<HardwareDevice>
```

`HardwareOnboardingException` exposes `StatusCode`, `Code`, and safe `Message`.
Direct store callers are trusted server-side adapters/services; browser actor
and Owner/CSRF checks belong to the mapped endpoints, not caller-supplied JSON.

`AssertCanDiscoverAsync` is a read-only preflight before issuing a new-admission
challenge: 503 for missing discovery prerequisites, 409 for closed/expired
admission, otherwise the current window/deadline. It grants no authority and
does not reserve admission. `RegisterDiscoveryAsync` rechecks admission under
the mutation lock, so a challenge cannot outlive a closed window.
`FindDiscoveryAsync` performs an in-process fingerprint lookup and returns a
safe device projection or null, including expired/rejected records; a lookup
is **not authentication**. `VerifyDiscoveryCapabilityAsync` checks token hash,
scope, expiry, device ID, and fingerprint and returns the safe device
projection. It does not renew the session or refresh heartbeat. The parent's
adapter must still verify its signed protocol independently. The complete
fingerprint never appears in browser device DTOs.

Register `AddHardwareOnboarding()` before parent-owned boot hosted services so
the store completes startup before their reads. The boot-window heartbeat
needs only this existing API:

```csharp
var snapshot = await store.GetSnapshotAsync(cancellationToken);
DateTimeOffset? admissionUntil = snapshot.Readiness.CanDiscover && snapshot.Window.IsOpen
    ? snapshot.Window.ExpiresAt
    : null;
```

The parent owns short-lived serving leases/files and must not issue a lease
past `admissionUntil`. `HeartbeatAsync(HardwareDeviceSession, ...)` is a
different operation: it records authenticated **device** liveness, not the
boot-serving lease. No file protocol or network service is added here.

The registration receipt is exactly four fields: `deviceId`, `token`,
`expiresAt`, `verificationCode`. It no longer includes `inventoryRevision`;
read that from the verified device or session configuration. `HardwareDevice`
adds `verificationCode`; all browser requests, including approval, otherwise
keep their existing contract. Comparison is an Owner UI/physical-console
workflow, not a second bearer credential. `HardwareDeviceSession.Capability`
is the in-process name for the receipt's `token`.

Registration deduplicates **only by session-key fingerprint**, not MAC.
Canonical inventory ordering prevents ordering-only revisions. A duplicate
returns `token: null`; it cannot rotate/recover the capability or extend
expiry. Changed inventory increments the revision and invalidates a pending
approval. If authority was already granted, changed inventory instead fails
the device and revokes its session; no reapproval/reinstall is possible.
Known managed registrations are refused without creating inventory.
Null disk IDs and MAC addresses are retained as unknown values. Nonnull disk
IDs must still be unique and valid; all disk paths must be unique. Disks with
null IDs sort by their actual path for deterministic hashing, not for identity
or approval. Losing a previously approved stable ID invalidates that approval.

Approval stores the inventory SHA-256/revision and session fingerprint in
private durable task metadata. Grant requests require the same device,
capability, installation scope, exact disk and revision, unexpired approval,
and current installation readiness. A persisted request UUID consumes that
task once. Exact retries return the identical grant while still authorized;
other UUIDs or altered disk/revision fail, including after restart.

Grant issuance leaves the device `Approved` until an authenticated device
reports `Installing`. Device reports may move `Installing` to
`AwaitingEnrollment`, repeat the current progress phase, or report `Failed`.
No report can claim `Managed` or go backward. Reports are observations, not
verified installer/enrollment success. Status/config reads do not refresh
last-seen or heartbeat. Heartbeat alone never advances a phase.

## Persistence and actual limits

- One lifetime exclusive `.onboarding.lease`; `StopAsync` retains it until
  disposal. Its flushed initialization marker prevents a deleted manifest
  being silently recreated as an empty registry. Do not delete/replace a live
  lease file. Unsupported file locking fails startup.
- Versioned `state.json` contains devices, tasks, and bounded sequenced events.
  Schema 3 uses the requested 12-hex fingerprint comparison code. Schema-1
  state without a code and schema-2 state with the earlier 16-hex code are
  validated before upgrading. The new display code is derived from the same
  stored key; jobs, consumed request IDs, capability hashes, and deadlines are
  preserved without renewing authority. Corrupt legacy codes/data or a
  collision in the shortened codes fail startup without overwriting state.
  Serialization is asynchronous behind one semaphore, with copy-on-write,
  private same-directory pending files, flushed contents, and atomic rename.
  Successful mutation responses follow persistence. I/O write failures
  fault the running store; it issues no further authority until repaired and
  restarted. Cancellation before rename does not commit.
- Load validates required/unknown/duplicate fields, schema, counts, identifiers,
  timestamps, hashes, relationships, transitions, scope, and bounded authority.
  Corruption, future event timestamps/clock rollback, missing initialized
  manifests, and unsupported managed records fail startup, preserving data.
  Startup always persists a closed admission window while retaining jobs and
  their original deadlines. It never silently resets corrupt state.
- Local service-account storage is the trust boundary: this is not encrypted
  storage, an HMAC-protected database, a distributed lock, or an HA design.
  .NET's portable file APIs do not provide a directory-fsync guarantee across
  abrupt power loss; underlying volume/rename durability remains a deployment
  qualification. Orphan pending files never supersede an existing manifest.
- At most **128 devices**, **256 historical tasks**, **2048 most recent audit
  events**, and **33554432 bytes (32 MiB)** state. The state ceiling was raised
  with the expanded per-device report ceiling. Devices/tasks are not silently pruned; full
  registries reject new entries with 503. Audit events use a bounded ring;
  there is no audit export endpoint, retention daemon, or browser events DTO.
- Capabilities are random 256-bit hex secrets, hashed at rest, matched using
  `CryptographicOperations.FixedTimeEquals`, and valid **30 minutes from initial
  admission**. Installation authority is at most **15 minutes after approval**,
  further bounded by original discovery expiry. These are fixed ceilings, not
  sliding expirations. Heartbeats do not renew either.
- Heartbeat freshness is `Unknown` without data, `Fresh` for less than two
  minutes, otherwise `Stale`. It is never a `Healthy` assertion.
- Inventory: max **131072 bytes (128 KiB)** of normalized UTF-8 JSON
  (`HardwareOnboardingStore.MaximumHardwareReportBytes`),
  **1..128 interfaces**, **0..32 addresses/interface**, **0..128 disks**.
  `MaximumInterfaces`, `MaximumAddressesPerInterface`, `MaximumDisks`, and
  `MaximumStateBytes` are also public store constants. All count/text limits
  and the aggregate byte limit apply together; nothing is truncated to fit.
  CPU count **1..4096**, memory **1..1125899906842624 bytes**, and disk size
  **1..1152921504606846976 bytes**. Zero CPU count, zero memory, and zero-size
  disks are rejected rather than replaced with invented data; an empty disk
  array is valid discovery with no eligible installation disk.
  Architecture/boot strings max 32, interface names 64, optional
  descriptive strings 256, UUID canonical 36 characters. Text rejects control
  and surrogate characters; IP and nonnull MAC formats are checked.
- Stable IDs may be null (display-only/ineligible) or match `/dev/disk/by-id/`
  plus a 1..200-character safe component;
  partition suffixes `-partN`, traversal, and shell metacharacters are rejected.
  Paths are single-component `/dev/` names, 1..64 characters, without traversal
  or shell syntax. Whole-disk/read-only/removable truth still requires the
  trusted adapter's actual block-device inspection.
- Installation eligibility requires `architecture: "x86_64"` and
  `bootMode: "uefi"`. The development target has Secure Boot OFF, but
  `secureBoot` is reported inventory/posture, not an independent installation
  gate: false, true, and unknown are preserved without blocking approval or
  grant restoration. Installation still requires configured `canInstall`,
  explicit Owner approval of the exact disk, and the matching bounded session
  authority. Posture changes remain inventory revisions like other hardware
  changes. ARM, legacy BIOS, disks without stable IDs, removable media, and read-only disks remain
  unsupported.
- Node report JSON may omit `secureBoot`; it becomes null/unknown and is not
  an install blocker. Discovery records `x86_64`/`aarch64` and `uefi`/`bios`
  as reported; only x86_64 UEFI has an installation path. The core does not
  fabricate architecture, posture, storage, or enrollment evidence.
- The parent-requested expanded bounds are implemented here. At integration,
  align the separately owned node inspector/report and response limits too:
  its files were still advertising the earlier 32 KiB/16-NIC/32-disk/eight-address
  limits when this change was made. A full boot status response includes the
  hardware report plus metadata, so a 64 KiB client response ceiling cannot
  cover the new maximum; either allow a bounded larger response or coordinate
  a smaller parent-owned boot status projection.
- No capability renewal/recovery, managed enrollment transition, reinstall,
  remote shell, package management, boot asset distribution, DHCP/PXE
  configuration, or executor is implemented. Enrollment must introduce a
  separately verified long-lived identity rather than extending a discovery
  bearer capability.
