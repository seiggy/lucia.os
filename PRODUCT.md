# Lucia

<!-- impeccable:product-schema 1 -->

## Platform

web

The browser-based management dashboard is the ongoing management surface. A separate Avalonia/.NET desktop app bootstraps the DGX Spark and handles initial identity setup; it is not another management dashboard. The desktop targets Windows, macOS, and Linux.

## Users

Lucia serves two equally important audiences:

- People who want a homelab and self-hosted applications without needing to learn server administration first.
- Homelab enthusiasts who want less manual setup and maintenance without losing control.

The product should be a place where users can learn and grow. Simplicity must not become a ceiling for more experienced users, and expertise must not be a prerequisite for getting started.

Default interactions should work for people who work in technology but are not confident managing infrastructure or navigating a cloud console.

## Product Purpose

Lucia is a self-managing homelab platform: an SRE agent combined with a homelab "easy button" dashboard and task tracker.

The goal is for Lucia to manage itself and all hardware entrusted to it, rather than merely recommend commands for the owner to run. Success means less routine administration, understandable task progress, and room for the owner to learn and take more control.

## Positioning

The Spark retains NVIDIA's DGX OS for the central AI and management host. Newly onboarded commodity servers target Debian 13.7 (Trixie). The management dashboard, OpenTelemetry (OTEL), and task platform coordinate those managed devices.

The README describes an opinionated combination of open-source software, Lucia's own software, and a proprietary desktop application. These are product intentions, not evidence that those integrations or the desktop application are implemented.

## Operating Context

- Users manage their own homelab and the hardware they give Lucia authority to manage.
- The README's intended entry path is to connect a DGX Spark to the network and use a desktop application to get started, followed by cloud-like management in the browser.
- Intended work includes installing applications, setting up servers, and running authorized security tests against the owner's managed environment.
- The dashboard and task tracker provide access to the autonomous system's work; OTEL is part of the platform's observability foundation.

## Capabilities and Constraints

### Confirmed product direction

- Retain DGX OS on the Spark and target Debian 13.7 on newly managed servers.
- Host AI, dashboard, OTEL, and task capabilities on that platform.
- Aim for autonomous management of Lucia itself and entrusted hardware.
- Let the owner configure autonomy per action or managed resource, including boundaries for destructive changes, security-sensitive changes, and spending.
- Support beginners and enthusiasts equally, with opportunities to learn and grow.
- Make the GUI the normal way to operate Lucia; users should not need to manage API keys or make API calls manually.
- Use LDAP as the account authority for managed devices, with Authentik handling OAuth/OIDC for web applications and APIs. Current static keys are development/bootstrap access, not the intended user sign-in experience.
- Offer a persistent private CA when no DNS provider is configured, and Let's Encrypt DNS-01 when one is. Test the private-CA installation first, then migrate to a domain/provider without recreating accounts or directory data. Install public CA trust in managed node images and on other clients only with the owner's approval.
- Use Avalonia and .NET for the cross-platform desktop bootstrap app. Start after NVIDIA's DGX OS setup, networking, and SSH access are complete; factory-first-boot onboarding is outside the initial milestone.
- Make owner-account creation part of LDAP/Authentik setup itself. The desktop collects the owner's username and password securely, creates the LDAP account and administrative memberships, synchronizes it into Authentik, and verifies sign-in before declaring completion. Existing accounts are preserved, not reset or replaced.
- Provide Owner-initiated, time-limited hardware onboarding, off by default and after host restart. Keep UniFi as DHCP authority. Initial PXE discovery must not write disks; installation requires identifying the server, selecting a stable target disk, and explicitly approving erasure. Closing admission does not cancel already-approved installations, and enrolled machines are never automatically reimaged. Every boot screen advances on its own with no keyboard or monitor; an attached monitor shows Lucia-branded progress, the verification code, and plain-language failures.
- Support UniFi Network as the only LAN-gateway platform for now, including Dream Machine deployments. Gateway network-boot configuration is a documented, manual, one-time user step; Lucia does not currently automate it or collect UniFi administrator credentials. Network Boot supplies DHCP options 66/67; the separate TFTP Server option is not also enabled. Routine discovery windows are controlled in Lucia without changing gateway DHCP settings.
- Use Debian preseed for OS installation, cloud-init for first-boot CA/LDAP/node enrollment, and a native systemd node agent for ongoing management. Keep human LDAP identities separate from machine credentials. Never distribute CA private keys or directory administrator credentials in boot configuration.
- Start with x86-64 UEFI hardware. The owner's first dev server uses Secure Boot disabled for separate accelerator firmware work; firmware flashing and changes to firmware security settings are outside Lucia's onboarding scope. Report Secure Boot state honestly.
- Configure an external AdGuard Home connection independently under Settings, then require it for DNS/SSL onboarding. A future Lucia-managed AdGuard module supplies the same local-DNS capability. Support Cloudflare account-owned API tokens only in v1, limited to Zone DNS Edit and Zone Read for the selected zone; registrar transfer is unnecessary.
- Let owners choose the domain, local namespace, Spark name, and each service URL. Suggested Lucia/Auth/Spark names are editable, not conventions imposed on the lab. Use reviewed certificate SAN coverage, local exact rewrites, DNS-01 staging, explicit ACME agreement/DNS consent, and controlled canonical-URL activation. Keep services local without public A/AAAA records, preserve private CA/LDAP and legacy API recovery, and isolate administrator registration changes in the approved scoped native worker.

### Current implementation

The React/TypeScript/Vite dashboard uses real Authentik sign-in. Home displays actual Local AI availability. Devices and Tasks now read a durable hardware-onboarding control plane, with Owner-only timed admission, reported hardware, disk-specific approval controls, and recorded installation observations. Discovery is unavailable until configured, and installation remains disabled until its boot, private-CA, and directory-enrollment prerequisites are qualified. Synthetic inventory, incidents, activity, repair timers, and demo scenarios remain removed. Appearance still supports light/dark/system modes, theme presets, and custom colors.

Home also gives Owners read-only DGX Spark metrics: CPU, shared system memory, GPU utilization/temperature/power, host-volume capacity, default-interface traffic, and uptime. Sampling is every 10 seconds, with a rolling one-hour in-memory cache and no historical database. History resets on host restart. Unsupported sensors, failed collection, stale data, and empty history remain explicit; GB10 shared memory is not presented as separate VRAM. Health describes telemetry availability and capacity warnings, not a blanket assertion that every service or hardware component is healthy.

Spark updates lets Owners check, review, and install apt updates for the Spark OS itself, with per-package changelogs, Owner-confirmed service or Spark restarts, and one scheduled install/restart window. A root systemd worker (`tools/packages/package_worker.py`, installed once with sudo) accepts only fixed actions through a private file exchange; the web host never gets root or runs commands. Only already-installed packages are upgraded; an install that would remove packages, or pull in platform packages without that group's confirmation, is refused. NVIDIA driver, CUDA, kernel, bootloader, firmware, Docker/container runtime, and DGX packages are a separate opt-in group, and Lucia unloads models first and restores them after. Ubuntu phased-rollout updates are shown as such. Unattended-upgrades is not installed on the Spark, so security updates are not automatic.

The first node client performs read-only Linux inventory and signed discovery using a local key, private-CA HTTPS, and short-lived device capabilities. A separate boot-file service defaults closed and needs a short-lived admission heartbeat; it does not provide DHCP or DNS. The OS installer, managed-node certificate/LDAP enrollment, and ongoing package/service operations are not yet implemented or qualified. A reported discovery is not a managed or healthy node, and a recorded installation observation is not proof of successful enrollment.

Local AI displays the real hosted model/endpoint and streams text generation. In managed deployments it uses same-origin cookie-authenticated API requests with CSRF protection. Authentik access tokens with the correct audience, issuer, scope, and role also secure OpenAI-compatible endpoints; machine API keys remain supported separately. The inference-only Vite bridge remains a development aid, not the production authentication path.

Owners can create named inference-only application keys, reveal their secret once, and revoke them. Keys do not expire by default; optional expiry is supported. The model manager provides Hugging Face account-token management, GGUF repository search, pinned quantization choices, bounded-header context previews, the local model library, queued downloads, and explicit load/unload/delete actions. Only one chat LLM and one embedding model may be loaded; successful startup selections persist. Unknown compatibility and unavailable estimates remain explicit, and the voice reservation is preserved. Hugging Face tokens are encrypted on the host, never retained in browser storage or passed as CLI arguments.

The ASP.NET Core server contains an in-process TensorSharp inference host, Microsoft Agent Framework integration, authenticated model-management APIs, and persistent Hugging Face downloads. Aspire connects the server, Redis, and frontend.

The Avalonia desktop implements Connect, Review, Your account, Set up, and Ready steps, with system/light/dark appearance, SSH fingerprint approval, password/private-key authentication, remote preflight, packaged identity and host provisioning, progress/reconnect handling, and explicit local certificate trust/export. Its supplied owner password is used directly for LDAP enrollment. It reconciles the managed host and Authentik application/provider/access registration on reruns and opens Lucia at a stable HTTPS origin. The managed Docker host uses NVIDIA CDI and persistent model/data-protection storage. Windows interaction and real Spark deployment/sign-in have been exercised; unsigned cross-platform builds are not yet a fully qualified, signed release. Missing Docker/OS access remains a blocking prerequisite.

A separate Aspire Docker deployment and repeatable Python installer provision Authentik, authoritative OpenLDAP, PostgreSQL, a private CA, and a TLS gateway with certificate renewal. A complete installation requires a real LDAP-backed initial owner and verified Authentik password sign-in, not just healthy services. The owner belongs to LDAP administrative groups; the synchronized `lucia-owners` group grants Authentik administrator access only after its source identity is checked. Reruns preserve account IDs and passwords. Authentik reads the LDAP directory over verified LDAPS; normal account passwords remain directory-owned. The remote installer exports public trust material and a Debian node-image trust hook, but does not itself change client trust stores; the desktop asks for separate permission to do that locally. Additional user enrollment, self-service LDAP password changes, device login integration, and DNS-provider certificate migration are not implemented yet.

The selected bundled model is Qwen3.6-35B-A3B-MTP at Unsloth UD-Q6_K_XL, with pinned artifact provenance in the host catalog. Exact-file Spark qualification remains pending. The host reserves at least 8 GiB for the future voice stack separately from OS/services headroom and exposes conservative context estimates. The current SRE tools are read-only; do not present autonomous remediation, qualified OS imaging, managed-node enrollment, general task execution, application installation, or voice as available functionality.

### Open product decisions

- Initial end-to-end workflow and release scope.
- Desktop signing, distribution, native platform qualification, and post-bootstrap recovery/upgrade scope.
- Qualification of additional managed hardware, Secure Boot-enabled network boot, and alternative OS profiles.
- Supported managed hardware and the curated software/integration catalog.
- AI execution mechanism, task lifecycle, and how telemetry supports autonomous operations.
- Default autonomy policies, approval and escalation behavior, recovery, rollback, and audit requirements.
- LDAP account enrollment, role/scope mapping, remote access, and production network trust boundaries.
- Product-specific accessibility standard and localization requirements.

## Brand Commitments

The product name is Lucia. The owner approved consumer-device simplicity: familiar controls, plain language, clear priorities, and progressive disclosure. The original three-tab "simple check-in" composition has been superseded by a compact workspace switcher with contextual tool navigation, explicitly selected by the owner as the portal grows. Overview, Your lab, Local AI, and Settings organize implemented capabilities; Playground, Models, and API keys are siblings rather than links buried inside chat. Desktop and tablet use a compact searchable directory; phones expand that directory into a full-height labeled sheet. Keep the shell uncluttered without hiding capabilities or inventing future pages. Colors remain customizable, with light, dark, and system appearance modes. Motion should support orientation, not delay tasks.

## Evidence on Hand

- `README.md`: original product description and intended setup workflow.
- `src\frontend\src\App.tsx`: authenticated dashboard foundation, not proof of live device or task management.
- `.impeccable\mocks\layout-c-comparison.png`: owner-approved desktop/mobile composition.
- `src\frontend\public\lucia.svg`: current simple Lucia favicon.
- `src\Lucia.Homelab.AppHost\AppHost.cs`: current development resource wiring.
- `src\Lucia.Homelab.Server\Program.cs`: current demonstration API.

The product direction and autonomy boundary above were confirmed by the owner during initialization. No customer testimonials, performance benchmarks, pricing, or proof of autonomous homelab operation were supplied; future work must not fabricate them.

## Product Principles

1. Remove the requirement to administer servers, not the opportunity to understand them.
2. Automate real work rather than turn the dashboard into a command manual.
3. Keep autonomous authority under owner-defined policies for each action or resource.
4. Make the system's work understandable through tasks and observability.
5. Build on a commodity operating system and an opinionated platform, with DGX Spark as the target.
