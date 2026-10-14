---
name: app-troubleshooting
description: Diagnose an app that is down, unhealthy, restarting or misbehaving, from its state and logs, then propose a fix. Use when the owner reports an app problem or asks why something isn't working.
metadata:
  lucia.execution: node
  lucia.tier: read
---

# App troubleshooting

Look first, change nothing until you know the cause.

## Investigate

1. `list_apps` for the app's server, desired and actual state, revision and image updates. A mismatch between desired and actual state is the first clue.
2. `get_app` for its compose, environment (secret values are hidden), web addresses and container status.
3. `list_containers` with the `node` shows every container and listening port the server last reported, including ones Lucia doesn't manage. Look for a stopped or restarting container and for a port clash.
4. `read_logs` with the `node` and `container` name (from `get_app` or `list_containers`). Start with the default 200 lines; raise `tail` only if the cause isn't there.
5. If the server itself may be the problem, `list_nodes` or `get_node` show whether it is reporting, and `get_spark_health` covers the Spark.
6. For a catalog app, `get_catalog_app` shows the settings and requirements it expects. `read_web_page` can read the project's documentation for an error message.

## Propose a fix

Say what is wrong, the evidence (a log line, a state), and the smallest fix. Typical fixes:

- `app_action` with `restart` for a stuck container, or `start` for a stopped app.
- `save_custom_app` with `edits` for a compose mistake (see the custom-app-authoring skill).
- `install_catalog_app` to change a catalog app's settings.
- `app_action` with `update`, or `upgrade_app_images`, when the logs point at a known bug in an old image (see container-image-updates).
- `restore_backup` only as a last resort: it replaces the app's data.

In Execute mode, apply one fix, then re-check `get_app` and `read_logs` to confirm. In Plan mode, describe the fix and stop. If a fix needs a shell, use `run_command` sparingly: read before you change, one step at a time.
