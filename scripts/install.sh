#!/usr/bin/env bash
set -euo pipefail
umask 077

[[ "${EUID}" -ne 0 ]] || { echo "Run as your normal Codex user, not root."; exit 1; }
if [[ -f /etc/bloodraven/bloodraven.env ]]; then
  echo "Bloodraven is already configured. Run the upgrade command in the README; setup will not overwrite your settings."
  exit 1
fi
invocation="${BLOODRAVEN_DEFAULT_WORKING_DIRECTORY:-${PWD}}"
source_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${source_root}"
for command_name in dotnet codex git python3 sudo; do
  command -v "${command_name}" >/dev/null 2>&1 || { echo "Missing command: ${command_name}"; exit 1; }
done
codex login status >/dev/null 2>&1 || { echo "Run codex login first."; exit 1; }
temporary="$(mktemp -d -t bloodraven-setup.XXXXXXXX)"
trap 'rm -f -- "${temporary}/pairing.json" "${temporary}/bloodraven.env"; rmdir -- "${temporary}"' EXIT
python3 scripts/setup.py pair "${temporary}/pairing.json"

read -r -p "Git repository Codex should work in [${invocation}]: " working_directory
working_directory="$(python3 scripts/setup.py resolve "${working_directory:-${invocation}}" "${invocation}")"
case "${working_directory}" in /tmp|/tmp/*|/var/tmp|/var/tmp/*)
  echo "Choose a repository outside /tmp and /var/tmp; the service uses a private temporary directory."
  exit 1 ;;
esac
if ! git -C "${working_directory}" rev-parse --show-toplevel >/dev/null 2>&1; then
  read -r -p "No Git work tree exists at ${working_directory}. Create one? [y/N]: " answer
  [[ "${answer,,}" == y || "${answer,,}" == yes ]] || { echo "Setup cancelled without creating the directory."; exit 1; }
  mkdir -p -- "${working_directory}"
  git -C "${working_directory}" init
fi
echo "Full access lets Codex use the network and all files accessible to your Linux user."
read -r -p "Codex sandbox (read-only/workspace-write/danger-full-access) [danger-full-access]: " sandbox
sandbox="${sandbox:-danger-full-access}"
case "${sandbox}" in read-only|workspace-write|danger-full-access) ;; *) echo "Invalid sandbox."; exit 1 ;; esac

service_user="$(id -un)"
service_group="$(id -gn)"
codex_path="$(command -v codex)"
dotnet_path="$(command -v dotnet)"
python3 scripts/setup.py config "${temporary}/pairing.json" "${working_directory}" "${codex_path}" "${sandbox}" "${temporary}/bloodraven.env"
dotnet publish src/Bloodraven/Bloodraven.csproj -c Release -o .publish
sudo install -d -m 0755 /etc/bloodraven
sudo install -d -o "${service_user}" -g "${service_group}" -m 0700 /var/lib/bloodraven
sudo install -m 0600 "${temporary}/bloodraven.env" /etc/bloodraven/bloodraven.env
sudo python3 scripts/apparmor.py /etc/bloodraven/bloodraven.env
sudo python3 scripts/deploy.py "${source_root}/.publish" "${service_user}" "${service_group}" "${dotnet_path}" "${PATH}"
echo "Bloodraven installed. Check it with: sudo systemctl status bloodraven"
