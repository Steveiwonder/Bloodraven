#!/usr/bin/env python3
import json
import os
import subprocess
import sys
import time

if "--version" in sys.argv or "login" in sys.argv:
    sys.exit(0)
mode = os.environ.get("TEST_CODEX_MODE", "normal")
prompt = sys.stdin.read()
if mode == "progress":
    print(json.dumps({"type": "item.completed", "item": {"type": "command_execution", "id": "cmd1", "command": "docker inspect plex", "aggregated_output": "Container is healthy", "exit_code": 0}}), flush=True)
    # Reproduce a final answer arriving before slow process shutdown. A progress
    # tick must occur during this delay without forwarding the answer early.
    print(json.dumps({"type": "item.completed", "item": {"type": "agent_message", "text": "Task complete"}}), flush=True)
    time.sleep(12)
    sys.exit(0)
if mode in ("sleep", "malformed"):
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    with open(os.environ["TEST_PID_FILE"], "w") as handle:
        handle.write(f"{os.getpid()} {child.pid}")
    if mode == "malformed":
        print("not-json", flush=True)
    time.sleep(60)
elif mode == "oversized":
    print("x" * 1_048_577, flush=True)
elif mode == "failure":
    print("private stderr details", file=sys.stderr)
    sys.exit(2)
else:
    print(json.dumps({"type": "thread.started", "thread_id": "11111111-1111-1111-1111-111111111111"}))
    print(json.dumps({"type": "item.completed", "item": {"type": "agent_message", "text": json.dumps({
        "prompt": prompt, "cwd": os.getcwd(), "args": sys.argv[1:],
        "secret": os.environ.get("BLOODRAVEN_TELEGRAM_BOT_TOKEN")})}}))
