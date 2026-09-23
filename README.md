# Bloodraven

Task diagnostics are available with `sudo journalctl -u bloodraven --since "15 minutes ago" --no-pager -n 100`. Version 0.3.5 logs task IDs, submitted settings, elapsed time, failure stages, new/resumed execution and available exit codes. Known CLI errors are classified into actionable reasons; unknown errors remain explicitly unclassified. Raw stderr, prompts, answers and credentials are not logged. A failed task keeps its saved conversation; use a separate `/conversation diagnostic` for testing instead of resetting your existing history.

Bloodraven is a small, self-hosted Telegram interface for the OpenAI Codex CLI. It runs on your own Ubuntu machine, uses your existing Codex login, and requires no inbound ports or public web server.

> [!WARNING]
> Anyone who controls your authorised Telegram account can instruct Codex with the permissions you configure. Read [SECURITY.md](SECURITY.md) before installation.

## What it does

- Receives messages through Telegram long polling
- Silently ignores every user except the configured numeric Telegram user ID
- Runs `codex exec --json` inside a Git repository
- Saves the exact Codex session ID and resumes it on later messages
- Persists queued messages and replies across restarts, running one Codex task at a time
- Converts common Markdown formatting to Telegram HTML automatically
- Keeps named Codex conversations, a controllable queue, and durable recurring schedules
- Accepts photos/documents and returns repository files on request
- Edits one progress message per task and sends the complete final answer separately
- Offers native Codex approval buttons and `/health` diagnostics
- Runs as a hardened systemd service

Bloodraven stores no personal configuration in Git. Its installer writes secrets to `/etc/bloodraven/bloodraven.env` with mode `0600` and conversation state to `/var/lib/bloodraven`.

## One-command Ubuntu installation

First change into the Git repository that Codex should use, then run the installer as your normal Ubuntu user—not as `root`:

```bash
cd ~/path/to/your-agent-repository
(set -e; script=$(mktemp); trap 'rm -f -- "$script"' EXIT; curl -fsSL https://raw.githubusercontent.com/Steveiwonder/Bloodraven/master/install.sh -o "$script"; bash "$script")
```

Bloodraven remembers the directory in which you launch this command and offers it as the default working directory. You can enter a different path during setup. Every new and resumed Codex session will start in the selected directory, so its Git history, `AGENTS.md`, skills, scripts, and other repository instructions are available to Codex.

The interactive setup will:

1. install the required Ubuntu packages;
2. install the .NET 10 SDK if needed;
3. install the Codex CLI using OpenAI's official installer if needed;
4. pause for Codex sign-in if this machine is not already authenticated;
5. download the latest Bloodraven source;
6. securely pair your Telegram user and private-chat IDs using a one-time code;
7. offer the directory where you launched the installer as Codex's working repository, while allowing you to override it;
8. build Bloodraven and install it as an automatically starting systemd service;
9. store secrets outside the repository with root-only file permissions.

Before running it, create a Telegram bot so you have its token ready.

### Create your Telegram bot

1. Open Telegram and start a chat with [@BotFather](https://t.me/BotFather). Check that the username is exactly `@BotFather` and that it has Telegram's verification tick.
2. Send `/newbot`.
3. Enter a display name, such as `My Bloodraven`.
4. Enter a unique username ending in `bot`, such as `my_bloodraven_bot`.
5. BotFather will reply with an HTTP API token. It looks similar to `123456789:AAExample...`. Keep it private—you will paste it into the installer shortly.

You do not need a webhook, public IP address, domain name, or open firewall port. Bloodraven connects out to Telegram using long polling.

### Follow the prompts

1. Paste the private Telegram bot token from BotFather.
2. When prompted, open a **private** chat with the bot and send the exact `/start <one-time-code>` command printed by setup. The code expires after ten minutes.
3. Return to the terminal, press Enter, and confirm the displayed numeric Telegram user and chat IDs by typing `yes`. Unrelated messages and group chats cannot claim ownership.
4. Confirm the displayed working repository by pressing Enter, or type a different path. If the selected directory is not already a Git repository, setup asks before initialising one.
5. Choose a Codex sandbox. Press Enter for `danger-full-access`, allowing network access and access to files available to your Linux user.
6. Choose how often Telegram should receive progress updates. Press Enter for **30 seconds**, enter **10–3600** seconds, or enter **0** to turn them off.
7. Choose whether to enable approval buttons. Press Enter for **off**, which preserves the usual sandbox behaviour, or enter **on** for the approval mode explained below.

The installer builds Bloodraven, stores its private settings outside the Git repository, installs a systemd service, and starts it automatically.

Relative directory overrides are resolved against the directory where you launched setup. Git worktrees and submodules are supported. Keep the repository outside `/tmp` and `/var/tmp`, which are private to the service.

If setup finds an existing configuration, it stops without overwriting it. Use the upgrade command below to upgrade or reinstall the application while preserving your settings. If first-time activation failed after writing configuration, the upgrade command can retry activation as the same Linux user.

### Test it

Open your bot in Telegram and send:

```text
Tell me which repository you are working in and list its files.
```

The bot should acknowledge the conversation, show `Working…`, and then send Codex's answer. The working message is edited in place as the task progresses. You can check the service on Ubuntu with:

```bash
sudo systemctl status bloodraven
```

If it is not running, view the latest logs:

```bash
sudo journalctl -u bloodraven -n 100 --no-pager
```

### Permissions

New installations default to `danger-full-access` with approvals off, suitable for administering your homelab over the network. Your Telegram account becomes a powerful remote-control interface to files and systems accessible to the service user. Choose `workspace-write` for restricted repository work or `read-only` for inspection. Existing installations retain their saved settings on upgrade; the runtime fallback when no sandbox setting exists remains `workspace-write`. Enabling approval mode uses its separate read-only sandbox regardless of this installation choice.

### Inspect before running

If you prefer to inspect the installer before executing it:

```bash
curl -fsSL https://raw.githubusercontent.com/Steveiwonder/Bloodraven/master/install.sh -o bloodraven-install.sh
less bloodraven-install.sh
bash bloodraven-install.sh
```

## Telegram commands

| Command | Action |
|---|---|
| `/conversation homelab` | Create or switch to a named conversation |
| `/conversations` | List conversations with switch buttons |
| `/new` | Reset the selected conversation when this command reaches the queue |
| `/status` | Show the active conversation, session, running task and queue depth |
| `/health` | Show Bloodraven version, repository, live Codex/login check, polling, schedules and recent failures |
| `/queue` | Show tasks with **Move to next** and **Remove** buttons |
| `/front ID` / `/remove ID` | Move a pending task to next, or remove it |
| `/clear` | Remove pending tasks; keep the running task and schedules |
| `/cancel` | Cancel the running Codex task, including an approval wait |
| `/schedule` | Show scheduling examples and controls |
| `/approvals on` / `/approvals off` | Enable or disable native approval mode for new tasks |
| `/file reports/summary.md` | Send a file from the configured repository as a Telegram document |
| `/help` | Show available commands |

Ordinary text goes to Codex as prompt data, never as CLI arguments. Unknown slash commands are rejected with help; native interactive Codex slash commands are not implemented.

### Named conversations

Send `/conversation homelab`, then your request. Use `/conversation research` for another history, and `/conversations` to switch back. Conversation names use 1–32 lowercase letters, digits, underscores or hyphens; up to 30 are supported. Your existing conversation becomes **default** automatically.

All conversations use the working repository chosen during installation, including its `AGENTS.md` instructions. One task runs at a time. Each task remembers the conversation selected when it was queued; switching conversations never redirects waiting work. `/new` resets only that conversation and follows queue order.

### Photos and documents

Send a photo or attach a file with a caption describing what you want. Supported documents: `.txt`, `.log`, `.md`, `.json`, `.yaml`, `.yml`, `.csv`, `.xml`, `.pdf`, `.png`, `.jpg`, `.jpeg` and `.webp`. Each attachment is limited to **10 MiB**. Photos/images are passed to Codex as images; other documents are downloaded to a private local file and its path is included in the prompt. Reading a PDF may require tools available on your host. Albums arrive as separate tasks.

To retrieve a generated file, send `/file path/relative/to/repository`. Bloodraven snapshots it before delivery. Files must be at most 10 MiB and inside the selected repository; absolute paths, `..`, `.git`, and symlinks are rejected. Files are only uploaded when you request them. Downloaded attachments are removed after the task; pending outgoing documents survive restarts and are removed after delivery. Telegram itself retains the copies you send or receive.

### Recurring tasks

Examples:

```text
/schedule add every 30m | Check container health and report any problems
/schedule add daily 08:00 Europe/London | Summarise the overnight logs
/schedule add weekly sat 08:00 Europe/London | Check for available updates; do not install them
/schedule list
```

Intervals accept `m`, `h`, or `d`, from one minute to one year. Daily and weekly times use the timezone you supply; weekly days are `mon` through `sun`. The list includes pause/resume/delete buttons, or use `/schedule pause ID`, `/schedule resume ID`, and `/schedule delete ID`. Up to 20 schedules are supported.

Schedules remember their conversation and survive service restarts. Due tasks join the normal queue and use the current approval setting. A schedule cannot overlap itself. After downtime, missed occurrences become at most one queued run per schedule, rather than a burst of old work. Spring-forward times that do not exist are skipped; a repeated autumn time runs once. Pausing/deleting removes that schedule's pending tasks but does not cancel one already running. Resuming calculates the next future occurrence.

### Approval buttons

Send `/approvals on` to enable this optional mode. Bloodraven starts **Codex app-server** with `untrusted` approval policy and a **read-only sandbox**, and forwards native command/file approval requests to your authorised private chat. The operation stays blocked until you choose **Approve once** or **Decline**. Requests expire after five minutes; cancellation or a service restart invalidates them. Requests that cannot be displayed completely, or unsupported permission requests, are declined. There is no fallback to unrestricted execution if the protocol is unsupported.

This is Codex's native approval boundary, not a filter that guesses whether every operation is dangerous. Trusted read-only commands may run without asking. Existing Codex execution rules and external MCP tools affect what Codex asks to approve; external tools can have permissions outside the command sandbox. Review those tools and rules before relying on approval mode for sensitive administration. The service's systemd restrictions still apply after approval.

Approval mode uses a separate session history for each conversation. Turning it on also upgrades pending tasks to require this mode; turning it off applies only to newly queued tasks. Running work keeps the policy it started with. `/approvals` shows the setting, which is saved across restarts. Existing installations keep approvals off unless explicitly enabled; upgrades do not introduce another prompt. The installation environment default is `BLOODRAVEN_APPROVALS="off"`; a saved `/approvals` choice takes precedence.

Replies support **bold**, italics, strikethrough, inline code, fenced code, headings, HTTP(S) links, and bullet lists through a conservative Markdown-to-HTML formatter. Markdown tables become readable sections: two-column rows use a bold title and description; wider tables use a bold title and labelled fields. Links stay clickable, and tables inside fenced code stay unchanged. Unsupported or unmatched syntax remains readable text. Raw HTML is escaped, links do not generate previews, and each long-message chunk has complete formatting tags without splitting emoji. If Telegram rejects HTML parsing, the affected chunk is sent as plain text. This is not a full CommonMark renderer; complex nesting may remain plain text.

## Operations

```bash
sudo systemctl status bloodraven
sudo journalctl -u bloodraven -f
sudo systemctl restart bloodraven
```

To change configuration, edit `/etc/bloodraven/bloodraven.env`, then restart the service.

## Upgrade an existing installation

Run this as the same Linux user that originally installed Bloodraven:

```bash
(set -e; script=$(mktemp); trap 'rm -f -- "$script"' EXIT; curl -fsSL https://raw.githubusercontent.com/Steveiwonder/Bloodraven/master/upgrade.sh -o "$script"; bash "$script")
```

The upgrader installs the .NET 10 SDK if necessary, downloads and builds the latest Bloodraven, refreshes its systemd service, and verifies that it starts. It preserves the Telegram configuration, authorised user/chat IDs, Codex working directory, saved Codex session, sandbox choice, and existing Codex authentication. Existing state is read automatically by 0.2; no re-pairing is needed. Use `/health` after upgrading to confirm the version. Before a manual downgrade, stop the service and back up `/var/lib/bloodraven`: older versions do not understand named jobs, schedules or approval policies and must not consume a 0.2 journal.

New releases are staged under `/opt/bloodraven/releases/` before the old service is stopped. The service validates its repository, Codex login and Telegram token under the real service account, then the upgrader waits for successful polling. If activation fails, it attempts to restore and restart the previous service. Old releases and service backups are retained for recovery rather than automatically deleted. The original flat `/opt/bloodraven` installation is supported as a rollback target.

### Telegram replies stuck with HTTP 400 after upgrading to 0.2.0

Upgrade again using the command above to install **0.2.1 or later**. Version 0.2.0 sent a null optional keyboard field on ordinary replies, which Telegram rejected. Version 0.2.1 omits absent fields and retries the existing reply queue correctly. Do not delete `journal.json` or re-pair the bot: queued replies are retained for delivery. After upgrading, `/health` shows the installed version.

### Approval-mode tasks fail immediately on 0.2.0 or 0.2.1

Upgrade to **0.2.2 or later** using the normal command above. Those versions sent incorrect approval-policy and thread-sandbox enum values to Codex app-server. Version 0.2.2 uses the generated protocol's `untrusted` policy and `read-only` thread sandbox, and includes the explicit turn input/network fields. The turn sandbox discriminator remains `readOnly`, as required by its separate schema.

Approval mode stays enforced if setup fails. Errors now identify the app-server request stage and numeric RPC code without exposing raw server messages or stderr. `/new` cannot fix a protocol mismatch. After upgrading, send `/approvals on` and retry the failed request. CI validates actual runner requests against schemas generated by pinned Codex 0.155.1 and checks real app-server initialization/thread creation without calling a model.

### Progress during long tasks

While Codex works, Bloodraven edits its single working message every 30 seconds by default, showing elapsed time, completed/running command counts, up to two running commands and how long they have been observed, recent command completions with exit codes, and short excerpts of newly reported command output. Only the latest three details are retained per interval. When nothing new is reported, the update says so. This confirms the bridge is waiting for Codex; it cannot prove that an individual command is making progress. Output is available only when Codex emits it, which may be after a command finishes. Counts cover up to 1,024 command IDs per task; an update labels that limit if reached.

Installation asks for the interval in seconds: **10–3600**, or **0** to disable updates. Upgrades keep a saved value without asking again, including **0**. An interactive upgrade only prompts when the setting is missing; press Enter to accept **30 seconds**. Non-interactive upgrades preserve settings without prompting; when the setting is absent, the application defaults to **30 seconds**.

To change it later, run `sudo nano /etc/bloodraven/bloodraven.env`, set `BLOODRAVEN_PROGRESS_INTERVAL_SECONDS="60"` (for example), save, then run `sudo systemctl restart bloodraven`.

Command names and output excerpts are shown as code, with length limits and redaction of the bridge token, common credential assignments, authorization values, and private-key output. Redaction cannot recognise every possible secret; command output excerpts may contain other sensitive data. Set the interval to **0** to disable progress delivery. Codex message text is reserved for the final reply, so an answer arriving before process exit is not repeated as progress. Stderr, tool result payloads, and reasoning events are not forwarded. Progress stops when the task finishes, fails, or is cancelled. The working message becomes a short completion status; the complete final Codex answer is sent separately and split into readable chunks when necessary. Deleting the progress message does not prevent delivery of the final answer. Updates are skipped during delivery problems instead of being saved for later; the final answer still uses the persistent reply queue. Telegram delivery and rate limits can delay updates, so the interval is not an exact delivery guarantee.

### Restart and delivery behaviour

- Accepted work and Telegram update IDs are stored together in `/var/lib/bloodraven/journal.json` before acknowledgement. Queued tasks survive restarts.
- A task interrupted while running is **not automatically rerun**: it may already have changed files. The bot reports the interruption; inspect the repository before resubmitting. Other queued tasks continue.
- Replies are persisted and retried during network failures or Telegram rate limits. A rare duplicate reply is possible if Telegram accepts it immediately before a crash/network timeout; Telegram has no idempotency key for sending messages.
- The queue holds 20 tasks. A backed-up reply outbox pauses intake/processing to bound resource usage. Telegram only retains pending updates for a limited time (normally 24 hours); prolonged outages are not an unlimited archive.
- `/cancel` stops the current task, not queued tasks. Tasks time out after one hour by default. Codex JSON lines are limited to 1 MiB and final replies to 100,000 characters; oversized output fails safely.
- Do not delete a corrupt journal just to restart: back it up and investigate. Local state contains prompts and replies, so treat backups as sensitive. Durability protects normal process crashes/restarts; it is not a substitute for disk/filesystem backups or a guarantee against power-loss/storage failures.

## Configuration

| Variable | Required | Default | Purpose |
|---|---:|---|---|
| `BLOODRAVEN_TELEGRAM_BOT_TOKEN` | yes | — | Token issued by BotFather |
| `BLOODRAVEN_ALLOWED_USER_ID` | yes | — | Only accepted Telegram sender |
| `BLOODRAVEN_ALLOWED_CHAT_ID` | no | — | Additionally restricts the private chat |
| `BLOODRAVEN_WORKING_DIRECTORY` | yes | — | Git repository in which Codex runs |
| `BLOODRAVEN_STATE_DIRECTORY` | no | `./state` | Persistent session storage |
| `BLOODRAVEN_CODEX_EXECUTABLE` | no | `codex` | Absolute path or executable name |
| `BLOODRAVEN_CODEX_SANDBOX` | no | `workspace-write` | `read-only`, `workspace-write`, or `danger-full-access` |
| `BLOODRAVEN_TASK_TIMEOUT_SECONDS` | no | `3600` | Task time limit, 30–86400 seconds |
| `BLOODRAVEN_PROGRESS_INTERVAL_SECONDS` | no | `30` | Progress interval, 10–3600 seconds; `0` disables updates |
| `BLOODRAVEN_APPROVALS` | no | `off` | Default native approval mode; saved `/approvals` choice takes precedence |

Only private chats are accepted. If the optional chat ID is set, it must be a valid positive integer; malformed configuration fails startup rather than removing the restriction.

## Model and reasoning controls

Each named conversation remembers its own model and reasoning settings. In Telegram:

Send `/model` to list models reported by your installed Codex, with buttons to select one. Send `/effort` (or `/reasoning`) to choose a reasoning level. For an explicitly selected model, the menu lists the levels Codex reports for it. If the effective model is inherited or the catalogue is unavailable, the effort menu clearly labels its general, model-dependent list. Manual `/model MODEL_ID` and `/effort LEVEL` commands also work.

Buttons apply to the conversation where the menu was opened, expire after ten minutes or a restart, and reject choices if its saved settings have since changed. Choosing a model with a button clears the previous effort override; use `/effort` to select a compatible level afterwards. The catalogue request never starts a model turn. If model discovery fails, the bot reports it without guessing a model list.

```text
/model
/model YOUR_MODEL_ID
/reasoning high
```

Use the exact model ID available to your Codex login. Bloodraven validates the input format; Codex checks whether your account and model support the selection. There is no automatic fallback to another model if a task fails.

| Command | Effect in the current conversation |
| --- | --- |
| `/model` | List available models with selection buttons |
| `/effort` or `/reasoning` | List reasoning levels with selection buttons |
| `/model MODEL_ID` | Choose a model |
| `/model default` | Remove the model override |
| `/effort LEVEL` or `/reasoning LEVEL` | Choose `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max` or `ultra` (support depends on the model and CLI) |
| `/reasoning default` | Remove the reasoning override |
| `/preset fast` | Low reasoning effort |
| `/preset balanced` | Medium reasoning effort |
| `/preset thorough` | High reasoning effort |
| `/preset default` | Remove the reasoning override; keep the chosen model |

Presets change reasoning effort only. They do not switch models, change approval permissions, or guarantee a response time. Higher effort may take longer and use more of your allowance.

Changes apply to **newly queued tasks**. Running and already queued tasks keep their submitted settings. Future scheduled runs use their conversation's choices when they enter the queue. Settings survive restarts and upgrades, and `/new` clears conversation history while retaining these preferences. Both approval modes support the controls. Your working directory and repository instructions stay the same.

“Codex default” means Bloodraven sends no override for that setting; Codex's configuration and resumed-session behaviour decide the effective value. `/status` and `/health` show the saved Bloodraven choices, not an account model catalogue. To remove both overrides, send `/model default` and `/reasoning default`.

Existing installations need only the normal upgrade command—there are no new setup prompts.

## Ubuntu AppArmor compatibility

Installation and upgrades automatically add a Codex-specific user-namespace exception when Ubuntu’s AppArmor user-namespace restriction is enabled. This fixes sandbox startup errors such as `bwrap: loopback: Failed RTM_NEWADDR: Operation not permitted`. The helper detects the native Codex executable behind supported npm launchers or native installations, validates and loads `/etc/apparmor.d/bloodraven-codex`, then deployment restarts Bloodraven. It does not disable AppArmor or change the system-wide namespace restriction, Codex approval policy or sandbox mode.

The exception permits Codex and processes inheriting its AppArmor profile to create user namespaces with the capabilities needed by its own sandbox. It is tied to the resolved executable path and also applies when that executable is launched outside Bloodraven. After changing or updating your Codex installation, run the Bloodraven upgrader to refresh the path. Unsupported/ambiguous launcher layouts or custom conflicting profiles stop setup with an explanation. The previously documented manual profile is adopted automatically. Hosts without the restriction need no profile.

This follows [Ubuntu’s documented per-application user-namespace approach](https://discourse.ubuntu.com/t/ubuntu-24-04-lts-noble-numbat-release-notes/39890).

## Scope

Bloodraven 0.3 supports Ubuntu, one authorised bot owner, named conversations sharing one task queue, photos/documents, recurring schedules, per-conversation model/reasoning controls and optional native approval buttons. Multiple owners, multiple repositories, Docker packaging and other chat platforms are outside this release. `/health` performs repository/Codex/login checks with a five-second timeout and reports recent task outcomes; it does not claim every external service is healthy.

## Development

```bash
dotnet build
dotnet run --project tests/Bloodraven.Tests --configuration Release
python3 -m unittest discover -s tests -p 'test_*.py' -v
bash -n install.sh upgrade.sh scripts/install.sh scripts/upgrade.sh
dotnet run --project src/Bloodraven
```

Set the variables listed above before running locally. Bloodraven targets .NET 10 and has no third-party .NET package dependencies.

Regression tests use fake Codex processes and a fake Telegram HTTP handler; they require Linux, Git, Python 3 and .NET 10 but no real credentials. CI runs these tests as well as installer/pairing/rollback tests. Deployment tests simulate systemd failures; they do not replace an end-to-end test on your Ubuntu host.

## Licence

[MIT](LICENSE)

/Bloodraven
