# Security

Bloodraven turns a Telegram account into a remote interface for Codex. Treat the bot token and authorised Telegram account as privileged credentials.

- Use a dedicated bot in a private one-to-one chat.
- Keep the numeric user and chat allowlists enabled.
- Start with `workspace-write`; use `danger-full-access` only on a trusted, isolated host when it is genuinely required.
- Never commit the bot token, Codex authentication, SSH keys, or environment file.
- Revoke the bot token with BotFather immediately if it is exposed.

Setup pairs a private chat using a fresh random one-time code and explicit identity confirmation. A dedicated bot must have no webhook or competing polling process.

HTTP request URL logging is disabled because Telegram embeds the bot token in its URL path. Bridge-specific environment variables are removed before launching Codex, and raw Codex stderr/exception details are not relayed to Telegram. Existing installations predating this hardening may have logged token-bearing URLs: treat old journal exports as sensitive, and rotate the bot token through BotFather if those logs were shared. Update the token in the root-only environment file and restart afterward.

The systemd service runs as your selected Linux user, with that user's Codex authentication and repository access. `NoNewPrivileges`, `PrivateTmp`, and the Codex sandbox are defence in depth, not a separate account isolation boundary. A process running as that user can access the user's files and may be able to inspect other processes. Removing inherited bridge secrets does not make a hostile same-user process safe.

The persistent queue/reply journal contains message content and must be protected like conversation history. Systemd uses `UMask=0077`; the installer creates state with mode `0700`, configuration with `0600`, and new state files are owner-only. Never post raw state files, old logs, or credentials in issues. Interrupted tasks are not automatically replayed, but may already have produced side effects.

Please report vulnerabilities privately through GitHub Security Advisories rather than a public issue.
