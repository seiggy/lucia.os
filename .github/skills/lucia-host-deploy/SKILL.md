---
name: lucia-host-deploy
description: Build and deploy Lucia web UI, server, rendered templates/stacks and Spark-side Python to the Spark. Use when the user says push/deploy/publish to the Spark or lab.
---

# Host deploy to the Spark

Covers `src/frontend`, `src/Lucia.Homelab.Server`, stack/observability/Garage/AdGuard templates rendered by the server, and `tools/host/*.py`. Load `lucia-lab` for access details. Scripts are in `scripts/`; run them from a scratch dir (output lands next to them).

1. **Clean copy** into `%TEMP%\lucia-clean`: robocopy `src` (exclude `node_modules bin obj dist _fixtures universe.html`) and `tools\host`. Delete stale folders first after renames. Also copy gitignored `src\frontend\public\universe\meshes\jumpgate` (licensed; rebuild with `node tools/assets/build-jumpgate.mjs <pack dir>`).
2. **Build:** `scripts\build-clean.ps1 -Controller <name>` -> `spark-updates\host-linux-arm64.tar.gz` + `manifest.json`, prints the artifact sha. (`build.ps1` builds from the working tree and also writes `managed-node-agent.tar.gz`.)
3. **Upload** tarball, `manifest.json` and `deploy-agent-jobs.py` to `zackw@192.168.0.222:.cache/lucia-assistant-deploy/`.
4. **Run:** `ssh zackw@192.168.0.222 "cd .cache/lucia-assistant-deploy && python3 deploy-agent-jobs.py"`. ~5-10 min, rollback-safe, requires the PXE discovery window closed, and asserts strings in the served JS bundle (update the asserts if UI text changes).
5. **Verify** with `curl -sk https://lucia.homelab.seiggy.com/...` and the UI.

`deploy-agent-jobs.py` is the template for other payloads: add files to `SOURCES` (they are copied into `~/.local/share/lucia/bootstrap/app/`; required for `tools/host/package.py`, `provision_host.py`, `AppHost.cs`).

## Gotchas
- `package.py` drops path segments `models bin obj src config logs`; 3D assets live in `public/universe/meshes`.
- `wwwroot` files need an extension in `WEB_EXTENSIONS`; new static types also need a MIME map in `Host/HostWebHosting.cs`.
- The Spark validates artifacts with its own `package.py` copy; sync it via `SOURCES`.
- If the node agent changed, follow with `lucia-node-agent-upgrade`; installer overlay changes need `lucia-installer-deploy` (host deploy first).
