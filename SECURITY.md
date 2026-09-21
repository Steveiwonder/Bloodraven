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

## Approval mode and files

Native approval mode uses Codex app-server with a read-only sandbox and `unlessTrusted` policy. The bridge responds to command/file requests only after the configured owner approves the complete displayed operation. Unknown requests, oversized proposals and additional permission grants fail closed. Approval buttons are bound to a random, single-use, in-memory request and the private chat, and expire with the task or after five minutes. They cannot authorise a later run after restart.

Codex decides which operations need approval. Existing Codex execution rules and external MCP tools must be reviewed separately; an MCP server can have privileges outside the local command sandbox. Approval mode is not a guarantee that every externally visible effect will produce a button. Do not configure untrusted external tools and assume the shell sandbox confines them.

Incoming files are accepted only from the authorised sender, capped at 10 MiB and stored under generated private paths. Their contents remain untrusted input. Explicit `/file` exports are restricted to repository paths with no traversal, `.git` or symlinks, and are snapshotted before delivery. This does not protect repository files from a hostile process running as the same Linux user. The authorised owner can intentionally export sensitive files in their repository; check the requested path before sending it.

Schedules execute real tasks with the current approval mode and the configured service permissions. Use inspection-only prompts unless you intend unattended changes, and pause a schedule before changing its purpose. Back up state before downgrading: older binaries must not process journals containing features they do not understand.
