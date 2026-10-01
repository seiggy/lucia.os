---
name: Lucia portal
description: A simple check-in with compact workspaces, visible tools, familiar controls, and changeable colors.
colors:
  ocean-light: "#285bdd"
  ocean-dark: "#97b3ff"
  meadow-light: "#19714e"
  meadow-dark: "#80cfac"
  plum-light: "#7951a8"
  plum-dark: "#c6a2ed"
  on-accent-white: "#ffffff"
  on-accent-black: "#000000"
  accent-soft: "color-mix(in srgb, var(--accent) 10%, var(--surface))"
  page: "#f3f5f9"
  surface: "#ffffff"
  surface-subtle: "#e9edf4"
  text: "#202839"
  muted: "#58677d"
  line: "#e0e5ee"
  page-dark: "#121722"
  surface-dark: "#1b2230"
  surface-subtle-dark: "#242c3c"
  text-dark: "#eef2fa"
  muted-dark: "#b1bbcd"
  line-dark: "#343e50"
  amber-bg: "#fff2dc"
  amber-line: "#e9d3aa"
  amber-ink: "#785417"
  amber-icon: "#f7deb0"
  amber-bg-dark: "#332b1f"
  amber-line-dark: "#6b5735"
  amber-ink-dark: "#f2cc8c"
  amber-icon-dark: "#51402a"
  green-bg: "#e6f2ea"
  green-ink: "#236d46"
  green-bg-dark: "#203b30"
  green-ink-dark: "#95d8b4"
  plum-bg: "#eeeafa"
  plum-ink: "#675298"
  plum-bg-dark: "#352e48"
  plum-ink-dark: "#cebbf0"
  failed-bg: "#fff0e9"
  failed-ink: "#963c2a"
  failed-bg-dark: "#3b2826"
  failed-ink-dark: "#f2b2a5"
typography:
  headline:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "42px"
    fontWeight: 720
    lineHeight: 1.13
    letterSpacing: "-.035em"
  headline-mobile:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "33px"
    fontWeight: 720
    lineHeight: 1.16
    letterSpacing: "-.035em"
  headline-narrow:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "31px"
    fontWeight: 720
    lineHeight: 1.16
    letterSpacing: "-.035em"
  portal-headline:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "33px"
    fontWeight: 720
    lineHeight: 1.2
    letterSpacing: "-.035em"
  brand:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "30px"
    fontWeight: 760
    lineHeight: 1
    letterSpacing: "-.035em"
  title:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "22px"
    fontWeight: 700
    lineHeight: 1.3
    letterSpacing: "-.02em"
  subheading:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "19px"
    fontWeight: 680
    lineHeight: 1.35
    letterSpacing: "-.02em"
  compact-heading:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "16px"
    fontWeight: 680
    lineHeight: 1.35
    letterSpacing: "-.02em"
  body:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "16px"
    fontWeight: 400
    lineHeight: 1.5
  paragraph:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "16px"
    fontWeight: 400
    lineHeight: 1.6
  control:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "15px"
    fontWeight: 650
    lineHeight: 1.35
  label:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "14px"
    fontWeight: 650
    lineHeight: 1.5
  metadata:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.5
  status:
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif'
    fontSize: "14px"
    fontWeight: 550
    lineHeight: 1.4
rounded:
  shortcut-keycap: "7px"
  color-swatch: "8px"
  theme-swatch: "10px"
  field: "12px"
  theme-example-icon: "13px"
  control: "14px"
  notice: "16px"
  icon-tile: "18px"
  surface: "25px"
  circle: "50%"
spacing:
  gap-tight: "8px"
  gap-control: "12px"
  gap-compact: "14px"
  inset-small: "16px"
  inset-compact: "18px"
  gap-medium: "20px"
  inset-control: "21px"
  inset-tile: "22px"
  section: "24px"
  inset-reading: "28px"
  gap-directory: "32px"
  portal-gutter: "40px"
components:
  button-primary:
    backgroundColor: "var(--accent)"
    textColor: "var(--on-accent)"
    typography: "{typography.control}"
    rounded: "{rounded.control}"
    padding: "12px 21px"
  button-secondary:
    backgroundColor: "var(--surface)"
    textColor: "var(--text)"
    typography: "{typography.control}"
    rounded: "{rounded.control}"
    padding: "12px 21px"
  button-secondary-hover:
    backgroundColor: "var(--surface-subtle)"
  button-text:
    backgroundColor: "transparent"
    textColor: "var(--accent-ink)"
    typography: "{typography.label}"
    padding: "6px 0"
  management-field:
    backgroundColor: "var(--surface)"
    textColor: "var(--text)"
    rounded: "{rounded.field}"
    padding: "12px 14px"
    width: "100%"
  color-input:
    backgroundColor: "var(--surface)"
    rounded: "{rounded.field}"
    padding: "4px"
    width: "50px"
    height: "44px"
  workspace-switch:
    backgroundColor: "var(--surface)"
    textColor: "var(--text)"
    rounded: "{rounded.field}"
    padding: "10px 14px"
  contextual-navigation-item:
    textColor: "var(--muted)"
    typography: "{typography.label}"
    padding: "12px 2px"
  contextual-navigation-item-current:
    textColor: "var(--accent-ink)"
  directory-link:
    textColor: "var(--text)"
    rounded: "{rounded.field}"
    padding: "10px 12px"
  directory-link-current:
    backgroundColor: "var(--accent-soft)"
    textColor: "var(--accent-ink)"
  tool-search:
    textColor: "var(--text)"
    rounded: "{rounded.field}"
    padding: "0 14px"
  navigation-dialog:
    backgroundColor: "var(--surface)"
    textColor: "var(--text)"
    rounded: "{rounded.surface}"
    padding: "{spacing.inset-reading}"
  model-library-tab:
    backgroundColor: "var(--surface)"
    textColor: "var(--text)"
    typography: "{typography.label}"
    rounded: "{rounded.field}"
    padding: "10px 16px"
  status-available:
    textColor: "var(--green-ink)"
    typography: "{typography.status}"
  reading-surface:
    backgroundColor: "var(--surface)"
    textColor: "var(--text)"
    rounded: "{rounded.surface}"
    padding: "{spacing.inset-reading}"
  assistant-dock:
    backgroundColor: "var(--surface)"
    textColor: "var(--text)"
    width: "400px"
  assistant-owner-message:
    backgroundColor: "var(--accent-soft)"
    textColor: "var(--text)"
    rounded: "{rounded.control}"
    padding: "10px 14px"
  assistant-mode-choice:
    textColor: "var(--muted)"
    rounded: "{rounded.field}"
    padding: "4px 10px"
  assistant-mode-choice-current:
    backgroundColor: "var(--accent-soft)"
    textColor: "var(--accent-ink)"
  assistant-send:
    backgroundColor: "var(--accent)"
    textColor: "var(--on-accent)"
    rounded: "{rounded.field}"
    width: "36px"
    height: "36px"
---

# Design System: Lucia portal

## Overview

**Creative North Star: "A simple check-in"**

Lucia uses iPhone-like, toy-like consumer simplicity for people who work in technology but find infrastructure consoles difficult. Quiet neutral fields, familiar rounded controls, a system sans face, and short, plain-language priorities make health and the next action understandable. Technical detail remains available through progressive disclosure rather than becoming the default experience.

The shipped portal extends that identity with a compact workspace switcher and visible contextual tools, not a permanent sidebar. Overview, Your lab, Local AI, and Settings group the available destinations; the directory and search are permission-aware. The reusable voice remains direct and reassuring without disguising uncertainty or presenting an example as a live result.

This system describes the managed, Authentik-protected browser portal and its existing working controls. The separate native bootstrap extension below remains scoped to Avalonia. The former preview scenarios, simulated task labels, invented device/activity cards, and three-tab/bottom-bar navigation are not current component specimens. The documentation records the built interface, not additional backend capabilities.

**Key Characteristics:**
- Consumer-device simplicity rather than cloud-console density.
- Neutral light and dark fields with user-changeable accents.
- Familiar rounded controls and recognizable SVG device icons.
- Plain-language state labels, visible priorities, and progressive disclosure.
- Compact workspace selection with visible, role-aware sibling tools.
- Explicit unconnected boundaries and honest unknown or failed states.

> Source of truth: `src/frontend/src/App.tsx`, `App.css`, `index.css`, `dashboard.ts`, `Icon.tsx`, `PortalNavigation.tsx`, `PortalNavigation.css`, `navigation.ts`, and the `LocalAI`, `ModelManager`, and `InferenceKeys` TSX/CSS pairs. `PRODUCT.md` supplies durable product constraints. The five-block contract in `src/frontend/index.html` and `.impeccable/surfaces/src-frontend-src-app-tsx.md` record the owner-selected compact area switcher (candidate 3, seed `c426180e`); the brief retains surface strategy. The original layout-C comparison is historical, not the current shell.

> Evidence boundary: `.impeccable/review/portal-review-packet.png` is the supplied eight-screen composite; the accompanying `portal-*.png` captures use labeled synthetic fixtures, not actual hardware readings. The handoff reports behavior validation at 320/390/820/1440px, role-aware search, focus/Escape, preserved chat, and management regressions. Finish reviewer `f9252198-f03a-4968-841d-66be060d4457` reported SHIP after the opaque-opening fix. This documentation pass checks source and schema only; it does not repeat browser tests, inspect deployment, or claim additional qualification.

## Colors

Cool, quiet neutrals carry the interface; a selectable accent marks actions and selection, while separate status tones retain their meaning.

### Primary

Ocean is the default preset; Meadow and Plum are equal alternatives, not secondary and tertiary brand colors. Each preset has the light/dark pair recorded in frontmatter. Custom color replaces the active accent in either appearance; it does not replace the neutral or status palette. The bright yellow in the dark settings evidence is an edge-case customization, not Lucia's default.

Appearance defaults to **System**, listens to OS color-scheme changes, and also permits explicit **Light** or **Dark**. Both schemes are first-class. `App.tsx` sets the root `data-scheme` and updates `--accent`, `--on-accent`, and `--accent-ink` with `themeColors` from `dashboard.ts`. Frontmatter component colors retain these live CSS bindings intentionally; do not substitute an immutable Ocean value.

The dynamic rule is:

1. Choose the preset value for the resolved scheme, or the validated six-digit custom hex unchanged, as `--accent`.
2. Calculate relative luminance using sRGB linearization (threshold 0.04045; divide by 12.92 below it, otherwise raise `(channel + 0.055) / 1.055` to 2.4) and channel weights 0.2126, 0.7152, and 0.0722. Contrast is `(lighter + 0.05) / (darker + 0.05)`.
3. Set `--on-accent` to whichever frontmatter black/white choice has greater contrast against the accent; ties choose white. This is independent of whether the scheme is light or dark.
4. Start `--accent-ink` at the accent. Compare against `page` in light appearance and `surface-subtle-dark` in dark appearance. While contrast is below 4.5:1, move each 8-bit channel 20% toward black in light appearance or white in dark appearance, rounding each iteration. Only the ink is adjusted; the selected accent stays unchanged.
5. Derive `--accent-soft` with the frontmatter sRGB mix: accent at 10% against the current surface. The ink colors links, selected icons, outlines, and accent-status text.

This is the implemented contrast rule, not a claim that the function measures every possible mixed surface. The CSS-only accent-ink bootstrap value is replaced by the layout effect and is not a separate brand color. Sidecar tonal ramps are synthesized OKLCH visualizations of the source colors, not additional shipped palette steps or theme-computation logic.

### Neutral

| Tokens | Use |
| --- | --- |
| `page` / `page-dark` | The quiet field behind the application. |
| `surface` / `surface-dark` | Working panels, fields, contextual navigation, and opaque directory/search surfaces. |
| `surface-subtle` / `surface-subtle-dark` | Development disclosure, neutral icon tiles, notices, and quiet-control hover states. |
| `text` / `text-dark` | Headings, labels, and primary reading text. |
| `muted` / `muted-dark` | Supporting explanation, metadata, and unknown-state text. |
| `line` / `line-dark` | Thin container borders and internal dividers. |

Unsuffixed neutral and status tokens are the light scheme; `-dark` records the corresponding root override, not an additional simultaneous color.

### Status and supporting tones

- **Amber:** the amber family distinguishes attention and unavailable model status.
- **Green:** `green-ink` supports reported model availability, not a claim that all devices are healthy. The background pair remains defined; the unconditional Playground Live badge is no longer rendered.
- **Plum:** `plum-bg` and `plum-ink` remain in the palette from the retired media-device example; they are separate from the selectable Plum accent preset.
- **Failure:** `failed-bg` and `failed-ink` keep request failures visible; failure does not acquire a success treatment.
- **Unknown:** neutral/muted treatment plus explicit wording and an unknown/offline icon. Missing information is not green health.

**The Changeable Accent Rule.** Bind interactive accent colors to the runtime theme properties; Ocean is the default, not an immutable brand hue.

**The State Has Words Rule.** Pair state color with readable text and an icon or explicit context; never treat unknown health as healthy.

## Typography

**Application headings and body font:** the platform system stack recorded in frontmatter. There is no separate display face or web-font dependency. System sans is the explicitly approved functional application hierarchy, not a display-face prescription for unrelated surfaces. Disclosed request JSON retains a small native monospace `pre` treatment rather than creating a primary information tier.

**Character:** familiar, compact, and sentence-case. Weight and spacing establish hierarchy without uppercase kickers or decorative font changes. The observed scale is role-based, not a mathematical type ratio.

### Hierarchy

| Role | Application |
| --- | --- |
| `portal-headline` | Intro headings throughout the portal use this compact role at desktop and phone widths, overriding the older global heading ramp. Balanced wrapping and the inherited heading weight/tracking remain. |
| `headline` / `headline-mobile` / `headline-narrow` | Retained global `h1` fallback outside portal intro headings: default, at or below 700px, and at or below 380px. The 31px narrow role is an unchanged scoped functional fallback, not the portal's default heading size. |
| `brand` | The existing Lucia wordmark only; the portal reduces it to 22px at or below 600px. This 30px role is not an extra content-heading tier. |
| `title` | Section and reading-surface headings, including the directory title; no shared 21px mobile title override is present. |
| `subheading` / `compact-heading` | Supporting headings and model information; the compact role is reused by directory groups and model/key rows. |
| `body` / `paragraph` | Base reading style; paragraphs have the more open line height. Reading-surface paragraphs use 15px; supporting explanation commonly uses 13px or 14px. |
| `control` | Primary and secondary action labels; compact composer actions retain their scoped 14px variant. |
| `label` | Field legends, inline actions, and contextual navigation. The workspace selector uses 16px/650 on desktop and 14px/650 on phones; directory links remain regular-weight 14px text. |
| `metadata` | Footer text and small explanatory notes. |
| `status` | Word-and-icon model availability; the hosted-model detail uses its existing 12px variant. No retired task-chip or unconditional Live-label role is promoted. |

Introduction paragraphs are constrained to 56ch; unconnected-feature summaries allow 48ch and longer connection explanations allow 65ch. Paragraphs can wrap long content anywhere. Small Local AI metadata variations are scoped exceptions, not a new default body size.

**The Plain Language Rule.** Lead with the understandable state or decision; keep technical detail behind disclosure rather than making it the primary label.

## Layout

The portal's centered main container and footer have a maximum width of 1280px. Main content uses 40px horizontal insets and 40px/64px top/bottom margins. The full-width sticky header is 88px high; the contextual strip sits below it at `top: 88px`, with a 62px minimum height. This replaces the old bounded header and three-tab track. The 1032px/28px global main rule remains a fallback outside the portal wrapper, not the active portal geometry.

Sections have more separation than their internal controls; spacing is explicit and role-based, not a strict four- or eight-pixel scale. Reading and management surfaces share the 28px inset, while compact forms, rows, and action groups reuse smaller gaps. Current responsive behavior:

- At or below **1000px**, portal header/context/main insets become 28px, main's top margin becomes 32px, the breadcrumb hides, and the keyboard hint and account name hide. Accessible button names remain.
- At or below **900px**, the existing settings gap becomes 20px and the Playground keeps its compact two-column variant. Home stacks host/model panels across the 601–900px tablet range.
- At or below **700px**, Settings, Playground, model search/slots, and key fields stack; reading/settings and model/key form panels retain 24px vertical / 21px horizontal insets. These working-control breakpoints are distinct from the shell's 600px phone breakpoint.
- At or below **600px**, the header becomes 76px high with 18px side insets; the context strip moves to `top: 76px` with a 54px minimum height. Main uses 20px side insets and 28px/40px vertical margins. The labeled sibling row remains visible; there is no fixed bottom navigation.
- Directory and search dialogs become edge-to-edge, square-cornered **100dvh** sheets at 600px, with a sticky opaque title/close row, vertical scrolling, and a safe-area-aware bottom inset. The grouped directory changes from two columns to one. Account is a separate, content-height top-sheet exception with rounded bottom corners.
- At or below **380px**, the global narrow heading fallback and wrapping theme example remain, but portal main gutters and intro headings keep the portal overrides. Body minimum width remains 320px.

The desktop/tablet directory is a centered modal, up to 720px wide (bounded by the viewport minus 40px) and no taller than the viewport minus 64px. Working content stays wide because no permanent side rail is reserved. Home's quick task links precede real host/model status; model/key surfaces lead with existing resources rather than creation forms. These are current compositions, not mandatory grids for future screens; the surface brief owns their strategy.

The sticky shell is accounted for by scroll padding and control scroll margins (174px/24px desktop, 146px/20px phone). Dialog controls use their own 80px/16px scroll margins so keyboard focus can clear the sticky sheet heading.

## Elevation & Depth

Depth is primarily tonal: page, surface, and subtle-surface layers combine with thin borders and generous separation. Working panels are not uniformly raised; contextual navigation uses an underline, not a shadowed selection tile. Directory/search/account overlays add a diffuse shadow and a dimmed backdrop. The panel itself stays opaque throughout opening; backdrop dimming is not glass or backdrop blur. No gradients or hard offset shadows are part of this portal treatment.

### Shadow vocabulary

- **Retained root shadow, light:** `0 3px 9px #2534530a`; not the new contextual-selection treatment.
- **Retained root shadow, dark:** `0 3px 10px #0000001a`; the same limited role.
- **Navigation overlay:** `0 24px 80px #10182730`; the dimming backdrop is `#10182766`.
- **Primary hover:** `0 3px 9px #18233224`.
- **Primary pressed:** `inset 0 1px 2px #00000026`.

**The Quiet Depth Rule.** Use neutral layers and thin borders for structure; reserve the larger shadow for modal separation and the smaller interaction shadows for enabled primary actions.

## Shapes

Rounded rectangles are the recurring form, not pill-shaped containers everywhere. Fields, the workspace selector, directory links, and close controls share the field radius; primary actions and appearance choices share the control radius. Reading surfaces and desktop dialogs share the surface radius. Contextual links are underlined, not rounded selection tiles. Mobile directory/search sheets deliberately remove outer rounding.

The unchanged theme preset swatch (10px), native color-picker inner swatch (8px), theme-example icon (13px), and round notice-dismiss control are limited functional roles, not new general card or button defaults. The 7px keyboard-hint corner belongs to the shortcut keycap, not a resurrected task badge.

Borders are generally one pixel, using the neutral line or a meaningful state line. Shared icon wells are 58px squares with 29px icons; Local AI and settings use smaller, scoped variants.

Icons are inline SVG from `Icon.tsx`: a 24-by-24 view box, no fill, current-color stroke (1.75), and rounded line caps and joins. They are hidden from assistive technology when adjacent text already supplies meaning. Compact search, account, and dismissal controls have accessible names. Keyboard modifier text in the shortcut hint is literal notation, not a glyph-icon system.

## Components

The sidecar catalogs current shared controls and portal navigation, replacing the retired preview specimens. Its self-contained HTML/CSS samples resolve live root properties with Ocean-light fallbacks outside the app. They illustrate appearance and native CSS states, not connected backend actions, live hardware readings, or a framework-powered replica.

### Buttons

Familiar, readable actions with restrained feedback. Primary and secondary buttons share the control typography, radius, and padding tokens and a minimum height (49px), with a 12px icon gap and 19px icons.

- **Primary:** runtime accent and computed on-accent text. Hover adds the primary-hover shadow; pressing uses the inset shadow.
- **Secondary:** neutral surface, text, and a one-pixel line border. Hover switches to the subtle surface.
- **Text action:** accent ink, no border or fill, and underline on hover. Portal text actions have a 44px minimum height, overriding the retained 36px global fallback. Retry and secondary navigation use this lighter treatment.
- **Focus and disabled:** actionable elements use an accent-ink outline (3px, offset 4px). Disabled primary/secondary buttons use 0.65 opacity.

Button background/shadow transitions last 160ms with `ease-out`. Reduced-motion preference collapses animation and transition duration to 0.01ms, limits animation iteration to one, and restores automatic scrolling.

### Status

Home reports checking, ready to respond, no chat model loaded, or status unavailable using words and an icon. Availability comes from the host's model response, not a timer or fixture. The unconditional Playground Live badge has been removed. Retired synthetic task tags are not reused as status specimens; unknown data stays explicit.

### Cards / Containers

Reading, settings, model/key management, and Home Local AI panels share the surface radius and neutral line border. Model and key collections use stacked rows and internal dividers rather than one raised card per item. Devices and Installation tasks retain their existing real onboarding controls and recorded-state surfaces; the old preview device/activity cards are not their design source. Failed requests remain visible rather than silently disappearing.

### Inputs / Fields

The shipped fields include the native color input, Playground message/system textareas and response settings, read-only endpoint, model search/provider controls, and named-key/optional-expiry fields. They share the field radius, neutral line/surface, explicit labels, and visible focus. Textareas use 15px system text at 1.55 line height, vertical resizing, and 88–300px height bounds. Numeric Playground fields are at least 44px high; endpoint and management fields are at least 48px high. Management fields use 12px/14px padding; the native color picker remains 50-by-44px with its scoped inner swatch.

Appearance and theme choices are real buttons grouped by fieldset/legend, with `aria-pressed`, a neutral border, and the control radius. Selection applies accent-soft plus an accent-ink border; hover accents the border. Theme presets also show a check mark. Preference persistence errors remain visible in an alert notice; appearance is stored locally, not credentials.

### Navigation

The compact area selector names the current workspace and opens a grouped directory. Desktop selector text is 16px/650 in a 48px-minimum neutral bordered control; the phone variant is 14px/650 and at least 44px high. The header also exposes **Find a tool** and account/appearance. There is no permanent sidebar and no three-destination bottom bar.

Contextual sibling links are 14px/650 with 52px minimum height and a two-pixel bottom border. Hover strengthens text; `aria-current="page"` applies accent ink to text and underline. The route registry groups **Home** under Overview, **Devices / Installation tasks** under Your lab, **Playground / Models / API keys** under Local AI, and **Appearance** under Settings. Owner-only destinations disappear from the directory, search, and sibling row for non-Owners; areas with no visible destinations are omitted. Route authorization remains the host's responsibility.

The grouped directory uses plain 14px links with 44px minimum height, accent-soft hover/current fill, and an SVG arrow or selected check. Search opens through its button or **Ctrl/Cmd+K**, matches destination labels/descriptions/keywords, and searches navigation only—not private data. Its field is at least 52px high, with a two-pixel accent-ink focus-within outline at two-pixel offset. Result rows are at least 64px high with a 15px/650 title and 12px supporting description; unavailable tools are not advertised.

All three panels use native modal `dialog`: focus starts on search or the close button, Escape and dismissal restore the opener, and route selection closes the panel and focuses main content. ArrowDown from the query moves to the first result; ordinary links retain native keyboard navigation. The title and explicit close control remain visible in the phone sheet.

Opening uses **180ms `cubic-bezier(.16,1,.3,1)`**, translating from `translateY(-8px)` to zero at full opacity. There is no opacity/scale animation or decorative page entrance. Reduced motion uses the shared duration override.

**The Opaque Navigation Rule.** Navigation opens by translation only; keep panel opacity at full strength so underlying tools never bleed through the moving surface.

### Focused model, key, and conversation work

Models opens `#/ai/models` with **Loaded now** and **On your Spark** library content. **Find & download** is a separate hash view (`#/ai/models/find`), with the Hugging Face connection in a disclosure beside acquisition rather than ahead of the library. Its two local-view links use the field radius, a 44px minimum height, and accent-soft/accent-ink selection. Existing download, load/unload/delete, estimate, and confirmation controls keep their working behavior; this shell introduces no new model capability.

API keys opens the existing application-key list with an explicit **New API key** action. The named-key/optional-expiry form appears on request; one-time secret handling and per-row revoke confirmation remain separate, explicit states. Creation does not replace the list-first default, and the documentation supplies no example secret.

Playground stays mounted but hidden after its first visit so draft and transcript survive portal navigation in memory. Refresh, sign-out, or loss of the authenticated view clears that component state; nothing here promises stored conversation history or persistence across sessions. The model and key routes remain visible siblings for Owners, not features hidden behind the conversation.

### Disclosure, feedback, and service boundaries

Technical information uses native `details`/`summary` with a divider; portal summaries have a 44px minimum height. Route changes move focus to the main content, and a skip link is available. Current status is announced through live regions; request and storage failures remain visible.

Managed deployments gate access through Authentik and show account controls. Development-only hosts retain an explicit sign-in-disabled disclosure. Hardware onboarding extends the existing visual system with a visible admission state and real device/task records. Unknown heartbeat state is not health, and a device-reported installation phase is not verified enrollment. Installation controls remain disabled until the host qualifies their prerequisites. Retired device/task detail URLs show a not-available page.

Local AI shows real streamed text and explicit completed, response-limited, stopped, unavailable, and interrupted states. Conversation text is rendered as text, not executable markup. Managed browser calls use the server session and CSRF protection; the inference-only Vite bridge remains development-only. Appearance persists locally under the existing preference key; account credentials and chat history do not. Real host readings and onboarding observations must retain their uncertainty and qualification boundaries; the portal adds no fake graph or new backend feature.

### Native desktop setup — scoped extension

The Avalonia 12.1.2/.NET 10 utility extends **"A simple check-in"** without replacing the dashboard or Local AI system above. It targets Windows, macOS, and Linux after DGX OS, networking, and SSH setup. Its user-approved sequence is **Connect / Review / Your account / Set up / Ready**, not a second management console. The fixed sequence was approved directly; no generated concept seed or random FORM selection is claimed.

Source of truth: `src/Lucia.Desktop/App.axaml`, `MainWindow.axaml` (including its five-part opening contract), `MainWindow.axaml.cs`, and `SetupForm.cs`. The matching `.impeccable/surfaces/src-lucia-desktop-mainwindow-axaml.md` owns surface strategy. The measurements below describe this native implementation in Avalonia device-independent units (DIP); they do not change the web frontmatter, CSS behaviors, or HTML specimens.

#### Colors and typography

- Native `Page`, `Surface`, `Subtle`, `Ink`, `Muted`, and `Line` match the existing light/dark neutral pairs. Readiness uses the existing green, amber, and failure ink families with the words **Ready**, **Setup needed**, and **Needs attention**.
- Native `Accent` stays Ocean (`#285BDD`) with white primary-action text in both themes. `AccentInk` is `#2855BE` in light and the existing Ocean-dark in dark; `AccentSoft` is `#E9EFFF` / `#263656`, not the web runtime color mix. Appearance offers **System / Light / Dark**; no desktop accent presets or custom color picker are implemented.
- The declared native family is `Segoe UI, San Francisco, Noto Sans`; the Windows render resolved Segoe UI. This is the approved system-sans application hierarchy, not a new display-font rule. Headings are 31/38, semibold; sections and current operations are 18, semibold; body is 15, with 23 line height on muted explanations; labels are 14, semibold; captions are 13/20. Fingerprint fields use monospace at 13.

**The Native Desktop Scope Rule.** In the native setup utility, use the shipped Ocean resources and System/Light/Dark appearance selector; the web custom-accent behavior remains web-only.

#### Layout, depth, and shapes

One scrollable task pane keeps its action footer outside the scroll area. The window starts at 1040 × 840 with a 740 × 640 minimum; the step-rail column is 208 wide. Below 960, the rail disappears, its column becomes zero, and a compact “n of 5 · step” label appears inside the pane. This is compact desktop behavior, not the dashboard's phone navigation.

The shell inset is 28 horizontal, 22 top, and 18 bottom; pane content uses 34 horizontal, 30 top, and 24 bottom. Footer padding is 28 horizontal / 18 vertical, with a one-unit top divider. Form groups commonly use 18 separation; label-to-field space is 8. Tonal layers and thin neutral borders supply depth; the app adds no custom shadows or decorative transitions over Avalonia Fluent controls. Pane corners are 24, step/error containers 12, and the fingerprint-consent panel 14.

#### Controls and authority boundaries

- Buttons are at least 44 high, with 18 horizontal / 10 vertical padding and 11 corners. Primary is Ocean/white; quiet actions are transparent with `AccentInk`. Fields are at least 44 high, with 12 horizontal / 10 vertical padding, 10 corners, a `Surface` fill and `Line` border; focus changes the border to `AccentInk`. Placeholders use `Muted` at opacity 1 (reported foreground/surface contrast: 5.75:1 light, 8.24:1 dark). Remaining interaction states come from Fluent; the web hover shadows and focus outline are not copied.
- Native labels, masked secrets, heading levels, a default primary button, and field-targeted validation support keyboard use. Errors stay visible in an assertive region; current operations and trust status use polite announcements. Fingerprints, progress details, and certificate details remain selectable or disclosed rather than dominating the form.
- **Connect and Review:** require explicit SSH fingerprint approval before credentials are sent. Review shows inspected readiness and proposed changes; blocked checks prevent continuation. Approval precedes setup or existing-service verification. An already-running job instead offers **Resume monitoring**, without another owner-password prompt.
- **Your account:** owner enrollment is part of LDAP/Authentik setup, not a later terminal task. A new-owner password requires at least 14 characters and confirmation. For a ready existing owner, a nonempty current password passes through unchanged for sign-in verification; the new-password rule is not imposed and no password reset is offered. Known owner/address fields are read-only.
- **Set up:** real remote events supply operation text and a redacted, bounded 120-entry details log; progress is indeterminate with elapsed time, not a fabricated percentage. **Stop watching** stops monitoring, not remote setup. Reconnect resumes the inspected job instead of starting a second installation.
- **Ready:** requires verified owner sign-in and the requested managed host/application readiness, not service health alone. **Open Lucia** launches the Authentik-protected dashboard. CA trust remains a separate, initially unchecked approval with the OS-specific scope shown before installation. Public certificate export is an alternative and does not change trust; opening an endpoint does not silently grant local trust.

Evidence is bounded: the user exercised the Windows desktop flow, confirmed successful identity provisioning, and confirmed managed-dashboard access after Authentik sign-in. Earlier Skia/headless captures in `desktop-screens/contact-sheet.png` use labeled synthetic fixtures, not proof of deployment. Native macOS/Linux qualification remains outstanding.

Not canonized or repaired for this extension: missing generated FORM-seed provenance remains a nonfunctional process note, not a fabricated selection record or a new design rule. The existing-owner password and placeholder-contrast fixes are reflected in the shipped source; no further UI changes are part of this pass.

### Assistant dock — scoped extension

The Owner-only assistant extends **"A simple check-in"** as a working companion beside the page, not a destination. It answers questions about the current page and how Lucia works; for now it only advises and cannot read or change anything in the lab. It is separate from the Playground's local-model conversation. This is a code-led local extension without an approved comp; no generated concept seed or FORM selection is claimed.

Source of truth: `src/frontend/src/AssistantDock.tsx` and `AssistantDock.css` (layouts, entry, focus, and motion), `AssistantPanel.tsx` and `assistant.css` (header, conversation, composer, states, and history), the header entry in `PortalNavigation.tsx`/`PortalNavigation.css`, and the model select in `components/ui/select.tsx`. The **Assistant dock** and **Direction contract** sections of `.impeccable/surfaces/src-frontend-src-app-tsx.md` own surface strategy. A theme layer scoped to the dock maps the chat components' background, primary, muted, destructive, border, and ring roles onto the existing surface, accent/on-accent, surface-subtle/muted, failed-ink, line, and accent-ink properties, so custom accents and dark appearance carry through. The dock adds no color token; its corners use the field (12px) and control (14px) radii.

#### Layout and motion

- **Push (1200px and wider):** a 400px full-height surface column beside the page, divided by a one-pixel line. The page reflows into the remaining width and scrolls in its own column, which ends at that divider. While pushed at or below 1400px, the portal-compact treatment starts early: 28px insets, a 32px top margin on main content, a centered context strip, and hidden shortcut hints, account name, and breadcrumb.
- **Overlay (700–1199px):** the same column floats over the unchanged page with the existing navigation-overlay shadow and no backdrop.
- **Phone (below 700px):** the header entry hides and a fixed bottom bar holds one **Ask the assistant** button (48px minimum, control radius, one-pixel line border, muted 15px text, accent-ink chat icon). It expands to a full-height sheet with a 64px header; the page behind it is inert and does not scroll, and the composer clears the bottom safe area. The bar is the brief's user-requested exception to the portal's no-fixed-bottom-bar layout: a single assistant entry, not bottom navigation.

The dock opens on the right by default; **Move to the left**/**Move to the right** swaps its side, divider, and page margin. The browser remembers the side, the current chat and model, and—at push width—whether the dock was open; chat transcripts are kept on the host. Closed, the dock reserves no rail. **Ctrl/Cmd+J** and the header button toggle it (the shortcut yields while a dialog is open). Opening moves focus to the message field; closing returns it to the opener. Escape closes the overlay and phone sheet; the pushed column closes from **Close**, the header entry, or the shortcut.

Opening uses the navigation timing, **180ms `cubic-bezier(.16,1,.3,1)`**: the column slides 16px in from its side and the phone sheet rises 24px. As shipped, both also fade in from zero opacity. Reduced motion uses the shared duration override.

#### Header, conversation, and composer

- **Header:** 88px plus a one-pixel bottom line that continues the portal header's rule. **Assistant** uses the compact-heading values above the current chat title in muted 14px text. The **New chat**, **History**, side-move (hidden in the phone sheet), and **Close** icon buttons are 40px square with the field radius and muted icons; hover adds surface-subtle, and pressed **History** uses accent-soft with accent-ink.
- **Conversation:** Owner messages are accent-soft blocks (control radius, 10px/14px padding, 15px/1.55 text, at most 85% wide). Replies are unboxed prose at 15px/1.6 with 16px/680 headings, underlined accent-ink links, and muted, line-ruled blockquotes; messages sit 24px apart. A **Thinking…** / **Thought for N seconds** disclosure (32px minimum, 14px/650 muted) reveals line-ruled 14px reasoning.
- **Code and tables:** one frame with a line border, the field radius, and a surface fill. Code adds a 40px top row with a muted 12px language label and a 32px copy button over a 12px/14px body in the portal's existing monospace stack; tables use the same frame without the top row.
- **Composer:** a control-radius group with a line border and no shadow holds the message field (15px/1.5, growing from 64px to 192px; 16px in the phone sheet) above a toolbar: the **Answer mode** pair (**Plan**, **Execute** by default), the model select, and send at the bottom right. Mode choices are 32px-minimum, field-radius, line-bordered 14px/650 muted buttons; the pressed choice uses the portal's accent-soft/accent-ink selection. The model select has no fill at rest and gains surface-subtle on hover or while open. Its menu uses the field radius, a line border, and a scoped menu shadow (`0 12px 32px #10182724`), with 36px options and a check on the current model. Send is a 36px field-radius accent button with an on-accent arrow; while answering it becomes **Stop answering**.

Solid accent appears only on send. Accent-soft or accent-ink marks owner messages, the chosen mode, the pressed **History** toggle, the current chat, the open header entry, links, starter arrows, and focus. Dock focus is the portal's 3px accent-ink outline at a 2px offset; the message group shows a 2px focus-within outline.

#### Entry, states, and history

- **Header entry:** **Assistant** reuses the **Find a tool** button (44px minimum, field radius, muted 14px) with a chat icon and a **⌘ / Ctrl J** hint in the shortcut-keycap style. When open, it uses accent-soft with accent-ink. The hint hides at the portal-compact width and while pushed at or below 1400px.
- **Empty chat:** an **On:** chip naming the current page (field radius, surface-subtle, muted 14px with the page name in text color), a muted introduction stating the advice-only limit, and three starter prompts: 49px-minimum, field-radius, line-bordered 14px/600 buttons with an accent-ink arrow. Hover accents the border.
- **States:** a stopped answer uses the amber status treatment with a stop icon and **Stopped**. A failed turn is a failed-ink line (**The model stopped responding.**) with a **Try again** text link. A chat already answering elsewhere shows a muted note with **Reload chat**. When GitHub is not signed in, an amber note above the composer offers a 40px secondary **Sign in with GitHub** button (**Get a new code** or **Try again** after an expired or declined code; a failed-bg/failed-ink note if the sign-in fails), the starters disable, and the model select hides. Signing in swaps it for a field-radius surface-subtle card: **Enter this code at github.com/login/device**, the code at 22px/700 with .1em tracking and a copy icon button, a secondary **Copy code and open GitHub** link button, a **Cancel** text link, and a muted **Waiting for GitHub…** spinner line. If Copilot refuses the account, the amber note names it with **Sign in again**. Request errors use a failed-bg/failed-ink notice with the field radius; a working answer shows a muted **Answering…** line with a spinner.
- **History:** **Your chats** lists 56px-minimum field-radius rows with a 14px/600 title and a 12px muted date or **Answering now**; hover is surface-subtle and the current chat accent-soft. Deleting confirms inline (**Delete this chat from your host? This can't be undone.**) with a failed-ink secondary **Delete chat** button and a **Cancel** text link. The empty list says chats are kept on the host. A footer below the list reads **Signed in to GitHub as @login** with a **Disconnect** text link that confirms inline (Lucia forgets the sign-in; GitHub keeps the authorization until it is revoked under **Authorized GitHub Apps**) with a failed-ink secondary **Disconnect GitHub** button and a **Cancel** text link.

**The Beside-the-Page Rule.** The assistant opens beside the page it is about and pushes that page aside wherever there is room; it never becomes a floating bubble launcher, and it reserves no desktop rail while closed.

Evidence is bounded: review captures in `.impeccable/review/assistant-*.png` (light and dark push, left side, overlay, phone bar and sheet, model menu, history, and stopped, failed, busy, and disconnected states) are fixture captures with synthetic data—a fixture Owner and a synthetic model—not real transcripts, model output, or hardware readings. Finish review round 1 returned eight fixes, all applied; the verdict pass returned ship with nothing remaining. This pass checked shipped source and captures only.

Not canonized or repaired for this extension: the opening fade departs from the brief's slide-only intent; it is recorded as shipped, not as a pattern for navigation panels, which stay under the Opaque Navigation Rule. The brief's 25px radius does not appear in the dock; the column is square-edged. The dock's 2px focus offset, 32px mode/model/reasoning/text-link targets, button-based reasoning disclosure, 8px copy-button and menu-option corners, and the scroll-to-latest button's library hairline shadow are dock-scoped; they do not replace the portal's 4px offset, 44px text actions, native disclosures, swatch-scoped 8px radius, or Quiet Depth vocabulary. The overlay column's backdrop-free navigation-overlay shadow and the model menu's shadow are limited to those floating dock surfaces; Quiet Depth still governs structure elsewhere. No source edits are part of this pass.

## Do's and Don'ts

### Do:

- Do preserve "A simple check-in" through plain-language priorities and familiar controls.
- Do use the runtime accent, on-accent, accent-ink, and accent-soft properties so customization remains intact.
- Do treat light, dark, and OS-following appearance as equally supported choices.
- Do pair health and task-state color with words and meaningful icons or context.
- Do retain visible focus, native labels and disclosures, reduced motion, and labeled navigation.
- Do keep the portal's directory and contextual tools permission-aware, with full-opacity opening surfaces.
- Do distinguish unconnected services from empty or healthy services, and keep test fixtures out of the dashboard.

### Don't:

- Don't turn Ocean, a custom yellow screenshot, or a bootstrap ink value into an immutable brand palette.
- Don't replace the approved consumer simplicity with a dense infrastructure console or decorative aesthetic metaphor.
- Don't present missing health as success or an unsuccessful recovery as resolved.
- Don't reintroduce synthetic devices, tasks, incidents, approvals, or repair controls as live functionality.
- Don't hide the Owner's Models and API keys behind chat or replace the selected compact portal switcher with a permanent sidebar.
- Don't describe in-memory conversation continuity as persistence across refresh or sign-out.
- Don't promote scoped wordmark, narrow-heading, swatch, or metadata roles—or synthetic tonal ramps—into universal design defaults.

Not canonized or repaired: leftover unused Live-badge/shortcut CSS and development-only “Authentik sign-in comes next” copy are incumbent drift, not rules or a description of managed deployments. The CSS-only accent-ink fallback and synthetic captures are not palette or hardware proof. The approved system stack, 30px wordmark, 31px global narrow heading, and 10px/8px swatches retain only their functional scopes; no broader display aesthetic is inferred. No source edits, fresh audit, or native requalification are part of this pass.
