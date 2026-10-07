---
title: Assistant and jobs
description: The SRE assistant's tools and approval model, scheduled assistant jobs, and web push notifications.
section: architecture
order: 8
---

Lucia's assistant is an SRE that lives in a side panel of the portal. It can read your lab: nodes, containers, logs, apps, backups, DNS and network clients. It can also change things, through a fixed set of tools rather than a shell it improvises in. Anything risky waits for you. Assistant jobs run saved prompts on a schedule, and the assistant notifies your phone when a job needs you. It's a colleague who checks the logs before paging you, which already puts it ahead of some colleagues.

The assistant and jobs are **owner-only**. Every assistant endpoint requires the `HostOwner` policy.

![The Assistant jobs page listing scheduled jobs with their next and last runs](/screenshots/jobs.png)

## Runtime and models

The assistant runs on Microsoft Agent Framework over the GitHub Copilot runtime, inside the web host on the Spark. You pick its model from the **model menu** in the panel's composer. There are three sources:

| Source | Needs | Notes |
|---|---|---|
| GitHub Copilot | A GitHub sign-in through the device flow | The GitHub token is encrypted on the Spark |
| LiteLLM | The LiteLLM catalog app | Lucia mints a LiteLLM virtual key for each owner and keeps it encrypted |
| Local AI | The Spark's models, or a Local AI app on a server | No GitHub sign-in needed |

Only Copilot models need the GitHub sign-in. LiteLLM and Local AI models run through the same runtime as bring-your-own-key providers.

## Tools

Every tool belongs to a tier, and the tier decides what happens when the assistant calls it.

| Tier | Tools |
|---|---|
| **Read** | `list_nodes`, `get_node`, `list_containers`, `read_logs`, `list_apps`, `get_app`, `list_catalog`, `get_catalog_app`, `list_storage`, `list_backups`, `dns_lookup`, `list_network_clients`, `ask_owner`, `notify_owner` |
| **Web** | `read_web_page`: public pages only, up to 2 MB fetched and 20,000 characters returned |
| **Change** | `save_custom_app`, `install_catalog_app`, `app_action`, `move_app`, `run_backup`, `upgrade_app_images`, `check_node_updates` |
| **Destructive** | `delete_app`, `restore_backup`, `set_public_route`, `node_action` (install updates, restart, update agent), `run_command` |
| **Secret** | `request_secret` |

A few details matter:

- `request_secret` asks *you* to type a password or token for a custom app. Lucia saves it in the app's environment. The model never sees the value.
- `run_command` runs a command or script **as root on a managed server** through the node agent. It's non-interactive and capped: a script of up to 16 KB, a 30-minute limit, and only the last 32 KB of output returned. The approval card says plainly: "It can change anything there."
- Tool output returned to the model is capped at 48 KB per call. A turn can make at most 50 tool calls.

## The approval model

Approvals are enforced on the server by a deny-by-default policy (Microsoft's Agent Governance Toolkit), not by a prompt asking the model to behave.

| Tier | In a chat | In a job |
|---|---|---|
| Read | Runs | Runs |
| Web | Runs for allowed sites; asks for others | Same, plus the job's own allowed sites |
| Change | Runs if allowed in Settings → Assistant or earlier in this chat; otherwise asks | Runs if allowed in settings or by the job; otherwise asks |
| Destructive | **Always asks** | Asks, unless that job's settings allow the tool |
| Secret | Asks you for the value | Same |

**Plan mode** is the other switch in the composer, next to **Execute**. In Plan mode the assistant can look things up and plan, but every change, destructive and secret tool is denied. "In Plan mode it changes nothing at all."

### Settings → Assistant

This page holds two standing permissions:

- **Changes it may make without asking.** Tick individual change tools to let them run unasked.
- **Sites it may read without asking.** A list of host names for `read_web_page`.

Destructive tools aren't on that list. In the page's own words: "In chats, it always asks before it deletes an app, restores a backup, puts an app on the internet or takes it off, updates or restarts a server, or runs a command on one."

### Approval cards

When a tool needs approval, the chat shows a card with what's about to happen, the exact input, and an impact line such as "Every app on lucialab01 is offline for a few minutes while it restarts." You can:

- **Approve** this one call;
- **Decline…**, optionally telling the assistant why;
- for change tools and web pages, approve **and let it do the same for the rest of this chat**.

A chat grant lives in memory on the host, so it ends with the chat or a restart of the host. The assistant waits up to **30 minutes** for an answer.

## Assistant jobs

**Local AI → Assistant jobs** schedules saved prompts. "The job runs as you, in a new chat each time." A job has:

- **A name and up to 10 prompts**, each up to 32,768 characters. Prompts run in order in the same chat, each after the last answer ends. If one fails or is stopped, the rest don't run.
- **A model**, chosen from the same sources as the panel.
- **A schedule**: presets for common repeats, or a custom five-field cron expression such as `30 6 * * 1-5`, in a time zone you choose. The editor previews the next run times. Untick "Run on this schedule" to pause the job. You can still use **Run now**.
- **What it may do without asking**: change tools, a separate **Risky changes** group of destructive tools, and extra sites it may read. These add to Settings → Assistant, for this job only.

```text
30 6 * * 1-5     weekdays at 06:30 in the job's time zone
0 3 * * 0        Sundays at 03:00
```

You can keep up to 50 jobs. Lucia keeps the 50 most recent runs of each, and every run is a normal chat you can open from the job's **History**. Deleting a job leaves its past chats in your chat history.

### Unattended approvals

When a job hits something it isn't allowed to do, it doesn't skip it or guess. The run pauses, Lucia sends a notification, and the job waits up to 30 minutes for your approval. In a job's chat, the approval card also offers **approve and allow for this job**. That adds the tool to the job's settings for every future run.

> [!CAUTION]
> Allowing a risky change for a job means it runs with nobody watching. The editor says so: "Nobody checks these before they happen." Only allow what the job's prompts truly need, and prefer allowing `run_command` for no job at all.

## Notifications

**Settings → Notifications** turns on web push for the device you're on, and lists every device you've added. In the page's words, "The assistant notifies these devices when a job needs your approval, fails, or has something to tell you. Tapping a notification opens its chat."

- Lucia generates its own P-256 VAPID key and keeps it protected with the host's Data Protection keys. Notifications travel through your browser vendor's standard push service. You don't need an account with any notification provider.
- Each device can be renamed, sent a test (**Send a test**) or removed. You can register up to 20 devices.
- On iPhone and iPad, add Lucia to your Home Screen and open it from there first. Safari only supports web push for Home Screen web apps.
- The assistant's `notify_owner` tool sends a short title and message. It's meant for jobs that find something you must know. In a chat you're watching, it just tells you.

> [!IMPORTANT]
> Web push needs the portal to be served over HTTPS from a name your devices trust. In practice that means the domain is set up (Settings → Domains) or your devices trust Lucia's private CA. See [Networking and DNS](/docs/architecture/networking-and-dns/).
