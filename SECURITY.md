# Security

Bloodraven turns a Telegram account into a remote interface for Codex. Treat the bot token and authorised Telegram account as privileged credentials.

- Use a dedicated bot in a private one-to-one chat.
- Keep the numeric user and chat allowlists enabled.
- Start with `workspace-write`; use `danger-full-access` only on a trusted, isolated host when it is genuinely required.
- Never commit the bot token, Codex authentication, SSH keys, or environment file.
- Revoke the bot token with BotFather immediately if it is exposed.

Please report vulnerabilities privately through GitHub Security Advisories rather than a public issue.
