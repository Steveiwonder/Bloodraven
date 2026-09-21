import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('bloodraven_apparmor', ROOT / 'scripts/apparmor.py')
aa = importlib.util.module_from_spec(spec)
spec.loader.exec_module(aa)


class AppArmorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.binary = self.native(self.root / 'native codex')
        self.config = self.root / 'config'
        self.config.write_text(f'BLOODRAVEN_CODEX_EXECUTABLE="{self.binary}"\n')
        self.profile = self.root / 'profiles' / 'bloodraven-codex'
        self.enabled = self.root / 'enabled'
        self.enabled.write_text('Y\n')
        self.restriction = self.root / 'restriction'
        self.restriction.write_text('1\n')
        self.calls = []

    def native(self, path):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'\x7fELFtest')
        path.chmod(0o755)
        return path

    def configure(self, run=None):
        with patch.object(aa.shutil, 'which', return_value='/usr/sbin/apparmor_parser'):
            return aa.configure(self.config, self.profile, self.restriction, self.enabled,
                                run=run or (lambda args, **kwargs: self.calls.append(args)))

    def launcher(self):
        path = self.root / 'node_modules/@openai/codex/bin/codex.js'
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('#!/usr/bin/env node\n')
        path.chmod(0o755)
        return path

    def test_native_symlink(self):
        link = self.root / 'codex'
        link.symlink_to(self.binary)
        self.assertEqual(aa.native_codex(link), self.binary)

    def test_nested_npm_current_layout(self):
        launcher = self.launcher()
        binary = self.native(launcher.parent.parent / 'node_modules/@openai/codex-linux-x64/vendor/x86_64-unknown-linux-musl/bin/codex')
        self.assertEqual(aa.native_codex(launcher, 'x86_64'), binary)

    def test_scoped_npm_path_installs_profile(self):
        launcher = self.launcher()
        binary = self.native(launcher.parent.parent / 'node_modules/@openai/codex-linux-x64/vendor/x86_64-unknown-linux-musl/bin/codex')
        self.config.write_text(f'BLOODRAVEN_CODEX_EXECUTABLE="{launcher}"\n')
        with patch.object(aa.platform, 'machine', return_value='x86_64'):
            self.assertTrue(self.configure())
        self.assertIn(f'"{binary}"', self.profile.read_text())
        self.assertEqual(len(self.calls), 2)

    def test_hoisted_arm_npm(self):
        launcher = self.launcher()
        binary = self.native(launcher.parent.parent.parent / 'codex-linux-arm64/vendor/aarch64-unknown-linux-musl/codex/codex')
        self.assertEqual(aa.native_codex(launcher, 'aarch64'), binary)

    def test_legacy_vendor_and_ambiguous_layout(self):
        launcher = self.launcher()
        base = launcher.parent.parent / 'vendor/x86_64-unknown-linux-musl'
        binary = self.native(base / 'codex/codex')
        self.assertEqual(aa.native_codex(launcher, 'x86_64'), binary)
        self.native(base / 'bin/codex')
        with self.assertRaises(ValueError):
            aa.native_codex(launcher, 'x86_64')

    def test_unknown_launcher_fails(self):
        script = self.root / 'wrapper'
        script.write_text('#!/bin/sh\n')
        with self.assertRaises(ValueError):
            aa.native_codex(script)

    def test_config_is_data_not_shell(self):
        self.config.write_text(f'BLOODRAVEN_TELEGRAM_BOT_TOKEN="secret"\nBLOODRAVEN_CODEX_EXECUTABLE="{self.binary}"\n')
        self.assertEqual(aa.configured_codex(self.config), self.binary)
        for text in ('', 'BLOODRAVEN_CODEX_EXECUTABLE=codex\n', 'BLOODRAVEN_CODEX_EXECUTABLE="/a"\nBLOODRAVEN_CODEX_EXECUTABLE="/b"\n'):
            self.config.write_text(text)
            with self.assertRaises(ValueError):
                aa.configured_codex(self.config)

    def test_path_policy_injection_rejected(self):
        for path in ('relative', '/tmp/a*', '/tmp/a{b,c}', '/tmp/@{HOME}/codex', '/tmp/a"', '/tmp/a\\b', '/tmp/a\nb', '/tmp/a?'):
            with self.assertRaises(ValueError):
                aa.profile_text(path)
        self.assertIn(f'"{self.binary}"', aa.profile_text(self.binary))

    def test_install_repeat_and_refresh(self):
        self.assertTrue(self.configure())
        self.assertEqual(self.profile.stat().st_mode & 0o777, 0o644)
        self.assertIn(str(self.binary), self.profile.read_text())
        self.assertEqual(self.calls[0][1:3], ['-Q', '-T'])
        self.assertEqual(self.calls[1][1:3], ['-r', '-T'])
        self.assertTrue(self.configure())
        updated = self.native(self.root / 'codex-v2')
        self.config.write_text(f'BLOODRAVEN_CODEX_EXECUTABLE="{updated}"\n')
        self.configure()
        self.assertIn(str(updated), self.profile.read_text())
        self.assertNotIn(str(self.binary), self.profile.read_text())
        self.assertEqual(list(self.profile.parent.glob('.bloodraven-*')), [])

    def test_manual_profile_adopted_custom_preserved(self):
        self.profile.parent.mkdir()
        manual = aa.profile_text(self.binary).removeprefix(aa.MARKER).replace(f'"{self.binary}"', str(self.binary))
        self.profile.write_text(manual)
        self.configure()
        self.assertTrue(self.profile.read_text().startswith(aa.MARKER))
        self.profile.write_text('# administrator policy\n')
        with self.assertRaises(ValueError):
            self.configure()
        self.assertEqual(self.profile.read_text(), '# administrator policy\n')

    def test_disabled_restriction_skips_resolution(self):
        self.config.unlink()
        self.restriction.write_text('0\n')
        self.assertFalse(self.configure())
        self.restriction.write_text('1\n')
        self.enabled.write_text('N\n')
        self.assertFalse(self.configure())
        self.assertFalse(self.profile.exists())
        self.assertEqual(self.calls, [])

    def test_parse_or_load_failure_preserves_disk(self):
        self.configure()
        before = self.profile.read_bytes()
        for fail_at in (1, 2):
            calls = []
            def fail(args, **kwargs):
                calls.append(args)
                if len(calls) == fail_at:
                    raise subprocess.CalledProcessError(1, args)
            with self.assertRaises(subprocess.CalledProcessError):
                self.configure(fail)
            self.assertEqual(self.profile.read_bytes(), before)
            self.assertEqual(list(self.profile.parent.glob('.bloodraven-*')), [])

    def test_publish_failure_restores_previous_kernel_profile(self):
        self.configure()
        self.calls.clear()
        with patch.object(aa.os, 'replace', side_effect=OSError('disk failure')):
            with self.assertRaises(OSError):
                self.configure()
        self.assertEqual(self.calls[-1], ['/usr/sbin/apparmor_parser', '-r', '-T', str(self.profile)])


if __name__ == '__main__':
    unittest.main()
