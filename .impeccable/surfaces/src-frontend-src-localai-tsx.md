---
version: 1
slug: "src-frontend-src-localai-tsx"
primary_target: "src/frontend/src/LocalAI.tsx"
related_targets: ["src/frontend/src/LocalAI.css","src/frontend/src/playground.ts","src/frontend/vite.config.ts"]
---

# Local AI

- Target: `src/frontend/src/LocalAI.tsx`, route `#/ai`. Visitor mode: Operate.
- Inherit Lucia's shipped consumer-device simplicity and all existing tokens. No new visual world, top-level navigation, or palette.
- Job: understand the actually hosted model and endpoint, then validate a real GUI-to-API conversation.
- Composition: a conversation workspace and composer lead on the left; model readiness/context/backend and copyable endpoint details sit on the right. Phones stack the conversation first.
- Entry points: Home's explicitly Live Local AI link and the Spark device detail.
- User explicitly requested a temporary integration check before Authentik, with no pairing/login system. Use a development-only proxy with a server-side inference-only token; never expose an owner token or store credentials in the browser.
- Bridge allows only model listing and chat completion, validates methods and browser origins, and is absent from production builds. Trusted-network development only, not production authentication.
- Live page banner must distinguish real inference from the rest of the simulated dashboard. Chat uses real streaming output, not canned responses. Unknown/offline/no-model/error/truncated/stopped states must stay explicit.
- Settings: temperature, output-token limit, optional system instruction. Conversation remains in component memory; navigation cancels an active request. No automatic retries or hidden prompt truncation.
- Endpoint details expose the current served context separately from the artifact's declared limit. Show embedding availability honestly.
- Media: semantic HTML/CSS, existing SVG icon language, native inputs; no raster assets required.
- Constraints: no model administration, credentials UI, permanent auth system, or invented throughput/health claims. Authentik replaces the temporary bridge next.
