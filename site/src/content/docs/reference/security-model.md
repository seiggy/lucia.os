---
title: Security model
description: Lucia's trust boundaries, what the web host can and cannot do, where secrets live, and which actions always need a person.
section: reference
order: 2
---

Lucia runs your lab's DNS, sign-in, certificates and servers, so a compromised portal could do real damage. The design goal is to make the web host a strong asker and a weak doer. It can ask for a lot of things, but the parts that actually touch the Spark, the directory and the disks decide for themselves what they'll do.

This page walks the boundaries from the outside in. [Architecture overview](/docs/architecture/overview/) has the component diagram.

## Identities: people and machines

Lucia keeps human and machine identities separate. Neither can stand in for the other.

| Identity | What it is | What it can do |
|---|---|---|
| A person | An OpenLDAP account. Sign-in goes through Authentik | Portal access (owners), SSO into apps that support it, SSH and `sudo` on managed servers for `lucia-owners` members |
| The owner API key | `HostPlatform:ApiKey`, a bearer key of 32+ characters | Owner role on the host API. Used for automation, not people |
| Inference API keys | Created under **Local AI → API keys** | The `/v1` model API only |
| A node agent | A key pair generated on the node, plus a private-CA certificate valid for about a day | Its own heartbeats, desired state and approved actions. Each request carries a signature over a fresh server challenge |
| `lucia-recovery` | A local account on each server | SSH with the single recovery public key chosen at approval. Its password is locked |

Bearer keys are compared as SHA-256 hashes in constant time, and the owner and inference keys must differ. Managed inference keys are stored only as hashes and shown once, when created. Lose one, make another.

## The web host's limits

The host (`lucia-host`) is an ordinary container on the Spark. It gets:

- a few `/data` subdirectories and the model directory;
- individual read-only host counter files from `/proc`, for health sampling;
- the private root CA's **public** certificate.

It doesn't get the Docker socket, and no Lucia container does. (The GitHub runner app runs its own Docker-in-Docker daemon in its own container, which is not the host's daemon.) It holds no Authentik admin token. Authentik's admin credentials are held by the Authentik container, the installer and the `lucia-domain-activation` worker.

### Privileged work goes through file queues

Things that must touch the Spark run as separate services outside the container. The host writes a small JSON request with an exact schema. The service validates it and decides what to do.

| Service | Runs as | What the host can ask for |
|---|---|---|
| `lucia-domain-activation` | Your user (systemd `--user`) | Activating a domain, people changes, app SSO clients. It alone holds Authentik admin credentials |
| `lucia-spark-model` | Your user | Start or stop the pinned TensorFold model. The recipe and settings belong to the worker |
| Spark GitHub runner | Your user | Repositories, labels, idle time and the token. It can never choose what runs on the Spark |
| `lucia-package-updates` | root | One of six fixed actions: `check`, `install`, `changelog`, `restart-services`, `restart-spark`, `repair`. Package names are validated. It never runs a command it was handed and refuses package removals |

So a compromised portal could queue an apt upgrade or restart the Spark. It couldn't run arbitrary commands as root on the Spark through these paths.

> [!NOTE]
> Managed servers are a different story, by design. The assistant's `run_command` tool and some node actions run as root on a managed server through its agent. These sit behind the approval model described below, and they're capped at a 16 KB script, 30 minutes, 4 concurrent commands and the last 32 KB of output.

## Network boundaries

- **Browser to gateway.** Traefik terminates TLS with a private-CA certificate, or with Let's Encrypt once a domain is connected. Sign-in is OIDC through Authentik.
- **Gateway to host.** The host trusts forwarded headers only from `HostAuthentication:TrustedProxyNetworks`.
- **Published ports.** Internal services, including PostgreSQL, Authentik's HTTP port and plain LDAP, aren't published. LDAP is reachable only as LDAPS through the gateway.
- **Host to nodes.** HTTPS verified against the private CA, plus per-request node proofs. The node agent makes outbound calls to the controller and doesn't run its own HTTP listener.
- **TensorFold.** It has no authentication of its own, so the worker binds it to the Docker bridge shared with the host, never to the LAN. Clients reach it only through Lucia's authenticated `/v1`.
- **Lab to internet.** Nothing is exposed by default. Domains use DNS-01 and never publish private A/AAAA records. Optional public access for an app goes through Cloudflare's proxy. See [Networking and DNS](/docs/architecture/networking-and-dns/).

## Certificates

Each install has its own Smallstep private CA. Its private keys stay on the Spark and are never copied to nodes, desktops or containers other than the CA itself. You distribute only the public root certificate.

> [!NOTE]
> The private CA publishes no CRL or OCSP endpoint. Short node certificate lifetimes limit how long a revoked node stays valid. They're not a substitute for revocation, so **Remove from Lucia** also revokes the agent's access on the controller side.

## Secrets at rest

Credentials you give Lucia are encrypted with ASP.NET Core Data Protection, using a separate purpose for each kind. The key ring lives in `/data/data-protection`.

| Secret | Entered on |
|---|---|
| Hugging Face token | Local AI → Models |
| Cloudflare API token | Settings → Domains |
| AdGuard credentials | Settings → AdGuard |
| UniFi API key | Settings → UniFi Network |
| SMB/NAS credentials | Settings → Storage |
| Registry sign-ins | Settings → Registries |
| Backup repository password | Apps → Backups |
| App environment secrets | Apps (including values entered through `request_secret`) |
| The assistant's GitHub sign-in | The assistant panel (GitHub device flow) |
| The assistant's LiteLLM virtual key | Minted automatically per owner from the LiteLLM app |
| The Web Push (VAPID) signing key | Generated automatically |

Encrypted doesn't mean the secret never leaves. Some have to be handed to the thing that uses them:

- SMB credentials sit in root-only credential files under `/etc/lucia/nas` on each server that mounts the share.
- Registry sign-ins go into each managed server's Docker configuration.
- The Spark GitHub runner's personal access token is written in plain text to a private `.env` file the runner container reads.

> [!IMPORTANT]
> Back up `/data/data-protection` together with the rest of `/data`. Without the key ring, every encrypted credential above is unreadable, and you'll have to enter them all again.

Portal pages show stored secrets as hidden or redacted. The assistant is told never to ask for, guess or show them. `request_secret` has you type a value straight into the app's environment, so the model never sees it.

## The assistant's approval boundary

The assistant's tool calls go through a policy engine whose default is **deny**. Rules are evaluated by priority:

1. **Plan mode** denies every change, destructive and secret tool. Reads still run.
2. In a **scheduled job**, destructive tools that you pre-approved for that job are allowed.
3. Otherwise, **destructive tools always ask**: deleting, restoring, publishing to the internet, server updates and restarts, and `run_command`. No setting turns this off in chat.
4. **Secret** requests are allowed, because they only ask you to type something.
5. **Reads** run.
6. **Web fetches** run for allowed hosts and ask for anything else.
7. **Changes** run automatically when you allowed that tool, and ask otherwise.

An approval request waits up to 30 minutes, and a turn is capped at 50 tool calls. "Allow for this chat" grants live only in memory, so they end with the chat or a restart. Every decision is logged and counted. See [Assistant and jobs](/docs/architecture/assistant-and-jobs/).

## Notifications

Web Push messages are encrypted end to end (RFC 8291) and VAPID-signed, but they still travel through your browser vendor's push service. Each one carries a title of up to 120 characters, a body of up to 1,500 characters and a link back to the portal. If that's more than you want leaving the lab, don't register devices under **Settings → Notifications**.

## Disk erasure

Nothing installs without an explicit approval on the Devices page. You give a hostname, the disk to put Debian on, a recovery key, and type `ERASE`.

> [!CAUTION]
> Approval erases **every** non-removable, writable disk in the machine, not only the one you pick for Debian. See [Nodes](/docs/architecture/nodes/) before approving anything with data on it.
