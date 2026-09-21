#!/usr/bin/env bash
set -euo pipefail

readonly REPOSITORY_URL="https://github.com/Steveiwonder/Bloodraven.git"
readonly CODEX_INSTALLER_URL="https://chatgpt.com/codex/install.sh"
readonly INVOCATION_DIRECTORY="${PWD}"

say() { printf '\n\033[1;36m%s\033[0m\n' "$*"; }
fail() { printf '\nBloodraven setup failed: %s\n' "$*" >&2; exit 1; }
cleanup() {
  rm -f -- "${dotnet_installer:-}"
  if [[ -n "${checkout_directory:-}" && "${checkout_directory}" == "${TMPDIR:-/tmp}"/bloodraven.* ]]; then
    rm -rf -- "${checkout_directory}"
  fi
}
trap cleanup EXIT

if [[ "${EUID}" -eq 0 ]]; then
  fail "Run this command as your normal Ubuntu user, not as root. The installer will use sudo when required."
fi

if [[ ! -r /etc/os-release ]]; then
  fail "This first release supports Ubuntu only."
fi
# shellcheck disable=SC1091
source /etc/os-release
if [[ "${ID:-}" != "ubuntu" ]]; then
  fail "This first release supports Ubuntu only (detected: ${PRETTY_NAME:-unknown Linux})."
fi

say "Bloodraven — Telegram for Codex"
echo "This setup will install required packages, Codex CLI, Bloodraven, and a systemd service."
echo "You will be asked for your Telegram bot token and Codex working directory."

command -v sudo >/dev/null 2>&1 || fail "sudo is required."
sudo -v

say "Installing Ubuntu prerequisites"
sudo apt-get update
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl git python3

export PATH="${HOME}/.local/bin:${HOME}/.codex/bin:${HOME}/.dotnet:${PATH}"

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  say "Installing .NET 10 SDK"
  dotnet_installer="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${dotnet_installer}"
  bash "${dotnet_installer}" --channel 10.0 --install-dir "${HOME}/.dotnet"
  hash -r
fi

if ! command -v codex >/dev/null 2>&1; then
  say "Installing Codex CLI"
  curl -fsSL "${CODEX_INSTALLER_URL}" | sh
  export PATH="${HOME}/.local/bin:${HOME}/.codex/bin:${PATH}"
fi
command -v codex >/dev/null 2>&1 || fail "Codex installed but was not found in PATH. Start a new terminal and run this command again."

if ! codex login status >/dev/null 2>&1; then
  say "Sign in to Codex"
  echo "Follow the Codex sign-in instructions below. Bloodraven will wait for you to finish."
  codex login
fi
codex login status >/dev/null 2>&1 || fail "Codex sign-in was not completed."

say "Downloading Bloodraven"
checkout_directory="$(mktemp -d -t bloodraven.XXXXXXXX)"
git clone --depth 1 "${REPOSITORY_URL}" "${checkout_directory}"

cd "${checkout_directory}"
BLOODRAVEN_DEFAULT_WORKING_DIRECTORY="${INVOCATION_DIRECTORY}" bash scripts/install.sh

say "Setup complete"
echo "Open your Telegram bot and send it a message."
echo "Service status: sudo systemctl status bloodraven"
echo "Live logs:      sudo journalctl -u bloodraven -f"
