#!/usr/bin/env python3
import json
import os
import pathlib
import sys
import time

if "--version" in sys.argv or sys.argv[1:] == ["auth", "status"]:
    sys.exit(0)

args = sys.argv[1:]
assert "--print" in args and "--verbose" in args
assert args[args.index("--output-format") + 1] == "stream-json"
assert "--sandbox" not in args and "--ask-for-approval" not in args
assert not any(key.startswith("BLOODRAVEN_") for key in os.environ)
text = sys.stdin.read()
pathlib.Path("claude-request.json").write_text(json.dumps({"args": args, "input": text}))
if "--resume" in args:
    assert args[args.index("--resume") + 1] == "claude-session"

print(json.dumps({"type": "system", "subtype": "init", "session_id": "claude-session"}), flush=True)
if text == "wait":
    pathlib.Path("claude-started").touch()
    time.sleep(60)
elif text == "credit-error":
    print(json.dumps({"type": "result", "subtype": "error_during_execution", "is_error": True, "errors": ["Credit balance is too low. private-diagnostic"]}), flush=True)
    sys.exit(1)
elif text == "missing-result":
    sys.exit(0)
elif text == "bad-json":
    print("invalid-json", flush=True)
    sys.exit(0)
elif text == "stderr-error":
    print("Not logged in. private-diagnostic", file=sys.stderr, flush=True)
    sys.exit(1)

print(json.dumps({"type": "assistant", "message": {"content": [{"type": "tool_use", "name": "Read", "input": {"private": "not-progress"}}]}}), flush=True)
print(json.dumps({"type": "result", "subtype": "success", "is_error": False, "result": "Claude answer", "session_id": "claude-session"}), flush=True)
