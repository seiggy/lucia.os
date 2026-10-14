---
name: os-security-patching
description: Check for and install operating system package updates on the DGX Spark and the managed servers, one machine at a time, verifying health in between. Use for patching, security updates, or "are my servers up to date".
metadata:
  lucia.execution: node
  lucia.tier: read
---

# OS security patching

The Spark that runs Lucia is not a managed server: it has its own tools. Always do the Spark first, then the servers one at a time. Stop at the first thing that looks wrong.

## 1. The Spark

1. `check_spark_updates` asks it to check now; read the result with `get_spark_updates`.
2. `get_spark_updates` marks security and platform updates (kernel, driver, CUDA) and whether a restart is needed.
3. In Execute mode, `install_spark_updates`:
   - Leave `packages` out for all available updates, or pass names from `get_spark_updates`.
   - Platform updates install only with `includePlatform`, and they pause local AI while they install. Use it only when the owner agreed.
   - Keep `restart` at `none`. The tool never restarts the Spark; if `get_spark_updates` says a restart is needed, tell the owner to do it from the Spark page.
4. Check `get_spark_health` afterwards.

## 2. Each managed server

Use `list_nodes` for the servers and their pending update counts. For each server, one at a time:

1. `check_node_updates` with the `node`, then read `list_nodes` or `get_node` for what is pending. Skip a server with nothing pending.
2. `node_action` with `install-updates`. Apps on that server may restart briefly.
3. If `list_nodes` shows a restart is needed and the owner allowed restarts, `node_action` with `restart`. Every app on that server is offline for a few minutes.
4. Verify before the next server: `list_nodes` shows it reporting again, and `list_apps` shows its apps running (desired and actual state agree). Use `list_containers` and `read_logs` to look into one that isn't.

## Report

For each machine: what was pending, what was installed, whether it restarted, and its health afterwards. If you stopped early, say where and why.

In Plan mode, list what you would install and restart, in order, and change nothing. Never restart or patch a server the owner didn't ask about.
