// Flags words from the humanize-writing skill's Tier 1 list in the docs' prose, ignoring code. It cannot judge tone;
// it only catches the words that most reliably give AI writing away.
//   node tools/docs/lint-prose.mjs                 check every page
//   node tools/docs/lint-prose.mjs a.md b.mdx      check these pages
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const dictionary = path.join(root, '.github/skills/humanize-writing/references/ai-patterns-dictionary.md');
const docs = path.join(root, 'docs-site/docs');

// The Tier 1 table's first column, with "a / b" rows split into both words.
function tierOneWords() {
  const lines = fs.readFileSync(dictionary, 'utf8').split(/\r?\n/);
  const start = lines.findIndex(line => /^###\s+Tier 1/.test(line));
  if (start < 0) throw new Error('The Tier 1 table is missing from the humanize-writing dictionary.');
  const words = [];
  for (const line of lines.slice(start + 1)) {
    if (/^#{1,3}\s/.test(line)) break;
    const cell = /^\|\s*([^|]+?)\s*\|/.exec(line)?.[1];
    if (!cell || /^(AI Word|-+)$/.test(cell)) continue;
    words.push(...cell.split('/').map(word => word.trim().toLowerCase()).filter(Boolean));
  }
  return words;
}

function* pages(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* pages(full);
    else if (/\.mdx?$/.test(entry.name)) yield full;
  }
}

// Prose lines only: no front matter, fenced code, inline code, imports, or URLs.
function* prose(text) {
  let fence = null, front = false;
  const lines = text.split(/\r?\n/);
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    if (index === 0 && line === '---') { front = true; continue; }
    if (front) { if (line === '---') front = false; continue; }
    const marker = /^\s*(`{3,}|~{3,})/.exec(line)?.[1];
    if (marker) { fence = fence === null ? marker[0] : fence === marker[0] ? null : fence; continue; }
    if (fence || /^\s*(import|export)\s/.test(line)) continue;
    yield [index + 1, line.replace(/`[^`]*`/g, ' ').replace(/https?:\/\/\S+/g, ' ')];
  }
}

const words = tierOneWords();
const pattern = new RegExp(`\\b(${words.map(word => word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})\\b`, 'gi');
const files = process.argv.length > 2 ? process.argv.slice(2).map(file => path.resolve(file)) : fs.existsSync(docs) ? [...pages(docs)] : [];
let found = 0;
for (const file of files) {
  if (!/\.mdx?$/.test(file) || !fs.existsSync(file)) continue;
  for (const [number, line] of prose(fs.readFileSync(file, 'utf8')))
    for (const hit of line.matchAll(pattern)) {
      found++;
      console.log(`${path.relative(root, file)}:${number}: "${hit[1]}" is a Tier 1 AI word. Say it plainly. See the humanize-writing dictionary.`);
    }
}
if (found) {
  console.log(`\n${found} problem${found === 1 ? '' : 's'}. Rewrite them with the humanize-writing skill.`);
  process.exit(1);
}
console.log(`Prose check passed (${files.length} page${files.length === 1 ? '' : 's'}, ${words.length} words).`);
