"""Validate actual Bloodraven requests against schemas emitted by pinned Codex.

The trace comes from the real .NET runner talking to fake-codex.py in CI.
No production prompts, credentials, or model calls are involved.
"""
import copy
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading

import jsonschema


def main(schema_directory, trace_file, executable):
    messages = [json.loads(line) for line in Path(trace_file).read_text().splitlines()]
    schemas = {
        "initialize": "InitializeParams.json",
        "thread/start": "ThreadStartParams.json",
        "thread/resume": "ThreadResumeParams.json",
        "turn/start": "TurnStartParams.json",
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
    with subprocess.Popen([executable, "app-server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.DEVNULL, text=True) as process:
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
                "cwd": str(Path.cwd()), "ephemeral": True,
                "model": "protocol-test", "modelProvider": "protocol_test",
                "config": {"model_providers.protocol_test": {
                    "name": "Offline protocol test", "base_url": "http://127.0.0.1:1/v1",
                    "wire_api": "responses", "requires_openai_auth": False,
                }},
            })
            result = request(start)
            assert result["thread"]["id"], "Missing thread ID"
            assert result["approvalPolicy"] == "untrusted", "Approval policy was not applied"
            assert result["sandbox"]["type"] == "readOnly", "Read-only sandbox was not applied"
            print("PASS real Codex initializes and creates an approval-mode thread without a model call", flush=True)
        finally:
            process.kill()
            process.wait(timeout=10)


if __name__ == "__main__":
    main(*sys.argv[1:])
