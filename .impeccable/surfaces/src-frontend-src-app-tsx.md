---
version: 1
slug: "src-frontend-src-app-tsx"
primary_target: "src/frontend/src/App.tsx"
related_targets: ["src/frontend/src/PortalNavigation.tsx","src/frontend/src/PortalNavigation.css","src/frontend/src/navigation.ts","src/frontend/src/LocalAI.tsx","src/frontend/src/ModelManager.tsx","src/frontend/src/InferenceKeys.tsx","src/frontend/src/AssistantDock.tsx","src/frontend/src/AssistantDock.css"]
---

# Responsive workspace portal

- Scope: App.tsx and the shared PortalNavigation shell across Home, Devices, installation tasks, Playground, Models, API keys, and Appearance. Visitor mode: Operate.
- Audience: novice homelab owners and experienced operators. Recognize where a tool lives without knowing the application's implementation.
- User-selected structure: compact area switcher, NOT a permanent side navigator. Overview, Your lab, Local AI, and Settings organize implemented tools. Local AI's Playground, Models, and API keys are visible siblings. The original layout-C three-tab shell is retired; its consumer clarity, neutral palette, system type, rounded controls, and changeable appearance remain.
- Navigation: sticky area selector plus contextual sibling links; desktop current-location breadcrumb; searchable tool directory via Find a tool and Ctrl/Cmd+K. Native dialog provides Escape/focus containment and restores the opener. On phones the same grouped directory becomes a full-height labeled sheet. Do not add unavailable tools as pretend destinations.
- Home: direct task links precede real Spark and model availability. Desktop pairs host readings with model status; smaller layouts stack them. Errors and unknown readings remain explicit.
- Focused work: Models opens the installed library; Find & download is a separate deep-linkable view with provider connection disclosed beside acquisition. API keys opens the existing list and offers New API key. No change to one-time secrets, permissions, download/load policies, or destructive confirmations.
- Playground: retain draft/transcript only in memory across portal navigation; no localStorage persistence. Browser refresh, sign-out, or losing the authenticated view clears it. The unconditional Live badge is removed.
- Responsive evidence: synthetic fixtures at 320/390/820/1440; navigation, role filtering, sibling transitions, draft/transcript preservation, and keyboard focus tested. Current review captures are .impeccable/review/portal-*.png; these are implementation evidence, not real hardware readings.
- Motion: one short navigation opening transition, reduced-motion safe. No theatrical page loads, fake status animation, or decorative infrastructure graph.

| Ingredient | Implementation commitment |
|---|---|
| Area switcher and contextual tools | Semantic buttons, links, native dialog, selected-page state |
| Directory/search | Role-filtered route registry; navigation-only matching, no private-data search |
| Current location | Area and page labels; no dependency on remembering the previous route |
| Home status/task hierarchy | Real API data; direct links above readings; responsive grid |
| Working session | Mounted hidden Playground with no secret or transcript persistence |
| Phone navigation | Full-height sheet and visible sibling row; no fixed bottom bar obscuring focus |

## Assistant dock

- Scope: AssistantDock beside every portal page, owners only. Visitor mode: Operate. User-confirmed entry: an Assistant button beside Find a tool plus Ctrl/Cmd+J; no reserved rail while closed.
- Job: ask about the page in view, read a short answer beside it, stop a turn, return to saved chats. This version only advises; it cannot read or change the lab, and the copy says so.
- Layout: right by default, left optional, remembered in localStorage. 1200px and wider pushes the page (page breakpoints are viewport-based, so content keeps at least 800px) and the page scrolls in its own column ending at the divider, keeping its reading position across open and close; 700-1199px overlays; below 700px a bottom bar expands to a full-height sheet, and main is padded so the bar never covers focus. This is the user-requested exception to "no fixed bottom bar".
- Constraints: distinct from the Local AI Playground (local models, memory-only); never hides Models or API keys; transcripts live on the server, not in the browser.

## Direction contract

THESIS: A working companion beside the page, not a destination. It opens next to whatever tool the owner is using, knows which page that is, and leaves no rail when closed. It refuses the category default of a floating bubble launcher over a modal chat window.

OWN-WORLD: The portal's own materials: a surface-colored column divided from the page by one 1px line, system type, 12/14/25 radii, choice-row pressed states. Accent only on send, the chosen mode, and focus. Assistant replies are unboxed prose; owner messages are accent-soft blocks. Amber marks stopped, failed marks errors. No gradients, glass, glow, or sparkle theatrics.

STORY: The owner asks about the page in front of them, reads a short answer beside it, stops it whenever they like, and reopens earlier chats. They understand it advises and does not act yet.

FIRST VIEWPORT: At 1440, a 400px full-height column on the right with the page reflowed beside it. Its 88px header shares the portal header's bottom rule: Assistant and the chat title, then New chat, History, Move to left, Close. The conversation fills the middle. The composer sits at the foot: textarea, a Plan/Execute pair and model select below it, and the accent send button at bottom right. Empty chats show an "On: <page>" chip and three honest starters.

FORM: Docked side panel, first of three on the ordered list (docked panel, bottom sheet, floating window). No seed key: local extension, concept-seed intentionally not run. Signature interaction: Ctrl/Cmd+J toggles the dock from anywhere, moving focus to the composer and back to the opener on close, while the page reflows instead of being covered.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
