---
name: container-image-updates
description: Check whether the container images Lucia's apps run have newer versions, and update them. Use when the owner asks what is out of date, whether an image has an update, or to update or upgrade apps.
metadata:
  lucia.execution: node
  lucia.tier: read
---

# Container image updates

Registry lookups go through `query_registry` and `list_node_images` only. Never use `run_command` or `read_web_page` to look an image up.

## Find what may be out of date

1. `list_apps` shows each app's server, state and any image updates Lucia already knows about. Start there.
2. For an app you want to check closely, `get_app` lists its images (in the compose).
3. `list_node_images` with the app's `node` (and `image` to narrow it) gives the registry digest each pulled image came from.
4. `query_registry` with the image **without a tag** (such as `ghcr.io/home-operations/jackett`, or `nginx` for Docker Hub) returns the tag count, the digest of `latest` and the digests of the 5 newest tags. Use `search` for a specific tag family such as `8.9` or `alpine`.
5. Compare digests. If the digest of the tag the app runs differs from what the registry has now, the image changed. If a newer version tag exists, the app can move up.

Tags are ordered by version number; registries give no push dates, so don't claim when something was released.

## Report

Give a short list: app, current tag or digest, what is newer, and whether it looks like a major jump. Say which images you could not check and why (for example a private image needs the registry's sign-in in Settings → Registries; an unreachable registry may wait for the owner's approval).

## Update (Execute mode only)

- `app_action` with `update` pulls the app's images again and redeploys it. Use it for apps that track a tag such as `latest`.
- `upgrade_app_images` pins an app's images to their newest versions and redeploys it. Leave `major` false unless the owner agrees to a major version jump, which may need a migration.
- Do one app at a time. Afterwards check `get_app` or `list_apps` for a running state, and `read_logs` if it isn't.
- In Plan mode, list what you would update and stop.
