#!/usr/bin/env bash
# Upgrade only an existing system service; retain its environment and data paths.
set -euo pipefail
archive=${1:?server archive required}; expected=${2:?SHA256 required}; version=${3:?version required}
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || exit 2
[[ -f "$archive" ]] || exit 2
service=jarvis-mcp-server
base=/opt/jarvis-mcp-server
drop=/etc/systemd/system/$service.service.d/20-jarvis-release.conf
sudo -n true
systemctl is-active --quiet "$service"
printf '%s  %s\n' "$expected" "$archive" | sha256sum --check --status
stamp=$(date -u +%Y%m%d%H%M%S)
release="$base/releases/$version-$stamp"
backup="$base/backups/$stamp"
# The archive is produced by Build.ps1; reject absolute paths and traversal.
if tar -tzf "$archive" | grep -E '(^/|(^|/)\.\.(/|$))' >/dev/null; then exit 2; fi
sudo install -d -m 0755 "$release" "$backup" "$(dirname "$drop")"
sudo tar -xzf "$archive" -C "$release"
sudo chmod 0755 "$release/jarvis-mcp-server"
[[ -f "$release/wwwroot/index.html" && -f "$release/jarvis-mcp-server.runtimeconfig.json" ]]
# A drop-in changes only the executable/content root, never private configuration.
had_drop=0
if sudo test -f "$drop"; then
  sudo cp -p "$drop" "$backup/release.conf"; had_drop=1
fi
rollback() {
  status=$?
  trap - ERR
  if [[ "$had_drop" == 1 ]]; then
    sudo cp -p "$backup/release.conf" "$drop"
  else
    sudo rm -f "$drop"
  fi
  sudo systemctl daemon-reload
  sudo systemctl restart "$service"
  echo 'Jarvis upgrade failed; previous service command restored.' >&2
  exit "$status"
}
trap rollback ERR
config=$(mktemp)
trap 'rm -f "$config"' EXIT
printf '[Service]\nExecStart=\nExecStart=%s/jarvis-mcp-server --contentRoot %s\n' "$release" "$release" > "$config"
sudo install -m 0644 "$config" "$drop"
sudo systemctl daemon-reload
sudo systemctl restart "$service"
healthy=0
for attempt in {1..30}; do
  body=$(curl -fsS --max-time 2 http://127.0.0.1:18765/health 2>/dev/null || true)
  if printf '%s' "$body" | python3 -c 'import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get("status")=="ok" and d.get("version")==sys.argv[1] else 1)' "$version" 2>/dev/null; then healthy=1; break; fi
  sleep 1
done
[[ "$healthy" == 1 ]]
sleep 3
systemctl is-active --quiet "$service"
trap - ERR
printf 'Jarvis %s healthy; release=%s\n' "$version" "$release"
