---
version: 1
slug: "src-frontend-src-labmap-tsx"
primary_target: "src/frontend/src/LabMap.tsx"
related_targets: ["src/frontend/src/LabMap.css","src/frontend/src/labMap.ts"]
---

# Lab map

Scope: new Owner-only portal page, Your lab → Map (#/map), with a full-screen wallboard mode. Mode: Operate, with an Experience-grade canvas.
Audience and job: the owner, a beginner or an enthusiast, checks on the lab or shows it off; it is also left running on a TV. Within five seconds they see what is alive, busy and broken, and within two clicks they reach the real page or ask the assistant.
Proof: every object and light comes from UniFi, the node agents, the stack store or Prometheus. Missing data is shown dim and labelled, never invented.
Constraints: portal chrome stays DESIGN.md; only the canvas owns the neon world. Flat 2D fallback for reduced motion or no WebGL. A mirrored keyboard/screen-reader list. The only write is client reclassification.
Brief: session file map-shape-brief.md (confirmed 2026-10-10).

## Direction contract

THESIS: The lab is a place you enter, not a diagram you read: a cyberspace grid where every construct is real and every light is measured traffic. It refuses the category default, the icon node-link topology graph.

OWN-WORLD: Void #04050a ground; a cyan hairline grid fading into horizon fog. Constructs are 1px wireframe edges with 6–10% translucent faces. Neon is meaning, never decoration: cyan #00f0ff traffic, acid #a6ff00 running, amber #ffb000 attention, red #ff4d6d failed, magenta #ff2bd6 only for selection and the lit route. HUD plates use Chakra Petch 500 uppercase with a hairline leader. Toolbar and detail panel stay portal (surface-dark, system stack, Ocean accent).

STORY: The owner sees the whole lab alive at once and notices what is busy or broken. They search or click into one thing, watch its route to the WAN light up, read the facts, and jump to Manage, the app or logs, or ask the assistant.

FIRST VIEWPORT: A slim portal toolbar (Map, search, layer toggles, Wallboard) over a full-workspace canvas. The camera tilts about 35° toward the gateway portal at centre, a ring gate whose WAN beam rises to the horizon. VLAN constructs ring the gateway, each a wireframe plinth with a name and subnet plate; server towers rise inside, stacked with container blocks; client swarms drift with count plates; packets streak along the lanes. A status strip sits bottom-left with counts and freshness. The detail panel docks right only on selection.

FORM: Cyberspace grid, position 1 of 7 on the re-rolled list (round 1, steer "conceptual, cyberpunk, digital universe"); seed key 03ed2053. Signature interaction: select, fly-to, and the route lights hop by hop.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
