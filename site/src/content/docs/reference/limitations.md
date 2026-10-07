---
title: Limitations
description: An honest list of what Lucia doesn't do yet, what isn't qualified, and what stays manual, checked against the current code.
section: reference
order: 3
---

Lucia is early software built in one lab. This page lists what doesn't exist yet, what exists but hasn't been tested beyond that lab, and what's deliberately left to you. The list is checked against the code, not a roadmap. If something here changes, the code changes first and this page follows.

Three words recur with specific meanings:

- **Not implemented:** the code doesn't do it.
- **Not qualified:** it might work, but nobody has tested it, so don't count on it.
- **Manual:** you do it yourself, on purpose. Lucia won't.

## Installing Lucia

| Item | Status |
|---|---|
| Signed, notarized desktop release | **Not implemented.** You build an unsigned app with `tools\desktop\publish.ps1`. SmartScreen and Gatekeeper will warn you |
| TensorSharp GB10 packages | Not published, and how to build them isn't documented in the repository. The Linux ARM64 host publish fails without the local package feed. See [Install from source](/docs/getting-started/install-from-source/) |
| Running the desktop with `dotnet run` | Shows the UI, but can't deploy the host, because the bootstrap and host payloads only come from a publish |
| Controllers other than a DGX Spark | **Not qualified.** The identity stack uses ARM64 images, and inference assumes the GB10 with NVIDIA CDI |
| Cross-platform desktop builds | Windows interaction and a real Spark deployment have been exercised. Other desktop platforms build but aren't a qualified release |

## Managed servers

| Item | Status |
|---|---|
| Setting up the network boot service | **No user-facing entry point.** The production deployment adds it only when boot settings exist, and those are prepared outside the desktop today |
| Installation on a stock install | Gated behind host qualification flags. **Devices** says "Discovery is ready. Installation is not." until boot artifacts, the private CA path and directory enrollment are marked qualified |
| x86-64 UEFI PXE on the Spark's LAN | The only qualified layout |
| Legacy BIOS PXE | Not qualified |
| ARM64 managed servers | Not qualified |
| Routed or cross-VLAN PXE | Not qualified |
| Secure Boot | Reported, never changed. Qualified only with Secure Boot **off** |
| NIC firmware | The boot bundle carries Realtek `rtl_nic` firmware. Other NICs may need firmware added |
| Operating systems other than Debian 13 amd64 | Not implemented |

> [!CAUTION]
> When installation is enabled, approving a machine erases every non-removable, writable disk in it, not only the disk you chose for Debian. Read [Nodes](/docs/architecture/nodes/) first.

## Network and DNS

| Item | Status |
|---|---|
| Gateways other than UniFi Network | **Not supported.** Lucia relies on UniFi for DHCP, network boot options and reservations |
| UniFi Network Boot (DHCP options 66/67) | **Manual.** Lucia never changes DHCP boot options, networks or firewall rules |
| DNS servers in UniFi's DHCP settings | **Manual.** Lucia doesn't change UniFi's DHCP settings, so you point clients at your AdGuard addresses yourself |
| DNS providers other than Cloudflare | Not implemented. Without Cloudflare you stay on the private CA |
| More than one domain | Not implemented. One domain profile per install. The portal has no way to remove or replace an active one |
| AdGuard failback | Not implemented. Failover moves the primary role automatically. Moving it back is up to you |
| Revocation (CRL or OCSP) on the private CA | Not implemented. Clients that insist on revocation checks, such as curl with Schannel on Windows, may reject its certificates |

## Local AI

| Item | Status |
|---|---|
| The bundled chat model | Selected and pinned, but "exact-file TensorSharp and DGX Spark qualification is pending" |
| A bundled embedding model | None selected. The embedding slot stays empty until you load one |
| A voice pipeline on the Spark | **Not implemented.** At least 8 GiB of memory (`VoiceReserveGiB`) is reserved for it anyway. The Voice catalog app for Home Assistant runs on managed servers and is unrelated |
| Model memory estimates | Estimates, not proofs. `WeightMemoryMultiplier` is a calibration knob, not a measured constant |

## The assistant and jobs

| Item | Status |
|---|---|
| "Allow for this chat" grants | Kept in memory only. They end with the chat or a host restart |
| Copilot models | Need a GitHub account with Copilot. LiteLLM and Local AI models don't |
| Spark GitHub runner token | Stored in plain text in a private file the runner container reads. Every other credential is encrypted at rest |

## Observability

| Item | Status |
|---|---|
| Spark health history | In memory only: one hour of 10-second samples. A host restart starts it over |
| Per-node metrics on Devices | In memory only: one hour |
| Alert rules | None are provisioned. Grafana, Prometheus and Loki come with dashboards and data, not alerts |
| Long-term telemetry | Only with the optional Observability app installed |

## Spark updates

| Item | Status |
|---|---|
| Automatic security updates | Lucia doesn't install or enable `unattended-upgrades`. It reports whether Ubuntu's own is on |
| Background checks | Lucia doesn't run `apt-get update` on a timer. It checks when you ask, and rescans when the package database changes |
| Package removals during updates | Refused by design |
| Recurring maintenance windows | Not implemented. You schedule one window at a time |

## What isn't on this list

Anything the code doesn't do and nobody has asked for. If you're about to rely on something that isn't mentioned in these docs, check the code before you build your lab around it.
