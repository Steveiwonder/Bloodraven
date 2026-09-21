#!/usr/bin/env python3
import json
import os
import subprocess
import sys
import time

if "--version" in sys.argv or "login" in sys.argv:
    sys.exit(0)
mode = os.environ.get("TEST_CODEX_MODE", "normal")
if "app-server" in sys.argv:
    def send(value):
        print(json.dumps(value), flush=True)
    for line in sys.stdin:
        message = json.loads(line)
        if os.environ.get("TEST_CODEX_REQUEST_TRACE"):
            with open(os.environ["TEST_CODEX_REQUEST_TRACE"], "a") as trace:
                trace.write(json.dumps(message) + "\n")
        method = message.get("method")
        if mode == "model-settings":
            assert 'model="example-model"' in sys.argv
            assert 'model_reasoning_effort="high"' in sys.argv
            if method == "turn/start":
                assert message["params"]["model"] == "example-model"
                assert message["params"]["effort"] == "high"
        params = message.get("params", {})
        if method == "initialized":
            continue
        if method == "initialize":
            send({"id": message["id"], "result": {}})
        elif method in ("thread/start", "thread/resume"):
            if mode == "reject-thread":
                send({"id": message["id"], "error": {"code": -32602, "message": "private upstream diagnostic"}})
                continue
            assert params["sandbox"] == "read-only" and params["approvalPolicy"] == "untrusted"
            send({"id": message["id"], "result": {"thread": {"id": "thr_test"}}})
        elif method == "turn/start":
            assert params["sandboxPolicy"]["type"] == "readOnly"
            assert params["sandboxPolicy"]["networkAccess"] is False
            assert params["input"][0]["text_elements"] == []
            assert params["approvalPolicy"] == "untrusted"
            send({"id": message["id"], "result": {"turn": {"id": "turn_test"}}})
            if mode == "unsupported":
                send({"id": "approval", "method": "unknown/request", "params": {}})
            elif mode == "fileapproval":
                send({"method": "item/started", "params": {"item": {"type": "fileChange", "id": "item_test", "changes": [{"path": "example.txt", "diff": "+hello"}]}}})
                send({"id": "approval", "method": "item/fileChange/requestApproval", "params": {"itemId": "item_test"}})
            else:
                send({"id": "approval", "method": "item/commandExecution/requestApproval", "params": {"itemId": "item_test", "command": "touch approved.txt", "cwd": os.getcwd()}})
        elif message.get("id") == "approval":
            if mode == "unsupported":
                assert "error" in message
                decision = "decline"
            else:
                decision = message["result"]["decision"]
            if decision == "accept":
                with open("approved.txt", "w") as handle:
                    handle.write("approved")
            send({"method": "item/completed", "params": {"item": {"id": "answer", "type": "agentMessage", "phase": "final_answer", "text": decision}}})
            send({"method": "turn/completed", "params": {"turn": {"status": "completed"}}})
    sys.exit(0)
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
