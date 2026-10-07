---
title: Spark updates
description: How Lucia installs operating system and platform package updates on the DGX Spark through a fixed-action root worker.
section: architecture
order: 10
---

The Spark runs DGX OS, which is Ubuntu underneath. It gets apt updates like any other Ubuntu box, except some of those updates are a new kernel, NVIDIA driver or CUDA stack under your inference engine. **Spark updates** shows what's available, splits the risky packages from the routine ones, and installs them now or in a window you pick. You no longer have to remember which `apt upgrade` broke CUDA last time.

![The Spark updates page with everyday and platform updates listed separately](/screenshots/updates.png)

## How it's built

Installing packages needs root, and Lucia's web host deliberately doesn't have it. So the work is split in two:

```text
lucia-host (unprivileged, container)
   |  bounded JSON request files in <data>/packages/requests
   v
lucia-package-updates.service (root, systemd, on the Spark)
   |  fixed actions only
   v
apt-get / dpkg / needrestart / systemctl reboot
```

The worker is `tools/packages/package_worker.py`, installed to `/usr/local/lib/lucia/package_worker.py`. It accepts exactly six actions: `check`, `install`, `changelog`, `restart-services`, `restart-spark` and `repair`. It **never runs caller-supplied commands**. It validates package names against a strict pattern, and only installs packages that are currently listed as available updates.

### Setting up the worker

Until the worker is installed, the page shows **Set up Lucia's update service** with the one command to run on the Spark:

```bash
sudo python3 ~/.local/share/lucia/bootstrap/app/tools/packages/package_worker.py install
```

> [!IMPORTANT]
> The worker needs `python3-apt`. Its installer stops with "python3-apt is required." if it's missing. Run the command once, as your normal user with sudo. It installs, enables and starts `lucia-package-updates.service`. The page picks it up within a few seconds.

The worker writes a status heartbeat. The page treats it as ready while that heartbeat is under 90 seconds old. If it goes stale, the page says so and suggests `sudo systemctl status lucia-package-updates`.

## Everyday and platform updates

Updates are classified into two groups.

| Group | What's in it | Default |
|---|---|---|
| **Everyday updates** | Ubuntu and application packages. "Safe to install while you work." | New ones start selected |
| **Platform updates** | Kernel, NVIDIA driver, CUDA, DGX, the container runtime, GRUB, shim, firmware, microcode | Always opt-in. "Install these on their own, ideally after hours." |

Platform updates need their own confirmation. The checkbox reads "I understand Local AI will be unavailable and the Spark may restart." The worker enforces the same rule. If an everyday install would pull in a platform package as a dependency, it refuses and tells you which one.

Every package row has a **Changelog** link that fetches the package's changelog through the worker.

### What happens during an install

1. The worker simulates the install first (`apt-get -s install --only-upgrade`).
2. If the install would **remove** any package, it stops. Lucia doesn't remove packages during updates.
3. For platform installs, Lucia unloads Local AI first. Apps using Local AI get errors until it's back. The models reload when the updates finish, or after the Spark restarts. A three-hour guard makes sure Local AI isn't left paused forever if the worker disappears.
4. The worker runs `apt-get install --only-upgrade` with the packages you selected, non-interactively, keeping your existing config files.
5. Afterwards it does what you chose under **Afterwards**:
   - restart services, and the Spark if an update needs it;
   - restart only the services using updated files;
   - don't restart anything.

Service restarts use `needrestart`. Lucia configures it never to restart the worker, Docker or containerd behind your back. Docker and containerd updates are flagged separately: installing them restarts Docker, so Lucia goes offline for about a minute, while the Spark itself keeps running.

The install keeps running if you close the page. Progress, the current package and a live tail are shown on the page. Failures keep their log in **Update history**.

### Restarts and repair

When a restart is pending (for example "A new kernel is installed but the Spark is still running the old one"), the page offers **Restart now** or **Schedule restart**.

> [!CAUTION]
> **Restart Spark** reboots the controller. Lucia, Local AI and everything else on the Spark will be offline for a few minutes. Managed servers keep running their apps, but nothing can manage them until the Spark is back.

If an earlier install stopped part-way, **Repair package database** runs `dpkg --configure -a` followed by `apt-get -f install`. Nothing new is installed.

## Scheduled windows

**Schedule** sets a single maintenance window for the selected updates, for one group, or for **Update everything**.

| Rule | Value |
|---|---|
| Earliest start | 2 minutes from now |
| Latest start | 30 days ahead |
| Missed window | Skipped if Lucia was offline more than 60 minutes past the start time |
| Windows at once | One. Cancel or change it to plan another |
| Restart-only window | Yes, with **Schedule restart** |

The time is in your browser's time zone. A scheduled "every update" install takes whatever is available at that time. Without platform updates included, it takes only the everyday ones. The page shows the result of the last window, including a skip.

## Checking for updates

**Check for updates** runs `apt-get update` through the worker, then compares installed packages against the fresh lists. The worker also rescans when the package database changes underneath it, for example after you run apt by hand. Lucia doesn't run `apt-get update` on a timer of its own.

## Automatic security updates

Lucia doesn't install or enable `unattended-upgrades`. It **reports** what it finds:

- **On**: Ubuntu installs security fixes daily by itself. Those installs appear in Lucia's history only when Lucia did them.
- **Off, installed but not enabled**: security fixes wait for you on this page.
- **Off, not installed**: security fixes wait until you install them here.

## Managed servers

This page is for the Spark only. Managed Debian servers get their OS updates through their node agent. On **Devices**, or through the assistant's `check_node_updates` and `node_action` tools, you can check for updates, install them, or restart a server. Installing updates and restarting always ask first in chat. See [Nodes](/docs/architecture/nodes/) and [Assistant and jobs](/docs/architecture/assistant-and-jobs/).
