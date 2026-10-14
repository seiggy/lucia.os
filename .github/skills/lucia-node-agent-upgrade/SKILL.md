---
name: lucia-node-agent-upgrade
description: Upgrade the Lucia node agent on managed nodes (lucialab01, lucialab-temp) after agent code changes.
---

# Node agent upgrade

1. Build the agent: `lucia-host-deploy`'s `build.ps1` writes `managed-node-agent.tar.gz` (`dotnet publish src\Lucia.NodeAgent -c Release -r linux-x64 --self-contained`).
2. Run Linux checks if the agent changed: copy `src/Lucia.NodeAgent` and `tests/Lucia.NodeAgent.Checks` (no bin/obj) into WSL `/tmp`, `dotnet run` with `PATH=$HOME/.dotnet:$PATH`.
3. `scp managed-node-agent.tar.gz scripts/upgrade-agent.sh lucia-recovery@<ip>:/tmp/`, then `ssh lucia-recovery@<ip> "sudo bash /tmp/upgrade-agent.sh"` (strip CRLF first). It stops the service, backs up to `/usr/lib/lucia/agent.prev`, extracts, and restarts; it prints `active` when healthy.
4. Node IPs and access: see `lucia-lab`.
