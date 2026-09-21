"""Pair a private Telegram owner and safely encode systemd configuration."""
import argparse
import getpass
import json
import os
from pathlib import Path
import secrets
import re
import sys
import tempfile
import time
import urllib.request


def request(token, method, data=None):
    try:
        payload = json.dumps(data or {}).encode()
        req = urllib.request.Request(f"https://api.telegram.org/bot{token}/{method}",
                                     data=payload, headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=40) as response:
            result = json.load(response)
        if not result.get("ok"):
            raise ValueError("Telegram rejected the request")
        return result["result"]
    except Exception:
        # urllib exceptions include the URL (and therefore the secret).
        raise RuntimeError("Telegram request failed. Check the token, network and that no other bot instance is polling.") from None


def matching_owners(updates, code, started):
    owners = set()
    for update in updates:
        message = update.get("message", {})
        sender, chat = message.get("from", {}), message.get("chat", {})
        if (message.get("text", "").strip() == f"/start {code}"
                and message.get("date", 0) >= started - 5
                and chat.get("type") == "private" and not sender.get("is_bot", False)
                and isinstance(sender.get("id"), int) and sender["id"] > 0
                and chat.get("id") == sender["id"]):
            owners.add((sender["id"], chat["id"]))
    return owners


def pair(output):
    token = getpass.getpass("Telegram bot token (from @BotFather): ").strip()
    bot = request(token, "getMe")
    webhook = request(token, "getWebhookInfo")
    if webhook.get("url"):
        raise RuntimeError("This bot has a webhook. Use a dedicated bot or remove its webhook before pairing.")
    code, started = secrets.token_hex(16), int(time.time())
    print(f"Open a PRIVATE chat with @{bot['username']} and send exactly:\n/start {code}")
    input("After sending the code, press Enter here: ")
    if time.time() - started > 600:
        raise RuntimeError("Pairing expired. Start setup again.")
    offset, owners = 0, set()
    # Drain old pages without selecting arbitrary last messages.
    for _ in range(100):
        updates = request(token, "getUpdates", {"offset": offset, "limit": 100, "timeout": 0,
                                               "allowed_updates": ["message"]})
        owners.update(matching_owners(updates, code, started))
        if updates:
            offset = max(u["update_id"] for u in updates) + 1
        if len(updates) < 100:
            break
    else:
        raise RuntimeError("Too many pending updates. Use a dedicated bot and retry.")
    if len(owners) != 1:
        raise RuntimeError("No unique matching private-chat owner found. Check the pairing code and retry.")
    user, chat = owners.pop()
    print(f"Authorise Telegram user {user}, private chat {chat}?")
    if input("Type yes to confirm [no]: ").strip().lower() != "yes":
        raise RuntimeError("Pairing cancelled.")
    # Acknowledge setup updates so the code is never sent as a Codex prompt.
    request(token, "getUpdates", {"offset": offset, "timeout": 0, "limit": 1})
    write_private(output, json.dumps({"token": token, "user": user, "chat": chat}))


def env_quote(value):
    if any(c in value for c in "\n\r\0"):
        raise ValueError("Configuration values must not contain newlines or NULs")
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'


def write_private(path, content):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as handle:
        os.fchmod(handle.fileno(), 0o600)
        handle.write(content)


def config(pairing, directory, codex, sandbox, output):
    identity = json.loads(Path(pairing).read_text())
    values = {
        "BLOODRAVEN_TELEGRAM_BOT_TOKEN": identity["token"],
        "BLOODRAVEN_ALLOWED_USER_ID": str(identity["user"]),
        "BLOODRAVEN_ALLOWED_CHAT_ID": str(identity["chat"]),
        "BLOODRAVEN_WORKING_DIRECTORY": directory,
        "BLOODRAVEN_STATE_DIRECTORY": "/var/lib/bloodraven",
        "BLOODRAVEN_CODEX_EXECUTABLE": codex,
        "BLOODRAVEN_CODEX_SANDBOX": sandbox,
        "BLOODRAVEN_PROGRESS_INTERVAL_SECONDS": prompt_progress("30"),
    }
    write_private(output, "".join(f"{key}={env_quote(value)}\n" for key, value in values.items()))


PROGRESS_KEY = "BLOODRAVEN_PROGRESS_INTERVAL_SECONDS"


def validate_progress(value):
    if not re.fullmatch(r"[0-9]+", value) or not (int(value) == 0 or 10 <= int(value) <= 3600):
        raise ValueError("Enter 0 to disable updates, or 10–3600 seconds.")
    return str(int(value))


def prompt_progress(current):
    while True:
        value = input(f"Telegram progress interval in seconds (0=off, 10–3600) [{current}]: ").strip()
        try:
            return validate_progress(value or current)
        except ValueError as exc:
            print(exc)


def progress_config(path):
    # Never source the secret-bearing environment file as shell code.
    path = Path(path)
    original = path.read_text()
    pattern = rf'^{PROGRESS_KEY}=.*$'
    matches = re.findall(pattern, original, re.MULTILINE)
    current = "30"
    if matches:
        current = validate_progress(matches[-1].split("=", 1)[1].strip().strip('\"').strip("'"))
    if not sys.stdin.isatty():
        print(f"Keeping Telegram progress interval: {current}s (non-interactive upgrade).")
        return
    chosen = prompt_progress(current)
    if matches and chosen == current:
        return
    updated = re.sub(pattern, "", original, flags=re.MULTILINE).rstrip('\n') + f'\n{PROGRESS_KEY}="{chosen}"\n'
    stat = path.stat()
    fd, temporary = tempfile.mkstemp(prefix=".progress-", dir=path.parent)
    try:
        with os.fdopen(fd, "w") as handle:
            os.fchmod(handle.fileno(), 0o600)
            os.fchown(handle.fileno(), stat.st_uid, stat.st_gid)
            handle.write(updated)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def resolve_directory(value, invocation):
    path = Path(value).expanduser()
    return str((path if path.is_absolute() else Path(invocation) / path).resolve())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="action", required=True)
    p = sub.add_parser("pair")
    p.add_argument("output")
    p = sub.add_parser("resolve")
    p.add_argument("value")
    p.add_argument("invocation")
    p = sub.add_parser("config")
    for name in ("pairing", "directory", "codex", "sandbox", "output"):
        p.add_argument(name)
    p = sub.add_parser("progress")
    p.add_argument("path")
    args = parser.parse_args()
    try:
        if args.action == "pair":
            pair(args.output)
        elif args.action == "resolve":
            print(resolve_directory(args.value, args.invocation))
        elif args.action == "progress":
            progress_config(args.path)
        else:
            config(args.pairing, args.directory, args.codex, args.sandbox, args.output)
    except (RuntimeError, ValueError, OSError) as exc:
        parser.exit(1, f"Setup failed: {exc}\n")
