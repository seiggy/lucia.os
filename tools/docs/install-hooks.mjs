// Points this clone's git at the repo's hooks, so commits run the docs agent. Run once per clone: node tools/docs/install-hooks.mjs
import { spawnSync } from 'node:child_process';

const run = (...args) => spawnSync('git', args, { encoding: 'utf8' });
const set = run('config', 'core.hooksPath', '.githooks');
if (set.status !== 0) { console.error(set.stderr); process.exit(1); }
run('update-index', '--chmod=+x', '.githooks/pre-commit');
console.log('Hooks installed: commits now run the docs agent (tools/docs/pre-commit.mjs). LUCIA_SKIP_DOCS=1 skips it for one commit.');
