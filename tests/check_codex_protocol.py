"""Validate actual Bloodraven requests against schemas emitted by pinned Codex.

The trace comes from the real .NET runner talking to fake-codex.py in CI.
No production prompts, credentials, or model calls are involved.
"""
import copy
import json
import os
import tempfile
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

import jsonschema


def main(schema_directory, trace_file, executable):
    messages = [json.loads(line) for line in Path(trace_file).read_text().splitlines()]
    schemas = {
        "initialize": "InitializeParams.json",
        "thread/start": "ThreadStartParams.json",
        "thread/resume": "ThreadResumeParams.json",
        "turn/start": "TurnStartParams.json",
        "model/list": "ModelListParams.json",
        "config/read": "ConfigReadParams.json",
        "thread/fork": "ThreadForkParams.json",
    }
    seen = set()
    validators = {}
    for method, filename in schemas.items():
        paths = list(Path(schema_directory).rglob(filename))
        assert len(paths) == 1, f"Expected one generated schema for {method}, found {len(paths)}"
        schema = json.loads(paths[0].read_text())
        validator = jsonschema.validators.validator_for(schema)
        validator.check_schema(schema)
        validators[method] = validator(schema)
    for message in messages:
        method = message.get("method")
        if method in validators:
            validators[method].validate(message["params"])
            seen.add(method)
    assert seen == set(schemas), f"Missing production request coverage: {set(schemas) - seen}"
    first = {method: next(m for m in messages if m.get("method") == method) for method in schemas}
    # Demonstrate that both spellings shipped in 0.2.0/0.2.1 are rejected.
    for field, bad_value in (("approvalPolicy", "unlessTrusted"), ("sandbox", "readOnly")):
        broken = copy.deepcopy(first["thread/start"]["params"])
        broken[field] = bad_value
        assert not validators["thread/start"].is_valid(broken), f"Old {field} regression was not detected"
    print("PASS actual Bloodraven requests match Codex schemas; both old wire values are rejected", flush=True)

    # Exercise initialization and thread creation against the real binary, with
    # a deliberately unreachable local provider. Never start a model turn.
    with tempfile.TemporaryDirectory(prefix="bloodraven-protocol-") as codex_home, subprocess.Popen([executable, "-c", 'model_reasoning_effort="high"', "app-server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.DEVNULL, text=True, env={**os.environ, "CODEX_HOME": codex_home}) as process:
        incoming = queue.Queue()

        def read():
            for line in process.stdout:
                incoming.put(json.loads(line))
            incoming.put(None)

        threading.Thread(target=read, daemon=True).start()

        def request(message):
            process.stdin.write(json.dumps(message) + "\n")
            process.stdin.flush()
            while True:
                response = incoming.get(timeout=30)
                assert response is not None, "Real app-server disconnected"
                if response.get("id") == message["id"]:
                    assert "error" not in response, f"Real app-server rejected {message['method']}: {response.get('error')}"
                    return response["result"]

        try:
            request(first["initialize"])
            process.stdin.write('{"method":"initialized","params":{}}\n')
            process.stdin.flush()
            start = copy.deepcopy(first["thread/start"])
            start["params"].update({
                "cwd": str(Path.cwd()), "ephemeral": False,
                "model": "protocol-test", "modelProvider": "protocol_test",
                "config": {"model_providers.protocol_test": {
                    "name": "Offline protocol test", "base_url": "http://127.0.0.1:1/v1",
                    "wire_api": "responses", "requires_openai_auth": False,
                    "request_max_retries": 0, "stream_max_retries": 0,
                }},
            })
            result = request(start)
            assert result["thread"]["id"], "Missing thread ID"
            assert result["reasoningEffort"] == "high", "Reasoning config override was not applied"
            assert result["approvalPolicy"] == "untrusted", "Approval policy was not applied"
            assert result["sandbox"]["type"] == "readOnly", "Read-only sandbox was not applied"
            # Codex persists rollouts lazily on the first turn, not thread/start.
            # The loopback provider cannot reach a model; starting a turn creates
            # real saved history so the fork exercises the production path.
            seed = copy.deepcopy(first["turn/start"])
            seed["id"] = 99
            seed["params"]["threadId"] = result["thread"]["id"]
            request(seed)
            # The turn acknowledgement can precede the asynchronous rollout write.
            # Wait for a complete metadata record before forking this new fixture.
            deadline = time.monotonic() + 10
            while True:
                ready = False
                for path in Path(codex_home).rglob(f"*{result['thread']['id']}*.jsonl"):
                    with path.open() as rollout:
                        line = rollout.readline()
                    if line.endswith("\n"):
                        try:
                            ready = json.loads(line).get("type") == "session_meta"
                        except json.JSONDecodeError:
                            pass
                    if ready:
                        break
                if ready:
                    break
                assert time.monotonic() < deadline, "Codex did not persist the test session metadata"
                time.sleep(0.05)
            # The source turn may still be running; do not depend on completion
            # notifications arriving before or after the turn acknowledgement.
            config_read = copy.deepcopy(first["config/read"])
            config_read["id"] = 100
            config_read["params"]["cwd"] = str(Path.cwd())
            assert "config" in request(config_read)
            fork = copy.deepcopy(first["thread/fork"])
            fork["id"] = 101
            fork["params"].update({"threadId": result["thread"]["id"], "cwd": str(Path.cwd())})
            # The production override must also survive real config parsing.
            fork["params"]["config"].update(start["params"]["config"])
            fork["params"].update({"model": "protocol-test", "modelProvider": "protocol_test"})
            assert fork["params"]["excludeTurns"] is True
            forked = request(fork)
            assert forked["thread"]["id"] != result["thread"]["id"]
            assert forked["thread"].get("turns", []) == []
            history = request({"id": 102, "method": "thread/read", "params": {
                "threadId": forked["thread"]["id"], "includeTurns": True,
            }})
            assert history["thread"]["turns"], "Metadata-only fork lost inherited context"
            assert forked["sandbox"]["type"] == "readOnly"
            assert forked["approvalPolicy"] == "untrusted"
            print("PASS real Codex creates a separate read-only BTW fork without a model call", flush=True)
            print("PASS real Codex initializes and creates an approval-mode thread without a model call", flush=True)
        finally:
            process.kill()
            process.wait(timeout=10)


if __name__ == "__main__":
    main(*sys.argv[1:])
