# JavaScript and TypeScript script skeleton

Save as `scripts/check.mjs` (or `.ts`, run with Node's type stripping, so use only erasable types). Arguments arrive as JSON in `LUCIA_SKILL_ARGS` and on stdin.

```js
import { execFileSync } from "node:child_process";

const NAME = /^[a-z0-9][a-z0-9_.-]{0,63}$/;

function fail(message, code = 2) {
  console.error(`Error: ${message}`);
  process.exit(code);
}

let args;
try {
  args = JSON.parse(process.env.LUCIA_SKILL_ARGS || "{}");
} catch {
  fail("the arguments aren't valid JSON.");
}

const container = args.container;
if (typeof container !== "string" || !NAME.test(container)) {
  fail("container must be a container name: lowercase letters, digits, _, - or .");
}

const tail = Number.isInteger(args.tail) ? args.tail : 100;
if (tail < 1 || tail > 500) fail("tail must be a whole number from 1 to 500.");

// A fixed argument array, no shell, a time limit.
try {
  const out = execFileSync("docker", ["logs", "--tail", String(tail), container], {
    encoding: "utf8", timeout: 20000, maxBuffer: 1024 * 1024, stdio: ["ignore", "pipe", "pipe"],
  });
  console.log(out.slice(-20000));
} catch (error) {
  fail(error.killed ? "docker took too long." : "docker couldn't read that container.", error.killed ? 124 : 1);
}
```

In the sandbox there is no `child_process`: use JavaScript there only for data work. Rules: `execFile` with an argument array, never `exec` with a built string; no `eval` or `new Function`; test every value against a pattern or allow-list; cap output.
