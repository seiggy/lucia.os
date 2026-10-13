// Pre-commit hook body: when staged changes could make the docs wrong, run the docs-keeper agent, stage what it wrote,
// and check the prose. It never blocks a commit because the agent failed or isn't installed; it blocks only on prose the
// lint rejects, and LUCIA_SKIP_DOCS=1 skips the whole thing.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const git = (...args) => spawnSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).stdout ?? '';
const say = text => console.log(`[docs] ${text}`);
const model = process.env.LUCIA_DOCS_MODEL || 'claude-haiku-5.5';
const minutes = Number(process.env.LUCIA_DOCS_MINUTES) || 8;

if (process.env.LUCIA_SKIP_DOCS === '1' || process.env.LUCIA_DOCS_RUNNING === '1') process.exit(0);

const staged = git('diff', '--cached', '--name-status', '--diff-filter=ACMRD').split('\n').filter(Boolean)
  .map(line => { const [status, ...rest] = line.split('\t'); return { status: status[0], file: rest.at(-1).replaceAll('\\', '/') }; });
if (!staged.length) process.exit(0);

const globToRegExp = glob => new RegExp('^' + glob.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*\*/g, '\0').replace(/\*/g, '[^/]*').replace(/\0/g, '.*') + '$');
const map = JSON.parse(fs.readFileSync(path.join(root, 'docs-map.json'), 'utf8')).entries
  .map(entry => ({ ...entry, patterns: entry.paths.map(globToRegExp) }));
const ignored = /(^|\/)(package-lock\.json|bin|obj|node_modules|dist|\.checks)\//;
const isDoc = file => file.startsWith('docs-site/') && !file.startsWith('docs-site/src/') && !/^docs-site\/(docusaurus\.config|sidebars|package)/.test(file) || file === 'docs-map.json';
// Mapped code, plus anything else in the product folders that isn't a test.
const relevant = file => !isDoc(file) && !ignored.test(file) && !/(^|\/)tests?\//.test(file) && !file.startsWith('site/')
  && (map.some(entry => entry.patterns.some(pattern => pattern.test(file))) || /^(src|tools|deployment|docs)\//.test(file));
const changed = staged.filter(item => relevant(item.file));
const stagedDocs = staged.filter(item => isDoc(item.file) && /\.mdx?$/.test(item.file) && item.status !== 'D').map(item => item.file);

function runAgent() {
  const copilot = spawnSync(process.platform === 'win32' ? 'where' : 'which', ['copilot'], { encoding: 'utf8' });
  if (copilot.status !== 0) return say('The Copilot CLI isn\'t installed, so the docs weren\'t checked.');
  const before = new Set(git('status', '--porcelain').split('\n').filter(Boolean).map(line => line.slice(3)));
  const patch = path.join(os.tmpdir(), `lucia-docs-${process.pid}.patch`);
  const task = path.join(os.tmpdir(), `lucia-docs-${process.pid}.md`);
  const diff = git('diff', '--cached', '-U3', '--', ...changed.map(item => item.file));
  fs.writeFileSync(patch, diff.length > 400_000 ? diff.slice(0, 400_000) + '\n[diff cut at 400 KB: read the files for the rest]\n' : diff);
  fs.writeFileSync(task, [
    'A commit is about to be made. Update the docs for it.',
    '',
    `Repo root: ${root}`,
    `Staged diff of the changed product files: ${patch}`,
    'Changed files:',
    ...changed.map(item => `- ${item.status} ${item.file}`),
    '',
    'Follow the lucia-docs and humanize-writing skills. Change only docs-site/ and docs-map.json. Do not stage or commit.',
    'If nothing a person would notice changed, change nothing and say so.',
  ].join('\n'));
  say(`${changed.length} product file${changed.length === 1 ? '' : 's'} changed. Asking docs-keeper (${model}) what the docs need…`);
  const run = spawnSync('copilot', ['--agent', 'docs-keeper', '--model', model, '--allow-all-tools', '--allow-all-paths', '--add-dir', root, '-s', '-p',
    `Read ${task} and do what it says.`], {
    cwd: root, encoding: 'utf8', shell: process.platform === 'win32', timeout: minutes * 60_000, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, LUCIA_DOCS_RUNNING: '1' },
  });
  for (const file of [patch, task]) fs.rmSync(file, { force: true });
  if (run.error || run.status !== 0) { say(`docs-keeper didn't finish (${run.error?.code ?? `exit ${run.status}`}). Commit goes ahead; run it by hand later.`); return; }
  const report = (run.stdout || '').trim();
  if (report) console.log(report.split('\n').slice(-12).map(line => `[docs]   ${line}`).join('\n'));

  const after = git('status', '--porcelain').split('\n').filter(Boolean).map(line => line.slice(3).replaceAll('\\', '/'));
  const touched = after.filter(file => !before.has(file) || isDoc(file));
  const stray = touched.filter(file => !isDoc(file) && !before.has(file) && !file.startsWith('docs-site/'));
  if (stray.length) say(`docs-keeper also changed files outside the docs, which I left unstaged: ${stray.join(', ')}`);
  git('add', '--', 'docs-site', 'docs-map.json');
}

if (changed.length) runAgent();
else if (staged.some(item => !isDoc(item.file))) say('No product code changed, so the docs weren\'t checked.');

const checked = [...new Set([...stagedDocs, ...git('diff', '--cached', '--name-only', '--diff-filter=ACM').split('\n').filter(file => isDoc(file) && /\.mdx?$/.test(file))])];
if (checked.length) {
  const lint = spawnSync('node', [path.join(root, 'tools/docs/lint-prose.mjs'), ...checked.map(file => path.join(root, file))], { encoding: 'utf8' });
  if (lint.status !== 0) {
    console.log(lint.stdout);
    say('Commit stopped. Fix the wording above (humanize-writing skill), stage the pages, and commit again. LUCIA_SKIP_DOCS=1 skips this check.');
    process.exit(1);
  }
}
