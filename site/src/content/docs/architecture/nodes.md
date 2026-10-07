---
title: Nodes
description: How Lucia discovers, installs, enrolls and manages x86-64 servers over UniFi network boot, and what is qualified today.
section: architecture
order: 5
---

A **node** is an x86-64 server that Lucia installs and manages. You plug it in and network-boot it. Lucia shows you what it found, and you approve it with an erase confirmation. The machine then installs Debian 13, enrolls itself and starts reporting in. After that, the plan is that you never need to plug a monitor into it again.

![The Devices page listing managed servers with their health](/screenshots/devices.png)

## The lifecycle

```text
 closed --(Devices: Add hardware)--> discovery window open (30 min)
   |                                         |
   |                       PXE boot via UniFi Network Boot (options 66/67)
   |                                         v
   |                   discovered: hardware report + verification code
   |                       |                                   |
   |               Reject (no erase)              Approve: hostname, disk,
   |                                              recovery key, type ERASE
   |                                                           v
   |                          Debian 13 installer, plan-specific preseed
   |                                                           v
   |                          agent writes its service into the new system
   |                                                           v
   |                          first boot: node agent enrolls (CA certificate)
   |                                                           v
   +---------------------------------------------> managed: heartbeat every 30 s
```

## Discovery: a timed window

Discovery is **off by default** and closes again whenever the host restarts. An owner opens it from **Devices → Add hardware**:

- The window lasts **30 minutes**. Use **Extend** or **Stop discovery** to change it.
- While it's open, Lucia's TFTP service answers and new machines may register.
- Once a machine has been approved, its installation authority lasts **15 minutes**.

The UniFi Network Boot setting stays on permanently. The window is the switch, so you never revisit UniFi for each server.

A booting machine loads Lucia's verified Debian installer environment. It checks the hardware, registers over HTTPS and shows a **verification code** on its screen. A monitor is optional: every screen moves on by itself and never waits for a key. Match that code against the one shown in **Devices** before you approve anything.

## One-time UniFi Network Boot setup

In **UniFi Network → Settings → Networks**, pick the network new servers boot on. Keep UniFi as the DHCP server, then turn on **Network Boot**:

| Setting | Value |
|---|---|
| Boot server | The Spark's IPv4 address, with no scheme or port. Example: `192.168.0.222` |
| Boot file | `debian-installer/amd64/bootnetx64.efi` |
| Separate **TFTP Server** toggle | Leave it off |

Network Boot supplies DHCP options **66 and 67**. Don't also enable UniFi's TFTP Server option. It's just option 66 again, not a TFTP daemon. Don't add duplicate custom options either. The boot file is a TFTP path: keep the forward slashes and the directory prefix exactly as shown.

Some UEFI firmware fetches the shim correctly and then asks for GRUB from the TFTP root. Lucia serves an identical, verified `grubx64.efi` there for that case. The boot environment also includes Debian's verified Realtek `rtl_nic` firmware, so common 2.5 GbE NICs work during install.

### DNS for booting machines

The installer fetches its preseed over HTTPS from the controller's hostname, and it checks the certificate. That hostname has to resolve through the DNS servers that DHCP hands out. For example, add an AdGuard rewrite for `spark-9423` pointing at `192.168.0.222`. A hosts-file entry on your desktop does nothing for a server that's still booting.

Also check that the target's clock is right. Lucia never skips certificate verification to make an install work.

### Firewall

If you segment your network, allow the boot network to reach only the following:

| Destination | Purpose |
|---|---|
| Spark UDP 69 and UDP 40000–40016 | TFTP and its transfer ports |
| Spark TCP 443 | Verified preseed and API |
| Spark TCP 9080 (if the boot profile uses it) | Boot asset delivery over HTTP |
| Your DNS resolver, UDP/TCP 53 | Controller name lookup |

No WAN port forwarding is needed. Don't run a second DHCP server.

> [!NOTE]
> Classic PXE and TFTP don't authenticate the boot configuration or the initrd. Run onboarding on a network you trust. Lucia verifies everything it can from the HTTPS preseed onward.

## Approval: the destructive step

Approving a discovered machine opens a form that asks for:

1. A **hostname**: lowercase letter first, then letters, numbers and hyphens.
2. The **disk to install Debian on**. Nothing is preselected, and a disk without a stable identity can't be chosen.
3. A **recovery SSH public key**, pasted or imported from a GitHub username. It's the only key the local `lucia-recovery` account will accept.
4. A checkbox confirming you checked the physical device and matched its verification code.
5. The word `ERASE`, typed by hand.

> [!CAUTION]
> Approving an installation erases **every** non-removable, writable disk in the machine, not only the one you pick. Debian goes on the chosen disk. All other disks are combined into one data volume mounted at `/srv/data`. This can't be undone. **Reject discovery** never erases anything.

## Installation and enrollment

After approval:

1. The discovery agent is still running inside the installer environment, waiting. It inspects the hardware again, and stops if anything changed since discovery. It then checks that the chosen disk is unused and writes a Debian preseed for exactly the approved plan.
2. The Debian 13 (trixie) amd64 installer runs unattended. Partitioning needs a fresh one-time grant from the host.
3. Before the installer finishes, the agent writes into the new system:
   - its own runtime;
   - the public CA;
   - the hostname;
   - the `lucia-recovery` account, locked, with your recovery key and `sudo`;
   - `sudo` rules for `lucia-owners`;
   - an SSH configuration;
   - the `lucia-node-agent` systemd service.

   It runs only fixed executables, never a shell, and checks each piece (`visudo --check`, `sshd -t`) before enabling the service.
4. On first boot, the agent enrolls. It creates a key pair on the machine and sends a certificate request, authorized by the installation grant. In return, it gets a certificate from the private CA that's valid for about 24 hours. It renews that certificate once less than 6 hours remain.
5. The agent starts sending a heartbeat every **30 seconds**.

**Your lab → Installation tasks** follows each install. If setup stops on the machine, its screen says what happened and why.

### The qualification gate

Installation ships behind host qualification flags. It stays blocked until the host marks three things as qualified: the boot artifacts, the private CA path and directory enrollment. Until then, **Devices** shows "Discovery is ready. Installation is not." with the explanation "No installation can start until boot, private CA, and directory enrollment are qualified by the host."

The network boot service itself is optional. The production AppHost adds it only when boot settings exist, and those are prepared outside the desktop today. A stock install has discovery and installation both unavailable until a maintainer runs that preparation. See [Limitations](/docs/reference/limitations/).

## Living with a managed node

### Health and names

The node shows **online** while its last heartbeat is less than 2 minutes old. Lucia keeps one hour of per-node metrics in memory, 180 samples at the heartbeat interval.

Once a domain is active:

- Lucia keeps an AdGuard rewrite `<hostname>.<namespace>`, for example `lucialab01.homelab.seiggy.com`, pointing at the address from the latest heartbeat.
- With UniFi connected, Lucia also reserves that address.
- Setting UniFi's **Domain Name** to your namespace makes `ssh lucialab01` work.

### SSH and sudo

Sign in with your Lucia account, for example `ssh <username>@lucialab01`. Only members of `lucia-owners` can sign in, and `sudo` asks for your Lucia password.

Keys you add in **Settings → SSH keys** reach every node at its next check-in. Removing a key there revokes it everywhere. If LDAP is unreachable, `lucia-recovery` accepts only the recovery key you chose at approval.

### Node actions

From **Devices**, an owner can run these actions on a node:

- **Check now**
- **Install updates**
- **Restart**
- **Update agent**

The assistant can do the same through `node_action`, and it always asks first.

The assistant can also run a command on a node with `run_command`, which always asks too. The command:

- runs as a transient systemd unit named `lucia-exec-<job>`;
- may use bash, sh or python3;
- is limited to 16 KB of script and 30 minutes of run time;
- returns the last 32 KB of output;
- shares a limit of 4 concurrent commands per node.

### Removing a node

**Remove from Lucia** forgets the node, revokes its agent's access and drops its DNS name. It doesn't erase or shut down the machine, so move its apps off first. To repurpose it, reinstall it from scratch with a one-time network boot. Day to day, boot nodes from their local disk, and select network boot only when you mean to reprovision.

## What is qualified

| Configuration | Status |
|---|---|
| x86-64 UEFI network boot, same LAN as the Spark | Qualified layout |
| Secure Boot | Reported, never changed. Qualified with Secure Boot **off** |
| Legacy BIOS PXE | Not qualified |
| ARM64 servers | Not qualified |
| Routed or cross-VLAN PXE | Not qualified |
| NICs needing firmware other than Realtek `rtl_nic` | May need firmware added to the bundle |

## Troubleshooting

| Symptom | Check |
|---|---|
| PXE times out | Is the discovery window open? Then check the boot file path, the Spark's IP address and the UDP firewall rules |
| Bootloader loads, then TFTP reports `/grubx64.efi` missing | Re-prepare the boot bundle so it includes the root GRUB copy. Don't change the UniFi boot file |
| HTTPS preseed or registration fails | Check client DNS, the target's clock, and that the hostname matches the certificate |
| Installer stops at `check-missing-firmware` | Read the missing filename on the console (Alt+F4), add the verified Debian firmware to the bundle, then boot again |
| Installation controls are disabled | Expected until the host qualifies installation |
