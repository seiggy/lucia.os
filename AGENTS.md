# Agent notes

Lucia is a homelab platform (Spark controller + managed nodes). Lab operations live in skills under `.github/skills/`; use them instead of rediscovering how things work:

- `lucia-lab`: topology, ssh access, secrets handling.
- `lucia-host-deploy`: build and push web UI, server, templates and Spark-side Python to the Spark.
- `lucia-stack-templates`: editing catalog stack templates, version bumps and re-rendering installed stacks.
- `lucia-node-agent-upgrade`: upgrade the node agent on managed nodes.
- `lucia-installer-deploy`: PXE installer bundle qualification and deploy.

Commit, push and deploy only when the user asks. Never print secrets.

## Conventions
- `.cs` files use CRLF; `src/frontend/src/UniverseScene.ts` is LF and ASCII-only.
- Repo is public: no secrets, no licensed assets (`public/universe/meshes/jumpgate/` is gitignored); avoid new lab IPs/hostnames in docs.
- Python isn't installed locally; use `uv`, `node`, WSL, or run it on the Spark.
- Lab map preview: Vite on port 5199 with `universe.html`; `tools/demo/gate.mjs` screenshots gates.
- Commit trailer: `Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>`.

## Documentation
- Docs live in `docs-site/` (Docusaurus, mounted at `/lucia.os/docs/`); the landing page stays in `site/` (Astro). Load the `lucia-docs` skill before touching either.
- Three audiences: `docs/guide` (homelab users new to computers), `docs/deploy` (DevOps engineers), `docs/code` (developers). Each page opens with `WhoWhatWhy`; domains answer who, what and why in the body. Use code snippets from real code, Mermaid diagrams, MDX components and screenshots (fixture data only, `lucia-docs-screenshots` skill).
- As you change behaviour, settings, tools, endpoints, flows or deployment, update the docs in the same change. `docs-map.json` maps source paths to pages; add an entry when you add a domain.
- Write every sentence of docs prose with the `humanize-writing` skill and review it against that skill before committing docs (the skill's "not for technical writing" note is overridden for Lucia's docs; code, commands, paths and numbers never change). `node tools/docs/lint-prose.mjs` checks the worst offenders.
- The pre-commit hook runs the `docs-keeper` agent (default model `claude-haiku-5.5`) on staged product changes and stages its edits. Install it once per clone with `node tools/docs/install-hooks.mjs`; skip one commit with `LUCIA_SKIP_DOCS=1`. Override the model with `LUCIA_DOCS_MODEL`.
