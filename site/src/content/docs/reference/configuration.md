---
title: Configuration
description: The configuration sections and keys the Lucia host reads, their defaults, and what the Spark installer sets in production.
section: reference
order: 1
---

The Lucia host is an ASP.NET Core app, so it reads standard .NET configuration: `appsettings.json`, then environment variables, where `Section__Key` overrides `Section:Key`. On the Spark you rarely touch any of this. The installer generates the production deployment and sets the values in the **Production** columns below. Most day-to-day settings, such as the domain, AdGuard, UniFi, storage, registries and assistant permissions, live in the portal under **Settings**, not in configuration.

This page is the reference for when you're running from source, or need to know why the production deployment looks the way it does.

> [!NOTE]
> Lucia has no settings page for the keys on this page. The production values are what `tools/host/provision_host.py` and the Identity AppHost (`src/Lucia.Homelab.Identity.AppHost`) generate. Secrets in production are generated files under the installation's state, passed as container secrets. They're never written into `appsettings.json`.

## HostPlatform

Models, inference and the host API. Bound from `HostPlatform` and validated at startup, so an out-of-range value stops the host from starting.

| Key | Code default | Shipped `appsettings.json` | Production | Range and notes |
|---|---|---|---|---|
| `ApiKey` | none (required) | none | Generated secret | At least 32 characters. Owner-level bearer key for the host API |
| `InferenceApiKey` | none | none | Generated secret | At least 32 characters, and must differ from `ApiKey`. A built-in inference key. The assistant uses it to reach the Spark's own model |
| `ModelDirectory` | `data/models` | `data/models` | `/models` | Where model files live |
| `HuggingFaceExecutable` | `hf` | `hf` | `/opt/hf/bin/hf` | The Hugging Face CLI used for downloads |
| `HuggingFaceHomeDirectory` | none | none | `/data/huggingface` | The `hf` cache and auth home |
| `HuggingFaceToken` | none | none | not set | Optional fallback token. Prefer entering it on Local AI → Models, where it's encrypted. `HF_TOKEN` is honored too |
| `Backend` | `ggml_cuda` | `ggml_cuda` | `ggml_cuda` | TensorSharp backend for chat |
| `ContextTokens` | 32768 | 32768 | 8192 on a fresh install | 256–1,048,576. Default served context for chat loads |
| `MaxOutputTokens` | 2048 | 2048 | 2048 | 1–32,768 |
| `SreThinking` | true | true | true | Whether the assistant's local model reasons before answering |
| `MemoryBudgetGiB` | none (detected) | none | not set | 1–8,192. Set it explicitly on GPUs other than the Spark |
| `OsReserveGiB` | 4 | 8 | 8 | 0–1,024 |
| `ServicesReserveGiB` | 3 | 8 | 8 | 0–1,024 |
| `VoiceReserveGiB` | 8 | 8 | 8 | **8**–1,024. Held for a future voice pipeline |
| `RuntimeReserveGiB` | 3 | 8 | 8 | 1–1,024 |
| `WeightMemoryMultiplier` | 1 | 2 | 2 | 1–8. A calibration knob for the weight estimate, not a measured constant |
| `RequireTensorSharp` | true | true | true | Turn off when another engine (llama.cpp, vLLM) serves the library, so GGUFs TensorSharp can't run still become Ready |
| `LlamaCache` | none | none | not set | Node-local directory that Ready GGUFs are mirrored into, for llama.cpp |
| `LlamaLoadOnStartup` | none | none | not set | Comma-separated llama.cpp model names to load at start |
| `ChatModelId`, `EmbeddingModelId` | none | none | not set | Model selections to restore at startup |

The installer keeps `ContextTokens` and `VoiceReserveGiB` in its host settings record (`context_tokens`, default 8192; `voice_reserve_gib`, default 8). A reinstall keeps the previous values. Memory reserves are explained in [Local AI](/docs/architecture/local-ai/).

## HostAuthentication

Portal sign-in through Authentik (OIDC). Off by default in code. Production turns it on.

| Key | Production | Notes |
|---|---|---|
| `Enabled` | `true` | When off, as in a bare development run, the portal has no sign-in |
| `Authority` | Authentik's Lucia application on the Spark | For example `https://<spark>:<authentik-port>/application/o/lucia/` |
| `ClientId` | From identity provisioning | |
| `ClientSecretFile` | `/run/secrets/host-oidc-client-secret` | A file path, never the secret itself |
| `PublicOrigin` | The portal URL | For example `https://lucia.homelab.example.com` once a domain is active |
| `AdditionalPublicOrigins` | not set | Extra origins the portal answers on |
| `AuthorityUsesSystemTrust` | not set | Trust Authentik through the system store instead of the CA file |
| `CaCertificatePath` | `/trust/lucia-root-ca.crt` | Lucia's private root CA, public certificate only |
| `DataProtectionKeysDirectory` | `/data/data-protection` | Keys that encrypt stored secrets and cookies. Back this up with the rest of `/data` |
| `TrustedProxyNetworks` | The identity network's subnets | Indexed: `TrustedProxyNetworks__0`, `__1`, and so on |

## Storage directories

Every store has a directory key. In production all of them sit under the host container's `/data` volume.

| Key | Production |
|---|---|
| `Assistant:Directory` | `/data/assistant` |
| `InferenceKeys:Directory` | `/data/inference-keys` |
| `HuggingFaceManagement:CredentialsDirectory` | `/data/provider-credentials` |
| `AdGuardManagement:CredentialsDirectory` | `/data/network-credentials` |
| `CloudflareDomains:CredentialsDirectory` | `/data/network-credentials` |
| `DomainOnboarding:StateDirectory` | `/data/domains` |
| `HardwareOnboarding:StateDirectory` | `/data/onboarding` |
| `PackageUpdates:Directory` | `/data/packages`, shared with the root package worker |
| `Telemetry:RelayDirectory` | `/data/telemetry`. Setting it makes the controller write the Spark's relay config |

## Assistant

| Key | Default | Notes |
|---|---|---|
| `Directory` | `Lucia/data/assistant` under the user's local app data folder | Must be an absolute path. Holds per-user assistant settings, jobs, run history, push devices, and the encrypted GitHub and LiteLLM sign-ins |
| `GitHubClientId` | Lucia's GitHub App | A public client ID for the GitHub device flow. No secret is involved |
| `Model` | none | The model used when the chat doesn't pick one. Null lets the runtime choose |

## DomainOnboarding

| Key | Production | Notes |
|---|---|---|
| `StateDirectory` | `/data/domains` | |
| `GatewayDirectory` | `/domain-gateway` | Where the Traefik dynamic configuration is written |
| `CertificateGatewayDirectory` | `/domain-certificates` (code default) | |
| `InstallationId` | From the installer | Ties domain state to this installation |
| `IngressAddress` | The Spark's LAN address | The address local DNS rewrites point at |

## HardwareOnboarding and Boot

These are set only when the boot service is provisioned. The qualification flags gate what the Devices page can do. See [Nodes](/docs/architecture/nodes/).

| Key | Production | Notes |
|---|---|---|
| `HardwareOnboarding:DiscoveryNetworkCidr` | The boot LAN, for example `192.168.0.0/24` | |
| `HardwareOnboarding:BootBaseUrl` | The portal's public origin | |
| `HardwareOnboarding:DiscoveryAdapterQualified` | From boot provisioning | Off by default |
| `HardwareOnboarding:InstallationEnabled` | From boot provisioning | Off unless installation is qualified |
| `HardwareOnboarding:BootArtifactsQualified` | Same as above | |
| `HardwareOnboarding:EnrollmentQualified` | Same as above | |
| `HardwareOnboarding:EnrollmentStatusFile` | `/data/nodes/enrollment-worker.json` | |
| `Boot:Enabled` | `true` | |
| `Boot:ControlDirectory` | `/data/boot-control` | |
| `Boot:AllowedNetworks:N` | The boot LAN CIDR | Indexed list |

## SparkTelemetry

| Key | Code default | Production |
|---|---|---|
| `Enabled` | `false` | `true` |
| `ProcDirectory` | `/host-metrics` | `/host-metrics`, a read-only bind of the host's `/proc` files |
| `StoragePath` | `/data` | `/data` |

## Running from source (development AppHost)

`src/Lucia.Homelab.AppHost` runs the server, Redis and the Vite frontend through Aspire. It takes these parameters, which you set as Aspire parameters or user secrets. Use your own values, and never commit them.

| Parameter | Sets | Required |
|---|---|---|
| `host-api-key` | `HostPlatform__ApiKey` | Yes (32+ characters) |
| `inference-api-key` | `HostPlatform__InferenceApiKey`, and the Playground's key in development | No |
| `huggingface-token` | `HostPlatform__HuggingFaceToken` | No |

```powershell
dotnet user-secrets set "Parameters:host-api-key" "<owner-token-at-least-32-characters>" --project src\Lucia.Homelab.AppHost
aspire start --non-interactive
```

In the Development environment, the server maps the OpenAPI document and a Scalar API explorer at `/scalar/v1`, and `/` redirects there. Outside Development, neither is mapped.
