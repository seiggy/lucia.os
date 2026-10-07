---
title: Architecture overview
description: The components that make up a Lucia lab, where each one runs, and where the trust boundaries sit.
section: architecture
order: 1
---

Lucia is one controller and some servers. The controller is a DGX Spark running a set of Docker containers deployed by Aspire to Docker Compose. The servers are x86-64 machines Lucia installs with Debian 13 and then manages through a small node agent. Everything else is plumbing between those two, and this page is the map.

## The picture

```text
                       Your browser / phone (portal, web push)
                                      |
                                 HTTPS :443
                                      |
+----------------------------- DGX Spark (controller) -----------------------------+
|                                                                                   |
|  traefik gateway  :443 -> Lucia host     :9443 Authentik   :636 LDAPS  :9444 CA   |
|        |                                                                          |
|  +-----v--------------------+     +------------------ identity ----------------+  |
|  | lucia-host (container)   |     | Authentik server + worker (OIDC)          |  |
|  |  - ASP.NET Core API      |<--->| OpenLDAP (users, groups, sudo)            |  |
|  |  - React portal          |OIDC | PostgreSQL (Authentik)                    |  |
|  |  - TensorSharp inference |     | step-ca private CA  + cert renewer        |  |
|  |  - SRE assistant + jobs  |     +--------------------------------------------+  |
|  |  - fleet controllers     |                                                     |
|  |  NO Docker socket        |     telemetry relay (OTel), node + GPU exporters    |
|  +-----^-----------^--------+     boot service (TFTP/HTTP, optional)              |
|        |           |                                                              |
|  file queues   /proc (read-only)                                                  |
|        |                                                                          |
|  user services: domain activation, node enrollment, Spark model, Spark runner     |
|  root service:  lucia-package-updates (fixed apt actions only)                    |
+--------|--------------------------------------------------------------------------+
         | HTTPS + challenge-signed proofs (enrollment certificate), heartbeat every 30 s
         v
+-- managed server (Debian 13) --+   +-- managed server --+      UniFi gateway (DHCP,
|  lucia node agent (root)       |   |  node agent        |      Network Boot 66/67)
|  Docker apps / stacks          |   |  apps, AdGuard     |      AdGuard Home (DNS)
|  AdGuard Home + its own VIP    |   +--------------------+      Cloudflare (DNS-01,
|  OpenLDAP-backed SSH + sudo    |                                optional ingress)
+--------------------------------+
```

## The controller

The Spark runs the production topology defined in `src\Lucia.Homelab.Identity.AppHost`. Aspire publishes it to Docker Compose.

| Container | Role |
|---|---|
| `lucia-host` | The Lucia server: REST API, the React portal, in-process TensorSharp inference, the assistant, and every background controller (apps, AdGuard, DNS, DHCP, telemetry) |
| Authentik server and worker | Sign-in for the portal and apps (OIDC). Version 2026.8.3 |
| OpenLDAP | The directory: users, groups, and the source of SSH and `sudo` on managed servers |
| PostgreSQL | Authentik's database. Not published to the network |
| step-ca 0.30.2 | The private certificate authority |
| Renewer | Renews the gateway and service certificates |
| Traefik v3.6 | The single TLS gateway: 443 for Lucia, 9443 Authentik, 636 LDAPS, 9444 CA, plus a separate public entrypoint used only when public access is on |
| Telemetry relay | OpenTelemetry Collector 0.161.0 receiving OTLP on 127.0.0.1, plus node and GPU exporters |
| Boot | Optional TFTP and HTTP service for network-installing servers. Added only when boot settings exist |

The host container gets a handful of `/data` subdirectories, the model directory and some read-only `/proc` files for health sampling. It doesn't get the Docker socket, and neither does any other container. Internal ports such as PostgreSQL 5432, Authentik 9000 and plain LDAP 389 aren't published.

### Things that need more privilege

Some jobs need to touch the Spark itself, for example restarting the stack after a domain change or installing apt packages. These run **outside** the web host as narrowly scoped services that read requests from a file queue:

- **User services** (systemd `--user`, so lingering is needed): domain activation and people changes, node enrollment, the optional TensorFold Spark model, and the on-demand Spark GitHub runner.
- **One root service**: `lucia-package-updates` runs a fixed list of apt actions and never executes a command it was handed.

The web host asks and the service decides. If the portal were compromised, the attacker could queue an apt upgrade but not run `rm -rf`.

## Identity

Every person is an OpenLDAP account. Authentik handles sign-in to the portal and to apps that support OIDC, such as Grafana, Immich and LiteLLM. The same LDAP account provides SSH and `sudo` on managed servers for members of `lucia-owners`.

Machines don't use these accounts. A node agent proves who it is with a short-lived certificate from the private CA, plus a signature over a fresh server challenge, made with a key that never leaves the node. See [Identity](/docs/architecture/identity/).

## Managed servers

Servers are discovered by network boot, approved by you (with an explicit erase confirmation) and installed with Debian 13. They're then enrolled with a node agent that:

- reports every 30 seconds;
- applies desired state: apps, SSH keys, mounts and addresses;
- runs node actions you or the assistant approved.

See [Nodes](/docs/architecture/nodes/) and [Apps](/docs/architecture/apps/).

## Networking

Lucia assumes a UniFi gateway stays in charge of DHCP. It adds address reservations through the UniFi API, and nothing else. AdGuard Home serves local names. With a Cloudflare domain connected, Lucia gets real certificates by DNS-01 and never publishes private addresses. AdGuard itself can run on every managed server with failover. See [Networking and DNS](/docs/architecture/networking-and-dns/) and [AdGuard HA](/docs/architecture/adguard-ha/).

## Local AI and the assistant

The host runs one chat model and one embedding model in-process through TensorSharp on the GB10 GPU. It serves them through an OpenAI-compatible `/v1` API guarded by API keys. An optional TensorFold model can take over the Spark for a much bigger model.

The SRE assistant is a tool-using agent on the GitHub Copilot runtime. It can use Copilot, LiteLLM or local models, and an explicit policy decides which tools ask first. Scheduled jobs reuse that same policy. See [Local AI](/docs/architecture/local-ai/) and [Assistant and jobs](/docs/architecture/assistant-and-jobs/).

## Telemetry

The host samples Spark health every 10 seconds and keeps one hour in memory. While the Observability app is installed, Lucia runs an OpenTelemetry relay on each machine, listening on loopback. The relays scrape hardware exporters and forward everything to Prometheus, Loki, Tempo and Grafana. Without that app, no telemetry is shipped anywhere. See [Observability](/docs/architecture/observability/).

## Trust boundaries in one list

1. **Browser to gateway**: TLS from the private CA, or from Let's Encrypt once a domain is connected. Sign-in is through Authentik.
2. **Gateway to host**: the host trusts forwarded headers only from configured proxy networks.
3. **Host to Spark OS**: no Docker socket. Privileged work goes through file queues to services with fixed action lists.
4. **Host to nodes**: HTTPS verified against the private CA. Each node request carries a challenge-signed proof bound to that node's enrollment certificate, which lasts about a day. Disk erasure and the actions that always ask need an owner's approval.
5. **Assistant to everything**: default-deny tool policy. Risky tools ask a person unless a scheduled job was explicitly granted them.
6. **Lab to internet**: nothing is exposed by default. Optional public access goes through Cloudflare's proxy to a separate gateway entrypoint.

[Security model](/docs/reference/security-model/) goes through each boundary in detail.
