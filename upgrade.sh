#!/usr/bin/env bash
set -euo pipefail
umask 077

readonly REPOSITORY_URL="https://github.com/Steveiwonder/Bloodraven.git"

say() { printf '\n\033[1;36m%s\033[0m\n' "$*"; }
fail() { printf '\nBloodraven upgrade failed: %s\n' "$*" >&2; exit 1; }
cleanup() {
  rm -f -- "${dotnet_installer:-}"
  if [[ -n "${checkout_directory:-}" && "${checkout_directory}" == "${TMPDIR:-/tmp}"/bloodraven-upgrade.* ]]; then
    rm -rf -- "${checkout_directory}"
  fi
}
trap cleanup EXIT

if [[ "${EUID}" -eq 0 ]]; then
  fail "Run this command as the Linux user that installed Bloodraven, not as root."
fi
if [[ ! -r /etc/os-release ]]; then
  fail "This first release supports Ubuntu only."
fi
# shellcheck disable=SC1091
source /etc/os-release
[[ "${ID:-}" == "ubuntu" ]] || fail "This first release supports Ubuntu only (detected: ${PRETTY_NAME:-unknown Linux})."
[[ -f /etc/bloodraven/bloodraven.env ]] || fail "Bloodraven is not installed. Run the installation command from the README first."

command -v sudo >/dev/null 2>&1 || fail "sudo is required."
sudo -v

service_user="$(systemctl show bloodraven --property=User --value 2>/dev/null || true)"
if [[ -z "${service_user}" && ! -f /etc/systemd/system/bloodraven.service ]]; then
  service_user="$(stat -c %U /var/lib/bloodraven)"
fi
[[ "${service_user}" == "$(id -un)" ]] || fail "Run the upgrader as ${service_user:-the original installation user}."

say "Preparing the Bloodraven upgrade"
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl git python3
export PATH="${HOME}/.dotnet:${HOME}/.local/bin:${HOME}/.codex/bin:${PATH}"

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  say "Installing .NET 10 SDK"
  dotnet_installer="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${dotnet_installer}"
  bash "${dotnet_installer}" --channel 10.0 --install-dir "${HOME}/.dotnet"
  hash -r
fi

say "Downloading the latest Bloodraven"
checkout_directory="$(mktemp -d -t bloodraven-upgrade.XXXXXXXX)"
git clone --depth 1 "${REPOSITORY_URL}" "${checkout_directory}"

cd "${checkout_directory}"
BLOODRAVEN_UPGRADE_DOTNET="$(command -v dotnet)" bash scripts/upgrade.sh

say "Upgrade complete"
echo "Your Telegram settings, working directory, Codex session, and Codex login were preserved."
echo "Service status: sudo systemctl status bloodraven"
