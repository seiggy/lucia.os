# SKILL.md template

Send the body below as `body` in `draft_skill`; Lucia writes the frontmatter (name, description, execution, tier) for you. Send the arguments' JSON Schema separately as `parametersSchema`; it is saved as `parameters_schema` in the frontmatter `metadata` and `load_skill` shows it to the model that runs the skill.

```markdown
# Check ZFS pool health

Use this when the owner asks whether a server's ZFS pools are healthy.

## Arguments

Pass them as one JSON object in `arguments`, never as a quoted string or an array. `node` names the server (from list_nodes) and goes inside the object, not beside it.

Example: `{"node": "<server>", "pool": "tank"}`

- `node` (required): the server to check.
- `pool` (optional): one pool name, letters, digits, `_`, `-` and `.`. Default: every pool.

## Steps

1. Run `scripts/check.sh` with the arguments.
2. Read the output: one line per pool with its state and error counts.
3. Report pools that aren't ONLINE, and any with errors. Don't suggest destructive fixes; offer to look at logs with read_logs.

## Output

`pool=<name> state=<state> read=<n> write=<n> cksum=<n>` per pool. An `Error:` line comes from the script: the input was refused or the check couldn't run. A line that starts with `HOST:` comes from Lucia before the script ran: the call itself was malformed.
```

And the `parametersSchema` to send with it:

```json
{"type":"object","properties":{"node":{"type":"string","description":"The server to check."},"pool":{"type":"string","pattern":"^[A-Za-z0-9_.-]{1,64}$"}},"required":["node"]}
```

Keep it short. State what the skill does not do, and never ask the model to pass shell text.
