---
name: lucia-stack-templates
description: Change, version and roll out Lucia catalog stack templates (local-ai, observability, object-storage, adguard, immich, etc.) running on managed nodes. Use when editing StackCatalog.cs or re-rendering an installed stack.
---

# Stack templates and versioning

Catalog apps are C# classes in `src/Lucia.Homelab.Server/Stacks/StackCatalog.cs` (`internal sealed class XApp() : CatalogApp("id", <version>, ...)` with `Render(settings, node, env)` producing compose, env, requirements and routes). Installed stacks store `manifest.template = {id, version, settings}`; the UI/API compare it with the catalog's `Version` (`template.latest`) and flag out-of-date stacks.

## Rules
- **Any change to the rendered compose/env/config of an app must bump its `Version`** (the integer in the constructor). Otherwise installed stacks never see it. Current versions: `grep "CatalogApp("`.
- Image digests are pinned (`image:tag@sha256:...`). Updating images = new digest in the template + version bump. Per-stack image updates (minor/major) are separate: `POST /api/host/stacks/{name}/upgrade-images {major}` from the Stacks UI, which edits the stored compose rather than the template.
- Escape `$` as `$$` in embedded configs (compose interpolation), e.g. Grafana dashboards JSON `.Replace("$", "$$")`.
- Adding a Grafana dashboard or config means a new `configs` entry plus a mount, and a version bump.
- The agent redeploys a stack only when its **revision** changes; anything that edits a stack out-of-band (e.g. SSO env injection via `StackStore.SetSso`) must bump the revision.
- Add or update checks in `tests/Lucia.Homelab.DomainChecks/StackChecks.cs` and run them.

## Rolling out a template change
1. Edit the template, bump `Version`, run the DomainChecks.
2. Deploy the host: `lucia-host-deploy`. Deploying the server does **not** touch installed stacks.
3. **Re-render** each installed stack onto the new version (Owner only, needs CSRF):
   - `GET /api/host/stacks/<name>` for `stack.revision`, `placement` and `manifest.template.settings`.
   - `PUT /api/host/stacks/<name>` with `{"compose":null,"env":null,"expectedRevision":<revision>,"manifest":{"schemaVersion":1,"placement":<placement>,"template":{"id":"<id>","version":<new>,"settings":<settings>}}}`. The server re-renders and keeps its own requirements; the revision bumps and the node redeploys.
   - Auth options: (a) the Playwright browser logged in at `https://lucia.homelab.seiggy.com` with `page.evaluate(fetch ...)` and header `X-CSRF-TOKEN` from `/api/auth/session` `csrfToken`; or (b) a script on the Spark using the owner key `~/.local/share/lucia/identity/secrets/host-owner-api-key` (see `lucia-lab`).
4. Verify on the node (`lucia-lab`): `sudo -n docker ps`, container logs, and the stack's status in the UI. Stack files live in `/srv/lucia/stacks/<name>`.

## Notes
- ObjectStorage and Observability ship bootstrap scripts (e.g. Garage bucket/key creation via `GetBucketInfo` then `CreateBucket`); changes there also need a version bump and re-render.
- Stack backups use restic (`--tag lucia:stack=<name>`); flows skip stacks that are moving or restoring.
