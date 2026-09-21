# systemd deployment

The installer generates the service using `scripts/deploy.py`, with correctly quoted absolute executable paths, the installing user's HOME/PATH, and a root-only environment file. There is deliberately no second hand-maintained template.

Each deployment stages an immutable release under `/opt/bloodraven/releases/`. The service points directly to that release. `ExecStartPre` validates Git (including worktrees), Codex login, and Telegram authentication under the service account. Activation also waits for successful polling and sustained activity.

Before stopping the existing service, deployment validates the candidate with `systemd-analyze verify`. Path directives (`WorkingDirectory` and `EnvironmentFile`) use systemd path syntax, not command-line quoting. Failed stops of already inactive or unloaded units are tolerated, allowing an upgrade to repair an invalid unit left by an earlier release. Rollback restores the previous unit even if stopping fails; if systemd cannot confirm the service stopped, manual recovery is required rather than starting another process.

If activation fails, the previous service definition is restored and started. Previous releases and `previous.service` backups remain available; no configuration, journal, session, or Codex authentication is removed. Original flat installations are supported as the rollback target.

The service uses `NoNewPrivileges`, `PrivateTmp`, `KillMode=control-group`, and `UMask=0077`. These are defence in depth, not isolation from the service user's account. Keep the configured working repository outside `/tmp` because the service has a private temporary directory.
