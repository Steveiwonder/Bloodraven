#!/usr/bin/env bash
set -euo pipefail

config_file="/etc/bloodraven/bloodraven.env"
service_file="/etc/systemd/system/bloodraven.service"
install_root="/opt/bloodraven"

[[ "${EUID}" -ne 0 ]] || { echo "Run this script as the Linux user that installed Bloodraven, not as root."; exit 1; }
[[ -f "${config_file}" ]] || { echo "Bloodraven configuration was not found. Run the installer first."; exit 1; }
[[ -f "${service_file}" ]] || { echo "The Bloodraven systemd service was not found. Run the installer first."; exit 1; }

for command_name in dotnet sudo systemctl getent; do
  command -v "${command_name}" >/dev/null 2>&1 || { echo "Missing required command: ${command_name}"; exit 1; }
done

service_user="$(sudo systemctl show bloodraven --property=User --value)"
service_group="$(sudo systemctl show bloodraven --property=Group --value)"
[[ -n "${service_user}" ]] || { echo "Could not determine the Bloodraven service user."; exit 1; }
[[ -n "${service_group}" ]] || service_group="$(id -gn "${service_user}")"
user_home="$(getent passwd "${service_user}" | cut -d: -f6)"
[[ -n "${user_home}" ]] || { echo "Could not determine the Bloodraven service user's home directory."; exit 1; }
dotnet_path="${BLOODRAVEN_UPGRADE_DOTNET:-$(command -v dotnet)}"

dotnet publish src/Bloodraven/Bloodraven.csproj -c Release -o .publish

sudo systemctl stop bloodraven
sudo install -d -m 0755 "${install_root}"
sudo cp -a .publish/. "${install_root}/"
sed -e "s|@@USER@@|${service_user}|g" -e "s|@@GROUP@@|${service_group}|g" -e "s|@@HOME@@|${user_home}|g" -e "s|@@DOTNET@@|${dotnet_path}|g" deploy/bloodraven.service | sudo tee "${service_file}" >/dev/null
sudo systemctl daemon-reload
sudo systemctl start bloodraven

if ! sudo systemctl is-active --quiet bloodraven; then
  echo "Bloodraven did not start after the upgrade. Recent logs:"
  sudo journalctl -u bloodraven -n 50 --no-pager
  exit 1
fi
