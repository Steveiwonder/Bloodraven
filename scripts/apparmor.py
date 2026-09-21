"""Permit Codex's own sandbox on Ubuntu with restricted user namespaces.

Never disable the host-wide restriction or execute a user-owned launcher as root.
"""
import argparse
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import subprocess
import tempfile

MARKER = '# Managed by Bloodraven: Codex user namespace compatibility.\n'
PROFILE = Path('/etc/apparmor.d/bloodraven-codex')


def configured_codex(config):
    values = []
    for line in Path(config).read_text().splitlines():
        if line.startswith('BLOODRAVEN_CODEX_EXECUTABLE='):
            fields = shlex.split(line.split('=', 1)[1], comments=False)
            if len(fields) != 1:
                raise ValueError('Invalid configured Codex executable.')
            values.append(fields[0])
    if len(values) != 1 or not Path(values[0]).is_absolute():
        raise ValueError('Expected one absolute BLOODRAVEN_CODEX_EXECUTABLE path.')
    return Path(values[0])


def is_native(path):
    if not path.is_file() or not os.access(path, os.X_OK):
        return False
    with path.open('rb') as handle:
        return handle.read(4) == b'\x7fELF'


def native_codex(launcher, machine=None):
    launcher = Path(launcher).resolve(strict=True)
    if is_native(launcher):
        return launcher
    # npm has shipped both an inline vendor tree and separate optional packages.
    if launcher.name != 'codex.js' or launcher.parent.name != 'bin':
        raise ValueError('Cannot identify native Codex binary from this launcher. Use an official native or npm installation.')
    architecture = {'x86_64': ('x86_64-unknown-linux-musl', 'codex-linux-x64'),
                    'aarch64': ('aarch64-unknown-linux-musl', 'codex-linux-arm64')}.get(machine or platform.machine())
    if architecture is None:
        raise ValueError('Unsupported Codex architecture for AppArmor setup.')
    target, package = architecture
    root = launcher.parent.parent
    roots = [root, root / 'node_modules' / '@openai' / package, root.parent / package]
    candidates = {path.resolve() for base in roots for suffix in ('bin/codex', 'codex/codex')
                  if is_native(path := base / 'vendor' / target / suffix)}
    if len(candidates) != 1:
        raise ValueError('Cannot uniquely identify native Codex binary. Repair the Codex installation before retrying.')
    return candidates.pop()


def profile_text(binary):
    path = str(binary)
    # AppArmor paths have their own glob/macro language. Reject its metacharacters.
    if not path.startswith('/') or re.search(r'[\x00-\x1f\x7f"\\*?\[\]{}@]', path):
        raise ValueError('Codex path contains unsupported AppArmor metacharacters.')
    return (MARKER + 'abi <abi/4.0>,\ninclude <tunables/global>\n'
            + f'profile bloodraven-codex "{path}" flags=(unconfined) {{\n  userns,\n}}\n')


def configure(config, profile=PROFILE,
              restriction=Path('/proc/sys/kernel/apparmor_restrict_unprivileged_userns'),
              enabled=Path('/sys/module/apparmor/parameters/enabled'), run=subprocess.run):
    if not enabled.exists() or enabled.read_text().strip().lower() not in ('y', 'yes', '1'):
        print('AppArmor is not enabled; no Codex profile needed.')
        return False
    if not restriction.exists() or restriction.read_text().strip() != '1':
        print('AppArmor user namespace restriction is not enabled; no exception needed.')
        return False
    parser = shutil.which('apparmor_parser')
    if not parser:
        raise ValueError('apparmor_parser is missing. Install the Ubuntu apparmor package and retry.')
    binary = native_codex(configured_codex(config))
    content = profile_text(binary)
    profile = Path(profile)
    previous = profile.read_bytes() if profile.exists() else None
    # Adopt the exact manual fix documented for existing installations, but do
    # not overwrite a customised/admin-owned profile with different rules.
    manual = content.removeprefix(MARKER).replace(f'"{binary}"', str(binary)).encode()
    if previous is not None and not previous.startswith(MARKER.encode()) and previous not in (manual, content.removeprefix(MARKER).encode()):
        raise ValueError(f'{profile} contains custom rules. Review it before allowing Bloodraven to manage this profile.')
    profile.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix='.bloodraven-codex-', dir=profile.parent)
    temporary = Path(name)
    loaded = False
    try:
        with os.fdopen(fd, 'w') as handle:
            os.fchmod(handle.fileno(), 0o644)
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        run([parser, '-Q', '-T', str(temporary)], check=True)
        run([parser, '-r', '-T', str(temporary)], check=True)
        loaded = True
        os.replace(temporary, profile)
    except BaseException:
        if loaded:
            # Publication failed: restore the kernel's previous policy too.
            if previous is not None:
                run([parser, '-r', '-T', str(profile)], check=True)
            else:
                run([parser, '-R', str(temporary)], check=True)
        raise
    finally:
        temporary.unlink(missing_ok=True)
    print(f'AppArmor: enabled Codex sandbox user namespaces for {binary}. Host-wide restrictions remain enabled.')
    return True


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('config')
    args = parser.parse_args()
    if os.geteuid() != 0:
        parser.error('Run this helper through sudo.')
    try:
        configure(args.config)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f'Codex AppArmor setup failed: {error}\n')
