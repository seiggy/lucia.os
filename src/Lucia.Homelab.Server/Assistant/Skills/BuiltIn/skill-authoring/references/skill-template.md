# SKILL.md template

Send the body below as `body` in `draft_skill`; Lucia writes the frontmatter (name, description, execution, tier) for you.

```markdown
# Check ZFS pool health

Use this when the owner asks whether a server's ZFS pools are healthy.

## Arguments

Pass these in the arguments object. `node` names the server (from list_nodes).

- `node` (required): the server to check.
- `pool` (optional): one pool name, letters, digits, `_`, `-` and `.`. Default: every pool.

## Steps

1. Run `scripts/check.sh` with the arguments.
2. Read the output: one line per pool with its state and error counts.
3. Report pools that aren't ONLINE, and any with errors. Don't suggest destructive fixes; offer to look at logs with read_logs.

## Output

`pool=<name> state=<state> read=<n> write=<n> cksum=<n>` per pool. An `Error:` line means the input was refused or the check couldn't run.
```

Keep it short. State what the skill does not do, and never ask the model to pass shell text.
