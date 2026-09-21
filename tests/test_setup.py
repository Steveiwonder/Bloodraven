import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / f"{name}.py")
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


setup, deploy = module("setup"), module("deploy")


class PairingTests(unittest.TestCase):
    def message(self, user=123, text="/start nonce", date=100, kind="private"):
        return {"message": {"from": {"id": user}, "chat": {"id": user, "type": kind},
                            "text": text, "date": date}}

    def test_unrelated_latest_sender_not_authorised(self):
        updates = [self.message(), self.message(user=999, text="/start")]
        self.assertEqual(setup.matching_owners(updates, "nonce", 100), {(123, 123)})

    def test_reject_stale_group_and_wrong_code(self):
        updates = [self.message(date=1), self.message(kind="group"), self.message(text="/start wrong")]
        self.assertEqual(setup.matching_owners(updates, "nonce", 100), set())

    def test_multiple_candidates_require_rejection(self):
        self.assertEqual(len(setup.matching_owners([self.message(), self.message(user=999)], "nonce", 100)), 2)

    def test_relative_override_uses_invocation_directory(self):
        self.assertEqual(setup.resolve_directory("../repo two", "/home/user/repo"), "/home/user/repo two")

    def test_environment_quotes(self):
        self.assertEqual(setup.env_quote('a "b" \\ c'), '"a \\"b\\" \\\\ c"'.replace('\\\\"', '\\"'))
        self.assertRaises(ValueError, setup.env_quote, "line\nINJECT=yes")

    def test_unit_quotes_and_environment(self):
        text = deploy.service_text("user", "group", "/home/user", "/home/user/.dotnet/dotnet",
                                   Path('/opt/release a/Bloodraven.dll'), "/home/user/node/bin:/usr/bin",
                                   Path("/etc/bloodraven/bloodraven.env"), Path("/var/lib/bloodraven"))
        self.assertIn('ExecStart="/home/user/.dotnet/dotnet" "/opt/release a/Bloodraven.dll"', text)
        self.assertIn("ExecStartPre=", text)
        self.assertIn("PATH=/home/user/node/bin:/usr/bin", text)
        self.assertEqual(deploy.unit_quote("a%$b"), '"a%%$$b"')
        self.assertIn('WorkingDirectory=/opt/release a\n', text)
        self.assertIn('EnvironmentFile=/etc/bloodraven/bloodraven.env\n', text)

    def test_generated_unit_passes_real_systemd_validator(self):
        with tempfile.TemporaryDirectory() as tmp:
            unit = Path(tmp) / "bloodraven.service"
            text = deploy.service_text("root", "root", "/root", "/usr/bin/true",
                                       Path('/opt/release a%/Bloodraven.dll'), '/usr/bin:/bin',
                                       Path('/etc/bloodraven/bloodraven.env'), Path('/var/lib/bloodraven'))
            unit.write_text(text)
            deploy.Systemd().verify(unit)
            # Demonstrate that this test detects the original quoting regression.
            unit.write_text(text.replace('WorkingDirectory=/opt/release a%%',
                                         'WorkingDirectory="/opt/release a%%"'))
            result = subprocess.run(['systemd-analyze', 'verify', str(unit)], capture_output=True)
            self.assertNotEqual(result.returncode, 0)

    def test_invalid_paths_rejected(self):
        for path in ('relative', '/tmp/new\nUser=root', '/tmp/\0file'):
            with self.assertRaises(ValueError):
                deploy.unit_path(path)

    def test_failed_stop_only_tolerated_for_inactive_or_missing_unit(self):
        for status in (0, 1, 3, 4):
            with self.subTest(status=status), patch.object(deploy.subprocess, 'run') as run:
                run.side_effect = [subprocess.CalledProcessError(1, 'systemctl'),
                                   subprocess.CompletedProcess([], status)]
                if status in (3, 4):
                    deploy.Systemd().run('stop', 'bloodraven')
                else:
                    with self.assertRaises(subprocess.CalledProcessError):
                        deploy.Systemd().run('stop', 'bloodraven')


class FakeSystemd:
    def __init__(self, fail=None):
        self.calls = []
        self.fail = fail
        self.failed = False

    def verify(self, unit):
        self.calls.append(("verify",))
        if self.fail == "verify":
            raise RuntimeError("Invalid candidate")

    def run(self, *args):
        self.calls.append(args)
        if self.fail == "rollback-stop" and (args[0] == "start" or
                (args[0] == "stop" and self.calls.count(args) == 2)):
            raise RuntimeError("Injected start/rollback stop failure")
        if self.fail == args[0] and not self.failed:
            self.failed = True
            raise RuntimeError("Injected activation failure")

    def healthy(self, ready):
        self.calls.append(("healthy",))
        if self.fail == "health":
            raise RuntimeError("Injected health failure")


class DeploymentTests(unittest.TestCase):
    def exercise(self, failure=None, existing=True):
        with tempfile.TemporaryDirectory(prefix="bloodraven-deploy-test-") as tmp:
            base = Path(tmp)
            publish, root, state = base / "publish", base / "install", base / "state"
            unit = base / "units" / "bloodraven.service"
            publish.mkdir()
            state.mkdir()
            unit.parent.mkdir()
            (publish / "Bloodraven.dll").write_text("new binary")
            if existing:
                unit.write_text("old service")
            systemd = FakeSystemd(failure)
            render = lambda executable: f"new service {executable}"
            if failure:
                with self.assertRaises(RuntimeError):
                    deploy.deploy(publish, root, unit, state, render, systemd)
                if existing:
                    self.assertEqual(unit.read_text(), "old service")
                    if failure == "verify":
                        self.assertEqual(systemd.calls, [("verify",)])
                    elif failure == "rollback-stop":
                        self.assertEqual(systemd.calls[-1], ("daemon-reload",))
                    else:
                        self.assertEqual(systemd.calls[-1], ("start", "bloodraven"))
                else:
                    self.assertFalse(unit.exists())
            else:
                release = deploy.deploy(publish, root, unit, state, render, systemd)
                self.assertIn(str(release), unit.read_text())
                self.assertEqual((release / "Bloodraven.dll").read_text(), "new binary")
                self.assertEqual(systemd.calls[-1], ("enable", "bloodraven"))
                if existing:
                    self.assertEqual((release / "previous.service").read_text(), "old service")

    def test_success_preserves_previous_service(self): self.exercise()
    def test_failed_start_restores_service(self): self.exercise("start")
    def test_failed_health_restores_service(self): self.exercise("health")
    def test_failed_enable_restores_service(self): self.exercise("enable")
    def test_failed_first_install_removes_only_new_unit(self): self.exercise("health", existing=False)
    def test_invalid_candidate_does_not_stop_previous_service(self): self.exercise("verify")
    def test_failed_rollback_stop_still_restores_previous_unit(self): self.exercise("rollback-stop")


if __name__ == "__main__":
    unittest.main()
