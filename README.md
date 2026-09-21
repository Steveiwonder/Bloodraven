# Bloodraven

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
- Supports `/new`, `/status`, `/cancel`, and `/help`
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
5. Choose a Codex sandbox. Press Enter to accept the safer `workspace-write` default.
6. Choose how often Telegram should receive progress updates. Press Enter for **30 seconds**, enter **10–3600** seconds, or enter **0** to turn them off.

The installer builds Bloodraven, stores its private settings outside the Git repository, installs a systemd service, and starts it automatically.

Relative directory overrides are resolved against the directory where you launched setup. Git worktrees and submodules are supported. Keep the repository outside `/tmp` and `/var/tmp`, which are private to the service.

If setup finds an existing configuration, it stops without overwriting it. Use the upgrade command below to upgrade or reinstall the application while preserving your settings. If first-time activation failed after writing configuration, the upgrade command can retry activation as the same Linux user.

### Test it

Open your bot in Telegram and send:

```text
Tell me which repository you are working in and list its files.
```

The bot should reply `Queued.`, then `Working…`, followed by Codex's answer. You can check the service on Ubuntu with:

```bash
sudo systemctl status bloodraven
```

If it is not running, view the latest logs:

```bash
sudo journalctl -u bloodraven -n 100 --no-pager
```

### Permissions

The default `workspace-write` sandbox lets Codex edit the selected working repository. Choose `read-only` if it should only inspect files. Choose `danger-full-access` only if Codex must administer systems outside the repository and you understand that your Telegram account then becomes a powerful remote-control interface to that machine.

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
| `/new` | Forget the saved Codex session and start fresh on the next message |
| `/status` | Show idle/working state, session ID, and queue depth |
| `/cancel` | Cancel the running Codex process |
| `/help` | Show available commands |

All other text is passed to the current Codex conversation as prompt data, not executable CLI arguments. Native interactive Codex slash commands are not implemented.

Replies support **bold**, italics, strikethrough, inline code, fenced code, headings, HTTP(S) links, and bullet lists through a conservative Markdown-to-HTML formatter. Unsupported or unmatched syntax remains readable text. Raw HTML is escaped, links do not generate previews, and each long-message chunk has complete formatting tags without splitting emoji. If Telegram rejects HTML parsing, the affected chunk is sent as plain text. This is not a full CommonMark renderer; complex nesting and tables may remain plain text.

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

The upgrader installs the .NET 10 SDK if necessary, downloads and builds the latest Bloodraven, refreshes its systemd service, and verifies that it starts. It preserves the Telegram configuration, authorised user/chat IDs, Codex working directory, saved Codex session, sandbox choice, and existing Codex authentication.

New releases are staged under `/opt/bloodraven/releases/` before the old service is stopped. The service validates its repository, Codex login and Telegram token under the real service account, then the upgrader waits for successful polling. If activation fails, it attempts to restore and restart the previous service. Old releases and service backups are retained for recovery rather than automatically deleted. The original flat `/opt/bloodraven` installation is supported as a rollback target.

### Progress during long tasks

While Codex works, Bloodraven sends a short update every 30 seconds by default, showing elapsed time and command/file/tool activity. When there is nothing new, it sends “Still working…” with the elapsed time. This confirms the bridge is waiting for Codex; it cannot prove that an individual command is making progress.

Installation asks for the interval in seconds: **10–3600**, or **0** to disable updates. Upgrades keep a saved value without asking again, including **0**. An interactive upgrade only prompts when the setting is missing; press Enter to accept **30 seconds**. Non-interactive upgrades preserve settings without prompting; when the setting is absent, the application defaults to **30 seconds**.

To change it later, run `sudo nano /etc/bloodraven/bloodraven.env`, set `BLOODRAVEN_PROGRESS_INTERVAL_SECONDS="60"` (for example), save, then run `sudo systemctl restart bloodraven`.

Updates contain general activity descriptions. Codex message text is reserved for the final reply, so an answer arriving before process exit is not repeated as progress. Raw command output, stderr, tool payloads, and reasoning events are not forwarded. Progress stops when the task finishes, fails, or is cancelled. Updates are skipped during delivery problems instead of being saved for later; the final answer still uses the persistent reply queue. Telegram delivery and rate limits can delay updates, so the interval is not an exact delivery guarantee.

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

Only private chats are accepted. If the optional chat ID is set, it must be a valid positive integer; malformed configuration fails startup rather than removing the restriction.

## Scope

The first release supports Ubuntu, one bot owner, plain-text messages, and one conversation queue. Attachments, multiple users, streaming progress, Docker, and other chat platforms are intentionally left for later releases.

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
