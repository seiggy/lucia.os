// Records narrated Lucia demo episodes: voice-over + Playwright screencast, muxed by ffmpeg into MP4 + SRT.
//   node record.mjs --login        sign in once in a visible browser; the session lasts 8 hours
//   node record.mjs 02 --script    voice and print the narration with timings, without recording
//   node record.mjs 02             record episodes/02-*.mjs to artifacts/demo/02-*/
// The voice is Kokoro's, unless artifacts/demo/narrator.json names an audio.cpp server that clones a reference clip:
//   { "server": "https://…", "model": { "id", "path", "family", "task" }, "asr": { the same, optional },
//     "voice": "narrator.wav", "transcript": "what narrator.wav says", "request": { "language": "English", "seed": 1 },
//     "rate": 48000 to keep a model's full bandwidth, rather than 24000 }
// With asr, a line that drops caption words is voiced again with the next seed, up to four tries, and the best one is kept.
// An episode default-exports beats: 'narration', async helpers => {}, or ['narration', async helpers => {}].
// A pair speaks while it acts, and the next beat starts once both finish. Spotlights last until the line ends unless given ms,
// and never outlast it.
// helpers.cut(act) jumps past the silent part of a wait on a live server, keeping a moment either side.
import { chromium } from 'playwright';
import ffmpeg from 'ffmpeg-static';
import { KokoroTTS } from 'kokoro-js';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// The phonemizer's handlers rethrow every failure with its whole minified source attached.
process.removeAllListeners('uncaughtException').removeAllListeners('unhandledRejection');
const here = path.dirname(fileURLToPath(import.meta.url));
const out = path.resolve(here, '../../artifacts/demo');
const auth = path.join(out, 'auth.json');
// Sign-in can move to the lab's own domain, so the saved session cookie names the portal's host.
const session = fs.existsSync(auth) && JSON.parse(fs.readFileSync(auth, 'utf8')).cookies.find(c => c.name === '__Host-Lucia.Session');
const base = process.env.LUCIA_URL ?? (session ? `https://${session.domain}/` : 'https://spark-9423/');
const voice = process.env.LUCIA_VOICE ?? 'af_heart';
const narrator = fs.existsSync(path.join(out, 'narrator.json')) && JSON.parse(fs.readFileSync(path.join(out, 'narrator.json'), 'utf8'));
const rate = narrator.rate ?? 24000, gap = 200;
const args = process.argv.slice(2);
const sleep = ms => new Promise(resolve => setTimeout(resolve, Math.max(0, ms)));
const clock = ms => new Date(Math.round(ms)).toISOString().slice(14, 19);
// Gray bars cover IP and MAC addresses, fingerprints, hardware UUIDs, key hints, emails, private model names and the name after
// an Account: label wherever they render.
// They also cover the lab's domain between a host's first label and its top-level domain, so sonarr.home.example.com reads sonarr.▇.com.
const [, ...labels] = new URL(base).hostname.split('.'), tld = labels.pop();
const domain = labels.length && String.raw`\b(?:${labels.slice(0, -1).map(l => `${l}\\.`).join('')})?${labels.at(-1)}(?=\.${tld}\b)`;
const privateModels = String.raw`\S*(?:abliterated|uncen|heretic)\S*`;
const redact = [String.raw`\b(?!0\.0\.0\.0\b)(?:\d{1,3}\.){3}\d{1,3}\b`, String.raw`\b(?:[0-9a-f]{2}:){4,}[0-9a-f]{2}\b`,
  String.raw`\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b`,
  String.raw`\b[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){4,7}\b`, String.raw`\b[0-9a-f]{0,4}(?::[0-9a-f]{0,4})*::[0-9a-f:]*[0-9a-f]\b`,
  String.raw`lucia_inf_\S+`, String.raw`[\w.+-]+@[\w-]+(?:\.[\w-]+)+`, privateModels,
  String.raw`[^\n]*internal only[^\n]*`, domain].filter(Boolean).join('|');
// Rows and model library entries named for these apps or private models disappear, and mentions inside other text are erased.
const conceal = [String.raw`[,;]?\s*\bwhisparr\S*`, privateModels].join('|');

if (args.includes('--login')) {
  const browser = await chromium.launch({ channel: 'msedge', headless: false });
  const context = await browser.newContext({ viewport: null });
  await (await context.newPage()).goto(base);
  console.log('Sign in to Lucia in the new Edge window. It closes by itself once you are in.');
  let cookie;
  while (!(cookie = (await context.cookies()).find(c => c.name === '__Host-Lucia.Session'))) await sleep(1000);
  await sleep(2000);
  fs.mkdirSync(out, { recursive: true });
  await context.storageState({ path: auth });
  await browser.close();
  console.log(`Saved the session for ${cookie.domain} to ${auth}.`);
  process.exit(0);
}

const episodes = fs.readdirSync(path.join(here, 'episodes'));
const name = args.find(a => !a.startsWith('--'));
const file = name && episodes.find(f => f.startsWith(name));
if (!file) throw new Error(`Usage: node record.mjs --login | <episode> [--script]. Episodes: ${episodes.join(', ')}`);
const id = path.basename(file, '.mjs'), dir = path.join(out, id);
const episode = await import(pathToFileURL(path.join(here, 'episodes', file)).href);
const beats = episode.default.map(b => typeof b === 'string' ? [b] : typeof b === 'function' ? [null, b] : b)
  .map(([text, act]) => [text?.replace(/\s+/g, ' ').trim(), act]);

// Respellings for the voice only; captions keep the real names.
const lexicon = [[/\bDGX\b/g, 'D G X'], [/\bGGUF\b/g, 'G G U F'], [/\bAdGuard\b/g, 'Ad Guard'],
  [/\bAuthentik\b/g, 'Authentic'], [/\bOIDC\b/g, 'O I D C'], [/\bQwen3\.8-Flash-Next\b/g, 'Kwen three point eight Flash Next'],
  [/\bQwen(?=\d)/g, 'Kwen '], [/\bQwen\b/g, 'Kwen'], [/\bhomelab\b/gi, 'home lab'],
  [/\bLLMs\b/g, 'L L Ems'], [/\bk3s\b/g, 'K three S'], [/\bGitea\b/g, 'Git tea'], [/\bTraefik\b/g, 'Traffic'],
  [/\bLiteLLM\b/g, 'Light L L M'], [/\bFlareSolverr\b/gi, 'Flare Solver'], [/\bUniFi\b/g, 'Unify'], [/\bNAS\b/g, 'nazz'],
  [/\bwhoami\b/g, 'who am I'], [/\bQ4_K_M\b/g, 'Q four K M'], [/\bvLLM\b/g, 'V L L M'], [/\bllama\.cpp\b/g, 'llama dot C P P'],
  [/(?<!\w)\.NET\b/g, 'dot net']];
const reference = narrator && fs.readFileSync(path.join(out, narrator.voice));
let tts;
const voiced = new Map();
async function speak(text) {
  if (voiced.has(text)) return voiced.get(text);
  const words = lexicon.reduce((t, [pattern, sound]) => t.replace(pattern, sound), text);
  const key = narrator ? JSON.stringify([narrator.model.id, narrator.request, createHash('sha1').update(reference).digest('hex'), rate, words])
    : `${voice}\n${words}`;
  const cached = path.join(out, 'voice', createHash('sha1').update(key).digest('hex').slice(0, 16) + '.f32');
  if (!fs.existsSync(cached)) {
    let audio;
    if (narrator) audio = await clone(text, words);
    else {
      if (words.length > 400) throw new Error(`Split this line; Kokoro cuts off long input: ${text}`);
      tts ??= await KokoroTTS.from_pretrained('onnx-community/Kokoro-82M-v1.0-ONNX', { dtype: 'q8', device: 'cpu' });
      ({ audio } = await tts.generate(words, { voice }));
    }
    fs.mkdirSync(path.dirname(cached), { recursive: true });
    fs.writeFileSync(cached, Buffer.from(audio.buffer, audio.byteOffset, audio.byteLength));
  }
  const bytes = fs.readFileSync(cached);
  const samples = new Float32Array(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
  voiced.set(text, samples);
  return samples;
}

async function post(route, body) {
  const json = !(body instanceof FormData);
  const res = await fetch(new URL(route, narrator.server), {
    method: 'POST', headers: json ? { 'content-type': 'application/json' } : {}, body: json ? JSON.stringify(body) : body,
  });
  if (!res.ok) throw new Error(`${route} ${res.status}: ${await res.text()}`);
  return res;
}
const loaded = new Set();
async function ready(model) {
  if (loaded.has(model.id)) return;
  const { data } = await (await fetch(new URL('/v1/models', narrator.server))).json();
  if (!data.some(m => m.id === model.id && m.loaded !== false)) {
    await post('/v1/models/load', { ...model, mode: 'offline', load_options: {}, session_options: {} });
  }
  loaded.add(model.id);
}
// Caption words over three letters that the transcript lacks, compared by their first four letters. Respelled names don't count,
// and neither do number words, which the transcript may write as digits.
const tokens = s => s.toLowerCase().replace(/['’]/g, '').match(/[a-z0-9]+/g) ?? [];
const number = /^(?:three|four|five|seven|eight|nine|eleven|twelve|(?:thir|four|fif|six|seven|eigh|nine)teen|(?:twen|thir|for|fif|six|seven|eigh|nine)ty|hundred|thousand|million)$/;
function missed(text, heard) {
  const said = new Set(tokens(heard).map(w => w.slice(0, 4)));
  return tokens(lexicon.reduce((t, [pattern]) => t.replace(pattern, ' '), text))
    .filter(w => w.length > 3 && !number.test(w) && !said.has(w.slice(0, 4)));
}
async function clone(text, words) {
  await ready(narrator.model);
  let best;
  for (let seed = narrator.request.seed ?? 0, tries = narrator.asr ? 4 : 1; tries-- && best?.misses.length !== 0; seed++) {
    const wav = Buffer.from(await (await post('/v1/audio/speech', {
      model: narrator.model.id, input: words, ...narrator.request, seed,
      voice_ref: { type: 'base64', data: reference.toString('base64') }, reference_text: narrator.transcript,
    })).arrayBuffer());
    let heard = '';
    if (narrator.asr) {
      await ready(narrator.asr);
      const form = new FormData();
      form.set('model', narrator.asr.id);
      form.set('language', 'en');
      form.set('file', new Blob([wav], { type: 'audio/wav' }), 'line.wav');
      heard = (await (await post('/v1/audio/transcriptions', form)).json()).text;
    }
    const misses = narrator.asr ? missed(text, heard) : [];
    if (!best || misses.length < best.misses.length) best = { wav, heard, misses };
  }
  if (best.misses.length) console.warn(`  The voice may have dropped ${best.misses.join(', ')}. Heard: ${best.heard}`);
  // Mono samples at rate, without the silence the model leaves at either end.
  const trim = 'silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.05,areverse';
  const raw = execFileSync(ffmpeg, ['-v', 'error', '-i', '-', '-af', `${trim},${trim}`, '-f', 'f32le', '-ac', '1', '-ar', String(rate), '-'],
    { input: best.wav, maxBuffer: 1 << 28 });
  return new Float32Array(raw.buffer.slice(raw.byteOffset, raw.byteOffset + raw.byteLength));
}

let spoken = 0;
for (const [text] of beats) {
  if (!text) continue;
  const ms = (await speak(text)).length / rate * 1000;
  if (args.includes('--script')) console.log(`[${clock(spoken)}] ${(ms / 1000).toFixed(1)}s  ${text}`);
  spoken += ms + gap;
}
console.log(`${id}: ${clock(spoken)} of narration`);
if (args.includes('--script')) process.exit(0);

// The screencast captures physical pixels, so a real 1.5 scale turns the 1280x720 layout into crisp 1080p frames.
const browser = await chromium.launch({ channel: 'msedge', args: ['--force-device-scale-factor=1.5'] });
const context = await browser.newContext({
  storageState: fs.existsSync(auth) ? auth : undefined, viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1.5,
});
// Highlights paint over matches without changing text React owns; concealed rows only gain a data attribute.
// Highlights can't reach input values, so bars drawn above the page cover the matching part of a single-line input's value,
// and new API keys and textareas holding redacted text blur instead.
await context.addInitScript(([redact, conceal]) => {
  try { localStorage.setItem('lucia.notifications.prompt.v1', 'dismissed'); } catch {}
  const covered = new RegExp(redact, 'gi'), hidden = new RegExp(conceal, 'gi');
  const range = (node, match) => {
    const range = new Range();
    range.setStart(node, match.index);
    range.setEnd(node, match.index + match[0].length);
    return range;
  };
  const scan = () => {
    const bars = [], erased = [], walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node; (node = walker.nextNode());) {
      // React renders "Account: {name}" as separate text nodes, so the name is found by the label before it.
      if (/\bAccount:\s*$/.test(node.previousSibling?.data ?? '')) {
        const bar = new Range();
        bar.selectNodeContents(node);
        bars.push(bar);
      }
      for (const match of node.data.matchAll(covered)) bars.push(range(node, match));
      for (const match of node.data.matchAll(hidden)) {
        const row = !node.data.slice(0, match.index).trim() && node.parentElement.closest('tr, li, .model-library-list > article');
        if (row) row.dataset.demoHidden = ''; else erased.push(range(node, match));
      }
    }
    CSS.highlights.set('redact', new Highlight(...bars));
    CSS.highlights.set('erase', new Highlight(...erased));
    // search ignores the g flag's lastIndex, unlike test.
    for (const el of document.querySelectorAll('textarea')) el.toggleAttribute('data-demo-blur', el.value.search(covered) >= 0);
  };
  // Every frame, measure where matches sit in each input's text and bar the visible ones. The layer stays below Playwright's
  // overlays, which live in the top layer, and the bars skip any input something else covers.
  const layer = document.createElement('div'), ctx = document.createElement('canvas').getContext('2d');
  layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483646';
  let drawn = '';
  const draw = () => {
    let html = '';
    for (const el of document.querySelectorAll('input:not([type=hidden], [type=password])')) {
      const text = el.value || el.placeholder;
      if (text.search(covered) < 0) continue;
      const s = getComputedStyle(el), px = name => parseFloat(s[name]), r = el.getBoundingClientRect(), size = px('fontSize');
      const left = r.left + px('borderLeftWidth'), right = r.right - px('borderRightWidth'), x = left + px('paddingLeft') - el.scrollLeft;
      const middle = r.top + (px('borderTopWidth') + px('paddingTop') + r.height - px('borderBottomWidth') - px('paddingBottom')) / 2;
      ctx.font = `${s.fontStyle} ${s.fontWeight} ${s.fontSize} ${s.fontFamily}`;
      for (const m of text.matchAll(covered)) {
        const a = Math.max(left, x + ctx.measureText(text.slice(0, m.index)).width);
        const b = Math.min(right, x + ctx.measureText(text.slice(0, m.index + m[0].length)).width);
        const hit = b > a && document.elementFromPoint((a + b) / 2, middle);
        if (hit === el || hit?.localName === 'x-pw-glass') {
          html += `<i style="position:absolute;left:${a}px;top:${middle - size * .65}px;width:${b - a}px;height:${size * 1.3}px;background:#64748b"></i>`;
        }
      }
    }
    if (html !== drawn) layer.innerHTML = drawn = html;
    requestAnimationFrame(draw);
  };
  let queued = 0;
  addEventListener('DOMContentLoaded', () => {
    document.head.append(Object.assign(document.createElement('style'), {
      textContent: `::highlight(redact){color:transparent;background-color:#64748b}::highlight(erase){color:transparent}
        [data-demo-hidden]{display:none!important}#new-inference-key,[data-demo-blur]{filter:blur(8px)}`,
    }));
    scan();
    document.documentElement.append(layer);
    draw();
    const rescan = () => queued ||= requestAnimationFrame(() => { queued = 0; scan(); });
    new MutationObserver(rescan).observe(document.body, { childList: true, subtree: true, characterData: true });
    addEventListener('input', rescan, true);
  });
}, [redact, conceal]);
const page = await context.newPage();
await page.goto(new URL(episode.start ?? '', base).href);
await sleep(1500);

const escape = s => s.replace(/[&<>"]/g, c => `&#${c.charCodeAt(0)};`);
const cuts = [];
// When the current beat's narration ends, and when the last title card fades.
let ends = 0, card = 0;
const helpers = {
  page, base,
  pause: ms => page.waitForTimeout(ms),
  open: async route => { await page.goto(new URL(route, base).href); await page.waitForLoadState(); },
  // Shows a title card for ms, but lets the beat move on after wait ms.
  chapter(title, description, ms = 2600, wait = ms) {
    card = Date.now() + ms;
    return Promise.race([page.screencast.showChapter(title, { description, duration: ms }), sleep(wait)]);
  },
  // Dims the page around a locator, with an optional caption, once any title card is gone. It lasts until the line ends,
  // or ms if that's sooner, and at least 1.2 seconds. showOverlay blocks for its duration.
  async spotlight(target, label = '', ms = Infinity) {
    await sleep(card - Date.now());
    await target.scrollIntoViewIfNeeded();
    const b = await target.boundingBox(), x = b.x - 6, y = b.y - 6, w = b.width + 12, h = b.height + 12;
    const top = y + h + 60 < 720 ? y + h + 12 : Math.max(8, y - 52);
    const caption = label && `<div style="position:absolute;left:${Math.min(Math.max(8, x), 900)}px;top:${top}px;max-width:360px;padding:7px 13px;border-radius:9px;background:#0f172a;color:#f8fafc;font:600 16px/1.4 'Segoe UI',system-ui,sans-serif;box-shadow:0 6px 20px rgba(0,0,0,.35);animation:in .3s ease-out">${escape(label)}</div>`;
    await page.screencast.showOverlay(`<style>@keyframes in{from{opacity:0}}</style><div style="position:absolute;left:${x}px;top:${y}px;width:${w}px;height:${h}px;border:3px solid #38bdf8;border-radius:12px;box-shadow:0 0 0 100vmax rgba(2,6,23,.45);animation:in .3s ease-out"></div>${caption}`,
      { duration: Math.min(ms, Math.max(1200, ends - Date.now())) });
  },
  // Smoothly scrolls a locator into view and waits until it settles. The portal's scroll padding clears its sticky bars.
  async scroll(target, block = 'center') {
    await target.evaluate((el, block) => el.scrollIntoView({ behavior: 'smooth', block }), block);
    for (let last, top; (top = await target.evaluate(el => el.getBoundingClientRect().top)) !== last;) {
      last = top;
      await page.waitForTimeout(150);
    }
    await page.waitForTimeout(300);
  },
  async cut(act, keep = 600) {
    const from = Date.now() - t0;
    await act();
    cuts.push([from, Date.now() - t0, keep]);
  },
};

fs.mkdirSync(dir, { recursive: true });
const video = path.join(dir, 'video.webm');
const t0 = Date.now();
await page.screencast.start({ path: video, size: { width: 1920, height: 1080 }, fps: 30 });
await page.screencast.showActions({ duration: 700, style: { point: 'width:30px;height:30px;border-radius:50%;background:rgba(56,189,248,.3);border:2px solid #38bdf8', title: 'display:none' } });
const cues = [];
for (const [text, act] of beats) {
  const samples = text && await speak(text), ms = text ? samples.length / rate * 1000 : 0;
  ends = Date.now() + ms;
  if (text) cues.push({ t: Date.now() - t0, text, samples });
  await Promise.all([sleep(ms), act?.(helpers)]);
  if (text) await sleep(gap);
}
await sleep(1500);
const length = Date.now() - t0;
await page.screencast.stop();
await browser.close();

// A cut starts once the narration it waited under has ended, and later narration and captions move up by what it removed.
const spans = cuts.map(([from, to, keep]) => [keep + Math.max(from, ...cues.filter(c => c.t <= from).map(c => c.t + c.samples.length / rate * 1000)), to - keep])
  .filter(([a, b]) => b - a > 500);
const shift = ms => ms - spans.filter(([, b]) => b <= ms).reduce((sum, [a, b]) => sum + b - a, 0);
const mix = new Float32Array(Math.ceil(shift(length) / 1000 * rate));
for (const { t, samples } of cues) mix.set(samples, Math.round(shift(t) / 1000 * rate));
const narration = path.join(dir, 'narration.f32');
fs.writeFileSync(narration, Buffer.from(mix.buffer));

// Captions split each sentence into even parts of at most about 84 characters and share the line's time by length.
const stamp = ms => new Date(Math.round(ms)).toISOString().slice(11, 23).replace('.', ',');
let srt = '', n = 0;
for (const { t, text, samples } of cues) {
  const chunks = text.match(/\S.*?(?:[.!?](?=\s|$)|$)/g).flatMap(sentence => {
    const size = sentence.length / Math.ceil(sentence.length / 84) + 4, parts = [''];
    for (const word of sentence.split(' ')) {
      if (parts.at(-1) && parts.at(-1).length + word.length >= size) parts.push(word);
      else parts[parts.length - 1] = `${parts.at(-1)} ${word}`.trim();
    }
    return parts;
  });
  const chars = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  let at = shift(t);
  for (const chunk of chunks) {
    const ms = samples.length / rate * 1000 * chunk.length / chars;
    srt += `${++n}\n${stamp(at)} --> ${stamp(at + ms)}\n${chunk}\n\n`;
    at += ms;
  }
}
const captions = path.join(dir, `${id}.srt`);
fs.writeFileSync(captions, srt);

const mp4 = path.join(dir, `${id}.mp4`);
const sec = ms => (ms / 1000).toFixed(3);
const trim = spans.length ? ['-vf', `select='not(${spans.map(([a, b]) => `between(t,${sec(a)},${sec(b)})`).join('+')})',` +
  `setpts='PTS-(${spans.map(([a, b]) => `gte(T,${sec(b)})*${sec(b - a)}`).join('+')})/TB'`] : [];
execFileSync(ffmpeg, ['-y', '-v', 'error', '-i', video, '-f', 'f32le', '-ar', String(rate), '-ac', '1', '-i', narration, '-i', captions,
  '-map', '0:v', '-map', '1:a', '-map', '2:s', ...trim, '-c:v', 'libx264', '-preset', 'slow', '-crf', '18', '-pix_fmt', 'yuv420p',
  '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', '-c:s', 'mov_text', '-metadata:s:s:0', 'language=eng', '-movflags', '+faststart', mp4],
  { stdio: 'inherit' });
console.log(`${mp4} (${clock(shift(length))}${spans.length ? `, ${sec(length - shift(length))} s of waiting cut` : ''})`);
