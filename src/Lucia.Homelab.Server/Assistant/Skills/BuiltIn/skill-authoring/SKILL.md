---
name: skill-authoring
description: Turn declined, expired or timed-out tool calls in past chats into narrow skills for the owner to review. Use for the /create-skill command and for review jobs that look through recent chats for skills worth drafting.
metadata:
  lucia.execution: node
  lucia.tier: read
---

# Skill authoring

Past chats show where a broad command didn't work: the owner declined it, never answered, it failed, or it ran out of time. When the same intent is a repeatable, narrow job, a skill can do it safely. You draft the skill; the owner reviews and enables it. Drafts are always saved disabled and nothing runs until the owner enables one.

## 1. Find candidates

- `list_chats` shows the owner's chats with counts of declined, expired, failed and timedOut calls and when each was last reviewed. Skip chats already reviewed unless the owner asked for them. For `/create-skill`, the context block names the chats to use.
- `read_chat_transcript` with `onlyIssues: true` shows just the problem calls, each with the message before it. Read the whole chat only if you need more context.
- A good candidate is a declined, expired or timed-out `run_command` (or similar) whose intent is a job the owner will want again: check a service, collect a diagnostic, read a status. Skip one-off fixes, anything tied to a single incident, and anything that only a free-form command could do.

## 2. Dedupe first

- Call `list_skills` and `get_skill` with `includeDisabled: true` every time: without it you only see enabled skills, and you would draft a copy of something already waiting for review.
- If an enabled skill already covers it, don't draft. Say which skill, and why the earlier call didn't use it.
- If your own disabled draft is close, improve it with `draft_skill` and `update: true` instead of creating a near-duplicate. You can't change an enabled skill, a built-in, the owner's skill or a draft the owner has edited; pick another name or describe the gap in your report.
- Changing an existing skill is `edit_skill`. The owner approves every edit, so use it only when they asked for the change, and never in Plan mode or a job.

## 3. What narrow means

- One purpose. The name and description say what it does and when to use it.
- Typed, validated parameters: allow-lists or strict patterns for hosts, units, container names, paths and numbers. Never take a command, a shell fragment or a free-form path.
- Read-only when possible. Idempotent. Bounded runtime and output (limit lines, use timeouts).
- Clear output for the model: short, labelled lines or one JSON object.
- Fail closed: on bad input or an unexpected state, print one short error and exit non-zero.
- Be honest about the tier: `read` only if the script changes nothing.

## 4. Choose where it runs

- `node`: inspects the server the model names (containers, services, disks, logs). The server must have the runtime the script needs.
- `spark`: needs the tools on the Spark, or calls Lucia or LAN APIs from there.
- `sandbox`: pure data processing in Python or JavaScript, with no access to hosts. Other languages are refused.

## 5. Write the package

Pick the template for the language in `references/`:

- `references/skill-template.md`: SKILL.md structure.
- `references/script-bash.md`, `references/script-python.md`, `references/script-javascript.md`, `references/script-csharp.md`: how arguments arrive (JSON in `LUCIA_SKILL_ARGS` and on stdin) and how to validate them.

Send the scripts as `files` under `scripts/` and notes under `references/`. Then call `draft_skill` with the `name`, `description`, `execution`, `tier`, `body`, `rationale`, `sourceChats`, `files` and `parametersSchema`. The rationale says why this narrow skill exists and which declined or timed-out calls it replaces. Read the returned `warnings`: they flag command passthrough, a tier that understates the script, and unvalidated arguments. Fix what you can with `draft_skill` and `update: true`.

Always send `parametersSchema`, the JSON Schema of the arguments object, as JSON text. It is stored as `parameters_schema` in the skill's metadata and is what `load_skill` shows the next model as the script's arguments. Without it a script shows only a bare default. The arguments are one JSON object, never a quoted string and never an array. For a `node` skill the server's name goes inside the object as `node` (Lucia adds that property to your schema if you leave it out), so describe every other input beside it, with `required`, types and limits. Example for a skill that checks patched packages:

```json
{"type":"object","properties":{"node":{"type":"string","description":"The server to run on."},"packages":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":50},"lockWaitSeconds":{"type":"integer","minimum":1,"maximum":300}},"required":["node","packages"]}
```

In the SKILL.md body, show one call whose `arguments` is a real object, such as `{"node": "<server>", "packages": ["openssl", "curl"]}`. The script still validates every value itself: the schema tells a model what to send, it doesn't replace the checks.

## 6. Finish

- `mark_chats_reviewed` for every chat you looked at, with a short note, even when you drafted nothing.
- Report what you drafted and why, any warnings, and that the owner must review and enable it on the Skills page. If you drafted nothing, say why.
