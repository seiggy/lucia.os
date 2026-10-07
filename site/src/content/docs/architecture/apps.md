---
title: Apps
description: How Lucia runs container apps on managed servers, from the catalog or your own compose files, with storage, registries, backups and updates.
section: architecture
order: 6
---

An **app** in Lucia is a Docker Compose stack that runs on one managed server. You pick it from the catalog or paste your own compose file. Lucia handles where it runs, its address, its storage, its name, its backups and whether its images are out of date. The node agent does the Docker work on the server, so the Spark's web host never needs a Docker socket.

![The Apps page listing installed apps and the servers they run on](/screenshots/apps.png)

## The catalog

**Your lab → Apps → Add an app** lists the apps Lucia knows how to install and wire up:

| Area | Apps |
|---|---|
| Lab infrastructure | AdGuard Home, Observability (Prometheus, Loki, Tempo, Grafana), GitHub Actions runner |
| AI | Local AI (engines: Lucia, vLLM, llama.cpp), LiteLLM |
| Photos and media | Immich, Plex, MusicBrainz |
| Media automation | Sonarr, Radarr, Lidarr, Seerr, Jackett, NZBHydra 2, FlareSolverr, a download client behind a gluetun VPN |
| Home | Home Assistant, Mosquitto, Voice (Wyoming Whisper, Piper and openWakeWord), ESPHome, Matter Server, Music Assistant, Node-RED |

Some catalog apps come with extra wiring:

- **AdGuard Home** turns on the [AdGuard HA](/docs/architecture/adguard-ha/) fleet.
- **Observability** turns on telemetry relays on every server. See [Observability](/docs/architecture/observability/).
- **Grafana, Immich and LiteLLM** sign in with Lucia. Each gets its own Authentik OIDC application, bound to `lucia-owners`.
- **The GitHub Actions runner** can run on the Spark on demand to get native ARM64 jobs. It runs Docker-in-Docker privileged, and the catalog warns you about that.

### Your own apps

**Your own app** takes a compose file, up to 128 KB, and an optional env file, up to 32 KB. Lucia stores them and the node agent applies them. That's the escape hatch for anything not in the catalog.

## Placement and limits

You choose a server for each app. You can **move** it later, and moving takes its data along to the new server.

| Limit | Value |
|---|---|
| Apps in total | 64 |
| Apps per server | 32 |
| Compose file | 128 KB |
| Env file | 32 KB |

Each app has a page with:

- its containers;
- start, stop and restart;
- the tail of each container's logs;
- its ports and web addresses;
- its own IP address, if it has one;
- delete.

**Apps → Containers** shows every container on every server.

> [!CAUTION]
> **Delete app** takes the containers down and deletes the app's compose and environment files from its server. The app's data in `/srv/lucia/stacks/<app>/` stays on that server until you remove it yourself. When the assistant deletes an app with `delete_app`, it always asks.

## Addresses and names

By default, an app's ports bind on its server's address. An app can also have **an IPv4 address of its own**, which is how AdGuard instances get their DNS addresses. The node agent claims the address next to the server's own, but only after checking that no other device answers for it. If the app moves, the old server releases the address first.

With a domain active, each app's web name, such as `immich.<namespace>`, is routed through the Spark's gateway with a real certificate.

Apps can also be made reachable from the internet through Cloudflare's proxy. That's opt-in for each app, as described in [Networking and DNS](/docs/architecture/networking-and-dns/).

## Storage

**Settings → Storage** adds shares from your NAS:

- **NFS**, mounted with `_netdev,nofail,hard,noatime`.
- **SMB/CIFS**. The credentials are encrypted on the Spark. On each server they're kept in a private credentials file under `/etc/lucia/nas`.

Every share is mounted on **every** server at the same path, `/mnt/lucia/nas/<nas>/<share>`. An app keeps its paths when it moves. Lucia only mounts shares and never changes anything on the NAS.

## Registries

**Settings → Registries** holds sign-ins for private container registries such as Docker Hub, GHCR or a self-hosted one. You can add up to 16. Lucia checks each sign-in with its registry before saving it. It keeps the secret encrypted on the Spark and gives it to every managed server's Docker. The image update checks use the same sign-ins.

## Backups

**Apps → Backups** backs up each app's folder every night with **restic** to one NAS share you choose as the destination. Backups are encrypted and deduplicated.

| Setting | Value |
|---|---|
| Schedule | Daily at 03:00, in the owner's time zone |
| Kept | 7 daily, 4 weekly, 6 monthly |
| Per-run time limit | 12 hours |

You can also choose **Back up now**, or **Restore…** any kept snapshot. Restoring stops the app and sets its current data aside in `/srv/lucia/stacks/.replaced-<app>-…`. The app then starts on the snapshot. Nothing is deleted. The assistant's `restore_backup` tool still always asks first.

## Image updates

Every 6 hours, Lucia checks whether a newer image exists for each app's tags, and flags each app as **Update available** or **Major update**. **Update** pulls the new images and restarts the app on them. The assistant can do the same with `upgrade_app_images`, which is a change tool. Whether it asks first depends on your settings in **Settings → Assistant**.

## Telemetry

While the Observability app is installed, Lucia gives every app an OTLP endpoint through its environment, pointing at that server's relay. It rechecks this every 15 seconds, so apps that support OpenTelemetry send logs, metrics and traces without extra setup.

## How it actually runs

The Spark stores each app's desired state: compose file, env, placement, address, mounts and registry credentials. On each server, the node agent's stack runner:

- pulls that state over its authenticated channel;
- writes the files;
- runs Docker Compose locally;
- reports container state back.

The controller can't run arbitrary Docker commands on a node. It can only publish the desired state that the agent applies.
