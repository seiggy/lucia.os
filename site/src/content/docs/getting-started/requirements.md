---
title: Requirements and assumptions
description: What Lucia expects of your lab before you install it, and what it doesn't support or hasn't qualified yet.
section: getting-started
order: 1
---

Lucia makes a few firm assumptions about your hardware and network. It isn't trying to be the right fit for every rack in every closet. This page lists those assumptions so you can check them before you start, not after an hour in an installer log.

Each assumption below is labeled as one of:

- **Required:** setup refuses to continue without it.
- **Manual:** you do it once, by hand. Lucia never changes it.
- **Not qualified:** it might work, but nobody has tested it, so don't count on it.

## The controller: an NVIDIA DGX Spark

Lucia's controller runs on one DGX Spark. The portal, the API, local inference, the assistant, the identity stack and the network-boot service all live there as Docker containers.

> [!IMPORTANT]
> Finish NVIDIA's own DGX OS first-run setup before you start. That means networking is up and you have an SSH account that can use Docker. Lucia starts from that point. It doesn't repair the OS, replace NVIDIA's Docker engine, change Docker group membership or install GPU drivers.

The desktop bootstrap runs these preflight checks over SSH. Anything marked blocking stops setup.

| Check | What it wants | Blocking |
|---|---|---|
| Operating system | Ubuntu 22.04 or 24.04, which is what DGX OS is based on | Yes |
| Host architecture | Linux ARM64 (`aarch64`) | Yes |
| Docker | Daemon running and reachable by the SSH account | Yes |
| Docker version | 28 or newer, which Aspire requires | Yes |
| Docker Compose | The official Compose plugin | Yes |
| NVIDIA CDI | An existing `nvidia.com/gpu=all` CDI device; Lucia makes no driver changes | Yes |
| Python | 3.11 or newer on the Spark | Yes |
| OpenSSL | Installed from the OS package manager | Yes |
| .NET and Aspire | .NET SDK 10.0.401 and Aspire 13.5.4; if missing, they can be installed user-locally from pinned, checksummed packages once you approve | Action |
| Ports | 443, 636 (LDAPS), 9443 (Authentik) and 9444 (private CA) free, or already owned by Lucia | Yes |
| Disk | At least 12 GiB free for a fresh install | Yes |

Give the Spark a stable IPv4 address. Once UniFi is connected, Lucia reserves the Spark's current lease for you. The examples on this site use `192.168.0.222`, a real lab address; yours will differ.

To get native ARM64 GitHub Actions jobs, run the GitHub Actions runner app on the Spark on demand. It's optional and nothing else depends on it.

### Persistent user services

Several Spark-side helpers run as **user** systemd services owned by the setup account, for example domain activation, node enrollment and the Spark model and runner services. The desktop installs them only if the account can keep services running after logout.

```bash
loginctl enable-linger <setup-user>
```

Without lingering enabled, setup still finishes, but domain activation, people management and node enrollment stay blocked, and the portal tells you so.

The root update worker for Spark packages is a separate install that needs `sudo`. See [Spark updates](/docs/architecture/spark-updates/).

## The network: UniFi, and only UniFi

**UniFi Network is the only supported LAN gateway.** Lucia keeps your UniFi gateway as the DHCP server and doesn't run a second one.

UniFi tasks fall into two groups.

**Manual, once:**

- Turn on **Network Boot** for the network new servers boot on. Set the Spark's IPv4 address as the boot server and `debian-installer/amd64/bootnetx64.efi` as the boot file. This supplies DHCP options 66 and 67. Leave the separate **TFTP Server** toggle off.
- Set the DNS servers that DHCP hands out, for example your AdGuard addresses.
- Optionally, set the network's **Domain Name** to your Lucia namespace so short hostnames resolve.

**Optional, through Lucia:**

- Connect a UniFi API key in **Settings → UniFi Network**. Lucia then pins DHCP reservations for the Spark and each managed server. It only adds reservations, and never touches an address you or another device already reserved.
- If you turn on public access, Lucia can also manage a TCP 443 port forward to its gateway.

Lucia never asks for your UniFi password. It never changes DHCP boot options, networks or firewall rules.

## Managed servers: x86-64 UEFI, Debian 13

Servers Lucia installs and manages get **Debian 13 (trixie), amd64**, installed over the network by the Debian installer. Lucia's agent drives the install from inside the installer and stays on as the node agent.

| Assumption | Status |
|---|---|
| x86-64 machines with UEFI network boot | Required |
| Same LAN or VLAN as the Spark when PXE booting | The only qualified layout |
| Legacy BIOS PXE | Not qualified |
| ARM64 managed servers | Not qualified |
| Routed or cross-VLAN PXE | Not qualified |
| Secure Boot | Reported, never changed. The maintainer's lab qualified with Secure Boot **off** |
| Controller hostname resolves via DHCP-supplied DNS | Required for the HTTPS preseed |
| Accurate clock on the target | Required, because certificates are verified and never skipped |

> [!CAUTION]
> Approving an installation erases **every** non-removable disk in that machine. Debian goes on the disk you pick, and any other disks are combined into one data volume at `/srv/data`. Rejecting a discovery never erases anything.

The installation path ships behind host qualification flags. Until the boot bundle, private CA and directory enrollment are marked qualified on your Spark, **Devices** reads "Discovery is ready. Installation is not." This is intended behavior. See [Nodes](/docs/architecture/nodes/).

## DNS and certificates: Cloudflare, AdGuard, or a private CA

Out of the box, Lucia issues every certificate from its own **private CA** (Smallstep). Browsers won't trust it until you import the public root certificate. The desktop can do this with a separate approval, or you can do it by hand after checking the fingerprint.

For publicly trusted certificates and friendly names, connect a domain:

- **Cloudflare** must already be the zone's authoritative DNS. The registrar can be anywhere. Lucia uses Let's Encrypt DNS-01 with an account-owned API token scoped to one zone with **DNS Write** and **Zone Read**.
- **AdGuard Home** serves the local names. Lucia publishes no private addresses as public A or AAAA records.

Cloudflare is optional. Without it, you stay on the private CA and the Spark's own hostname, for example `https://spark-9423/`.

AdGuard is optional for a bare install, but domain setup needs it. Lucia can also run AdGuard Home on every managed server for redundancy. See [AdGuard HA](/docs/architecture/adguard-ha/).

## Your desktop

The setup app is an Avalonia desktop app for Windows, macOS and Linux. A self-contained build needs no SDK, Python or Docker on the desktop.

You need SSH access to the Spark, using a password or a private key. You'll confirm the Spark's host-key fingerprint before any credential is sent.

A signed, downloadable release doesn't exist yet. Today you build it yourself. See [Install from source](/docs/getting-started/install-from-source/).

## For the assistant (optional)

The SRE assistant runs on the GitHub Copilot runtime. Using GitHub-hosted models needs a GitHub account with Copilot. Models from a LiteLLM app or Lucia's own Local AI work without a GitHub sign-in.

## What is not supported or not qualified yet

The full list is in [Limitations](/docs/reference/limitations/). The short version for planning:

- No signed or notarized desktop release.
- Network boot service setup has no user-facing entry point yet.
- PXE is qualified only for x86-64 UEFI on the same LAN.
- The private CA publishes no CRL or OCSP. Strict-revocation clients may refuse it.
- The bundled chat model is marked **qualification pending**.
- Memory is reserved for a voice pipeline on the Spark, but that pipeline isn't built.
- Gateways other than UniFi aren't supported.

If your lab meets these assumptions, continue to [Install from source](/docs/getting-started/install-from-source/). If it doesn't, the sooner you find out the better.
