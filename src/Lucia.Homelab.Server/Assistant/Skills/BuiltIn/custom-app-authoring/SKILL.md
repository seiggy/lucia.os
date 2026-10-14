---
name: custom-app-authoring
description: Create or change a custom app (the owner's own docker compose) with save_custom_app, including secrets, placement and web addresses. Use when the owner wants to run something that isn't in the catalog.
metadata:
  lucia.execution: node
  lucia.tier: read
---

# Custom app authoring

A custom app is a docker compose Lucia runs on one of its servers. Catalog apps are different: change those with `install_catalog_app`, and `save_custom_app` refuses them.

## Before you write

- `list_catalog` first: if Lucia already offers the app, prefer installing it.
- `list_nodes` and `get_node` to pick a server, and to see GPUs if the app needs one.
- `list_apps` to avoid a name clash. App names are lowercase letters, digits and dashes.
- If you need the image's tags or options, `query_registry` for tags and `read_web_page` for its documentation.

## Saving

- Send the whole `compose` for a new app or a rewrite. For a saved app, send `edits` (find/replace; each `find` must occur exactly once) instead of resending it. Don't send both.
- `node` sets the server for a new app (use `move_app` to move one later). `require` sets placement requirements such as `gpu=nvidia`; it replaces the saved list.
- `address` gives the app a LAN address of its own; `routes` lists its web addresses and replaces the saved list. Publishing on the internet is a separate step, `set_public_route`, which always asks the owner.
- Environment: `setEnv` sets plain values; values you don't mention are kept. Reference them from the compose as `${NAME}`.

## Secrets

- Never write a password, key or token yourself, and never ask for one in the chat.
- A secret nobody needs to know (a database password): list its variable in `generate`. Lucia fills in a strong random value and keeps it.
- A secret only the owner has (a claim token, an API key): `request_secret` with the `app`, the variable `name` and a `description` of what it is and where to find it. Name it as a secret, such as `PLEX_CLAIM_TOKEN` or `DB_PASSWORD`, and pass it to the container in the compose, such as `PLEX_CLAIM: ${PLEX_CLAIM_TOKEN}`. The owner types it; you never see it.
- Values shown as `[redacted]`, `[hidden]` or `••••••` are placeholders: never put them in a compose or env.

## After saving

Lucia delivers the change within a minute. Check `get_app` or `list_apps`, and `read_logs` if it fails to start. Tell the owner what you created and how to check it.
