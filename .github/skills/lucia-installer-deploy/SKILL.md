---
name: lucia-installer-deploy
description: Qualify and deploy the Lucia PXE installer bundle (deployment/boot, prepare.py, preseed, boot screens). Use for any change to unattended install or boot artifacts.
---

# Installer (PXE bundle) deploy

Any overlay, agent or boot-artifact change alters `installation_payload_sha256` and requires requalification.

1. **Host deploy first** (`lucia-host-deploy`): `deploy-install.py` asserts the live backend sha and agent files match the qualified ones.
2. New boot files must be added to `tools/desktop/bootstrap.py` FILES, `src/Lucia.Desktop.Core/BootstrapPayload.cs` and `Lucia.Desktop.csproj`; update expectations in `scripts/deploy-install.py`.
3. **Pack source** in WSL: `scripts/pack-source.py` -> `install-source.tar.gz` + `.sha256`.
4. **Qualify** (~50 min VM run, async): upload to `~/.cache/lucia-managed-qualification-*/` on the Spark and run `python3 run-managed-vm.py` (`scripts/`); it can use KVM on proxmox02. Check `phase: qualified`.
5. **Deploy:** upload `install-source.tar.gz`, `.sha256`, `deploy-install.py` and `managed-node-agent.tar.gz` to `~/.cache/lucia-managed-install-*/`, then `python3 deploy-install.py prepare` and `python3 deploy-install.py deploy`. Dev shortcut: add `--skip-qualification`.
6. Deploy closes the discovery window; tell the user to reopen it before PXE booting.
