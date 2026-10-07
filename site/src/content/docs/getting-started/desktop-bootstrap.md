---
title: Desktop bootstrap
description: What the Lucia desktop setup app does in each of its five steps, and what it deliberately leaves alone.
section: getting-started
order: 3
---

The desktop bootstrap is a small Avalonia app that takes a freshly set-up DGX Spark to a running Lucia over SSH. Its goal is that setup never asks you to open a terminal on the Spark. One exception remains: the root update worker. It needs `sudo`, and the app won't ask for your sudo password.

> [!NOTE]
> **Signed release: coming soon.** No signed or notarized download exists yet. Today you build the app from source with `tools\desktop\publish.ps1`. That build is unsigned, so Windows SmartScreen and macOS Gatekeeper will warn you about it. See [Install from source](/docs/getting-started/install-from-source/).

## The five steps

The app shows the same five steps every time: **Connect**, **Review**, **Your account**, **Set up** and **Ready**.

### 1. Connect

Enter the Spark's hostname or IP address, an SSH username, and either a password or a private key file.

Before any credential is sent, the app shows the server's SSH host-key fingerprint and asks you to confirm it. It saves a confirmed host key and refuses to connect if that key ever changes. A rebuilt Spark or a man in the middle both look like a changed key, and the app treats them the same way.

### 2. Review

The app runs read-only preflight checks over SSH and shows each one as passing, blocking or needing action:

- OS (Ubuntu 22.04 or 24.04, as shipped in DGX OS) and ARM64 architecture
- Docker running, reachable by your account, version 28 or newer, with the Compose plugin
- The NVIDIA CDI device `nvidia.com/gpu=all`
- Python 3.11 or newer, and OpenSSL
- .NET SDK 10.0.401 and Aspire 13.5.4
- Free ports 443, 636, 9443 and 9444
- At least 12 GiB of free disk

If .NET or Aspire is missing, the app offers to install them user-locally from pinned, checksummed packages. It never changes Docker, the GPU stack or system packages.

If a setup job is already running on the Spark, Review offers to reconnect to it. Lucia won't start a second installation. If an earlier identity install exists, Review reports that its existing users, passwords, trust and volumes will be kept.

### 3. Your account

Choose the owner's username and password. This becomes a real directory account. It's created in OpenLDAP, added to the `ldap-admins` and `lucia-owners` groups, and checked by signing in through Authentik before setup reports success.

This is the account you use for the portal, for Authentik, and later for SSH and `sudo` on every managed server. The password goes to the Spark over the pinned SSH connection. The setup script receives it as private JSON on standard input, never as a command-line argument.

### 4. Set up

The app uploads its hash-checked payload to `~/.local/share/lucia/bootstrap` and starts `bootstrap.py` there as a **durable remote job**. In order, the job:

1. Builds the identity stack: PostgreSQL, a Smallstep private CA, OpenLDAP and Authentik, behind a Traefik gateway.
2. Enrolls your owner account.
3. Registers Lucia as an Authentik OIDC application.
4. Unpacks the managed host and the portal.
5. Installs the Spark-side user services (only if `loginctl enable-linger` is on).
6. Deploys the whole thing with the Identity AppHost through Aspire to Docker Compose.

Progress streams into the app. Because the job runs on the Spark, you can choose **Stop watching** or simply close the app, and setup keeps going. Open the app again and use **Reconnect to Spark** to pick the job back up.

### 5. Ready

Ready shows Lucia's HTTPS address, for example `https://spark-9423/`. Sign in there with the owner account you created.

Trusting Lucia's private root certificate on this computer is a **separate, explicit approval**. Setup doesn't do it silently:

| Desktop OS | Where the root goes |
|---|---|
| Windows | Current user's Trusted Root store |
| macOS | Login keychain |
| Linux | The system-wide CA store, after a native privileged approval prompt. If that isn't available, the app asks you to have an administrator import it |

There's also an **Export certificate** button if you'd rather import it yourself.

Only the public root certificate is ever copied. The CA's private keys stay on the Spark.

## What the app never does

- Ask for or store your Spark `sudo` password.
- Install GPU drivers, change NVIDIA's Docker engine or edit Docker group membership.
- Package model weights or Hugging Face tokens.
- Set up the network boot service. That's prepared separately today; see [Nodes](/docs/architecture/nodes/).

## Re-running it

Running the app again against an installed Spark is safe:

- Preflight treats ports held by Lucia's own identity services as expected, not as conflicts.
- The installer keeps existing secrets and CA material. If any of them are missing, it stops and asks you to restore them instead of quietly generating new ones.

When it's done, head to [Your first hour](/docs/getting-started/first-hour/).
