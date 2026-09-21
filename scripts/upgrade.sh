#!/usr/bin/env bash
set -euo pipefail
umask 077
[[ "${EUID}" -ne 0 ]] || { echo "Run as the original installation user, not root."; exit 1; }
[[ -f /etc/bloodraven/bloodraven.env ]] || { echo "Run the installer first."; exit 1; }
source_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${source_root}"
for command_name in dotnet sudo systemctl python3; do
  command -v "${command_name}" >/dev/null 2>&1 || { echo "Missing command: ${command_name}"; exit 1; }
done
service_user="$(systemctl show bloodraven --property=User --value 2>/dev/null || true)"
if [[ -z "${service_user}" && ! -f /etc/systemd/system/bloodraven.service ]]; then
  # Recovery after a first installation failed to activate.
  service_user="$(stat -c %U /var/lib/bloodraven)"
fi
[[ "${service_user}" == "$(id -un)" ]] || { echo "Run the upgrader as ${service_user:-the original installation user}."; exit 1; }
service_group="$(id -gn "${service_user}")"
dotnet_path="$(command -v dotnet)"
dotnet publish src/Bloodraven/Bloodraven.csproj -c Release -o .publish
sudo python3 scripts/setup.py progress /etc/bloodraven/bloodraven.env
sudo python3 scripts/deploy.py "${source_root}/.publish" "${service_user}" "${service_group}" "${dotnet_path}" "${PATH}"
