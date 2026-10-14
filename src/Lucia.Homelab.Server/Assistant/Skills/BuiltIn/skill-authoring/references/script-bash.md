# Bash script skeleton

Save as `scripts/check.sh`. Lucia passes the arguments as JSON in `LUCIA_SKILL_ARGS` (and on stdin), without the `node` entry. `jq` may not be installed: check for it, or parse with `python3`.

```bash
#!/usr/bin/env bash
set -euo pipefail

fail() { echo "Error: $1" >&2; exit 2; }

args="${LUCIA_SKILL_ARGS:-}"
command -v jq >/dev/null 2>&1 || fail "jq isn't installed on this server."
pool="$(printf '%s' "$args" | jq -r '.pool // empty')"

# Validate against a strict pattern before using a value anywhere.
if [ -n "$pool" ] && ! [[ "$pool" =~ ^[A-Za-z0-9_.-]{1,64}$ ]]; then
  fail "pool must be letters, digits, _, - or . (up to 64 characters)."
fi

# Fixed command, bounded time and output. The value is one quoted argument, never part of a command string.
if [ -n "$pool" ]; then
  timeout 20 zpool status -- "$pool" | head -n 60
else
  timeout 20 zpool status | head -n 120
fi
```

Rules: no `eval`, no `bash -c "$value"`, no unquoted variables, a `timeout` on anything that can hang, `head` on anything that can be long.
