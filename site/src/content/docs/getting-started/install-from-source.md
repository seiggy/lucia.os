---
title: Install from source
description: The real path to a working Lucia install today, by building the desktop setup app and running it against your Spark.
section: getting-started
order: 2
---

There's no signed installer yet, so the only way in is to build Lucia yourself. That's less work than it sounds. You build one desktop package on your workstation, point it at your Spark and click through five steps. The package carries everything the Spark needs. The Spark never clones the repository.

This page walks the supported path, then covers the developer and recovery routes that the same code exposes.

> [!IMPORTANT]
> This guide assumes your lab meets [Requirements and assumptions](/docs/getting-started/requirements/): a DGX Spark past NVIDIA's first-run setup with SSH and Docker working, and a UniFi gateway.

## What you need on the build machine

| Tool | Why |
|---|---|
| .NET 10 SDK | Builds the server, the node agent and the desktop |
| Node.js and npm | Builds the React portal that ships inside the host package |
| Python 3 | Runs `tools\host\package.py`, which packs the host archive |
| PowerShell 7 (`pwsh`) | Runs the publish script |
| The TensorSharp GB10 package feed | Lucia's in-process inference engine; see below |

### The TensorSharp package feed

The server references `TensorSharp.Server`, `TensorSharp.Models` and `TensorSharp.Native.Cuda` at version `2.8.6-gb10.20260921.1`. These are **not** the public TensorSharp releases on NuGet. They're a locally built package set for the GB10 (Windows x64 and Linux ARM64). Lucia expects them in a local folder registered as a NuGet source.

```powershell
dotnet nuget add source "$HOME\.nuget\local-feeds\tensorsharp-cuda" --name TensorSharp-Local-Cuda
```

> [!NOTE]
> The repository doesn't yet document how to produce these packages, and they aren't published anywhere. Without them, the Linux ARM64 host publish fails with "The maintainer build requires the configured TensorSharp package feed", and no deployable desktop package is produced. You can still build and run the desktop UI on its own (see "Run the desktop UI only" below). This is the single biggest gap in the install-from-source story, and it's on the [limitations](/docs/reference/limitations/) list.

## Step 1: Get the source

```bash
git clone https://github.com/seiggy/lucia.os.git
cd lucia.os
```

## Step 2: Build the desktop package

From the repository root:

```powershell
pwsh -File tools\desktop\publish.ps1 -Runtime win-x64
```

Other runtimes are `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`. The script does the following, in order:

1. Builds the portal (`npm run build` in `src\frontend`). If `node_modules` is missing, it runs `npm ci` first.
2. Publishes `Lucia.Homelab.Server` for `linux-arm64`. This is the step that needs the TensorSharp feed.
3. Publishes the node agent self-contained for `linux-x64` into the server's `boot\node-agent-linux-x64` folder.
4. Copies the portal into the server's `wwwroot` and packs `host-linux-arm64.tar.gz` plus a manifest with `tools\host\package.py`.
5. Publishes the desktop app self-contained into `artifacts\desktop\<runtime>`, with `BootstrapPayload` (the hash-checked setup scripts and Identity AppHost) and `HostPayload` (the host archive) beside it.

The output is an **unsigned development build**. No model weights or Hugging Face tokens are packaged. Keep the whole output folder together, because the app finds `HostPayload` relative to itself. Build final distributables on their target OS so native executable permissions survive. On macOS, the output is wrapped in `Lucia.app`.

## Step 3: Prepare the Spark

On the Spark, as the account you'll use for setup:

```bash
loginctl enable-linger "$USER"
```

This allows the user services for domain activation, people management, node enrollment and the Spark model and runner workers to run after you log out. The desktop installs those services only if lingering is already on.

Leave .NET and Aspire alone. If they're missing, the desktop offers to install .NET 10.0.401 and Aspire 13.5.4 user-locally from pinned, checksummed packages.

## Step 4: Run the desktop and follow the five steps

Start the published app, for example `artifacts\desktop\win-x64\Lucia.Desktop.exe`. It walks you through **Connect**, **Review**, **Your account**, **Set up** and **Ready**. [Desktop bootstrap](/docs/getting-started/desktop-bootstrap/) explains each step.

In short, the app:

- connects over SSH and pins the Spark's host key after you confirm its fingerprint;
- runs read-only preflight checks;
- creates your owner account;
- uploads its payload;
- runs `tools/desktop/bootstrap.py` on the Spark as a durable job.

That job:

- provisions the identity stack with `tools/identity/provision.py` and enrolls your owner;
- registers Lucia's OIDC application;
- prepares the managed host;
- deploys everything with the Identity AppHost (Aspire to Docker Compose).

You can close the app or choose **Stop watching** at any time. The job keeps running on the Spark, and you can reconnect later to see its progress.

When it finishes, **Ready** opens Lucia at its HTTPS origin, for example `https://spark-9423/`.

## Step 5: Install the root update worker (optional, recommended)

Spark package updates go through a small root service with a fixed set of actions. The desktop never asks for your sudo password, so you install this service yourself:

```bash
sudo python3 ~/.local/share/lucia/bootstrap/app/tools/packages/package_worker.py install
```

It needs `python3-apt`. The **Spark updates** page shows this same command until the worker reports in. See [Spark updates](/docs/architecture/spark-updates/).

## Step 6: Trust the private CA

Until you connect a domain, Lucia's certificates come from its private CA. The desktop offers a separate, explicit approval to import the public root certificate. On Windows it goes into the current-user Root store, and on macOS into the login keychain.

You can also do it by hand. Copy `trust/lucia-root-ca.crt` and `trust/fingerprint.txt` from `~/.local/share/lucia/identity` on the Spark. Check that the SHA-256 fingerprint of the DER certificate matches, then import it:

```powershell
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path .\lucia-root-ca.crt).Path)
$certificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
# Compare with the trusted fingerprint before running the next command.
Import-Certificate -FilePath .\lucia-root-ca.crt -CertStoreLocation Cert:\CurrentUser\Root
```

Never copy the `ca` or `secrets` directories off the Spark.

Then go to [Your first hour](/docs/getting-started/first-hour/).

## Other routes the code supports

### Run the desktop UI only

The desktop UI and its checks need only the .NET 10 SDK:

```powershell
dotnet run --project src\Lucia.Desktop
dotnet run --project tests\Lucia.Desktop.Checks -- --payload .
python tools\desktop\check.py
python tools\identity\check.py
```

Running from source like this has no `HostPayload` next to it. The app can connect and run preflight, but it can't deploy the managed host. For an actual install, use the published package from step 2.

### Identity stack by hand (development and recovery)

`tools/identity/provision.py` is the same installer the desktop calls. You can run it directly from a checkout on the Spark that has Docker, Python 3, OpenSSL, .NET 10 and Aspire 13.5.4:

```bash
python3 tools/identity/provision.py prepare --host 192.168.0.222
python3 tools/identity/provision.py publish
python3 tools/identity/provision.py apply --owner-username <your-username>
python3 tools/identity/provision.py verify
```

`apply` includes prepare and publish, so it can do first-time setup on its own when given `--host`. To enroll an owner on an existing install, or to recheck that owner's sign-in:

```bash
python3 tools/identity/provision.py owner --owner-username <your-username>
python3 tools/identity/provision.py verify-owner-login
```

The CLI covers only the identity stack. Deploying the managed host and portal is the desktop's job.

State lives in `~/.local/share/lucia/identity`, which `--state` overrides. Back it up, encrypted. It holds the CA keys and every bootstrap secret.

> [!CAUTION]
> Never run `aspire destroy` against a live identity install. Its teardown can remove the Compose volumes that hold PostgreSQL, Authentik data and media. Aspire also derives Compose project and volume names from the AppHost path, so keep that path stable across upgrades.

### Local development loop

To work on the server and portal on a workstation, the dev AppHost runs the server, a Redis container and the Vite frontend:

```powershell
dotnet restore src\Lucia.Homelab.Server\Lucia.Homelab.Server.csproj
dotnet user-secrets set "Parameters:host-api-key" "<owner-token-at-least-32-characters>" --project src\Lucia.Homelab.AppHost
aspire start --non-interactive
aspire wait server --non-interactive
```

This also needs Docker and the Hugging Face CLI (`hf`) 1.27 or newer. The dev listeners bind on all interfaces for LAN testing. Don't expose them to the internet.

### Network boot service

The PXE and TFTP service that installs managed servers has its own builder (`tools/boot/prepare.py`) and preparation step (`tools/boot/provision_boot.py`). Neither the desktop nor a documented CLI command runs that preparation yet, so a stock install comes up with network boot disabled. [Nodes](/docs/architecture/nodes/) explains what happens once it's in place.
