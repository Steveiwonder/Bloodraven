# Bloodraven

Bloodraven is a small, self-hosted Telegram interface for the OpenAI Codex CLI. It runs on your own Ubuntu machine, uses your existing Codex login, and requires no inbound ports or public web server.

> [!WARNING]
> Anyone who controls your authorised Telegram account can instruct Codex with the permissions you configure. Read [SECURITY.md](SECURITY.md) before installation.

## What it does

- Receives messages through Telegram long polling
- Silently ignores every user except the configured numeric Telegram user ID
- Runs `codex exec --json` inside a Git repository
- Saves the exact Codex session ID and resumes it on later messages
- Queues messages and runs one Codex task at a time
- Supports `/new`, `/status`, `/cancel`, and `/help`
- Runs as a hardened systemd service

Bloodraven stores no personal configuration in Git. Its installer writes secrets to `/etc/bloodraven/bloodraven.env` with mode `0600` and conversation state to `/var/lib/bloodraven`.

## Install on Ubuntu

These instructions assume Ubuntu 22.04 or newer. Run commands as your normal user, not as `root`.

### 1. Install the basic tools

```bash
sudo apt update
sudo apt install -y curl git python3
```

Install the [.NET 8 SDK for your Ubuntu version](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu), then confirm it works:

```bash
dotnet --version
```

The result should start with `8.` or a later compatible version.

### 2. Install and sign in to Codex

Install the [Codex CLI](https://learn.chatgpt.com/docs/codex-cli), then sign in:

```bash
codex login
codex login status
```

Bloodraven runs as this same Linux user, so it can reuse the Codex login you just created.

### 3. Create your Telegram bot

1. Open Telegram and start a chat with [@BotFather](https://t.me/BotFather). Check that the username is exactly `@BotFather` and that it has Telegram's verification tick.
2. Send `/newbot`.
3. Enter a display name, such as `My Bloodraven`.
4. Enter a unique username ending in `bot`, such as `my_bloodraven_bot`.
5. BotFather will reply with an HTTP API token. It looks similar to `123456789:AAExample...`. Keep it private—you will paste it into the installer shortly.

You do not need a webhook, public IP address, domain name, or open firewall port. Bloodraven connects out to Telegram using long polling.

### 4. Choose Codex's working repository

Bloodraven asks Codex to work inside a Git repository. This can be an existing repository containing your instructions and scripts, or a new one:

```bash
mkdir -p ~/codex-workspace
cd ~/codex-workspace
git init
```

Make a note of its full path:

```bash
pwd
```

### 5. Download and run Bloodraven

```bash
cd ~
git clone https://github.com/Steveiwonder/Bloodraven.git
cd Bloodraven
chmod +x scripts/install.sh
./scripts/install.sh
```

The installer will guide you through the rest:

1. Paste the private Telegram bot token from BotFather.
2. When prompted, open the new bot in Telegram and press **Start** or send `/start`.
3. Return to the terminal and press Enter. Bloodraven will discover your numeric Telegram user and chat IDs automatically.
4. Enter the full path of the working repository from the previous step.
5. Choose a Codex sandbox. Press Enter to accept the safer `workspace-write` default.

The installer builds Bloodraven, stores its private settings outside the Git repository, installs a systemd service, and starts it automatically.

### 6. Test it

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

## Telegram commands

| Command | Action |
|---|---|
| `/new` | Forget the saved Codex session and start fresh on the next message |
| `/status` | Show idle/working state, session ID, and queue depth |
| `/cancel` | Cancel the running Codex process |
| `/help` | Show available commands |

All other text is passed to the current Codex conversation. Telegram's message limit is handled by splitting long final answers into multiple messages.

## Operations

```bash
sudo systemctl status bloodraven
sudo journalctl -u bloodraven -f
sudo systemctl restart bloodraven
```

To change configuration, edit `/etc/bloodraven/bloodraven.env`, then restart the service. To upgrade:

```bash
git pull
./scripts/install.sh
```

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

## Scope

The first release supports Ubuntu, one bot owner, plain-text messages, and one conversation queue. Attachments, multiple users, streaming progress, Docker, and other chat platforms are intentionally left for later releases.

## Development

```bash
dotnet build
dotnet run --project src/Bloodraven
```

Set the variables listed above before running locally. Bloodraven has no third-party .NET package dependencies.

## Licence

[MIT](LICENSE)
