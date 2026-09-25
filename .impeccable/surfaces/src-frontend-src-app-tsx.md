---
version: 1
slug: "src-frontend-src-app-tsx"
primary_target: "src/frontend/src/App.tsx"
related_targets: ["src/frontend/src/PortalNavigation.tsx","src/frontend/src/PortalNavigation.css","src/frontend/src/navigation.ts","src/frontend/src/LocalAI.tsx","src/frontend/src/ModelManager.tsx","src/frontend/src/InferenceKeys.tsx"]
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
