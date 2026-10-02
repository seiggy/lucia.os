# Read-only Debian discovery bundle

For the required manual Dream Machine / UniFi configuration, see the
[user-facing UniFi Network Boot guide](../../docs/unifi-network-boot.md).
UniFi Network is the only currently supported LAN gateway; neither this tooling
nor the desktop app automatically edits its DHCP/network-boot settings.

This is **preparation tooling, not permission to enable PXE or install Debian**.
The first target is x86-64 UEFI with Secure Boot **already off by owner choice**.
No firmware setting, DHCP configuration, host disk, or running deployment is
changed. The public bundle contains no node key, bearer capability, CA private
key, LDAP credential, or installation authorization.

## Prepare in an isolated Linux container

Build `tools/boot/Dockerfile`, **not** the boot-service Dockerfile in this
directory. Its pinned Debian 13.7 image supplies the trusted Debian archive
keyring, `gpgv`, ELF/module inspection tools, and optional x86-64 userspace
emulation on ARM builders. Apt verifies builder-tool packages normally.

Mount only a private source copy, the complete trusted-maintainer `linux-x64`
agent publish directory, and a private output directory containing the **public**
CA certificate. Never mount the identity service directory or host devices.
Use the source owner's UID/GID; inputs and their ancestors must not be
group/world-writable or symlinked. A Linux copy may be needed when Windows/WSL
mount permissions cannot express this boundary.

Example Linux-container invocation (paths are inside the container):

```sh
python3 /src/tools/boot/prepare.py build \
  --node-agent /release/boot/node-agent-linux-x64 --ca-file /out/public-ca.crt \
  --ca-der-sha256 fe5b8ea1e2dbc950cc292dd6cb69b6594ec7effc5ab7b61c9823a5c070e4d84e \
  --server https://spark-9423 --connect-address 192.168.0.222 \
  --output /out/bundle
```

`--node-agent` consumes the entire existing publish directory from the streamed
HostPayload's optional `boot/node-agent-linux-x64` entry. It does not download a
separate agent or alter the ARM64 server runtime. `--agent-publish` remains a
compatible alias. If the source layout is flattened in the release, specify
`--boot-assets DIR` containing `discover-and-wait`, `partitioner-guard`, and
`grub.cfg.in`; otherwise `deployment/boot` beside the source tree is used.
`--ca-der-sha256` optionally pins the supplied public certificate's DER encoding
before output is created. The example pin is the owner's verified Spark public
CA; a deliberate CA rotation needs a new explicitly trusted pin, not a TLS
bypass. Both PEM-file and DER hashes are recorded separately in the receipt.

The default keyring is the Debian package's regular file
`/usr/share/keyrings/debian-archive-keyring.pgp`. An explicitly supplied
`--keyring` must already be independently trusted; the tool does not download
trust anchors. The output must **not exist**. Failures leave only that newly
created, marked build directory for diagnosis; they never overwrite another
bundle. Use a new output path after a failed attempt.

```sh
python3 /src/tools/boot/prepare.py verify /out/bundle
python3 /src/tools/boot/check_prepare.py --bundle /out/bundle
```

`verify` is a local-receipt integrity check, **not a signed Lucia attestation**.
To reproduce offline, run `build` with a new output directory, identical inputs,
and `--replay /previous/bundle/evidence/downloads`, with container networking
disabled. Replay rechecks `gpgv`, signed metadata, every digest, and runtime
dependencies. Release freshness still applies: an expired, wrong-version, or
older-than-120-day Release is rejected rather than silently selecting a newer
installer. The generated CPIO has sorted paths, fixed metadata, and gzip mtime 0.

## Stable parent integration API (receipt v1)

`tools/boot/provision.py` may import these stdlib-only functions in the Linux
builder. Neither function prints or starts services; errors propagate as
`ValueError`, `OSError`, `KeyError`, or `subprocess.SubprocessError` for the
parent's explicit error reporting:

```python
from tools.boot.prepare import prepare_bundle, verify_bundle

receipt = prepare_bundle(
    output="/out/new-bundle",                         # must not exist
    node_agent="/release/boot/node-agent-linux-x64",  # entire trusted publish
    ca_file="/out/public-ca.crt",                    # public CA only
    ca_der_sha256="fe5b8ea1e2dbc950cc292dd6cb69b6594ec7effc5ab7b61c9823a5c070e4d84e",
    server="https://spark-9423",
    connect_address="192.168.0.222",                 # optional; default None
    keyring="/usr/share/keyrings/debian-archive-keyring.pgp",
    replay=None,                                    # or previous evidence/downloads
    boot_assets="/release/boot/assets",              # optional; default source layout
)
assert verify_bundle("/out/new-bundle") == receipt
```

All arguments to `prepare_bundle` are keyword-only. File/directory arguments
accept `str` or `pathlib.Path`; it returns the same dictionary persisted as
`output/receipt.json`. `verify_bundle(directory)` is read-only and returns that
dictionary after checking local artifact/evidence hashes, the exact eight-file
allowlist, guarded GRUB configuration and controller route. It does not replace
fresh signature authentication: `prepare_bundle(..., replay=...)` rechecks that
chain without network access. Existing `verify(directory)` remains an alias.

The schema is `schemaVersion: 1`; integration fields are:

| Field | Shape / meaning |
| --- | --- |
| `purpose` | `"read-only-discovery-no-installation"` |
| `debianVersion`, `installerBuild`, `kernelAbi` | Exact version strings |
| `controller` | `{server, discoveryUrl, connectAddress, publicCaSha256, publicCaDerSha256}`; absent override is `""`; hashes cover PEM bytes and DER respectively |
| `artifacts` | `{public-relative-path: lowercase-sha256}`; exactly the eight paths below |
| `upstream` | `{original-boot-path: sha256}` before replacing GRUB configuration |
| `agentFiles` | `{publish-filename: sha256}` of every trusted maintainer input |
| `overlayFiles` | `{initramfs-relative-path: {mode, sha256}}`; mode includes POSIX file type; symlink hashes cover target bytes |
| `release` | `{date, sha256, validSignatureFingerprints: [...], keyringSha256}` |
| `downloads` | `{archive-relative-path: {url, file, size, sha256}}`; `file` is under `evidence/downloads` |
| `packages` | `{package-name: {Version, Architecture, Filename, SHA256, Size}}`; signed Debian field spelling/types (`Size` is a string) |
| `runtimeCheck` | `{elfNeeded: [...], cliExecuted, inspectExecuted, execution, optionalNotRequired: [...], disclaimer}` |
| `qualification` | `{uefiVmBoot: false, diskUnchangedTest: false, realHardware: false, secureBootTarget: "owner-disabled"}` at preparation time |
| `readinessPrerequisites` | Array of unresolved deployment/hardware qualification conditions |

CLI `build` returns one small JSON summary on stdout:
`{bundle, prepared: true, liveReady: false, bootfile, overlaySha256}`; the full
receipt stays on disk. CLI `verify` returns
`{integrityVerified: true, liveReady: false, installerBuild}`. Nonzero exit
status is failure, never an invitation to serve a partial output. Always mount
only `output/public`; `prepared`/`integrityVerified` are not onboarding readiness
or authority to publish ports. Separate qualification evidence may be retained
beside the bundle without changing the preparation receipt.

## Provenance and output contract

The fixed upstream installer is `20250803+deb13u7`:

1. `https://deb.debian.org/debian/dists/trixie/InRelease`, authenticated by `gpgv`.
2. Its signed SHA256 entry authenticates
   `main/installer-amd64/20250803+deb13u7/images/SHA256SUMS`.
3. That manifest authenticates `netboot/netboot.tar.gz`; the known manifest,
   tarball, and original-initrd SHA256 values are also pinned in the builder.
4. Signed Release hashes authenticate the Debian amd64 package/udeb indexes,
   which authenticate all included runtime libraries and storage modules.

Only `bundle/public` is suitable for the public boot service. Its exact eight
regular files are:

```text
debian-installer/amd64/bootnetx64.efi
debian-installer/amd64/grubx64.efi
debian-installer/amd64/linux
debian-installer/amd64/initrd.gz
debian-installer/amd64/grub/grub.cfg
debian-installer/amd64/grub/font.pf2
debian-installer/amd64/grub/lucia-theme.txt
lucia/lucia-overlay.cpio.gz
```

The initial TFTP bootfile must be
**`debian-installer/amd64/bootnetx64.efi`**, not a flattened filename. Preserve
GRUB's embedded `debian-installer/amd64/grub` prefix. GRUB loads the untouched
upstream kernel and initrd, followed by the separate Lucia compressed CPIO.
No additional loader assets were needed in the isolated UEFI smoke. Mount
only this eight-file `public` directory as the **TFTP root too**, not just as
an HTTP allowlist: the original netboot tar contains unchecked installer/BIOS
menus. Build and verification reject additional public files, extra GRUB
entries/config includes, a missing guard overlay, and the old preseed route.
The receipt records upstream/output/overlay/publish hashes, signature
fingerprints, keyring hash, exact package versions, module ABI, and explicit
unqualified runtime gates. **Do not serve `receipt.json`, `evidence`, or work
directories.** Evidence includes the original upstream tarball/config.

The signed Debian shim, GRUB, and kernel are retained for provenance, but
Secure Boot is off on this target. Even with enforcement on, that signed chain
does **not** authenticate external GRUB configuration or the external initrds.
A trusted, owner-approved `192.168.0.0/23` provisioning LAN is mandatory;
these files are not a defense against hostile DHCP/TFTP peers.

## Networking and fail-closed discovery

Before live use, DNS must resolve **`spark-9423`** to the HTTPS endpoint, and its
certificate must match that hostname and the supplied public CA. On September
22, 2026, the parent verified the owner's AdGuard Home rewrite through LAN DNS
`192.168.1.230`: `spark-9423` now resolves to `192.168.0.222`. This tooling does
not alter DNS; resolution from the eventual booted target still needs checking.
When DHCP supplies a domain, the installer appends it to the dotless preseed host
(`spark-9423.homelab.seiggy.com`), so DNS must resolve that name too. Identity
provisioning adds each `/etc/resolv.conf` search-domain form of the public host to
the service certificate, and the controller accepts any host on `/api/boot`.
The agent's optional connect-address preserves SNI/hostname checks,
but **does not fix GNU Wget's preseed DNS lookup**. The target clock must also
be accurate. No TLS bypass, `allow_unauthenticated_ssl`, or IP-certificate
workaround is supplied.

The network/window-gated HTTPS controller endpoint
`https://spark-9423/api/boot/discovery.cfg` returns only discovery preseed:

```text
d-i preseed/early_command string /usr/lib/lucia/discover-and-wait
```

The agent uses `/api/boot/challenge`, `/api/boot/discover`, and
`/api/boot/devices/{uuid}`. There are no installation, enrollment, or grant
endpoints in this milestone. The boot service's public HTTP port and TFTP
service do not replace the controller's HTTPS preseed/API origin.

Network-preseed runs this **after** configured networking. The overlay does not
include an initrd `/preseed.cfg` early command, which would run too soon.
GNU Wget retains certificate/hostname verification with the public CA selected
in `/etc/wgetrc`; the agent uses its own public-CA argument.

The hook checks UEFI/architecture/kernel ABI, loads the pre-staged NVMe, SATA,
and virtio module closure, settles udev, invokes native discovery once, shows
the safe matching code on the Lucia status screen, and **blocks forever on both success and
failure**. Runtime private keys/capabilities are created only in `/run/lucia`.
The independent `/lib/partman/init.d/00lucia-approval` interlock also blocks
unconditionally, including if preseed fetching fails or its error is caught.
Its archive path uses `usr/lib/partman/...` to preserve Debian's `/lib` symlink.
Owner approval does not release either block. There are no disk choices,
partition recipes, erase confirmations, filesystem mounts, swap/RAID/LVM
activation, or installer execution in these scripts. This is not a sandbox
against a malicious root operator deliberately removing the guard.

## Boot screens (no keyboard or monitor needed)

Every screen advances on its own; none waits for a key. Screenshots are in
[the UniFi setup guide](../../docs/unifi-network-boot.md#what-the-servers-screen-shows).

- **GRUB** (`grub-theme.txt` → `grub/lucia-theme.txt`): a dark gfxmenu with a
  box-drawing LUCIA wordmark and a 3-second countdown. The stock `font.pf2`
  only has ASCII, arrows and `━┃┏┓┗┛│┌┐└┘`; `check_prepare.py` rejects any
  other glyph. If the font or theme fails to load, GRUB falls back to its text
  menu.
- **Kernel args** (`CONSOLE_ARGS` in `prepare.py`): `quiet`, `fb=false` and
  `vt.default_red/grn/blu`. `fb=false` keeps d-i on the kernel console instead
  of bterm, whose VGA palette is fixed. newt's blue/gray/red roles then draw as
  Lucia navy, pale cards and accent blue. These args sit before `---` and do
  not reach the installed system.
- **Status screen** (`screen.sh`, drawn on tty5 and brought to the front):
  sun mark and wordmark, five steps with live timers, the verification code in
  large glyphs, and a plain-language fail panel with the fix and the reason.
  `discover-and-wait` hands back to the installer on tty1 after approval, and
  `finish-install` brings it forward again if enrollment fails.

## Runtime support and qualification limits

The agent's entire trusted-maintainer publish is included. Verified Debian
`libc6`, `libstdc++6`, `libgcc-s1`, `libssl3t64`, `zlib1g`, and `libzstd1`
libraries live privately beside it. Debian OpenSSL requires `libzstd.so.1`.
The private ELF loader uses `--inhibit-cache --library-path`, avoiding changes
to stock installer glibc/OpenSSL. Co-location also matters because .NET's
apphost resolves its DLL relative to the explicitly executed loader.
`readelf` verifies the actual amd64 ELF dependency closure; OpenSSL/zlib
dynamic-load dependencies are checked explicitly. Optional LTTng, ICU and
Kerberos are not part of this discovery client's required runtime.

Matching `sata-modules`, `scsi-modules` and their kernel-ABI udeb dependencies
provide NVMe/SATA/virtio drivers. `depmod` metadata is generated offline and
`modprobe --show-depends` checks all requested module closures. No deferred
`anna-install` request is mistaken for a loaded driver. This does not guarantee
every physical HBA/NIC/firmware combination; missing/unusable hardware must
stop discovery, never select another target disk.

Validated preparation checks cover signature/hash authentication, exact stock
initrd retention, package/module closure, deterministic archive construction,
unsafe paths/links, input credentials, size bounds, and the blocking partition
guard. A userspace CoreCLR CLI smoke test can run under `qemu-x86_64` on ARM.
It is **not** a UEFI boot test. No host disk devices are exposed merely to make
container inventory succeed.

To exercise the native agent's fixture, signature, secure-state, and real
loopback TLS checks against **the libraries in the actual overlay**, also mount
the trusted `Lucia.NodeAgent.Checks` linux-x64 publish and run:

```sh
python3 /src/tools/boot/check_prepare.py --bundle /out/bundle \
  --agent-checks-publish /checks
```

This stages only the five Checks entry artifacts beside the overlay runtime
in a disposable private directory, then runs natively or via qemu-user. It
needs no host disks or external network. Require the complete current native
check count, not a reduced count with storage skips; keep the exact result
with that bundle's qualification evidence. This still does not substitute for
discovery inside the booted installer.

Before declaring this bundle ready, separately qualify an isolated
`qemu-system-x86_64` TCG/OVMF boot with Secure Boot off, private user networking
and TFTP (no bridged NIC, live PXE ports, or `-kernel` shortcut). Exercise the
actual shim → GRUB → kernel → both initrds, HTTPS preseed and signed discovery.
Hash disposable VM disks before/after success, wrong-CA/hostname, unavailable
DNS, closed admission, timeout, and restart cases. No UEFI VM boot,
unchanged-disk qualification, or real-hardware success is implied by
`prepared: true`; the receipt explicitly records those gates as false.

For a bounded **negative-network UEFI smoke**, build the builder Dockerfile
with `--target qualification` and run, in a container with `--network none`,
no ports/devices, and only the private bundle/source mounts:

```sh
python3 /src/tools/boot/check_prepare.py --bundle /out/bundle \
  --uefi-output /out/new-uefi-evidence
```

This takes at most five minutes plus shutdown. OVMF boots the official shim
from QEMU's private user-network TFTP; no separate iPXE image or direct
`-kernel` boot is used. A **private copy** of GRUB's config adds serial output.
The test records serial/QEMU logs and hashes its own 64 MiB disposable raw
virtio disk before/after. `restrict=on` and the isolated container deliberately
prevent controller access. Reaching the kernel and leaving that disk unchanged
is only a firmware-chain smoke: it does not qualify successful HTTPS discovery,
all failure cases, Secure Boot enforcement, or physical hardware. Its separate
result file never changes the bundle's readiness flags.

The isolated smoke reached the Debian `6.12.107+deb13-amd64` kernel and the
installer's name-server prompt through OVMF/shim/GRUB. Its disposable disk
retained SHA256
`3b6a07d0d404fab4e23b6d34bc6696a6a312dd92821332385e5af7c01c421351`.
It deliberately had no controller/DNS connectivity, so network-preseed,
in-installer signed discovery, and the successful pending-node wait remain
**unqualified**. This is not evidence that the disk remains unchanged through
every future workflow or failure case.
