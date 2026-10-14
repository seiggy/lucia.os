---
name: lucia-lab
description: Lab topology and access for the Lucia homelab (Spark, lucialab01, Proxmox, TrueNAS). Use before any ssh, API call, log check or deploy against the lab.
---

# Lucia lab access

- **Spark** (DGX Spark, arm64): `ssh zackw@192.168.0.222` (alias `spark` fails host-key checks). Runs the controller and portal at `https://lucia.homelab.seiggy.com`, identity, step-ca, AdGuard, Traefik, inference. State in `~/.local/share/lucia/`: secrets in `identity/secrets/` (e.g. `host-owner-api-key`), CA at `identity/trust/lucia-root-ca.crt`, origin in `identity/host-settings.json`.
- **lucialab01** (managed Debian node, x64): `ssh lucia-recovery@192.168.0.241`, passwordless sudo (`sudo -n docker ...`). Runs observability (`lucia-observability-prometheus-1`, Thanos) and Garage. Agent in `/usr/lib/lucia/agent`, service `lucia-node-agent`.
- Others: lucialab-temp (test VM on proxmox01), proxmox01/02 (proxmox02 has KVM for qualification), TrueNAS (REST only), UniFi UDM Pro.
- Local machine is Windows; **Python is not installed** (use `uv`, `node`, WSL, or run on the Spark).
- Pipe scripts through `tr -d "\r" | bash` over ssh; strip CRLF from uploaded `.py` with `sed -i 's/\r$//'`. Never `cat $(empty)` over ssh (hangs).
- Host API scripts: write to `$env:TEMP`, scp to Spark `/tmp`, read the owner key there. `/api/host/nodes` has node and GPU data.
- Never print secrets. Commit, push and deploy only when the user asks.

Deploy skills: `lucia-host-deploy` (web, server, templates), `lucia-node-agent-upgrade`, `lucia-installer-deploy` (PXE).
