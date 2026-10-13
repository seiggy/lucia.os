---
name: lucia-docs
description: How Lucia's documentation is organised, written and kept current. Use whenever you create or edit anything under docs-site/, when a code change needs its docs updated, or when asked to document a feature, domain or the platform.
---

# Lucia documentation

The docs are a Docusaurus site in `docs-site/`, published at `/lucia.os/docs/` next to the Astro landing page in `site/`. A documentation agent (`.github/agents/docs-keeper.agent.md`) runs from the pre-commit hook and uses this skill. People use it by hand too.

## Three audiences, three folders

Every page belongs to exactly one folder. Write for that reader and no one else.

| Folder | Reader | Focus |
|---|---|---|
| `docs/guide/` | A homelab user who is newer to computers and servers | What to click, what it does for them, what to do when something looks wrong. No jargon without a one-line explanation. No source code. |
| `docs/deploy/` | A DevOps engineer running the platform | Infrastructure and deployment architecture: machines, networks, containers, ports, certificates, secrets, upgrades, backups, failure modes, how to check health. |
| `docs/code/` | A developer changing the platform | How a domain is built: projects and folders, the main types, data flow, extension points, tests, and the reasoning behind the design. Code snippets and diagrams. |

The same feature often needs a page in two or three folders. Link them to each other instead of repeating content.

## Who, what, why

Every page starts with the `WhoWhatWhy` component right under the title. Say who the page is for, what it covers, and why that part of Lucia exists. Pages about a domain (assistant, stacks, identity, nodes, networking, updates, local AI, observability, installer) answer all three in the body too: who uses it or touches it, what it does, and why it was built this way.

```mdx
---
title: Assistant
description: How the assistant decides what it may do.
---

import WhoWhatWhy from '@site/src/components/WhoWhatWhy';

# Assistant

<WhoWhatWhy
  who="Developers changing the assistant's tools, permissions or runtime."
  what="The tool tiers, the broker that approves each call, and how a chat turn runs."
  why="An assistant that can restart servers has to be safe by construction, not by prompt."
/>
```

## Page shape

- Front matter: `title` and `description`. Keep `sidebar_position` when ordering matters.
- Start with the answer. Put the reason after it.
- One idea per heading. Headings are short and say what is in the section.
- Prefer a table for lookups, a list for steps, prose for reasoning.
- Link to the code with `<Source path="src/..." />` from `@site/src/components/Source`. Use repo-relative paths with forward slashes.
- Link other pages with relative `.md` or `.mdx` paths so the build catches broken links. Broken links fail the build.

## Code snippets

- Copy real code from the repository. Never invent an API. If you are unsure of a signature, read the file.
- Keep a snippet short, usually under 25 lines. Cut with a `// ...` line. Say what to look at before the block, not after.
- Always give the fence a language and, for files, a `title="path/to/File.cs"`.
- Use `{3-5}` line highlights to point at the lines that matter.
- Samples a reader can run (curl against the API, a compose file, a config) are worth more than a description. Check them against the code.

## Diagrams

Use Mermaid code fences. Pick the type that fits: `flowchart` for how parts connect, `sequenceDiagram` for a request or turn over time, `stateDiagram-v2` for lifecycles, `erDiagram` for stored data. Keep one diagram to one idea and under about 12 nodes. Name nodes in plain words, not class names, unless the page is about the class.

## MDX and React

Use the components in `docs-site/src/components/` where they help:

| Component | Use |
|---|---|
| `WhoWhatWhy` | Top of every page. |
| `Source` | Link a path in the repo. |
| `Screenshot` | A picture from `static/img/shots/`. Props `name` (file name without `.png`) and `alt`. Caption goes inside as children. |
| `AudienceCards` | The docs home page only. |

Docusaurus built-ins are fine too: `Tabs`/`TabItem` for per-OS or per-language variants, admonitions (`:::tip`, `:::warning`, `:::danger`) for things that cost someone an afternoon, and `<details>` for long optional detail. Add a new component when the same markup shows up three times.

## Screenshots

Screenshots go in `docs-site/static/img/shots/` as PNG, 1440 px wide at most, with names like `apps-list.png`. Take them from the frontend running against fixture data, never from a real lab, and never with real addresses, names, keys or tokens. See the `lucia-docs-screenshots` skill. If a screenshot is needed and you cannot take one, leave a `{/* TODO screenshot: name — what it should show */}` comment and say so in your report. Do not guess at one.

## Writing the words

Use the `humanize-writing` skill for every sentence of prose you write or change. That skill says it is not for technical writing. For Lucia's docs the owner has overridden that: run all three of its passes, work through its Quality Checklist before you finish, then leave facts alone.

- Never change code, commands, identifiers, paths, numbers, versions, URLs or the meaning of a sentence.
- Voice by folder: `guide` uses **warm-professional**; `code` and `deploy` use **clear-thinker**.
- Read `references/ai-patterns-dictionary.md` in the humanize-writing skill before writing. No Tier 1 words, no "it's not X, it's Y" openers, no rule-of-three padding, no closing summaries that repeat the page.
- Explain with specifics: a real file, a real number, a real failure. Replace "robust" and "seamless" with what actually happens.
- Active voice, present tense, second person for guides ("you"), plain third person for code and deploy pages.

Before committing docs, run `node tools/docs/lint-prose.mjs`. It checks the Tier 1 words from the dictionary outside code blocks. It cannot judge tone, so still read what you wrote against the skill.

## Keeping docs current

`docs-map.json` at the repo root maps source paths to the doc pages that describe them. When code changes:

1. Find the pages the map lists for the changed paths. If a changed path matches no entry, work out which page it belongs to and add an entry.
2. Read the diff for what a person would notice: behaviour, settings, endpoints, tools, flows, defaults, limits, errors, deployment steps. Skip refactors that change none of it.
3. Update only what became wrong or missing. Do not rewrite a page for style when nothing changed.
4. A new domain or feature gets a new page in each folder that has a reader for it, an entry in `docs-map.json`, and links from related pages.
5. A removed feature loses its section. Do not leave "deprecated" notes about things that no longer exist.
6. Check every claim you add against the code.

## Checking your work

From `docs-site/`:

```bash
npm install            # once
npm run build          # fails on broken links and bad MDX
npm run check:diagrams # parses every Mermaid block; the build does not
```

Run the build whenever you add a page or a component, and the diagram check whenever you add or change a diagram.
