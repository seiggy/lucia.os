---
name: lucia-docs-screenshots
description: Take screenshots of Lucia's web console for the docs, from fixture data and never from a real lab. Use when a docs page needs a picture of a screen.
---

# Docs screenshots

Screenshots live in `docs-site/static/img/shots/` and are used with the `Screenshot` component (see `lucia-docs`).

## Rules

- Fixture data only. A real lab's addresses, MACs, hostnames, domains, key hints, emails and model names must never appear. `src/frontend/public/_fixtures/` (`map.json`, `live.json`) holds sample lab data, and `.impeccable/checks/*-fixture.mjs` shows how to fake the host's API for a page.
- PNG, at most 1440 px wide, light theme unless the page is about dark mode. Crop to the part the page talks about when the full console would bury it.
- Name by screen and state: `apps-list.png`, `assistant-approval-card.png`.
- Alt text says what the picture shows ("The Apps page with Jellyfin running and Sonarr stopped").

## How

1. From `src/frontend/`, start the dev server with `npm run dev`.
2. Use the `playwright-cli` skill to open the page at a 1440 × 900 viewport. For pages that call the host, route `/api/**` requests to fixture responses, the way `.impeccable/checks/assistant-fixture.mjs` does for `/api/assistant`.
3. Capture the element or the viewport, save to `docs-site/static/img/shots/<name>.png`, and look at the result before using it.
4. Stop the dev server.

The older pictures in `site/public/screenshots/` came from a real lab with masking. Reuse them where they are good enough, but do not add new ones that way.

## Cannot take one?

Leave `{/* TODO screenshot: <name> — <what it should show> */}` in the page and list it in your report. A missing screenshot is better than a wrong one.
