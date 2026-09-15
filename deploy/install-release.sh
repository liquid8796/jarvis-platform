#!/usr/bin/env bash
# Install only Jarvis's own user service. Never edits global nginx, firewall, Docker or other units.
set -euo pipefail
umask 077
[[ $# == 3 ]] || { echo 'Usage: install-release.sh archive.tar.gz sha256 release-id' >&2; exit 2; }
archive=$(realpath "$1"); expected=$2; release=$3
[[ "$release" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$ ]] || exit 2
[[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || exit 2
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
bash "$script_dir/preflight.sh"
[[ "$(sha256sum "$archive" | cut -d' ' -f1)" == "$expected" ]] || { echo 'Checksum mismatch.' >&2; exit 1; }
root="$HOME/.local/share/jarvis-mcp-server"; config="$HOME/.config/jarvis-mcp-server"
[[ -f "$config/server.private.json" && -f "$config/Signing.pfx" && -f "$config/Encryption.pfx" ]] || { echo 'Install private config and OAuth certificates first, mode 0600.' >&2; exit 1; }
for secret in "$config/server.private.json" "$config/Signing.pfx" "$config/Encryption.pfx"; do
 [[ "$(stat -c '%a' "$secret")" == 600 && "$(stat -c '%U' "$secret")" == "$(id -un)" ]] || { echo 'Private files must be owned by the deploy user with mode 0600.' >&2; exit 1; }
done
mkdir -p "$root/releases" "$root/data" "$HOME/.config/systemd/user"
[[ ! -e "$root/releases/$release" ]] || { echo 'Release already exists; use a unique ID.' >&2; exit 1; }
# Validate all tar entries before extraction. No symlinks, hard links or path escapes.
python3 - "$archive" "$root/releases/$release" <<'PYTAR'
import sys, tarfile, pathlib
archive, output = sys.argv[1:]
with tarfile.open(archive) as tar:
    members=tar.getmembers()
    if sum(m.size for m in members)>2*1024**3: raise SystemExit('Archive exceeds 2 GiB unpacked.')
    for member in members:
        path=pathlib.PurePosixPath(member.name)
        if path.is_absolute() or '..' in path.parts or not (member.isfile() or member.isdir()):
            raise SystemExit('Unsafe archive member rejected.')
    pathlib.Path(output).mkdir(mode=0o700)
    tar.extractall(output, members=members)
PYTAR
[[ -f "$root/releases/$release/jarvis-mcp-server" ]] || { echo 'Missing self-contained executable.' >&2; exit 1; }
chmod u+x "$root/releases/$release/jarvis-mcp-server"
unit="$HOME/.config/systemd/user/jarvis-mcp-server.service"
if [[ -f "$unit" ]] && ! grep -q '# Managed by Jarvis deployment' "$unit"; then echo 'Refusing to replace an unrecognized unit.' >&2; exit 1; fi
cp "$script_dir/jarvis-mcp-server.service" "$unit"
previous=$(readlink "$root/current" || true)
[[ ! -e "$root/current" || -L "$root/current" ]] || { echo 'Current must be an application-owned symlink.' >&2; exit 1; }
ln -s "releases/$release" "$root/current.next"; mv -Tf "$root/current.next" "$root/current"
systemctl --user daemon-reload
systemctl --user enable jarvis-mcp-server.service
systemctl --user restart jarvis-mcp-server.service || true # Health gate below handles start failure and rollback.
for attempt in {1..30}; do
 if curl --fail --silent --max-time 2 http://127.0.0.1:18765/health | grep -q '"status":"ok"'; then
  echo "Installed Jarvis release $release. Shared ingress was NOT changed."; exit 0
 fi
 sleep 1
done
echo 'Health check failed. Rolling back only the Jarvis binary link.' >&2
systemctl --user stop jarvis-mcp-server.service
if [[ -n "$previous" ]]; then ln -s "$previous" "$root/current.rollback"; mv -Tf "$root/current.rollback" "$root/current"; systemctl --user start jarvis-mcp-server.service; fi
exit 1
