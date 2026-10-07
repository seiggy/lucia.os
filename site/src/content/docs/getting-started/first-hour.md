---
title: Your first hour
description: What to set up after the desktop finishes, in the order that avoids backtracking.
section: getting-started
order: 4
---

Setup is done and you're signed in to an empty Home page. Most of what's left is optional, but the order matters: domains need AdGuard, AdGuard HA needs a domain, and most apps want storage. This page walks the order that avoids undoing work.

Every step below is a portal page. Press **Ctrl+K** (**Cmd+K** on macOS) and use **Find a tool** if you lose track of where something lives.

![Lucia Home page showing the Spark's health and the lab at a glance](/screenshots/home.png)

## 1. Trust the private CA

If you skipped the trust approval in the desktop app, do it now on every computer that opens Lucia. The steps are in [Install from source](/docs/getting-started/install-from-source/). Until you connect a domain, every Lucia certificate comes from this CA.

## 2. Connect AdGuard Home (Settings → AdGuard)

Point Lucia at the AdGuard Home instance you already run: its address and an account. Lucia uses it to:

- add local DNS rewrites for its own names;
- check that those names resolve;
- optionally manage AdGuard's certificate.

The page summarizes the scope as "Connect the AdGuard Home instance you already run".

Domain setup refuses to start without this, which is why it comes first.

## 3. Connect a domain (Settings → Domains)

The page is titled **DNS & certificates**. You need a domain whose authoritative DNS is Cloudflare, plus an account-owned Cloudflare API token scoped to that one zone with **DNS Write** and **Zone Read**. Lucia walks you through:

1. creating the token;
2. choosing names such as `lucia.homelab.example.com`;
3. reviewing every change;
4. issuing a Let's Encrypt staging certificate;
5. issuing a production certificate;
6. moving the portal and Authentik onto it.

The lab used throughout these docs serves Lucia at `lucia.homelab.seiggy.com`, as an example.

No private address is published to public DNS. The full flow is in [Networking and DNS](/docs/architecture/networking-and-dns/).

> [!IMPORTANT]
> Domain activation is applied by a Spark user service. Activation stays blocked until `loginctl enable-linger <setup-user>` is enabled and the service is installed.

## 4. Connect UniFi (Settings → UniFi Network)

Add a UniFi API key. In Lucia's own words, it "adds DHCP reservations for managed nodes and never changes anything else". Do this before onboarding hardware so new servers keep their addresses.

While you're in the UniFi console, set **Network Boot** if you plan to network-install servers. See [Nodes](/docs/architecture/nodes/).

## 5. Add your SSH keys (Settings → SSH keys)

Paste public keys or import them from a GitHub account. Managed servers pick them up at their next check-in, so you can `ssh <you>@<server>` with your Lucia account.

## 6. Storage and registries (Settings → Storage, Settings → Registries)

**Storage:**

- Add NFS or SMB shares from your NAS. Lucia mounts each one at the same path on every server.
- In Lucia's words: "Lucia only mounts shares; it never changes anything on the NAS."
- Later, under **Apps → Backups**, choose one share as the destination for nightly app backups.

**Registries:** add credentials for private container registries if your apps need them.

## 7. Bring servers in (Your lab → Devices)

Open discovery from **Devices**. Then network-boot a machine and approve it. Approval asks for a disk and has you type `ERASE`.

On a stock install, Devices shows "Discovery is ready. Installation is not." until the host qualifies boot, the CA and enrollment. That's the guard rail working as intended. See [Nodes](/docs/architecture/nodes/).

## 8. Install the first apps (Your lab → Apps)

Two catalog apps are worth installing early:

- **AdGuard Home.** Once installed, Lucia runs a copy on every managed server, each with its own address, and keeps them in sync. Point your UniFi DHCP DNS settings at those addresses yourself. See [AdGuard HA](/docs/architecture/adguard-ha/).
- **Observability.** Prometheus, Loki, Tempo and Grafana, with Lucia's hardware and inference dashboards already loaded. See [Observability](/docs/architecture/observability/).

Everything else can wait until you actually want it. That includes Immich, Plex, Home Assistant, the media apps and custom compose apps.

## 9. Set up the assistant (Settings → Assistant)

Open the assistant panel and pick a model from its model menu. The menu lists:

- GitHub Copilot models, which need a GitHub device-flow sign-in and run on your Copilot plan;
- models served by a LiteLLM app;
- Lucia's own Local AI models.

Then go to **Settings → Assistant** and decide two things:

- Which change tools may run without asking.
- Which public sites it may read without asking.

Read-only tools always run. In chats, some actions always ask first:

- deleting an app;
- restoring a backup;
- putting an app on the internet or taking it off;
- updating or restarting a server;
- running a command.

See [Assistant and jobs](/docs/architecture/assistant-and-jobs/).

## 10. Turn on notifications (Settings → Notifications)

Add this browser as a notification device and send a test. Lucia uses web push to tell you when a job needs approval, fails, or has something to report. You can register up to 20 devices.

## 11. Schedule a job (Local AI → Assistant jobs)

A good first job is a weekday morning check: "look at node health and recent app logs, and notify me only if something is wrong." Give it a schedule, test it once by hand, and leave it running.

## 12. Install the Spark update worker (Your lab → Spark updates)

If you haven't already, run the one `sudo` command the page shows. After that, you can check, schedule and install Spark updates from the portal. See [Spark updates](/docs/architecture/spark-updates/).

That's the hour. The rest of the time, the idea is that you don't need to log in at all.
