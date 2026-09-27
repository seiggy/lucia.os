# Native discovery, approved installation handoff, and managed node agent

This standalone .NET 10 console executable inventories Linux hardware and proves
possession of a local discovery key. `inspect`, `discover`, and `status` remain
read-only with respect to disks and machine configuration. The explicit
installation commands prepare a guarded Debian installer handoff; they never
open disks for writes, partition, run apt, reboot, execute server-supplied
commands, change Secure Boot, or flash firmware. `stage-managed` writes only to
a verified installed `/target`; `managed-run` configures directory access on
the installed boot root and sends authenticated heartbeats.
The first deployment target is x86-64 Debian 13.7 UEFI with **Secure Boot already
off**. Observing Secure Boot on or unknown does not authorize changing it.

## Build and run

From the repository root:

```powershell
dotnet publish src\Lucia.NodeAgent\Lucia.NodeAgent.csproj -c Release -r linux-x64 --self-contained true
dotnet publish tests\Lucia.NodeAgent.Checks\Lucia.NodeAgent.Checks.csproj -c Release -r linux-x64 --self-contained true
```

Copy the **entire** agent `bin\Release\net10.0\linux-x64\publish` directory to the
Linux environment; the apphost alone is not sufficient. This is a self-contained
native Linux executable hosting CoreCLR, **not NativeAOT**, and not a static ELF.
There are no package references, ASP.NET SDK/runtime, GPU or TensorSharp dependencies.
Invariant globalization removes the ICU requirement.

On Linux:

```sh
./lucia-node-agent inspect
./lucia-node-agent discover --server https://lucia.example \
  --ca-file /etc/lucia/public-ca.crt --connect-address 192.0.2.10 \
  --state-directory /run/lucia
./lucia-node-agent status --server https://lucia.example \
  --ca-file /etc/lucia/public-ca.crt --state-directory /run/lucia
```

`inspect` emits exactly one camelCase JSON hardware report on stdout and exits.
No fixture-root override is exposed through the CLI. `discover` serializes that
report once, obtains a challenge, signs those exact UTF-8 bytes and registers it.
It stores the resulting capability before printing a safe JSON summary
`{deviceId,expiresAt,verificationCode}`. Human progress and the physical matching
code go to stderr. No token, private key, LDAP password, raw HTTP failure body or
exception text is printed. SIGINT/SIGTERM cancel requests. Discovery, status,
and the installation guard have a two-minute deadline; waiting has a 30-minute
ceiling, staging ten minutes, and the managed daemon runs until terminated.
Each HTTP attempt, including reading the body, has a 15-second deadline.
Only safe GETs retry (at most three attempts); registration POST never retries.

`status` makes one authenticated read, not an installation decision. Until the
server lifecycle contract is finalized it emits only
`{deviceId,statusRead:true}` and discards the body. Neither this command nor the
old discovery-only image automatically enters installation.

## Installer CLI contract

All three installation commands accept the same `--server`, `--ca-file`,
optional `--connect-address`, and `--state-directory` arguments as discovery.
They require root on Linux x86-64, UEFI, a previously registered discovery
identity, and the exact selected, stable, non-removable disk of at least 8 GiB.
No fixture roots, target overrides, shell fragments, or arbitrary package lists
are exposed as CLI options.

1. `discover` saves `inventory.json` beside the existing `identity.pem` and
   `discovery.json`. Registration remains read-only and never grants authority.
2. `wait-install` polls authenticated
   `GET /api/boot/devices/{deviceId}/installation` every five seconds until
   approved or the discovery authority expires. It shows the physical code,
   checks the current inventory against discovery, and writes
   `install-plan.json` plus the private **fixed** `/run/lucia/install.cfg`.
   No credentials, recovery key, or arbitrary shell appear in preseed.
   Parent d-i integration must load this file with `debconf-set-selections`
   **only after successful exit**. Writing preseed does not mean installation
   has started or completed.
3. The parent `/usr/lib/lucia/installation-guard` invokes
   `installation-guard` immediately before partman. The agent rechecks the
   full inventory, by-id resolution, device numbers, every child mount/swap/
   holder, boot ID and expiry. It durably saves a single `grant-attempt.json`
   request ID **before** fetching the challenge and posting the grant proof.
   An uncertain POST is never automatically repeated, even after restarting
   the command. Preserve the state and have the owner inspect it.
   A returned grant must exactly match every approved field. The agent must
   receive synchronous `Installing` progress acknowledgment and persist
   `installation-started.json` before returning success. Repeated guard calls
   reuse the saved grant, never request fresh authority, and repeat the same
   unused-disk and expiry checks. Any nonzero result must stop partman.
   After the agent succeeds, the parent guard resolves the approved by-id
   path (remembered in `/run/lucia/approved-disk-id` across its early_command
   and init.d runs) to its kernel device and sets `partman-auto/disk` and
   `grub-installer/bootdev` to it: partman's `mapdevfs` passes NVMe and
   virtio by-id links through unchanged, so a by-id value matches no disk.
4. Parent `/usr/lib/lucia/finish-install` invokes `stage-managed` after d-i
   installation; if it fails, the agent's reason (including which fixed setup
   step exited) is also written to the installer console, since d-i itself only
   reports "exit code 1". `/target` must be an ext4 root partition on the selected
   physical disk, not an arbitrary bind mount or directory. Unexpected
   nested target mounts are refused (d-i dev/proc/sys/run and the same disk's
   EFI mount are allowed). The entire **flat** self-contained runtime is copied
   from the running agent directory; root-owned regular files and bounded
   aggregate size are required. Library basenames may contain `+`. A runtime
   alias such as `libstdc++.so.6` is accepted only when its link target is one
   safe component naming a regular file in that same flat directory; its
   bytes are copied to a regular private target file. Absolute, escaping,
   directory-component and chained aliases are refused. Limits remain 512
   files and 512 MiB total, counting each materialized alias. All other secure
   file paths retain their existing no-follow rules.

Preseed contains fixed English/US/UTC/DHCP settings, the approved hostname,
Debian `trixie` at HTTPS `deb.debian.org/debian`, main and non-free firmware,
security/updates, single-disk GPT/EFI/ext4 (no LVM or RAID removal directives),
locked root/no password user, SSH/sudo/SSSD and Realtek firmware packages,
and the two fixed parent hooks. Parent scripts remain responsible for
unconditionally propagating failure and for not entering partman through an
unguarded alternative path. The agent itself does not perform installation.

Progress uses the server grant's two-hour `progressExpiresAt` rather than the
original discovery deadline (responses beyond approval expiry plus two hours
are refused). It grants **no additional erase
authority**. Best-effort static `Failed` progress is reported after a known
grant if guard/staging fails. A stage failure may leave partially staged state;
do not automatically rerun or declare the target ready.

## Installed runtime and first boot

Staging writes:

* `/usr/lib/lucia/agent/`: complete private root-owned runtime.
* `/etc/lucia/public-ca.crt` and `managed.json`: the original trusted HTTPS CA
  and origin; the discovery IP override is deliberately not persisted.
* `/var/lib/lucia-agent/private/`: unchanged discovery key/capability, original
  inventory, approved plan, grant, request ID and acknowledgment, mode 0700/0600.
* `/etc/systemd/system/lucia-node-agent.service`: root, `UMask=0077`,
  network-online ordering, restart-on-failure; command is the fixed
  `/usr/lib/lucia/agent/lucia-node-agent managed-run`.
* Fixed local `lucia-recovery` account: locked password, approved SSH public
  key only, private home and authorized_keys, local group and sudo membership,
  explicit `NOPASSWD: ALL` for the requested key-based root recovery capability.
  Supported keys are bounded Ed25519 (32-byte key), P-256 ECDSA (`nistp256`,
  valid 65-byte uncompressed point) and RSA (positive canonical SSH exponent/
  modulus, at least 2048-bit modulus) public keys. Options, multiline values
  and private keys are rejected. Optional comments are discarded; the pinned
  value is exactly `algorithm base64` with one space and canonical base64.
  GitHub import/review happens exclusively at the owner controller; the node
  never fetches GitHub keys or continuously synchronizes recovery access.
* SSH disallows root login, allows only `lucia-owners` and `lucia-recovery`,
  enables PAM for directory authentication, and requires public-key-only
  authentication for recovery. `AuthorizedKeysFile` also reads
  `/etc/ssh/lucia-authorized-keys/%u`: owner public keys added in Lucia
  (**Settings → SSH keys**) arrive in each heartbeat reply and are written
  there as root-owned 0644 files under a root-owned 0755 directory. Each file
  is validated like the recovery key (no options or comments); usernames must
  be lowercase POSIX names and never `root` or `lucia-recovery`. Only changed
  files are rewritten and files for accounts with no keys are deleted. A reply
  without the list (older Lucia) keeps the installed files. The agent rewrites
  the sshd drop-in and reloads sshd on every managed start, so upgraded nodes
  converge.
* `/etc/sudoers.d/lucia-owners`, mode 0440, grants exactly
  `%lucia-owners ALL=(ALL:ALL) ALL`: LDAP owners must authenticate with their
  password for sudo. A fixed in-target `visudo --check --file` validates it.
  This does not grant sudo to ordinary LDAP users or change recovery's
  explicitly requested key-based `NOPASSWD` access.

Only fixed `/usr/bin/in-target` account, ownership, config-check and systemd
enable operations run in d-i. No downloaded commands or package operations run.
SSSD is not enabled with credentials until first-boot enrollment succeeds.

`managed-run` accepts **no flags**. It checks the fixed installed executable
path, a different boot ID, and that `/` is an ext4 partition of the approved
disk. It persists a CSR made from the **same P-256 identity key**, CN=device GUID,
exactly one DNS SAN=approved hostname, and no other request extensions. All
retries reuse this CSR. Enrollment signs `{nodeId,taskId,csrPem}` with a fresh
authenticated discovery challenge. HTTP 202 Pending is not success. First-boot
enrollment retries within `progressExpiresAt`; expiration requires owner action.

HTTP 200 configuration is accepted only with a bounded leaf-first public PEM
chain (up to four certificates), valid private-CA chain and dates,
local-key SPKI, exact CN/SAN, non-CA leaf and digital-signature/client usage.
The returned CA is trusted for directory/node certificates because it arrives
over the original pinned-origin HTTPS channel; it does not replace that
original HTTPS trust anchor. LDAPS must use the same controller hostname
(default 636 or explicit valid port), with no credentials, path or query in its
URI. Simple bounded DC/OU/CN/UID DNs below the returned base are required;
the native bind DN must exactly equal
`uid=node-<nodeIdN lowercase>,ou=Services,<ldapBaseDn>`. The native base is
`dc=lucia,dc=home,dc=arpa`; owner group is `cn=lucia-owners,ou=Groups,<base>`.
The installed origin remains the original private `spark-9423` endpoint and
its original private CA, not a subsequently changed public portal hostname.
Native controller ACLs remain responsible for enforcing the node account's
read-only permissions. LDAP passwords are retained only in private files and
never command arguments or logs.

SSSD config is 0600 under 0700 `/etc/sssd`, RFC2307bis/uniqueMember, CA-required
LDAPS, LDAP identity/auth providers, `simple_allow_groups=lucia-owners`, cached
credentials, `/home/%u`, and bash fallback. NSS adds `sss` without changing
unrelated databases; PAM enables the distribution SSSD profile and
`pam_mkhomedir`. The original local recovery path remains available.
After restarting SSSD, first-boot configuration requires successful
`/usr/bin/getent group lucia-owners` before it can send a managed heartbeat:
at most three attempts, ten seconds each, separated by two seconds. Command
output is discarded. Failure preserves awaiting-enrollment behavior and local
recovery SSH; it does not prove or replace the separate VM LDAP password-login
qualification. An existing NSS/SSSD cache can satisfy a later group lookup.

Only a successful authenticated machine heartbeat marks a node managed at the
server. Every 30 seconds the daemon uses `/api/nodes/{id}/challenge` and
`heartbeat` with certificate plus the existing signed-proof format, **no bearer
token**. Metrics come from procfs, os-release and root filesystem statistics;
unsupported values are null, never invented. The native CA's existing policy
issues 24-hour node leaves; no CA claims or defaults are expanded. Below six
hours remaining validity the agent requests renewal with the same CSR/key. Pending or failed renewals
back off from 30 seconds to 15 minutes while heartbeats continue with the valid
identity. Invalid identities fail closed; expired identities are restricted to
the recovery-renewal path below. This path executes no LLM,
cloud workload, additional server command, or package update.

If a previously enrolled node's 24-hour leaf expires while the controller is
offline, its cached certificate is used **only** for recovery renewal, never
for a heartbeat. The expired leaf must still match the local key, node CN/SAN
and native bind DN; its complete private-CA chain is checked historically at
leaf NotBefore plus one minute against the unchanged installed private root.
The daemon obtains a fresh nonce and signs the normal renewal proof with its
existing key and persisted CSR, without using the revoked install capability.
Pending/unavailable renewal backs off to 15 minutes and suppresses heartbeats.
Only a newly returned, normally valid certificate under the same pinned root
is persisted/configured and then used for heartbeats. HTTPS certificate
validation is never relaxed; unknown roots, changed keys and expired renewal
responses are rejected.

## API contract

The public types live in `Models.cs`; `AgentJson.Options` applies camelCase.

* `HardwareInspector.Inspect()` returns `HardwareReport`:
  `architecture`, `bootMode`, nullable `secureBoot`, nullable `manufacturer`,
  `model`, `serialNumber`, `hardwareUuid`, `cpuModel`, `logicalCpuCount`,
  `memoryBytes`, `interfaces`, `disks`.
* `NetworkInterfaceReport`: `name`, nullable `macAddress`, `addresses`.
* `DiskReport`: nullable `id`, `path`, nullable `model`/`serial`, `sizeBytes`,
  `isRemovable`, `isReadOnly`.
* `DiscoveryClient.GetChallengeAsync(CancellationToken)`:
  GET `/api/boot/challenge` → `DiscoveryChallenge`
  `{challengeId,nonce,expiresAt}`.
* `DiscoveryClient.RegisterAsync(string reportJson, ECDsa key, CancellationToken)`:
  obtains the challenge and POSTs `/api/boot/discover` using `DiscoveryRequest`
  `{challengeId,publicKeyPem,reportJson,signature}` →
  `DiscoveryRegistration` `{deviceId,token,expiresAt,verificationCode}`.
* `DiscoveryClient.SignReport(...)` exports a public SPKI PEM and signs
  UTF-8(`"lucia-discovery-v1\n" + challengeId + "\n" + nonce + "\n" + reportJson`)
  with ECDSA P-256/SHA-256. `signature` is base64 of the **64-byte IEEE P1363**
  representation, not DER. The JSON string is never reserialized for signing.
* `DiscoveryClient.GetStatusAsync(DiscoveryCredentials, CancellationToken)`:
  GET `/api/boot/devices/{deviceId}` with the stored bearer capability; returns
  bounded JSON for a future agreed status projection. It refuses expired
  credentials or an origin mismatch.
* `SecureStateDirectory.LoadOrCreateKey()`, `SaveCredentials(...)` and
  `LoadCredentials()` persist the local key and registration without exposing
  either through command-line arguments.

The canonical report limit is 32 KiB UTF-8; responses are at most 64 KiB and depth
16 so a status response can include the hardware and metadata. Oversized reports
fail locally before any HTTP request, without omitting devices or addresses.
The server's 128 KiB raw signed-JSON limit is an envelope bound, not permission
to send a canonical hardware inventory larger than 32 KiB.
Challenge IDs are 1–128 visible ASCII characters, nonce 1–512, capability
1–4096; all exclude whitespace/control characters. Verification codes are
1–32 ASCII alphanumeric/hyphen characters. `deviceId` must be a non-empty GUID.
The current server returns a 19-character physical comparison code and an opaque
`d1.fingerprint.innerCapability` bearer token. Duplicate registration returns
409 intentionally; the agent neither recovers a token nor resets its identity
or initiates a reinstall.

## Inventory and identity boundaries

`/proc/cpuinfo`, `/proc/meminfo`, `/sys/class/block`, `/sys/class/net`, DMI and
EFI efivars are read, never written. EFI attributes occupy the first four bytes;
only the fifth SecureBoot byte is interpreted. Missing, unreadable or malformed
efivars mean **unknown**, not disabled. Optional missing strings are null.
Control characters are sanitized and strings bounded; malformed/missing critical
CPU, memory and disk data fail inspection rather than fabricate usable values.

Only whole devices with a device backing are reported; partitions, loop, RAM,
optical and virtual aggregate devices without backing are excluded. Disk `id`
is an actual resolved `/dev/disk/by-id/...` alias, never the kernel `/dev/sdX`
name. Missing aliases remain null: **such disks must not be approved for erase**.
Eligible aliases have a 1..200-character component matching
`[A-Za-z0-9][A-Za-z0-9._:+-]*`, without `..` or a `-partN` suffix. Ineligible
aliases are ignored without omitting the disk: no eligible alias means null.
Aliases are sorted deterministically and partition aliases excluded. Two disk
snapshots compare capacity, IDs, flags, model, serial and major/minor numbers;
observed hotplug invalidates the report. This is a best-effort read-only snapshot,
not a disk lock or proof against changes immediately afterward. The installation
guard therefore re-enumerates before and after the one-time grant/progress
exchange. Physical hotplug must remain prohibited between the final guard
check and partman; the agent cannot lock hardware against physical replacement.

Maximums: 4096 logical CPUs, 1125899906842624 memory bytes, 32 whole disks,
1152921504606846976 bytes per disk, 512 sysfs block entries, 2048 by-id entries,
1..16 non-loopback interfaces, eight addresses/interface, 64-character interface
and device-path components, and 256-character optional strings. IPv6 zone
suffixes are removed by preserving the address bytes; the NIC name carries the
scope. Missing MACs and stable IDs remain null, never synthetic defaults.
These bounds intentionally fail closed on larger inventories.

## Credential and TLS boundary

Live state storage is Linux-only. The final state directory is owned by the
current user with mode 0700. All private identity, installation, CSR and managed configuration files are
owned by that user with mode 0600. Existing bad permissions/keys fail; they are
not silently repaired or regenerated. Intermediate directories must already
exist, be owned by root/current user and not writable by group/others. All
components are opened descriptor-relative with `openat`/`O_NOFOLLOW`, not a
string-prefix containment check. Files must be regular with one hard link.
Writes use exclusive creation and atomic rename; key publication does not
overwrite a concurrently established identity. Only local ext, XFS, Btrfs,
tmpfs, ramfs and overlay filesystems are accepted; NFS, SMB, 9p and symlinked
paths are rejected. Root and other processes under the same UID remain trusted.
This small libc interop is necessary for race-resistant path checks absent
from portable BCL file-open options.

The CA must be one currently valid **public** PEM CA, with no private key, using
the same safe local path rules (public-file mode may be 0644). TLS uses
`CustomRootTrust` with only that CA, mandatory hostname, validity, chain and
server-auth EKU validation, no validation bypass callback, no redirects,
cookies or environment proxies. Optional IP connection override preserves the
original HTTPS hostname for SNI and certificate matching. Like the host's
private-CA backchannel policy, certificate revocation checking is not enabled:
the deployment currently supplies no CRL/OCSP service. This is **not** a TLS
certificate-validation bypass; a future revocation service needs a corresponding
policy change. CA downloads through AIA are disabled; the server must send its
intermediates. No global hosts or system CA configuration is changed.

## Debian installer runtime requirements

The validated .NET runtime pack was `Microsoft.NETCore.App.Runtime.linux-x64`
10.0.11. The published directory was approximately 79 MiB. `readelf`/`ldd`
confirmed the executable and normal runtime libraries require:

* `/lib64/ld-linux-x86-64.so.2` and glibc libraries (`libc`, `libm`, `libdl`,
  `libpthread`, `librt`) — Debian `libc6`.
* `libstdc++.so.6` and `libgcc_s.so.1` — `libstdc++6`, `libgcc-s1`.
* OpenSSL is loaded dynamically for ECDSA/TLS, so ELF `NEEDED` alone does not
  reveal it. Supply Debian 13 `libssl3t64` (`libssl.so.3`, `libcrypto.so.3`).
* Include `zlib1g` in the supported .NET runtime dependency set. The inspected
  10.0.11 compression shim did not have an external zlib `NEEDED` entry; do not
  infer a universal exemption for other servicing builds.
* The shipped optional LTTng tracing shim references `liblttng-ust.so.0`; it was
  not required for these executions. Kerberos/GSSAPI is not used by this client.
  ICU, ASP.NET Core and a separately installed `dotnet` host are not needed.

Require readable procfs/sysfs, `/dev` (including `disk/by-id` after udev settles),
a local writable state directory (normally `/run` tmpfs), functioning entropy,
networking, and an accurate clock for certificate expiry. Debian's d-i initrd is
not automatically a complete Debian runtime: **the matching .NET runtime and
all necessary distribution shared libraries must be staged and tested there**.
This project neither changes an initrd nor claims a real PXE boot is qualified.

## Console checks

Run on Linux from the repository root:

```sh
dotnet run --project tests/Lucia.NodeAgent.Checks/Lucia.NodeAgent.Checks.csproj
# Or execute the self-contained checks publish:
./tests/Lucia.NodeAgent.Checks/bin/Release/net10.0/linux-x64/publish/Lucia.NodeAgent.Checks
```

Tests create and remove bounded fixtures under `tests/Lucia.NodeAgent.Checks`.
`dotnet run --project tests\Lucia.NodeAgent.Checks -- installation-only` also
runs portable installation/PKI/SSSD/serialization checks on Windows without
requiring symlink privileges. Full checks require Linux. No test invokes
`StageAsync`, `ConfigureDirectoryAsync`, partman, apt, account tools or systemctl
against a real system, and no test opens an actual block device.
They use actual proc/sys fixture files and by-id symlinks, signature verification,
bounded fake HTTP exchanges, and real loopback TLS with correct/wrong CA,
hostname, EKU and expiry. Native storage checks verify key reuse, 0600 modes,
atomic credential round-trip and rejection of broad permissions, symlinks,
hardlinks and malformed stored identities. Symlink creation requires Linux or
Windows developer/admin privileges. Positive native storage checks report a
skip on unsafe or remote workspaces (including WSL 9p); run from a safe local
Linux filesystem for full coverage.

The new Linux fixture checks additionally exercise selected-disk child
mounts/swap/holders, changed by-id targets, target-root mounting, private plan/
CSR persistence, concurrency locks, durable uncertain-POST markers, repeated
guards, progress-before-permission, expired grants and changed disks. Fixture
roots and HTTP transports are internal constructors, never CLI options.

**Readiness:** build/unit/fixture checks do not qualify a boot image or authorize
erasing any physical machine. Disposable UEFI Debian 13.7 VM qualification of
the parent hooks, d-i preseed, recovery SSH, first-boot SSSD/PAM, enrollment and
renewal is still required before claiming the image ready.
