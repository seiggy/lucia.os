---
name: Lucia site
description: A Nixie laboratory counter that sells the 2 a.m. shift, and a quiet docs reader that matches the portal.
colors:
  chassis: "#0e0e10"
  chassis-2: "#121316"
  panel: "#17181b"
  panel-2: "#1c1e22"
  rule: "#24272c"
  rule-2: "#2f3338"
  mesh: "#3a3d42"
  lamp-off: "#5a6068"
  glass: "#a6acb4"
  ink-3: "#868c94"
  ink-2: "#a9afb6"
  ink: "#d7d9dc"
  ink-hi: "#eceef0"
  plasma: "#ff8a00"
  plasma-hi: "#ffc27a"
  plasma-glow: "rgb(255 138 0 / 0.45)"
  soon-amber: "#c47a2c"
  ocean: "#285bdd"
  ocean-ink: "#2855be"
  ocean-dark: "#97b3ff"
  docs-page: "#f3f5f9"
  docs-surface: "#ffffff"
  docs-surface-subtle: "#e9edf4"
  docs-text: "#202839"
  docs-muted: "#58677d"
  docs-line: "#e0e5ee"
  docs-code-bg: "#f6f8fb"
  docs-page-dark: "#121722"
  docs-surface-dark: "#1b2230"
  docs-surface-subtle-dark: "#242c3c"
  docs-text-dark: "#eef2fa"
  docs-muted-dark: "#b1bbcd"
  docs-line-dark: "#343e50"
  docs-code-bg-dark: "#161c28"
  note-bg: "#eef3ff"
  note-line: "#cfdcfb"
  note-bg-dark: "#1e2840"
  note-line-dark: "#34466e"
  amber-bg: "#fff2dc"
  amber-line: "#e9d3aa"
  amber-ink: "#785417"
  amber-bg-dark: "#332b1f"
  amber-line-dark: "#6b5735"
  amber-ink-dark: "#f2cc8c"
  failed-bg: "#fff0e9"
  failed-line: "#efc9bd"
  failed-ink: "#963c2a"
  failed-bg-dark: "#3b2826"
  failed-line-dark: "#6a3f37"
  failed-ink-dark: "#f2b2a5"
typography:
  display:
    fontFamily: "'Saira Extra Condensed', 'Arial Narrow', sans-serif"
    fontSize: "clamp(3.25rem, 6.6vw, 5.75rem)"
    fontWeight: 300
    lineHeight: 0.92
    letterSpacing: "0.035em"
  headline:
    fontFamily: "'Saira Extra Condensed', 'Arial Narrow', sans-serif"
    fontSize: "clamp(2.25rem, 4.2vw, 3.5rem)"
    fontWeight: 300
    lineHeight: 0.98
    letterSpacing: "0.04em"
  title:
    fontFamily: "'Saira Extra Condensed', 'Arial Narrow', sans-serif"
    fontSize: "1.5rem"
    fontWeight: 500
    lineHeight: 1.1
    letterSpacing: "0.1em"
  body:
    fontFamily: "'Saira', system-ui, sans-serif"
    fontSize: "1.0625rem"
    fontWeight: 400
    lineHeight: 1.6
  label:
    fontFamily: "'Saira Extra Condensed', 'Arial Narrow', sans-serif"
    fontSize: "0.8125rem"
    fontWeight: 500
    lineHeight: 1.3
    letterSpacing: "0.22em"
  button:
    fontFamily: "'Saira Extra Condensed', 'Arial Narrow', sans-serif"
    fontSize: "0.9375rem"
    fontWeight: 700
    lineHeight: 1
    letterSpacing: "0.22em"
  nixie:
    fontFamily: "'Saira Extra Condensed', 'Arial Narrow', sans-serif"
    fontSize: "calc(var(--w) * 1.4)"
    fontWeight: 200
    lineHeight: 1
  docs-headline:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif"
    fontSize: "clamp(2rem, 3.4vw, 2.625rem)"
    fontWeight: 720
    lineHeight: 1.13
    letterSpacing: "-0.035em"
  docs-lede:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif"
    fontSize: "1.1875rem"
    fontWeight: 400
    lineHeight: 1.55
  docs-title:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 700
    lineHeight: 1.25
    letterSpacing: "-0.02em"
  docs-subtitle:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif"
    fontSize: "1.1875rem"
    fontWeight: 650
    lineHeight: 1.3
  docs-body:
    fontFamily: "-apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif"
    fontSize: "1.0625rem"
    fontWeight: 400
    lineHeight: 1.65
  docs-label:
    fontFamily: "'Saira Extra Condensed', 'Arial Narrow', sans-serif"
    fontSize: "0.75rem"
    fontWeight: 500
    lineHeight: 1.3
    letterSpacing: "0.2em"
  docs-mono:
    fontFamily: "ui-monospace, 'Cascadia Mono', 'SF Mono', Consolas, monospace"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.6
rounded:
  hairline: "4px"
  control: "6px"
  docs-control: "8px"
  plate: "10px"
  bezel: "12px"
  instrument: "14px"
  lamp: "50%"
spacing:
  gutter: "clamp(18px, 4vw, 40px)"
  wrap: "1280px"
  docs-wrap: "1440px"
  section-y: "clamp(64px, 9vw, 120px)"
  channel-y: "34px"
  channel-feature-y: "44px"
  docs-column-gap: "clamp(24px, 3.5vw, 56px)"
components:
  button-lit:
    textColor: "{colors.plasma-hi}"
    typography: "{typography.button}"
    rounded: "{rounded.control}"
    padding: "12px 22px"
    height: "48px"
  button-lit-hover:
    textColor: "#ffffff"
  button-plain:
    textColor: "#c2c7cd"
    typography: "{typography.button}"
    rounded: "{rounded.control}"
    padding: "12px 22px"
    height: "48px"
  button-plain-hover:
    textColor: "{colors.plasma-hi}"
  button-soon:
    textColor: "#7a8088"
    typography: "{typography.button}"
    rounded: "{rounded.control}"
    padding: "12px 22px"
    height: "48px"
  tube-lit:
    textColor: "{colors.plasma-hi}"
    typography: "{typography.nixie}"
  tube-dim:
    textColor: "rgb(166 172 180 / 0.13)"
    typography: "{typography.nixie}"
  rack-channel:
    padding: "34px 28px"
  shot-plate:
    textColor: "{colors.ink-2}"
    rounded: "{rounded.bezel}"
    padding: "10px"
  docs-chassis:
    backgroundColor: "{colors.chassis}"
    textColor: "{colors.ink}"
    height: "56px"
  docs-nav-link-current:
    backgroundColor: "color-mix(in srgb, var(--accent) 12%, var(--surface))"
    textColor: "{colors.ocean-ink}"
    rounded: "{rounded.docs-control}"
    padding: "6px 10px"
  docs-callout-note:
    backgroundColor: "{colors.note-bg}"
    textColor: "{colors.ocean-ink}"
    rounded: "{rounded.plate}"
    padding: "14px 18px 16px"
  docs-callout-caution:
    backgroundColor: "{colors.amber-bg}"
    textColor: "{colors.amber-ink}"
    rounded: "{rounded.plate}"
    padding: "14px 18px 16px"
  docs-callout-destructive:
    backgroundColor: "{colors.failed-bg}"
    textColor: "{colors.failed-ink}"
    rounded: "{rounded.plate}"
    padding: "14px 18px 16px"
  docs-pager:
    backgroundColor: "{colors.docs-surface}"
    textColor: "{colors.docs-text}"
    rounded: "{rounded.plate}"
    padding: "16px 18px"
---

# Design System: Lucia site

Scope: the Astro site in `site/` (marketing landing, 404, and `/docs/`), published at https://seiggy.github.io/lucia.os/. The Lucia portal's own system is the repository-root `DESIGN.md`; this file does not govern the app, and the app's file does not govern this site. Every literal value in `site/` source belongs to the system recorded here, so a design hook reporting site values as "outside DESIGN.md" against the root file is expected, not drift.

## Overview

**Creative North Star: "The Nixie Laboratory Counter"**

The site is two worlds joined at one seam. The marketing world (Persuade mode: `index.astro`, `404.astro`, `Lab.astro`, `lab.css`) is a dark instrument panel: brushed chassis, hairline-ringed plates, rack rails, and banks of Nixie tubes whose lit digits are the only hot light in the room. Numbers in the tubes are real figures from the code or a working lab (02:00, 07:00, 30, 13, 200, 443, 10), so the counter is evidence, not ornament. Copy is short, uppercase-condensed at the top of each section and plain sentence-case Saira below.

The docs world (Read mode: `Docs.astro`, `docs.css`) deliberately reads like the portal: the app's cool neutrals, Ocean accent, system font stack, light/dark/system theme. It bridges back to the laboratory through a dark chassis header that never changes with theme, and through Saira Extra Condensed engraved labels on nav groups, callouts, pager and TOC.

Density is generous in marketing (sections breathe at 64–120px) and calm-reader in docs (70ch article, sticky sidebar and TOC). Imagery is unretouched evidence only.

**Key Characteristics:**
- Dark-only marketing chassis; plasma orange is light emission, rationed.
- Nixie tube banks with all ten ghost cathodes; lit, dim and blank states.
- Rack channel rows, spec plates and readouts instead of card grids.
- Provenance plate on every screenshot.
- One authored motion moment: the tube slot-roll.
- Docs match the portal (Ocean, system type, light/dark/system) under a chassis header.

## Colors

A near-black steel ramp lit by one plasma orange in marketing; the portal's cool neutrals and Ocean blue in docs.

### Primary
- **Plasma** (`plasma`): lit tube separators, the lit-word in headlines (`.lit-word`, with an 18px `plasma-glow` halo), the on-duty lamp and readout values in the shift-clock instrument, lit button lamps, the "on" toggle knob in the job card, the single lit indicator in the identity keyswitch, text selection, caret and native accent.
- **Plasma Hi** (`plasma-hi`): lit tube digits, primary-CTA label, the "Lucia on duty" label, every marketing hover (nav, `.more` links, footer links, plain buttons), and the marketing focus ring.

### Secondary
- **Ocean** (`ocean` light, `ocean-dark` dark; `ocean-ink` for links and labels on light): docs links, current nav item, TOC scrollspy marker, note-callout label, pager hover, focus ring, Pagefind primary. Same values as the portal's Ocean preset.

### Tertiary
- **Soon Amber** (`soon-amber`): two uses only, both "not lit yet / honest status" markers: the "Coming soon" tag on `soon` buttons and the "Unretouched" word on screenshot plates.

### Neutral
- **Chassis / Chassis 2** (`chassis`, `chassis-2`): page field; the body runs a fixed `chassis-2 → #0a0a0c` fall-off. `chassis` is also the docs header in both themes and the `theme-color`.
- **Panel / Panel 2** (`panel`, `panel-2`): raised plates (job card, skip link, button top stop).
- **Rule / Rule 2 / Mesh** (`rule`, `rule-2`, `mesh`): section hairlines, channel dividers, button and plate rings, trace spine and tube mesh.
- **Lamp Off** (`lamp-off`, reused literal): every unlit lamp, toggle knob, trace node and keyswitch ring.
- **Glass** (`glass`): tube envelope ring, ghost cathodes and dim digits (at 4.5–26% alpha).
- **Ink ramp** (`ink-3` labels and fine print, `ink-2` body prose, `ink` emphasis, `ink-hi` headings).
- **Docs neutrals** (`docs-page`, `docs-surface`, `docs-surface-subtle`, `docs-text`, `docs-muted`, `docs-line`, `docs-code-bg`, each with a `-dark` pair): identical to the portal's page/surface/text/muted/line set.
- **Docs status tones** (`note-*`, `amber-*`, `failed-*`, each with a `-dark` pair): callout fills, rings and label ink only.

### Named Rules
**The Rationed Plasma Rule.** Plasma and plasma-hi appear only where something is lit: tube digits, the primary CTA, the on-duty lamp and readout, the lit-word, state lamps, hover and focus. Resting links, body text, borders and decoration never take orange.

**The Two Worlds Rule.** Marketing is dark-only steel and plasma; docs is the portal's neutrals and Ocean with a light/dark/system switch. Orange enters docs only inside the chassis header; Ocean never enters the laboratory.

## Typography

**Display Font:** Saira Extra Condensed 200/300/500/700 (with Arial Narrow, sans-serif), self-hosted via Fontsource
**Body Font:** Saira 400/500 (with system-ui) in marketing; the system stack (-apple-system, BlinkMacSystemFont, Segoe UI, system-ui) in docs
**Label/Mono Font:** Saira Extra Condensed 500 for engraved labels in both worlds; ui-monospace / Cascadia Mono / SF Mono / Consolas for code

**Character:** A narrow instrument-face condensed for everything engraved or lit, over a softly geometric Saira (marketing) or the reader's native UI face (docs).

### Hierarchy
- **Display** (`display`, uppercase, balanced): the hero headline and the closing "Go back to bed." Capped at 5.75rem.
- **Headline** (`headline`, uppercase): section heads and the 404 title.
- **Title** (`title`, uppercase): channel names, trace steps (1.375rem), "Not yet" items (1.25rem), job-card name.
- **Body** (`body`): marketing prose in `ink-2`, measure 62ch (`.prose-m`), 56ch in channels, 50ch for the hero lede at 1.125rem.
- **Label** (`label`, uppercase): engraved plates, readout keys, spec-plate keys, provenance plates (0.6875–0.75rem variants). Readout values use the condensed 500 at 1.0625rem, 0.16em, in plasma.
- **Button** (`button`, uppercase): all marketing buttons; nav and `.more` links use condensed 500 0.875rem at 0.2–0.22em.
- **Nixie** (`nixie`, weight 200): tube digits; size is driven by the bank's `--w` per size (xl clamp(56px, 6.2vw, 84px), lg 58px, md 42px, sm 30px).
- **Docs Headline / Lede / Title / Subtitle / Body** (`docs-*`): the portal's 720-weight tight headline, muted 1.1875rem lede, 700/650 section heads, 1.0625rem/1.65 prose at 70ch.
- **Docs Label** (`docs-label`, uppercase): nav groups, "On this page", pager direction, sheet and search heads, chassis "Docs" mark; callout labels run 0.8125rem at 0.22em.

### Named Rules
**The Engraved Label Rule.** Condensed uppercase labels name a value, a group or a plate, the way engraving names a dial. They sit beside or below what they name, never as an eyebrow above a heading.

**The Lit Word Rule.** At most one phrase per headline is lit, and only when it carries the promise ("2 a.m."). Headings never use gradient text.

## Layout

Marketing content sits in `.wrap`: `min(100% - 2 × gutter, wrap)`. Sections are full-width bands separated by 1px `rule` hairlines, with `section-y` vertical padding and a faint top-down gradient on alternating bands. Section heads are a two-column grid (headline left, prose right, aligned to baseline end).

The hero is a 1.05fr / 0.95fr split: copy left, shift-clock instrument right. The rack is a single plate with screw-hole rails down both sides holding channel rows: 240px readout column + copy, and two "feature" rows that add a screenshot column (one flipped). The job run is a 1:1 split of job card and a vertical trace. Assumptions are a two-column spec plate with hairline cells.

Breakpoints (marketing): 1080px (feature screenshots drop to a full-width row, readout column 200px), 900px (all splits stack), 720px (nav keeps only Docs and GitHub), 640px (hero reorders to headline → instrument → lede → CTAs, rack loses its plate and rails, rows go single-column).

Docs is a three-column grid (248px sidebar, fluid article, 216px TOC) inside 1440px, `docs-column-gap` gaps, sticky sidebar and TOC at 88px under a sticky 56px chassis. At 1180px the TOC drops; at 860px the sidebar becomes a hamburger-opened sheet dialog (min(340px, 88vw)), the search button collapses to an icon, and the pager stacks.

**The Rack, Not Grid Rule.** Repeated features are rows in one rack, each with its own readout (tube bank, lamp list, keyswitch), never a grid of identical cards.

## Elevation & Depth

Marketing depth is physical: plates are lit from above and ringed, not floated. The recurring recipe is a 1px top highlight inset, a 1px hairline ring drawn with box-shadow, and a deep soft drop. Glow is reserved for emitted light (plasma). Docs are flat: 1px `docs-line` borders and tonal surfaces, with a scrim only behind dialogs.

### Shadow Vocabulary
- **Instrument** (`inset 0 1px 0 rgb(255 255 255 / 0.06), 0 0 0 1px #2a2d32, 0 30px 60px rgb(0 0 0 / 0.6)`): the hero shift clock only.
- **Bezel** (`inset 0 1px 0 rgb(255 255 255 / 0.06), 0 0 0 1px var(--rule-2), 0 24px 48px rgb(0 0 0 / 0.55)`): screenshot frames.
- **Rack** (`inset 0 0 0 1px var(--rule), 0 24px 60px rgb(0 0 0 / 0.4)`): the rack plate.
- **Key** (`inset 0 1px 0 rgb(255 255 255 / 0.08), 0 0 0 1px var(--rule-2), 0 6px 14px rgb(0 0 0 / 0.5)`): buttons; pressed swaps to `inset 0 1px 3px rgb(0 0 0 / 0.6)` plus the ring and a 1px drop.
- **Well** (`inset 0 1px 3px rgb(0 0 0 / 0.6), 0 0 0 1px var(--rule)`): recessed fields (the saved-prompt quote).
- **Plasma emission** (`0 0 8px var(--plasma)` on lamps; layered 4/14/30px text-shadow on tube digits; `0 0 22px rgb(255 138 0 / 0.22)` around the lit button).
- **Docs scrim** (`rgb(8 10 14 / 0.55)` dialog backdrop).

**The Emitted Light Rule.** Orange glow means something is switched on. Never use a glow, coloured shadow or hard offset shadow as decoration.

## Shapes

Machined, softly eased corners: 4px for focus rings and screenshot inner edges, 6px buttons, 8px docs controls, 10px rack, spec plate, callouts, code blocks and pager, 12px bezels and job card, 14px the instrument and search dialog. Lamps and trace nodes are perfect circles. Tubes are capsules: fully round top (`w/2`), tight 14% bottom radius, with a dark base cap. Borders are always 1px; dividers inside plates are 1px hairlines. Side rules (blockquotes, TOC marker, soon-tag divider) are 1px.

## Components

### Buttons
Tactile panel keys with a status lamp.
- **Shape:** gently machined (`rounded.control`), min-height 48px, 12px gap, `#25282d → #16181b` vertical face, Key shadow.
- **Lit (primary):** `plasma-hi` label, lit plasma lamp, `#6b3d0a` ring and a 22px plasma halo. One per CTA group. Hover whitens the label.
- **Plain:** `#c2c7cd` label, unlit `#50555c` lamp. Hover lights the label `plasma-hi` and the lamp `plasma`. Optional external-arrow SVG at 70% opacity.
- **Soon:** a non-link `span` with `aria-disabled`, dimmer face (`#1b1d21 → #131417`), `#7a8088` label, then a 1px divider and a "Coming soon" tag in `soon-amber`.
- **Motion:** colour and shadow at 0.18s `ease-out` (`cubic-bezier(0.22, 1, 0.36, 1)`), 1px press; transitions off under reduced motion.

### Tube Bank (signature)
`TubeBank.astro`: `role="img"` with a plain-language `aria-label`; tubes are `aria-hidden`.
- Every digit tube stacks all ten cathodes 0–9 as ghosts (`glass` at 4.5%), with the lit digit on top in `plasma-hi` and a three-layer plasma text glow. Non-digits render as separators in `plasma` at 58% size.
- Envelope: specular highlight, ±60° honeycomb mesh lines, warm-to-black fill, `glass` ring at 26%, inner shadow and a dark base cap.
- **States:** lit (default, rolls), `dim` (digits `glass` 13%, no glow; used for "held back" values), `blank` (no lit digit at all, ghosts only; used for "doesn't exist yet").
- **Sizes:** xl (hero), lg (feature channels, 404), md (channels), sm ("Not yet").

**The Ghost Cathode Rule.** A tube always shows all ten cathodes. Absence is shown as a dark tube, not a missing one.

**The One Motion Rule.** The only authored motion is the slot-roll: when a lit bank is 60% visible (IntersectionObserver, once), each tube flickers through random digits every 95ms (45ms dim at 18% opacity) for 6 + 3×index steps, then settles. Dim and blank banks never roll. With reduced motion the script does nothing and smooth scrolling is off. Everything else is a ≤0.2s state transition.

### Screenshot with provenance plate
`Shot.astro`: a 10px bezel (`#1f2126 → #121316`, Bezel shadow), 4px-cornered 1440×900 image, then a plate reading "Captured lucia.homelab.seiggy.com/{route} · On 2026-10-07 · Unretouched" in Saira body 0.8125rem `ink-2`, with engraved keys and "Unretouched" pushed right in `soon-amber`.

**The Provenance Plate Rule.** Every product screenshot, in either world, carries where and when it was captured and that it is unretouched.

### Rack channels, plates and readouts
- **Channel row:** readout column (tube bank, lamp list, or keyswitch) over a small engraved plate caption (`label` 0.75rem, `#8a9098`), then title, prose, and a `.more` link (condensed uppercase `ink`, a 30px rule that extends from 60% to full on hover, `plasma-hi` hover). Rows divided by 1px `rule`.
- **Lamp list:** 7px lamps (`plasma` + 8px glow when on, `lamp-off` when merely a step) beside names with engraved sub-labels.
- **Instrument readout / spec plate / modes list:** `dl` grids with engraved keys and 1px hairline cell dividers.
- **Job card:** `panel` plate, header split by `rule-2`, the saved prompt in a Well, permission toggles (30×16 track, 12px knob; on = `#3a2410` track with glowing plasma knob).
- **Trace:** 25px ringed nodes on a 1px `mesh` spine, condensed step titles, `ink-2` copy at 52ch.

### Navigation
- **Marketing masthead:** spaced condensed wordmark (300, 1.5rem, 0.5em tracking, `#c7cbd0`) and condensed 500 0.875rem nav in `#9aa1a9`, `plasma-hi` on hover; 1px `rule` underline. At 720px only Docs and GitHub remain.
- **Docs chassis:** sticky 56px `chassis` bar in every theme: hamburger (≤860px), spaced "LUCIA" mark, engraved "Docs" sub-mark after a 1px `mesh` divider (the build lights it in an off-token `#ff9e33`; new chassis marks use `plasma-hi`), search button (`panel` fill, `rule-2` ring, "/" kbd), theme toggle cycling system → light → dark (icons swap; choice stored in `localStorage['lucia-docs-theme']` and applied pre-paint via `data-theme`), GitHub icon. 40px icon buttons; focus ring `plasma` inside the chassis.
- **Sidebar / sheet:** engraved group labels over 0.9375rem links; current page = 12% Ocean tint, `ocean-ink`, 600 weight.
- **TOC:** h2/h3 list on a 1px `docs-line` rail; scrollspy (rootMargin -72px / -70%) moves a 1px Ocean marker and `aria-current="location"`.
- **Pager:** two bordered surface plates with engraved Previous/Next; Ocean border on hover.

### Docs content
- **Callouts** (`remark-callouts.mjs` maps GitHub alerts): NOTE/TIP → note (Ocean tint), IMPORTANT → requires (amber), WARNING → caution (amber), CAUTION → destructive (failed). Rendered as `aside`, 1px ring, engraved label with a 6px dot.
- **Figures** (`remark-shots.mjs`): any lone `/screenshots/*.png` becomes a figure with "Captured from lucia.homelab.seiggy.com/{route} on 2026-10-07. Unretouched." in 0.8125rem muted.
- **Code:** Shiki dual theme (github-light / github-dark-dimmed) following the theme switch; `docs-code-bg` block with 1px line; inline code on `docs-surface-subtle`, 5px corners.
- **Tables:** borderless except 1px row rules, muted 650 headers, horizontal scroll.
- **Search:** Pagefind UI in a 680px, 14px-cornered modal dialog opened by the chassis button or "/", lazy-loaded, themed through `--pagefind-ui-*` to docs tokens; a plain note explains search exists only in production builds.
- **Print:** chassis, sidebar, TOC, pager and edit link hidden; prose full width in black.

### Imagery
- `public/screenshots/*.png` (apps, assistant-settings, devices, domains, home, jobs, models, playground, updates) are unretouched captures of the live portal dated 2026-10-07. `people.png` is deliberately excluded because it shows personal data.
- `public/images/og.png` is rendered from the `TubeBank` component at 1200×630, not generated.
- Optional generated-image prompts live in `site/IMAGE-PROMPTS.md`; any generated image must be labelled as generated in its caption. None ships today.

## Do's and Don'ts

### Do:
- **Do** keep plasma for lit things: tube digits, the one lit CTA per group, lamps, readouts, the lit-word, hover and focus.
- **Do** put a real, sourced number in every lit tube and give the bank a plain-language `aria-label`.
- **Do** show missing or planned capability as `dim` or `blank` tubes and `soon` buttons, never as hidden content.
- **Do** frame every product screenshot with a provenance plate or caption (route, capture date, "Unretouched").
- **Do** build repeated features as rack rows with distinct readouts; use `dl` plates for specs and modes.
- **Do** keep marketing prose at ≤62ch and docs prose at 70ch.
- **Do** keep docs aligned with the portal: same neutrals, Ocean accent, system type, light/dark/system theme.
- **Do** honour `prefers-reduced-motion`: no roll, no smooth scroll, no transitions.

### Don't:
- **Don't** use orange for resting links, body text, borders, icons or decoration, or invent new orange tints.
- **Don't** place engraved labels as eyebrows above headings.
- **Don't** build identical card grids.
- **Don't** use gradient text, side stripes wider than 1px, or decorative glows and offset shadows.
- **Don't** set display type above 5.75rem (hard ceiling 6rem).
- **Don't** add a second authored motion; state changes stay ≤0.2s.
- **Don't** retouch, mock up or composite screenshots, or ship captures that show personal data.
- **Don't** bring Ocean into the laboratory, or let the docs chassis follow the light theme.
