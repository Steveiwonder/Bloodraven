#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -eq 0 ]]; then
  echo "Run this script as the normal Linux user who is logged into Codex, not as root."
  exit 1
fi

for command_name in dotnet codex curl python3 sudo; do
  if ! command -v "${command_name}" >/dev/null 2>&1; then
    echo "Missing required command: ${command_name}"
    exit 1
  fi
done

if ! codex login status >/dev/null 2>&1; then
  echo "Codex is not authenticated. Run 'codex login' first."
  exit 1
fi

read -r -p "Telegram bot token (from @BotFather): " bot_token
bot_name="$(curl --fail --silent "https://api.telegram.org/bot${bot_token}/getMe" | python3 -c 'import json,sys; d=json.load(sys.stdin); assert d["ok"]; print(d["result"]["username"])')"
echo "Open Telegram, send /start to @${bot_name}, then press Enter."
read -r

identity="$(curl --fail --silent "https://api.telegram.org/bot${bot_token}/getUpdates" | python3 -c 'import json,sys; d=json.load(sys.stdin); m=d["result"][-1]["message"]; print(str(m["from"]["id"])+" "+str(m["chat"]["id"]))')" || {
  echo "No message found. Send /start to the bot and run the installer again."
  exit 1
}
read -r user_id chat_id <<<"${identity}"

default_working_directory="${PWD}"
read -r -p "Git repository Codex should work in [${default_working_directory}]: " working_directory
working_directory="${working_directory:-${default_working_directory}}"
working_directory="$(realpath "${working_directory}")"
if [[ ! -d "${working_directory}/.git" ]]; then
  echo "That directory is not a Git repository."
  exit 1
fi

read -r -p "Codex sandbox (read-only/workspace-write/danger-full-access) [workspace-write]: " sandbox
sandbox="${sandbox:-workspace-write}"
case "${sandbox}" in read-only|workspace-write|danger-full-access) ;; *) echo "Invalid sandbox."; exit 1 ;; esac

install_root="/opt/bloodraven"
config_root="/etc/bloodraven"
state_root="/var/lib/bloodraven"
service_user="$(id -un)"
service_group="$(id -gn)"
user_home="${HOME}"
codex_path="$(command -v codex)"
dotnet_path="$(command -v dotnet)"

dotnet publish src/Bloodraven/Bloodraven.csproj -c Release -o .publish
sudo install -d -m 0755 "${install_root}" "${config_root}"
sudo install -d -o "${service_user}" -g "${service_group}" -m 0700 "${state_root}"
sudo cp -a .publish/. "${install_root}/"

temp_env="$(mktemp)"
trap 'rm -f "${temp_env}"' EXIT
{
  printf 'BLOODRAVEN_TELEGRAM_BOT_TOKEN=%s\n' "${bot_token}"
  printf 'BLOODRAVEN_ALLOWED_USER_ID=%s\n' "${user_id}"
  printf 'BLOODRAVEN_ALLOWED_CHAT_ID=%s\n' "${chat_id}"
  printf 'BLOODRAVEN_WORKING_DIRECTORY=%s\n' "${working_directory}"
  printf 'BLOODRAVEN_STATE_DIRECTORY=%s\n' "${state_root}"
  printf 'BLOODRAVEN_CODEX_EXECUTABLE=%s\n' "${codex_path}"
  printf 'BLOODRAVEN_CODEX_SANDBOX=%s\n' "${sandbox}"
} > "${temp_env}"
sudo install -m 0600 "${temp_env}" "${config_root}/bloodraven.env"

sed -e "s|@@USER@@|${service_user}|g" -e "s|@@GROUP@@|${service_group}|g" -e "s|@@HOME@@|${user_home}|g" -e "s|@@DOTNET@@|${dotnet_path}|g" deploy/bloodraven.service | sudo tee /etc/systemd/system/bloodraven.service >/dev/null
sudo systemctl daemon-reload
sudo systemctl enable --now bloodraven
echo "Bloodraven is installed. Check it with: sudo systemctl status bloodraven"
