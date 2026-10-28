---
name: platform-image-updates
description: Check and apply updates to the Spark's platform images (Authentik, the Traefik gateway, step-ca, PostgreSQL, OpenLDAP and the telemetry relay) and refresh the NVIDIA device list. Use when the owner asks whether Lucia's own services are up to date, to patch Authentik or the gateway, or when a GPU container won't start after package updates.
metadata:
  lucia.execution: node
  lucia.tier: read
---

# Platform image updates

The Spark runs third-party images that Lucia pins itself: Authentik (server and worker), the Traefik gateway, step-ca and its renewer, PostgreSQL, OpenLDAP, the OpenTelemetry relay and two exporters. They aren't apps, so `list_apps` and `upgrade_app_images` never cover them. You name services; Lucia picks the version it found, and the Spark's update service checks the digest with the registry again before anything restarts.

## Tools

| Tool | Tier | Use |
|---|---|---|
| `list_platform_image_updates` | read | Each service's running version, offered versions, their kind, release notes and days waiting |
| `apply_platform_image_updates` | change | Update services by name, optionally `patchOnly`; waits for the Spark's result |
| `refresh_nvidia_device_list` | change | Generate the NVIDIA device list again when it's out of date |
| `get_spark_updates` | read | The Spark's OS packages, including `gpuDevices` |

## 1. Look first

Call `list_platform_image_updates`. For each service note the running tag, each offer's `kind` and `applicable`, and `waitingDays`.

- `rebuild` is the same tag rebuilt with OS fixes, and `patch` changes only the last number. Both are low risk.
- `minor` stays on the same major version.
- `release-line` is Authentik's next line (2026.8 to 2026.10, say). Authentik migrates its database and can't skip a line, so it moves one line at a time.
- `major` and the CrowdSec plugin are shown with `applicable: false`. They need a new Lucia release; say so and move on.

Give the owner each offer's `notes` link, and `upgradeNotes` for a release line. They don't have to read them, but they should know where they are.

## 2. Check before you apply

- If `qwenRunning` is true, say so before you apply. Qwen3.8-Flash-Next holds most of the Spark's memory, so an update is slower and more likely to fail; the owner may want to stop it on the AI page first. Lucia still allows the update, and the result repeats this as `warning`.
- If `gpuDevices.current` is false, run `refresh_nvidia_device_list` first and check it came back current. Until then the update service refuses, because the GPU exporter can't be recreated.
- If `status.state` is `working`, an update is already running. Wait.

## 3. Apply in this order

1. Patches first: `apply_platform_image_updates` with every service that has a `patch` or `rebuild` offer and `patchOnly: true`.
2. Then `minor` offers, one or two services at a time.
3. An Authentik `release-line` last and on its own, only after its patches. One line per update, never two in a row without checking Authentik works in between.

Tell the owner what restarts before you call it. Authentik, the gateway, PostgreSQL or OpenLDAP restart every web route for about 10 to 15 seconds, sign-in included. The relay and exporters only lose a few seconds of metrics. The tool's result repeats this as `disruption`.

## 4. Read the result

The tool waits for the Spark. `succeeded` means every service came back healthy (Authentik takes about 30 seconds). `failed` with `rolledBack: true` means the Spark put the previous images back, and for a release line restored Authentik's database from the backup named in `backup`. `busy` means another deploy or a package install held the Spark; try again later. Quote the message as it is.

## Never

- Move Authentik more than one release line in one go, or with other services in the same call.
- Offer to apply a `major` or the plugin. Those wait for Lucia.

In Plan mode, list what's offered and the order you'd apply it, and change nothing.
