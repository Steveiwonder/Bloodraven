"""Stage immutable releases and restore the previous unit on activation failure."""
import argparse
import os
from pathlib import Path
import pwd
import shutil
import subprocess
import tempfile
import time


def unit_quote(value):
    if any(c in value for c in "\n\r\0"):
        raise ValueError("Invalid newline or NUL in systemd value")
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%").replace("$", "$$") + '"'


def service_text(user, group, home, dotnet, executable, path, config, state):
    # Environment= does not perform shell-style dollar expansion; ExecStart does.
    environment = lambda value: unit_quote(value).replace("$$", "$")
    return f"""[Unit]
Description=Bloodraven Telegram bridge for Codex
After=network-online.target
Wants=network-online.target

[Service]
Type=exec
User={user}
Group={group}
Environment={environment('HOME=' + home)}
Environment={environment('PATH=' + path)}
EnvironmentFile={environment(str(config))}
WorkingDirectory={environment(str(executable.parent))}
ExecStartPre={unit_quote(dotnet)} {unit_quote(str(executable))} --check
ExecStart={unit_quote(dotnet)} {unit_quote(str(executable))}
Restart=on-failure
RestartSec=5
TimeoutStartSec=150
TimeoutStopSec=30
KillMode=control-group
NoNewPrivileges=true
PrivateTmp=true
UMask=0077

[Install]
WantedBy=multi-user.target
"""


class Systemd:
    def run(self, *args):
        subprocess.run(["systemctl", *args], check=True, stdout=subprocess.DEVNULL)

    def healthy(self, ready):
        # Type=exec + ExecStartPre verifies auth/repository under the real service user.
        # Require successful polling and sustained activity, not a momentary active state.
        for _ in range(45):
            if ready.exists():
                self.run("is-active", "--quiet", "bloodraven")
                time.sleep(3)
                self.run("is-active", "--quiet", "bloodraven")
                return
            time.sleep(1)
        raise RuntimeError("No successful Telegram poll within 45 seconds")


def deploy(publish, root, unit, state, render, systemd):
    root.mkdir(parents=True, exist_ok=True)
    os.chmod(root, 0o755)
    releases = root / "releases"
    releases.mkdir(exist_ok=True)
    os.chmod(releases, 0o755)
    release = Path(tempfile.mkdtemp(prefix="release-", dir=releases))
    # copytree (not cp -a) retains root ownership when run by sudo.
    shutil.copytree(publish, release, dirs_exist_ok=True)
    for directory, _, files in os.walk(release):
        os.chmod(directory, 0o755)
        for name in files:
            item = Path(directory) / name
            os.chmod(item, 0o755 if os.access(item, os.X_OK) else 0o644)
    previous = unit.read_bytes() if unit.exists() else None
    backup = release / "previous.service"
    if previous is not None:
        backup.write_bytes(previous)
    candidate = release / "bloodraven.service"
    candidate.write_text(render(release / "Bloodraven.dll"))
    ready = state / "ready"
    activation_started = False
    try:
        activation_started = True
        systemd.run("stop", "bloodraven") if previous is not None else None
        ready.unlink(missing_ok=True)
        unit.parent.mkdir(parents=True, exist_ok=True)
        temporary = unit.with_suffix(".service.pending")
        shutil.copyfile(candidate, temporary)
        os.replace(temporary, unit)
        systemd.run("daemon-reload")
        systemd.run("start", "bloodraven")
        systemd.healthy(ready)
        systemd.run("enable", "bloodraven")
    except BaseException:
        if activation_started:
            systemd.run("stop", "bloodraven")
            if previous is not None:
                temporary = unit.with_suffix(".service.pending")
                temporary.write_bytes(previous)
                os.replace(temporary, unit)
            else:
                unit.unlink(missing_ok=True)
            systemd.run("daemon-reload")
            if previous is not None:
                systemd.run("start", "bloodraven")
        raise
    return release


def main():
    parser = argparse.ArgumentParser()
    for name in ("publish", "user", "group", "dotnet", "path"):
        parser.add_argument(name)
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error("Deployment must be invoked through sudo by the installation scripts")
    home = pwd.getpwnam(args.user).pw_dir
    safe_path = ":".join(p for p in args.path.split(":") if p.startswith("/"))
    safe_path += ":/usr/local/bin:/usr/bin:/bin"
    config, state = Path("/etc/bloodraven/bloodraven.env"), Path("/var/lib/bloodraven")
    renderer = lambda executable: service_text(args.user, args.group, home, args.dotnet,
                                               executable, safe_path, config, state)
    try:
        release = deploy(Path(args.publish), Path("/opt/bloodraven"),
                         Path("/etc/systemd/system/bloodraven.service"), state, renderer, Systemd())
    except Exception:
        print("Activation failed; rollback was attempted. Check 'sudo systemctl status bloodraven'. Previous unit backups remain under /opt/bloodraven/releases.")
        raise SystemExit(1) from None
    print(f"Activated {release}. Previous releases and configuration were retained.")


if __name__ == "__main__":
    main()
