Lucia is your home lab manager. Plug in a DGX Spark to your network, run the desktop application, and off you go. A full management dashboard that will run and control your homelab for you. Want to install an application, setup a new server, run pen tests, etc. All from a cloud-like interface in your browser. We use an opinionated collection of Open Source software, our own software, and the proprietary desktop app for those who don't know how to run command prompts or don't feel safe doing so. It's as simple as it gets.


Features:
- PXE Host. Plugin a box with PXE support, turn it on, and Lucia will take control as the manager. It will install the base host OS image according to the device's architecture, and then bring it into the fold, managing the storage, applications, and capabilities within your homelab. Works with any hardware that DebianOS can be installed to with PXE support.
- OAuth2 authority using Authentik
- LDAP for managing accounts on all your devices that have been onboarded
- Certificate management: a persistent private CA without a DNS provider, or Let's Encrypt DNS-01 with a supported provider.
- services URL management
- OpenAI compatible endpoint for apps such as OpenClaw or Hermes
- Model manager
- Full voice pipeline with Wake word, Diarization, STT, TTS, and voice print authentication
- Task and memory solution
- Recommended hardware and software blueprints
- Open Blueprint community to share blueprints for
- Request a blueprint capability.
- manage and self-heal the DGX Spark that Lucia "Owns" as it's home-brain.
- easy "voice assistant" - listen and take notes at the push of a button from any device with a mic. Windows, Android, iOS, MacOS, Linux. Run the app and it opens a "annotation stream" to the agent that will use speaker diaraization, voice print, and transcribe any live conversation for later break down and retrieval. New voice? Records a clip for human identification at a later point, or live annotation enigne in the app allows you to identify the unknown speaker for the system to remember later.


This is an opinionated homelab solution, and an AI that helps to manage it all. Jealous of that smart tech worker who always has it together, knows more than you, and seems to have all the time in the world? Now you have your own version of that person at the click of a button.

Architecture:

Lucia Homelab is built on C# .NET 10 and TensorSharp. The Spark retains DGX OS;
new managed servers target Debian 13.7 (Trixie).

## Local DNS and public certificates

**Settings → AdGuard** connects an existing AdGuard Home instance. Then
**Settings → Domains** guides a Cloudflare account token, accessible domain,
local namespace, editable service URLs, and an explicit DNS/ACME review.
Cloudflare handles DNS-01 challenge TXT records; AdGuard provides exact local
service rewrites. Existing private CA and LDAP trust remain separate.

See the [domain onboarding guide](docs/domain-onboarding.md) for Cloudflare
permissions, AdGuard credentials, certificate coverage, the scoped activation
service, renewal, and qualification boundaries. No domain or DNS record changes
occur merely by saving a connection.

**Public access** (Settings → Domains) is optional. When it is on:
- Lucia adds `*.<zone>` to the certificate and moves Authentik to `auth.<zone>`. Lucia restarts, and everyone signs in again.
- Traefik gets a `public` entrypoint on port 8445 that only accepts Cloudflare's address ranges.
- Lucia keeps proxied Cloudflare A records at the network's WAN address, only touching records that carry its "Managed by Lucia" comment or that already point at that address.
- With UniFi connected, Lucia keeps the router's TCP 443 forward pointed at that entrypoint.

An app's web address becomes public when it gets a public name on the app's page, and its Authentik app then launches at the public URL. Services that Lucia doesn't run can be listed under **Other services on your network**, with a private IPv4 address, a port and an optional public name.

## Hardware onboarding contract

Hardware onboarding is being implemented; the existing host does not yet provide
a qualified unattended imaging path. The approved first target is x86-64 UEFI.
The initial dev server will have Secure Boot disabled by its owner; Lucia must
report that posture, not change firmware settings or flash accelerator firmware.

**UniFi Network is the only currently supported LAN-gateway platform.**
Users must complete the [manual Dream Machine / UniFi setup](docs/unifi-network-boot.md)
once; Lucia does not currently automate gateway settings. Enable **Network Boot**
(DHCP options 66/67), leaving the separate **TFTP Server** option disabled.
UniFi remains the DHCP authority; Lucia does not introduce another DHCP server. The first test uses
the Spark's existing `192.168.0.0/23` network. A dedicated provisioning VLAN is
preferred for subsequent installations because traditional PXE/TFTP does not
authenticate downloaded boot configuration or initrds.

An Owner opens a temporary **Add hardware** window. Admission is closed by
default and after a host restart; expiry or closing the window stops new
discoveries without cancelling already-approved installation jobs. Initial
discovery is read-only. A discovered machine is not a trusted managed identity,
and MAC addresses are not enrollment credentials.

Before installing, the owner identifies the server and explicitly selects a
stable disk identity and confirms its erasure. The installer must recheck that
identity and inventory revision before partitioning. Existing managed servers
must never be automatically reimaged because they network-boot again.

PXE loads the discovery/installer environment; Debian preseed performs the
approved OS installation; cloud-init applies first-boot CA trust, LDAP/SSSD,
and native Lucia node-agent enrollment. Machine keys are generated on the node.
Neither CA private keys nor LDAP administrator credentials belong in boot assets,
preseed files, or cloud-init data. Ongoing operations belong to the systemd node
agent and audited, explicitly authorized jobs, not arbitrary AI shell execution.

The initial implementation includes a durable Owner-only control plane, a native
read-only discovery client, a signed challenge/device-capability protocol, and
lease-gated boot-file serving. The dashboard reads actual discovery/task records
and refuses installation while its prerequisites are unqualified. This is not
yet a managed-node enrollment or OS-installation implementation; the first boot
bundle must remain blocked before partitioning.

The discovery runtime is included under `boot/node-agent-linux-x64` in the host
artifact. It is auxiliary boot content, not an x64 dependency of the ARM64 host.
The prepared Debian boot bundle contains public CA trust only. Network boot also
needs the controller hostname to resolve through the network's DNS service; the
first lab now has an AdGuard rewrite for `spark-9423` to `192.168.0.222`.

## Desktop bootstrap

`src\Lucia.Desktop` is the Avalonia/.NET 10 setup app for Windows, macOS, and
Linux. It starts **after NVIDIA's initial DGX OS setup**, with networking and
an SSH account available. It installs the private CA, LDAP, Authentik, and the
managed homelab host with its production dashboard and OIDC sign-in.

The guided flow connects to the Spark, asks you to confirm its SSH fingerprint
before sending credentials, checks readiness without changing the target,
shows the proposed changes, and collects your **Lucia owner username and
password**. Those credentials create a real LDAP account with administrator
membership and are verified through Authentik before setup can finish.
Existing installations use the enrolled owner for sign-in verification; their
accounts, passwords, CA, and deployment volumes are not replaced.
Reruns also check the actual host and Authentik application/provider/access
bindings, repairing missing owned registration without rotating client secrets.
The final action opens Lucia at its stable HTTPS origin. The current Spark uses
`https://spark-9423/`; its OpenAI-compatible base URL is `https://spark-9423/v1`.

SSH password and private-key authentication are supported. The SSH account
is the DGX OS account; the Lucia owner is a separate directory identity.
Credentials are held in memory on the desktop, sent only over fingerprint-
verified SSH, and omitted from arguments, preferences, progress, and logs.
Public SSH host pins are retained in the user's application data; a changed
key is rejected, not silently trusted.

The app carries an allowlisted, hash-checked source payload. The Spark runs
the existing Aspire identity AppHost from a stable user-local path, independent
of the desktop version or development checkout. Durable remote jobs let you
close the app or **Stop watching**, reconnect, and resume monitoring without
starting a second installation. Stopping monitoring does not cancel or undo
work already running on the Spark. Failed or interrupted jobs remain visible.
Legacy installations created from a development checkout are verified in
place rather than redeployed under a new Compose volume namespace.

The managed host target needs Ubuntu Linux ARM64, Python 3.11 or newer, OpenSSL,
accessible Docker/Compose, and NVIDIA CDI (`nvidia.com/gpu=all`). Missing or inaccessible Docker is a blocking
preflight result: this increment does not replace NVIDIA's Docker installation,
change Docker group membership, or repair the OS. With your approval, missing
user-local .NET/Aspire tools can be installed from pinned, verified packages.
No SDK, Python, or Docker installation is needed on the desktop running a
self-contained build.

Local certificate trust is a **separate, explicit approval** after successful
setup. The app validates the public CA fingerprint obtained over SSH before
export/import. Windows uses the current-user Root store; macOS uses the login
keychain; supported Linux system stores require administrator approval.
Unsupported stores or declined elevation do not become success. Applications
with separate trust stores may still need a manual import. No private CA keys
are downloaded, and certificate warnings must not be bypassed.

### Develop and publish the desktop

Only the .NET 10 SDK is needed to build the desktop UI projects. Building a
complete deployable package additionally requires Node/npm, Python, and the
TensorSharp package feed described below for the bundled Linux ARM64 host:

```powershell
dotnet run --project src\Lucia.Desktop
dotnet run --project tests\Lucia.Desktop.Checks -- --payload .
dotnet run --project tests\Lucia.Desktop.UiChecks
python tools\desktop\check.py
python tools\identity\check.py
```

For an explicitly opted-in transport diagnostic, the core checks also accept
`--upload-probe <host> <ssh-user> <private-key-file> <payload-directory>`.
It requires an already-approved SSH host pin, uploads the real payload with
verified `0700` directory/`0600` file permissions, and checks JSON handoff over
SSH stdin. It does not start provisioning or install certificate trust.
The printed staging archive and its empty parent directory can be removed
after checking the printed SHA-256; ordinary checks never contact the Spark.

Publish a self-contained development build:

```powershell
pwsh -File tools\desktop\publish.ps1 -Runtime win-x64
```

Other targets are `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and
`osx-arm64`. Output is under `artifacts\desktop\<runtime>`; macOS gets a
`Lucia.app` wrapper. Keep the complete output folder, including
`BootstrapPayload` and `HostPayload`, together. The host archive includes its
runtime and dashboard, not model weights or Hugging Face credentials. A selected
existing model directory can be reused only after its old host is stopped;
managed hosts hold an exclusive catalog lease. These are **unsigned development builds**;
platform signing, notarization, installer distribution, and native macOS/Linux
qualification are separate release work. Build final distributables on their
target OS to preserve native executable permissions.

Windows native SSH/preflight interaction and the headless form/state checks
have been exercised. Cross-compilation is not a claim of native testing on
every target. A fresh Spark installation and reconnect/resume should be
qualified using the desktop against a disposable identity environment before
release; do not erase an existing lab to manufacture a clean test.

## Base host

The ASP.NET Core server now hosts TensorSharp inference in-process, the Lucia
assistant (Microsoft Agent Framework over the GitHub Copilot runtime), and
persistent Hugging Face model downloads. The
dashboard uses Authentik sign-in and same-origin authenticated API calls.

### Dashboard foundation

The portal uses a compact **workspace switcher**: Overview, Your lab, Local AI,
and Settings. Each workspace exposes its available tools in contextual navigation.
Local AI's **Playground**, **Models**, and **API keys** are siblings, reachable
without scrolling through a conversation. **Find a tool** (Ctrl/Cmd+K) searches
the role-appropriate navigation directory; it does not search private data or
perform actions. Phones show the same labeled directory as a full-height sheet.

Models opens on the installed library; **Find & download** is a separate view.
API keys opens on existing application keys, with an explicit **New API key**
action. Playground drafts and conversation state remain in memory across portal
navigation, but are cleared by browser refresh, sign-out, or a lost authenticated
session. This is not persistent chat history.

Home reads actual hosted-model availability and provides a direct Local AI
entry point. Devices and Tasks read durable hardware-onboarding state: an
Owner-controlled discovery window, reported inventory, explicit disk approval,
and recorded installation observations. Unconfigured discovery and unqualified
installation remain visibly disabled. There are no invented devices, incidents,
activity rows, or repair timers, and this is not a general-purpose task runner.
Tasks shows installations that are running or need attention; finished ones sit
behind Show finished. A managed server's device entry shows its Debian updates,
with Check now, Install updates, a confirmed Restart and Update agent; Remove
from Lucia forgets a retired machine without touching it (see
`src/Lucia.NodeAgent/README.md`).
Retired demo detail routes no longer display fixtures. Settings retains
light/dark/system appearance, three themes, and a custom accent. Existing browser
appearance choices are preserved.

`#/ai` shows the real model, context, backend, API address, and streaming
conversation with Stop/New chat and response settings. It calls `/v1` directly
using the browser's secure session and CSRF token; no shared API key is embedded.

### Connect applications and manage models

Owners can open **Local AI → Create or manage API keys**. Create a named key for
each client, copy its secret once, and use it with the displayed OpenAI base URL.
Generated keys are inference-only: they cannot administer the host, models,
provider credentials, or other keys. Keys have **no expiry by default**; an
optional expiry date is supported. Revocation affects subsequent requests, not
an inference request already running. Only hashes are persisted in the private
host volume; the dashboard does not save secrets in browser storage. Existing
installation-managed keys remain separate and are not displayed or rotated.

**Local AI → Manage models** provides the local library, Hugging Face search and
repository browsing, GGUF quantization/file selection, queued downloads,
cancel/retry, context estimates, load/unload, and confirmed local deletion.
Only one chat LLM and one embedding model are resident at a time. Replacing a
slot unloads its previous model before loading the replacement; the other slot
is retained. Successful selections and served context survive host restart.
An explicitly unloaded slot stays unloaded. A failed replacement can leave the
current slot empty; the last successfully saved selection remains the startup
choice.

The model-manager connection section accepts a Hugging Face access token.
Lucia validates it with Hugging Face and stores it encrypted using the host's
persistent Data Protection keys. The token is never returned by status APIs,
placed in URLs or CLI arguments, or saved in browser storage. Removal records
an explicit disconnected state so old environment/CLI credentials do not
silently reactivate. New downloads use the current credential; already-running
downloads retain the credential they started with. Prefer read/fine-grained
tokens. Account authentication can provide higher limits, but does not guarantee
download speed or grant access to gated repositories.

Repository choices are pinned to a commit and include every shard of a selected
GGUF. Quantization labels inferred from filenames are identified as such.
Unqualified models require an explicit opt-in; downloading a file neither loads
it nor proves TensorSharp compatibility. Context estimates retain the OS,
services, runtime, and at least 8 GiB voice reservations, and account for the
opposite loaded slot. Lower weight quantization can leave more memory for
context, at a potential quality cost; it does not change the model's native
context ceiling or guarantee allocation success.

For an installed LLM, **Context to serve** is a keyboard/touch-accessible slider
from **8,192 tokens** to the calculated memory-limited context capacity, in
256-token steps. The chosen token count and both limits are visible. If the
calculated capacity is below 8K, the dashboard explains the limit and disables
loading rather than offering an invalid range. Embedding context behavior is
unchanged; the host still rechecks memory admission when loading.

Before downloading, the selected quantization's preview reads at most 16 MiB of
its first GGUF shard and applies the same conservative KV-cache arithmetic as
local inspection. It does not download model weights or infer capacity from a
filename alone. Unsupported/incomplete headers return an explicit unavailable
estimate; embedding allocation still requires full local inspection. Preview
requests are pinned to the chosen commit and bounded in size/time, with
Hugging Face authorization stripped from CDN redirects. Loading always repeats
full format and memory checks against the actual downloaded file.

### Spark health

Owners see current Spark health on Home: CPU utilization and load, available
system memory, GPU utilization/temperature/power, host-volume capacity, network
rates on the host's default-route interface, and uptime. GB10 uses unified
CPU/GPU memory; unsupported discrete-VRAM figures are not represented as zero.

The host samples every 10 seconds and keeps **only a rolling hour in memory**
(at most 361 samples). There is no metrics database or new monitoring stack.
History clears when the host restarts; missing samples remain gaps. The Owner-only
`GET /api/host/telemetry` endpoint returns current status and this bounded history
with `Cache-Control: no-store`.

Only individual read-only host counter files are mounted into the container.
Network counters use `/proc/1/net/dev` and `/proc/1/net/route`; ordinary
`/proc/net` mounts would incorrectly report the container namespace. No host
process tree, root filesystem, Docker socket, or extra capabilities are exposed.
GPU readings use the existing NVIDIA CDI-provided `nvidia-smi`, with bounded
execution and output. Storage describes the filesystem hosting Lucia's `/data`,
not SMART health or the sizes of individual folders.

Health reports stale data after 30 seconds, identifies missing readings, and
flags less than 5% available host-volume capacity or 3% available system memory.
High CPU/GPU utilization alone is not a fault. These are host telemetry checks,
not a claim that every application, disk controller, or identity service is healthy.

### Telemetry relays

While the Observability app is installed and running, every managed server and the
Spark run a **telemetry relay**: an OpenTelemetry collector plus node-exporter (and
nvidia_gpu_exporter where there is an NVIDIA GPU), all on the host network and bound
to loopback. Apps and the node agent send OTLP to `127.0.0.1:14317` (gRPC) or
`127.0.0.1:14318` (HTTP); the relay adds `host.name`/`lucia.node`, scrapes hardware
and Local AI's engine (vLLM, or llama.cpp per loaded model), and forwards everything
to the Observability app. Servers get it as the reserved system app `telemetry-relay`;
the Spark's relay runs beside the controller and posts through
`/api/host/telemetry/relay/v1/{signal}` with a token only the relay can read. The
controller exports its own traces, metrics and logs the same way. Grafana opens on the
provisioned **Hardware** dashboard for every machine. llama.cpp has no OTLP support,
so only its Prometheus metrics are collected.

**Inference metrics.** The **Inference** dashboard puts every engine's raw performance
side by side: requests, input/output/KV-cache-reused tokens, time to first token,
prompt and generation tok/s, and total request time, per machine and model. Lucia
Inference (TensorSharp) reports each finished chat only as a log entry, so
`InferenceMetrics` turns that entry's numbers into `lucia.inference.*` metrics and
keeps the entry itself (prompt and answer) out of every other log. The controller
exports them over OTLP; node workers serve them at `/metrics` behind the inference key
for the relay to scrape. vLLM's and llama.cpp's own metrics fill the same panels.
llama.cpp keeps only running token and time totals, so it has no time to first token,
request time or request count.

**App sign-in.** Catalog apps that support SSO (Grafana, Immich and LiteLLM today) get an Authentik
client once a domain is active. The controller writes a request per app to
`data/domains/app-sso-requests/`; the Spark's `lucia-domain-activation` worker,
which alone holds Authentik admin credentials, reconciles an owner-only OIDC client
(`lucia-app-<stack>`, bound to `lucia-owners`, with up to four exact callbacks on the
app's address) and deletes clients no longer requested. After a matching success
response the controller adds `LUCIA_SSO_*` to the app's env and redeploys it. Grafana
signs owners in as Admin; its local admin form stays at `/login?disableAutoLogin=true`.

**Image updates.** Catalog images are pinned by tag and digest, so "Update images" only
re-pulls what's pinned. Every few hours the controller also lists each app image's tags
on its registry (anonymously, with a pull token when asked) and offers the newest tag
of the same shape, e.g. `v8.9.0-ls104` → `v9.1.0-ls108` but never a `-dev` or beta tag.
A change in the first number is offered separately as a major version. Databases
only get patch releases. Apps built for one server's hardware (Local AI) aren't
checked. `POST /api/host/stacks/{name}/upgrade-images` (`{"major": bool}`) rewrites a
custom app's compose; for a catalog app it records the new pins in the app's template
(`Images`), and the next catalog release that catches up drops them.

**Private images.** Owners add up to 16 registry sign-ins in **Settings → Registries**
(`#/settings/registries`): the registry (`docker.io`, `ghcr.io`, `registry.example.com:5000`),
a username and a token. Use a read-only token, such as a Docker Hub personal access token
with the Read-only scope. Lucia tries the sign-in before saving it and keeps the token
encrypted. It never shows the token again. Every managed node gets it for pulls, and the
update checks above use it too. Signing in to Docker Hub also lifts its anonymous pull
limit. A revoked token makes that registry's pulls fail, public images included, until
it's replaced or removed. API: `GET /api/host/registries`, `PUT /api/host/registries/{host}`
(`{"username","secret"}`; a null secret keeps the saved token), `DELETE /api/host/registries/{host}`.

**MusicBrainz mirror.** The catalog's MusicBrainz app runs the website and `/ws/2`
API over its own copy of the database, with Solr search. A new install imports the
latest data dump once (a few hours, about 100 GB); the website waits for it. With a
MetaBrainz access token (a `secret` setting: kept only in the app's encrypted env,
never in its manifest) it replicates every hour. Replication doesn't update search,
so the indexer rebuilds the indexes weekly while search keeps answering. Picard can
use the mirror directly; Lidarr reads metadata from its own API and needs a
separate metadata bridge to use it.

**Immich.** The catalog's Immich app keeps photos in a folder on a NAS share
(`/mnt/lucia/nas/<nas>/<share>/...`, which also pins it to servers with that share
mounted) or, left blank, on its server. Machine learning runs on the CPU or, when
chosen, an NVIDIA GPU. Backups stop the app so its database is consistent; photos on
a NAS share aren't part of the app's backup. Its `sso` service writes Lucia's client
into Immich's OAuth settings (which Immich reads on every sign-in); the phone app signs
in through the web address's `/api/oauth/mobile-redirect`. Immich finds existing users
by their Authentik user UUID, then by email.

**App telemetry.** Catalog apps that export OpenTelemetry get `LUCIA_OTLP_ENDPOINT`
and `LUCIA_OTLP_AUTHORIZATION` in their env while an Observability app is installed
(the controller re-checks every 15 seconds and redeploys the app when they change);
the lines are removed when Observability goes away.

**LiteLLM.** The catalog's LiteLLM app is an OpenAI-compatible gateway over the lab's
inference endpoints, with its own Postgres. Models are added in its UI. Owners sign
in through Authentik as proxy admins (their Authentik account needs an email), and
while that's on, the UI's master-key/password login is off; the master key,
`LITELLM_MASTER_KEY` in the app's env, still works for the API. Traces and metrics go to
Observability as service `litellm`.

**Plex.** The catalog's Plex app runs on the host network (port 32400) with up to two
NAS folders mounted at `/data` and `/media`. Transcoding is `cpu` or `nvidia` (NVENC,
which needs an NVIDIA node). A claim token from plex.tv/claim is only needed for a fresh
server; a restored config keeps its identity. Backups stop Plex, since its database is
SQLite.

**Media automation.** Sonarr, Radarr, Lidarr, Seerr, Jackett, NZBHydra 2 and
FlareSolverr are separate catalog apps, each a single container with a
`config` volume, a port and a web name. The apps that have folders mount a media folder
at `/data` and a downloads folder at `/downloads`. "Other media paths" mounts the media
folder again at the paths a moved app's library already uses, such as `/tv`, so its
database needs no path rewrites. Images that take PUID/PGID run as 1000:1000. The others
run as that user directly, and a one-off `init` container gives them their config folder.
The **Download client** app runs qBittorrent, Transmission, NZBGet or Soulseek behind
gluetun, with the client in gluetun's network namespace. It needs an OpenVPN username and
password, which are kept in the app's env, and can turn on port forwarding (PIA and
ProtonVPN). "Local networks" lists the LAN ranges the client may reach outside the
tunnel. Install one copy per client.

**Home Assistant.** Home Assistant runs on the host network (port 8123) with a `config`
volume. Backups stop it, since its history database is SQLite. A fresh install gets Home
Assistant's default configuration, set to trust `X-Forwarded-For` from private networks so
its web name works through Lucia's gateway. An existing configuration is left alone.
Companion apps: **Mosquitto** (MQTT with one login, rewritten on every start; the password
is kept in the app's env), **Voice** (Wyoming Whisper on 10300, Piper on 10200 and
openWakeWord on 10400, with models skipped by backups), **ESPHome** (6052), **Matter
Server** (5580, no web page) and **Music Assistant** (8095), all on the host network so
they can find devices, and **Node-RED** (1880), which needs
`node-red-contrib-home-assistant-websocket` from its palette and a Home Assistant token.

**GitHub Actions runner.** The runner app registers one ephemeral runner per repository or
organization, labelled `self-hosted`, `linux`, the architecture and the server's name, each
with its own Docker-in-Docker daemon, reached over a socket volume. It needs a fine-grained
token with Administration: read and write on the repositories (Self-hosted runners for an
organization). It can also run on the Spark on demand (`runs-on: [self-hosted, ARM64, spark]`):
the owner starts it from Apps, and `lucia-spark-runner.service` stops it after the idle
minutes pass with no job. Jobs queue on GitHub while it is stopped. The Docker daemon is
privileged, so a job can take over its host (on the Spark, that includes Lucia and the
identity stack): don't point it at public repositories that run fork pull requests. The token
is stored in plain text, readable only by its owner.

Managed OpenAI endpoints require `Owner` or `Inference` authorization.
Authentik access tokens are checked for issuer, audience, signature, expiry,
and `lucia_api` scope; ID tokens are not accepted as API access tokens.
Browser requests use the same identity through HttpOnly secure cookies, with
CSRF validation for unsafe methods. Separate opaque owner/inference API keys
remain supported for machine clients. Owner-only model administration and assistant
routes remain more privileged than inference. See
`src/Lucia.Homelab.Server/AUTHENTICATION.md` for the exact contract and limits.

For the optional, development-only Vite bridge, configure a separate
`Parameters:inference-api-key`. Aspire passes it to the API and to Vite as the
non-public `LUCIA_PLAYGROUND_API_KEY` environment variable. It is never a
`VITE_*` variable, included in JavaScript bundles, or stored in browser storage.
The bridge forwards only model-list GETs and chat-completion POSTs, not owner
operations, and rejects cross-origin browser requests. Anyone who can reach
this development UI can use that limited inference bridge: keep it on a trusted
network. Managed production builds do not use it, and static builds do
not contain the proxy. Other API clients still require their own token.

```powershell
npm --prefix src\frontend test
npm --prefix src\frontend run build
```

### Dependencies and startup

- .NET 10 and Aspire 13.5.4.
- Docker for the existing Aspire Redis resource.
- Hugging Face CLI (`hf`), version 1.27 or newer.
- The complete local TensorSharp package set, including `TensorSharp.Server`,
  `TensorSharp.Models`, and `TensorSharp.Native.Cuda`, version
  `2.8.6-gb10.20260921.1`. Configure the directory containing those `.nupkg` files
  as a NuGet source. They are not the older public TensorSharp releases.
- The native package supports Windows x64 and Linux ARM64 GB10. Cross-publish
  with an explicit RID. The target still needs a compatible NVIDIA driver and
  the native libraries required by its OS; Debian 12 binary compatibility has
  not been established by these host changes.

The package feed is already configured on the development machines:

| Machine | Feed directory |
|---|---|
| Windows | `%USERPROFILE%\.nuget\local-feeds\tensorsharp-cuda` |
| Spark | `$HOME/.nuget/local-feeds/tensorsharp-cuda` |

The current concrete locations are under `C:\Users\zackw` and `/home/zackw`,
respectively. Keep these machine-local paths in NuGet's user configuration,
not in the project files. On a new machine, add its feed before restoring:

```powershell
dotnet nuget add source "$HOME\.nuget\local-feeds\tensorsharp-cuda" --name TensorSharp-Local-Cuda
dotnet restore src\Lucia.Homelab.Server\Lucia.Homelab.Server.csproj
dotnet user-secrets set "Parameters:host-api-key" "<owner-token-at-least-32-characters>" --project src\Lucia.Homelab.AppHost
aspire start --non-interactive
aspire wait server --non-interactive
```

On the Spark, register the feed with
`dotnet nuget add source "$HOME/.nuget/local-feeds/tensorsharp-cuda" --name TensorSharp-Local-Cuda`
if it is not already registered.

For a user-local Spark SDK installation, the dev shell also needs
`DOTNET_ROOT=$HOME/.dotnet`, `DOTNET_ROOT_ARM64=$HOME/.dotnet`, and
`$HOME/.dotnet`, `$HOME/.dotnet/tools`, and `$HOME/.local/bin` on `PATH`.

The HTTPS dashboard listener binds to all interfaces on port `17179` for LAN
development; dashboard token authentication remains enabled. Its HTTP listener,
OTLP, and resource-service endpoints remain loopback-only. Direct LAN HTTPS
access requires a trusted certificate covering the hostname/IP being used;
the default localhost development certificate does not cover the Spark's LAN
address. SSH port forwarding is an alternative that preserves localhost access.
The server's HTTP/HTTPS endpoints and Vite frontend also bind on all interfaces
for this LAN development workflow. Model administration and inference still
require authentication; Redis remains private. Do not expose these development
listeners directly to the internet.

An owner key is required. An optional, **different** `Parameters:inference-api-key`
grants access to `/v1` without model administration or the assistant. Optional
`Parameters:huggingface-token` is passed only to the server. Tokens are not put
in download arguments or model manifests. For standalone hosting, the equivalent
settings are `HostPlatform__ApiKey`, `HostPlatform__InferenceApiKey`, and
`HostPlatform__HuggingFaceToken`; `HF_TOKEN` is also honored by the download CLI.
Never embed owner credentials in browser code. Use HTTPS outside local testing.
Authentik/OAuth integration remains a separate release feature.
The intended user experience is GUI sign-in and GUI-driven operations, not
manual API calls or key management. LDAP will own accounts for managed devices;
Authentik will provide OAuth/OIDC for web applications and APIs. Static keys
are temporary development/bootstrap access until that integration is in place.

### Identity services on the Spark

The separate `Lucia.Homelab.Identity.AppHost` provisions Authentik, PostgreSQL,
authoritative OpenLDAP, a Smallstep private CA, a Traefik TLS gateway, and a
certificate-renewal sidecar as digest-pinned ARM64 Docker containers. It does not
stop or share deployment state with the development/LLM AppHost. No container
mounts the Docker socket. Containers restart automatically unless explicitly
stopped.

`tools/identity/provision.py` is the reusable installer entry point: Python's
standard library, line-delimited JSON progress, nonzero exit on failure, and
persistent state outside the checkout. The desktop's packaged
`tools/desktop/bootstrap.py` invokes it over SSH and consumes its verified
result. The direct CLI remains available for development and recovery.
On the Linux Spark, with Docker, Python 3, OpenSSL, .NET 10, and
Aspire 13.5.4 available:

```sh
cd /home/zackw/src/lucia-os
python3 tools/identity/provision.py prepare --host 192.168.0.222
python3 tools/identity/provision.py publish
python3 tools/identity/provision.py apply --owner-username zackw
python3 tools/identity/provision.py verify
```

`apply` includes preparation and publishing, so it can also perform first-time
setup with `--host`. `publish` only generates Compose artifacts; `verify` checks
the existing services without reconciling their configuration. Each mutating
operation preserves existing secrets, CA identity, LDAP records, and database
volumes. Concurrent runs are locked. Changed hosts/ports/certificate modes,
missing credentials, and a changed AppHost checkout path fail rather than
silently reset an installation. Aspire derives Compose project/volume names
from the AppHost path: keep that path stable during upgrades until an explicit
volume-preserving migration is implemented. Do not run `aspire destroy` on a
live identity installation; its teardown can remove volumes.

A complete installation requires an initial LDAP-backed owner, not just
healthy containers. On an existing installation, enroll it without redeploying:

```sh
python3 tools/identity/provision.py owner --owner-username zackw
python3 tools/identity/provision.py verify-owner-login
```

The installer creates the POSIX account under `ou=Users`, assigns unused
UID/GID values starting at 10000, and adds it to `ldap-admins` and
`lucia-owners`. It checks the synchronized user/group's LDAP source links and
entry UUIDs before granting the `lucia-owners` group Authentik administrator
access. Future membership in that group is therefore privileged. This does
not create or alter OS accounts on the Spark or other existing machines.
The owner is recorded in `owner.json`; reruns never adopt an existing
same-name account, recreate a missing identity, reset a changed password, or
silently undo an administrator's removal of owner privileges.

First enrollment verifies that an incorrect password is rejected, that the
real password completes Authentik's normal sign-in flow, and that its audit
event records the **LDAP** backend. It does not use the bootstrap token to
sign in as the owner. The temporary verification session is revoked afterward.
`verify-owner-login` repeats that sign-in check; after changing the initial
password, supply a protected local `--password-file` instead. It deliberately
does not bypass MFA or another newly required sign-in stage.

Default state is `$HOME/.local/share/lucia/identity` (`--state` overrides it).
Keep this directory and the Compose PostgreSQL/Authentik data and media volumes in
encrypted backups, including the CA keys and all secrets. Files uploaded in
Authentik's admin (app icons, brand logos, flow backgrounds) live in the shared
`lucia-authentik-media` volume. Authentik only accepts upload names made of
letters, numbers, dots, hyphens, underscores and slashes; use the Custom Name
field for files with spaces or parentheses. The state directory contains private
deployment environment files and must not be published or committed.
Direct CLI subprocess failures leave a sanitized `last-command-error.log`
there. Desktop jobs instead expose the failed operation/exit code and bounded
progress events without persisting raw subprocess output.

| Endpoint | Default | Exposure |
|---|---|---|
| Authentik | `https://192.168.0.222:9443` | LAN HTTPS |
| LDAP | `ldaps://192.168.0.222:636` | LAN LDAPS |
| Private CA | `https://192.168.0.222:9444` | LAN HTTPS, authenticated certificate issuance |
| PostgreSQL / Authentik backend / LDAP backend | 5432 / 9000 / 389 | Docker network only, not host-published |

Keep these services on the trusted management LAN; this does not configure a
host firewall or internet-facing access. TLS terminates at the gateway; LDAP
and Authentik backend traffic is plaintext only within the identity Docker
network. The authoritative directory base is `dc=lucia,dc=home,dc=arpa`,
independent of a future public DNS domain.

Authentik imports users from `ou=Users` with `posixAccount` and groups from
`ou=Groups` with `groupOfUniqueNames`, using stable LDAP entry UUIDs. It checks
passwords against LDAP with explicit CA verification. Password write-back is
on: when someone changes their password in Authentik (Settings → Change
password), Authentik writes it to LDAP through its `uid=authentik` service
account, which is a member of `cn=ldap-password-reset` for exactly that
purpose. The trade-off is that the Authentik service account can now set any
directory password; it already reads the whole directory, so this adds write
access to one attribute rather than a new trust boundary. No demo users are
created. The initial owner is a real LDAP-backed
human account; managed-device sign-in remains separate work.

Owners manage everyone else in **Settings → People** (`#/settings/people`):
add, edit, disable, enable and delete people, set or generate a temporary
password (shown once), create and delete groups, and choose which groups can
open each app that signs in with Lucia (App access). The web host holds no
identity credentials: it queues a private request file, and the Spark's
`lucia-domain-activation` worker applies it to LDAP and Authentik
(`tools/identity/people.py`), deletes the request, and republishes a
secret-free `directory.json` snapshot. `lucia-owners` is bound to every app;
Lucia's first owner can't be deleted, disabled or removed from it; and adding
anyone to `lucia-owners` requires an explicit confirmation because owners get
sudo on every server and Authentik admin rights. App-side roles (for example
Grafana Admin) still follow `lucia-owners` only.

The bootstrap account `akadmin` is Authentik-only
break-glass access, not a normal LDAP user. Its generated password is in
`secrets/authentik-admin-password` under the state directory; retrieve it
locally through an authorized SSH session, never through dashboard assets or
logs. For direct CLI enrollment without a supplied password, the owner's
generated initial password is separately stored in `secrets/owner-initial-password`,
readable only by the provisioning user. The desktop uses your chosen password
instead and does not require this retrieval step. For a CLI-created test owner,
retrieve the generated credential in your own terminal (not in agent logs):

```powershell
ssh zackw@192.168.0.222 "cat /home/zackw/.local/share/lucia/identity/secrets/owner-initial-password"
```

Sign in to Authentik as `zackw`, not as the LDAP root DN or `akadmin`. Change
this initial password in Authentik under Settings → Change password (written
back to LDAP), or from an authorized interactive SSH terminal on the Spark,
which prompts for the old and new passwords without putting them in command
arguments:

```sh
docker exec -it -e LDAPTLS_CACERT=/trust/lucia-root-ca.crt -e LDAPTLS_REQCERT=demand \
  lucia-identity-ldap ldappasswd -x -H ldaps://identity-gateway:8636 \
  -D 'uid=zackw,ou=Users,dc=lucia,dc=home,dc=arpa' -W -S
```

The stored initial credential is not updated after a password change; remove
it through your normal secure credential-handling process once handed over.
Other bootstrap credentials stay in the protected `secrets` directory.
Device login integration remains separate work.
The managed host's OIDC application and dashboard/API security are configured
by the desktop; the temporary inference bridge is only for development.

#### Private CA trust

Provisioning currently implements the **private-CA path only**, including
automatic service-certificate renewal and gateway reload. Certificate issuance
is staged and validated before publication; the gateway waits for a checksum
marker covering the complete certificate/key pair before opening its listeners.
Renewal and recovery preserve the service key, publish certificates atomically
under a shared lock, and only then request a gateway reload. Setup waits for a
CA-verified TLS handshake before binding to LDAP; authentication failures are
not blindly retried into an account lockout. Export only
`trust/lucia-root-ca.crt` and `trust/fingerprint.txt`, not the `ca` or `secrets`
directories. Compare the certificate's SHA-256 fingerprint with
`fingerprint.txt` through a trusted channel before installing it. This is the
DER certificate fingerprint, not the hash of the PEM file.

Provisioning never changes the Spark's or a client's OS trust store. Browsers
will not trust Authentik until the owner explicitly installs the public CA;
do not bypass certificate warnings.

For Debian-family node images or an explicitly approved managed node, copy
the public certificate and `trust/install-node-trust.sh` from the state
directory, then run as root:

```sh
sh install-node-trust.sh lucia-root-ca.crt <expected-sha256-certificate-fingerprint>
```

The hook validates the fingerprint and CA certificate, refuses to replace a
different Lucia root, and runs `update-ca-certificates`. It requires the
image's `openssl` and `ca-certificates` packages. PXE/image automation will call
this hook when that feature is built; it has not been installed on existing
devices.

On Windows, the owner can explicitly import into their current-user Root
store after checking the fingerprint (requires modern PowerShell):

```powershell
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path .\lucia-root-ca.crt).Path)
$certificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
# Compare with the trusted fingerprint before running the next command.
Import-Certificate -FilePath .\lucia-root-ca.crt -CertStoreLocation Cert:\CurrentUser\Root
```

Applications with separate trust stores may need their own import. Release
hardening must also cover revocation: this initial private CA uses
short-lived service certificates but does not publish CRL/OCSP endpoints.
Strict-revocation clients (including Windows Schannel curl) can therefore
reject it even with an explicit CA file; do not disable certificate checking
to work around that requirement.

The next certificate phase is an explicit migration to a domain and Let's Encrypt
DNS-01 provider (Cloudflare first), preserving directory/accounts/data rather
than rebuilding them. DNS credentials, public certificates, and that migration
are not configured yet.

Run the provisioning contract checks without Docker or credentials:

```powershell
python tools\identity\check.py
```

An opt-in Linux regression (`python3 tools/identity/check_tls.py`) exercises
cold startup with incomplete certificate files and atomic renewal using
isolated, disposable containers and the already-cached pinned images.
It publishes no host ports, mounts no Docker socket, uses only temporary
test certificates, and does not modify the live identity installation.

### Bundled model and catalog

The selected release model is:

| Field | Pinned value |
|---|---|
| Repository | `unsloth/Qwen3.6-35B-A3B-MTP-GGUF` |
| Revision | `5bc3e238d916f48a861bac2f8a1990a0e9b7e98d` |
| File | `Qwen3.6-35B-A3B-UD-Q6_K_XL.gguf` |
| Download size | 32,611,711,264 bytes: 32.61 GB / 30.37 GiB |
| SHA-256 | `35fce994cd36104a7dc1bd8a4bdf13778145664c00fdef6773aebc9246e5019c` |
| Architecture | `qwen35moe` |
| Declared context | 262,144 tokens |

The catalog records **qualification pending**, not a claim of completed Spark
testing. Before release, qualify this exact file, quantization, embedded
template, tool-call round trips, native package, and memory settings on the
Spark. The MTP weights are included in the GGUF; speculative decoding stays
disabled until separately qualified. Text-only operation does not need a vision
projector.

Normal downloads are limited to catalog entries. The explicit `pro: true`
option permits other Hugging Face repositories, but still validates paths,
GGUF contents, TensorSharp architecture support, tokenizer metadata, and memory
admission. Split GGUFs are supported by selecting the first shard; all numbered
shards are downloaded together. A `.gguf` suffix alone does not make a model
valid. XLM-R embeddings are supported through TensorSharp's BERT/XLM-R
sentence-encoder **GGUF** format, with supported tokenization and pooling.
No embedding model has been selected for bundling.
The initial resident embedding encoder uses TensorSharp's managed CPU backend;
chat uses the configured backend. Host status reports the encoder's backend explicitly.

To stage a release with the selected model, publish first, then run the bundle
command from a Windows x64 or Spark host with `hf` installed:

```powershell
dotnet publish src\Lucia.Homelab.Server\Lucia.Homelab.Server.csproj -c Release -r linux-arm64 --self-contained false -o artifacts\host-linux-arm64
dotnet run --project src\Lucia.Homelab.Server -- --bundle-model artifacts\host-linux-arm64
```

The bundle command downloads approximately 32.6 GB, verifies its size and
SHA-256, validates GGUF metadata, and stages the upstream Apache-2.0 license and
quantization provenance. Hugging Face cache/auth storage stays outside the
publish directory. It does **not** certify inference quality or Spark execution.
Run the published host with its publish directory as the working directory, or
set an absolute `HostPlatform__ModelDirectory`. On startup, an installed bundled
model is loaded automatically. Other selections can be restored using
`HostPlatform:ChatModelId` and `HostPlatform:EmbeddingModelId`.
If startup loading fails, `/api/host/status` reports the failure and the owner
API remains available for recovery; missing inference is never reported ready.

### Memory and context

`HostPlatform` configuration keeps separate admission reserves for the OS
(8 GiB default), other services such as Redis/Authentik (8 GiB), voice
(**at least 8 GiB**), and runtime/scratch/recurrent state (8 GiB). All except the
voice minimum can be tuned within their configuration bounds. These are
capacity-planning reserves, not OS-enforced memory partitions.

Context estimates account for resident weights, the other loaded model, and KV
storage. Qwen's hybrid attention and MTP layers are distinguished from ordinary
full-attention layers. The initial policy conservatively estimates two weight
copies and F32 K/V plus a host/device mirror. `WeightMemoryMultiplier` is a
calibration knob, not a measured constant. Unknown cache metadata is rejected
rather than assigned an invented capacity.

The default served context is **32,768 tokens**, even if the estimate permits
more. A load request can select a larger context within the estimate and the
artifact's declared limit. Limits are rounded down to TensorSharp's 256-token
block size. Inference/model swaps are serialized and speculative/prefix caching
is disabled for this initial memory policy. Revisit those ceilings after
measuring the target hardware.

On non-Spark GPU hosts, set `HostPlatform:MemoryBudgetGiB` explicitly: system RAM
must not be mistaken for discrete GPU VRAM. The memory/context endpoint returns
the assumptions and reservations alongside the estimate. A model that does not
fit is not loaded.

### Host API

In development, open `/scalar/v1` on the .NET server for the Scalar API explorer.
The server root redirects there, and the underlying document is
`/openapi/v1.json`. Routes are grouped into host/model management, the assistant, and
OpenAI-compatible inference, with request examples for TensorSharp's raw JSON
handlers. Authentication requirements are documented; no keys are prefilled or
stored persistently by the explorer. Browsing the reference does not require a
key, but executing protected operations still does. Scalar and OpenAPI routes
are not exposed in production.

Send `Authorization: Bearer <token>`. Owner credentials are required for
`/api/host`; inference credentials may access `/v1`.

| Endpoint | Purpose |
|---|---|
| `GET /api/host/status` | Loaded models and active memory/context plans |
| `GET /api/host/model-catalog` | Curated artifacts and their qualification status |
| `GET /api/host/models` | Persistent local inventory/download states |
| `POST /api/host/models/bundled/download` | Queue the pinned model download |
| `POST /api/host/models/download` | Catalog or explicit Pro download |
| `GET /api/host/models/{id}` | Poll a download; errors remain visible |
| `POST /api/host/models/{id}/cancel` | Stop a download while retaining resumable files |
| `POST /api/host/models/{id}/retry` | Resume failed, canceled, or interrupted downloads |
| `GET /api/host/models/{id}/context` | Inspect GGUF and estimate usable context |
| `POST /api/host/models/{id}/load` | Load a model; body `{}` or `{"contextTokens":32768}` |
| `POST /api/host/models/{Chat\|Embedding}/unload` | Release a resident model |
| `DELETE /api/host/models/{id}` | Delete an unloaded, inactive model |
| `POST /api/assistant/chat` | Send an assistant message; streams AI SDK UI message chunks |
| `GET /api/assistant/sessions` | The owner's saved assistant chats |
| `POST /api/assistant/sessions/{id}/approvals/{approvalId}` | Approve or decline a waiting tool call; body `{"approved":true}`, optional `reason` and `always` (allow for this chat) |
| `POST /api/assistant/sessions/{id}/answers/{toolCallId}` | Answer the assistant's question or type the secret it asked for: `{"answer":…}`, `{"secret":…}` or `{"declined":true}` |
| `GET\|PUT /api/assistant/settings` | Change tools that run without asking, and sites the assistant may read without asking (host names, not addresses or URLs; at most 50). A refused site returns 400 `invalid_sites` with a message naming it |
| `GET /v1/models` | List loaded chat and embedding models |
| `POST /v1/chat/completions` | TensorSharp chat, including streaming |
| `POST /v1/responses` | TensorSharp Responses, including streaming |
| `GET /v1/responses/{id}` | Retrieve a temporarily stored response |
| `POST /v1/embeddings` | TensorSharp embeddings over a separate resident encoder |

Pro request shape; replace the repository/file values with a supported artifact:

```json
{
  "provider": "huggingface",
  "repository": "owner/model",
  "file": "model.gguf",
  "kind": "Chat",
  "revision": "main",
  "pro": true
}
```

Downloads run one at a time and retain partial files. Host restarts mark
unfinished downloads interrupted rather than pretending they completed.
The API refuses to delete loaded models or files belonging to active downloads.
Queued cancellations take effect immediately. If a model-state write fails
(for example, a full disk), the API exposes `persistenceError` and keeps owner
cleanup operations available rather than stopping the host.

The assistant runs Microsoft Agent Framework over the GitHub Copilot runtime,
which the server starts as a child process with none of its built-in tools,
files, shell, or ambient configuration; the model gets only Lucia's tools,
described below. Each owner signs in to GitHub from the chat bar
through GitHub's device flow: it shows a one-time code to enter at
github.com/login/device and continues once GitHub approves it. The flow uses
Lucia's GitHub App (`Assistant__GitHubClientId` overrides the client ID) and
needs no client secret. The host renews the App's expiring user token and keeps
it per owner under `github/` in `Assistant__Directory`, encrypted with ASP.NET
Core Data Protection. If Copilot refuses the token, check that the account has
Copilot and that the App has the Copilot Requests account permission, then
choose **Sign in again**. **Disconnect**, under chat history, deletes only the
host's copy; revoke Lucia under GitHub's
[Authorized GitHub Apps](https://github.com/settings/apps/authorizations) to
end the authorization. Turns keep running if the browser
disconnects, and the chat bar reattaches to them or stops them. Chats are
stored per owner as JSON under `Assistant__Directory` (`/data/assistant` on the
appliance). Build and publish download the SHA-256-checked Copilot runtime for
the target RID from GitHub releases (set `CopilotCliReleaseBaseUrl` to use a
mirror). The host package keeps only its launcher and module
(`runtimes/linux-arm64/native/copilot-runtime` and `runtime.node`).

Not every model needs GitHub. The model picker groups models by where they run:
GitHub Copilot, LiteLLM and Local AI. LiteLLM and Local AI models run in the
same Copilot runtime as bring-your-own-key OpenAI-compatible providers, so they
work without the GitHub sign-in. For an installed LiteLLM app, the host mints a
virtual key per owner with the app's master key, keeps it encrypted under
`litellm/` in `Assistant__Directory`, and mints a new one if LiteLLM rejects it.
The Spark's loaded chat model is reached at `http://127.0.0.1:<port>/v1` with
`HostPlatform__InferenceApiKey`: loopback `/v1` requests that carry a bearer key
skip the browser origin check, but the key is still verified, and Traefik never
connects over loopback. Each node's Local AI app adds its chat models, labeled
with the node. A source without models says why in the picker, for example not
installed, no model loaded, or not answering.

Lucia's tools let the assistant read the lab: servers and their agents,
containers and logs, apps and their settings, the catalog, storage and backups,
DNS (including AdGuard rewrites), UniFi clients, and public web pages. It can
also save a custom app, install a catalog app, start, stop, restart, move or
upgrade an app, run a backup, and check servers for updates; and it can delete an
app, restore a backup, publish an app on the internet or take it off, install a
server's updates, restart it, update its agent, or run a command on it. Every
call passes Lucia's policy, evaluated by the Agent Governance Toolkit: reads
run on their own; changes wait for the owner's approval in the chat unless the
owner lets that tool run automatically under **Settings → Assistant** or chooses
to allow it for the chat; the destructive tools in the second list always ask,
and their approval says what will happen, such as a restart taking the server's
apps offline; reading a site that isn't on the owner's allowed list asks; and Plan mode
refuses anything that changes the lab. An approval that waits 30 minutes is
declined, a turn makes at most 50 tool calls, results are capped at 48 KB with
secrets masked, and every decision is logged and counted on the
`AgentGovernance` meter. "Allow for this chat" lasts until the chat is deleted
or the host restarts.
The assistant can ask the owner a question, or ask them to type a password or
token for a custom app, which Lucia saves in the app's environment without the
model seeing it. Settings are kept per owner in `users/<owner>/settings.json`
under `Assistant__Directory`.

Commands run as root through the node's Lucia agent (`bash`, `sh` or `python3`,
up to 16 KB), each in its own `lucia-exec-<job>` systemd unit with a time limit
of up to 30 minutes and empty stdin; the assistant sees the exit code and the
last 32 KB of output, and a node runs at most four at once. Lucia sends commands
only to agents that announce them in their heartbeat, so update a node's agent
first. A command outlives an agent restart, but its exit code is lost.
`read_web_page` reaches public addresses only, never the lab or other private
networks, and doesn't follow a redirect to another site; it runs no
JavaScript and reads up to 2 MB, 20,000 characters at a time.

OpenAI compatibility comes from TensorSharp's protocol adapters, not a claim
of complete OpenAI API parity. This host exposes text chat and embeddings only,
not TensorSharp's web UI, code execution, skills, media upload, or video APIs.
Responses rejects `previous_response_id`; callers must supply history.
Response retrieval storage is bounded and in-memory. No loaded model produces
an explicit unavailable response, not fabricated inference.

### Checks

```powershell
dotnet run --project tests\Lucia.Homelab.Checks
dotnet run --project tests\Lucia.Homelab.AssistantChecks
```

The dependency-free console checks cover model/path validation, split files,
token handling, persistent download lifecycle, invalid GGUF rejection,
memory reservations, Qwen tool-call parsing, and HTTP permission boundaries.
They use a tiny synthetic GGUF and a fake CLI; they do not substitute for
selected-model GPU inference and tool-quality qualification on the Spark.
The assistant checks cover stream mapping, turn replay, request validation,
retries, per-owner chat storage, GitHub sign-in (device flow, token renewal
and sign-out) against a scripted GitHub, LiteLLM and Local AI model routing
against scripted apps, and the tool policy, approvals, questions, secrets,
settings, secret masking and web reader, without starting the Copilot runtime or
using a real GitHub token.