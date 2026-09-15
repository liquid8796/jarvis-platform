#!/usr/bin/env bash
# Read-only inventory for THIS application; no sudo or shared-service changes.
set -euo pipefail
[[ "$(id -u)" != 0 ]] || { echo 'Deploy as a dedicated non-root account.' >&2; exit 1; }
for command in python3 tar sha256sum systemctl ss curl; do command -v "$command" >/dev/null || { echo "Missing prerequisite: $command" >&2; exit 1; }; done
printf 'architecture=%s\nuser=%s\nhome=%s\n' "$(uname -m)" "$(id -un)" "$HOME"
printf '\nCurrent listeners (read only):\n'; ss -ltn
printf '\nMemory and space (read only):\n'; free -h; df -h "$HOME"
systemctl --user show-environment >/dev/null || { echo 'User systemd manager is not available. Provision it before deployment.' >&2; exit 1; }
[[ "$(loginctl show-user "$(id -un)" -p Linger --value)" == yes ]] || { echo 'User lingering is required for an always-on service. Ask the VM administrator to enable it for this account.' >&2; exit 1; }
if ss -H -ltn 'sport = :18765' | grep -q . && ! systemctl --user is-active --quiet jarvis-mcp-server.service; then
 echo 'Port 18765 is occupied by another service. No deployment will be made.' >&2; exit 1
fi
printf '\nPreflight passed. No service/configuration was modified.\n'
