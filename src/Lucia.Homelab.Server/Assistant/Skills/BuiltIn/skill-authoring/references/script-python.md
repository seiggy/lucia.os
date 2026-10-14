# Python script skeleton

Save as `scripts/check.py`. Arguments arrive as JSON in `LUCIA_SKILL_ARGS` and on stdin.

```python
import json
import os
import re
import subprocess
import sys

UNIT = re.compile(r"^[A-Za-z0-9@_.:-]{1,80}\.service$")
MIN_LINES, MAX_LINES = 5, 200


def fail(message: str, code: int = 2) -> None:
    print(f"Error: {message}", file=sys.stderr)
    sys.exit(code)


try:
    args = json.loads(os.environ.get("LUCIA_SKILL_ARGS") or "{}")
except json.JSONDecodeError:
    fail("the arguments aren't valid JSON.")

unit = args.get("unit", "")
if not isinstance(unit, str) or not UNIT.fullmatch(unit):
    fail("unit must be a systemd service name such as docker.service.")

lines = args.get("lines", 50)
if not isinstance(lines, int) or not MIN_LINES <= lines <= MAX_LINES:
    fail(f"lines must be a whole number from {MIN_LINES} to {MAX_LINES}.")

# A fixed argument list, no shell, a time limit.
try:
    result = subprocess.run(
        ["journalctl", "-u", unit, "-n", str(lines), "--no-pager"],
        capture_output=True, text=True, timeout=20, check=False,
    )
except subprocess.TimeoutExpired:
    fail("journalctl took too long.", 124)

print(result.stdout[-20000:])
sys.exit(result.returncode)
```

Rules: pass an argument list to `subprocess`, never `shell=True`; no `os.system`, `eval` or `exec`; check every value with `fullmatch` or an allow-list; cap output.
