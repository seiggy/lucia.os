# Image prompts

The site ships without generated imagery. Every raster in `public/` is either an unretouched portal capture (`public/screenshots/`) or rendered from the site's own code (`public/images/og.png`, captured from the `TubeBank` component at 1200×630). Nothing here is required to ship.

If you want to replace or add imagery, generate from these prompts and drop the files at the listed paths. Keep the provenance note in the commit message.

## 1. Social card (optional replacement)

- **Path:** `site/public/images/og.png`
- **Size / format:** 1200×630 PNG, under 300 KB
- **Prompt:** Close macro photograph of four Nixie tubes on a dark brushed-steel instrument panel reading "02:00", warm orange neon cathodes glowing, faint unlit ghost digits visible behind the lit ones, honeycomb anode mesh, shallow depth of field, near-black background, a single cool rack-status LED out of focus at the far left, no text, no logos, no people, photographic, not illustrated.
- **Compose:** keep the left 55% dark and empty; the headline is laid over it in Saira Extra Condensed 300, uppercase, `#eceef0`, with "2 a.m." in `#ff8a00`.

## 2. "Bench at 2 a.m." (optional, not wired in)

- **Path:** `site/public/images/bench.jpg`
- **Size / format:** 2400×1350 JPEG (quality 80), under 400 KB
- **Prompt:** A quiet home server shelf in a dark room at night, three small fanless mini PCs and a compact AI workstation stacked on a wire rack, patch cables neatly dressed, only small amber and green status LEDs lit, a Nixie tube clock on the shelf reading 02:00, an empty office chair pushed in, documentary photography, natural low light, no screens showing UI, no brand logos, no people.
- **Use:** a full-bleed band above "Go back to bed." in `src/pages/index.astro`. Add `alt` describing the shelf, and a provenance line ("Generated image") in the band's caption.