# OCI deployment runbook — shared VM safety

**No deployment was performed for this source delivery.** The provided archive supplied SSH keys but no host/IP, SSH username/port or trusted host fingerprint. The execution environment also lacked an SSH client and .NET SDK. No key content was printed or included in this source package. Do not paste private keys into chat or source.

## Before any write

Obtain the exact VM address/user/port and host-key fingerprint through an independent trusted channel. Create a known_hosts file with the verified server public host key. Do not use StrictHostKeyChecking=no or accept an unverified ssh-keyscan result. Determine `aarch64` versus `x86_64`, domain/DNS/TLS, existing ingress layout, memory and listeners. Choose a dedicated non-root deployment account; never deploy under root on a shared VM.

`deploy/preflight.sh` is read-only. It checks prerequisites, architecture, listeners, user systemd and lingering. It stops when 18765 belongs to something other than the Jarvis user service. It does not install packages, reboot, open firewall rules, alter OCI security lists, change another service or invoke sudo. If systemd user lingering is unavailable, have the VM administrator provision the dedicated account before continuing. This script is not a universal zero-touch installer for every OCI image.

## Build and configuration

On a machine with .NET10 SDK, run `scripts/Build.ps1 -Component Server -ServerRuntime linux-arm64` or linux-x64 based on the VM. Self-contained output still needs OS-native dependencies of the .NET runtime/SQLite, which must be reviewed for that image. No VM package manager changes are automatic.

Run PowerShell7 `scripts/New-ServerConfig.ps1` with HTTPS origin, actual remote home and exact OAuth callbacks. It generates **OAuth** signing/encryption certificates plus `server.private.json` in an external private folder. These are not public TLS certificates. Restrict local folder permissions and encrypt backups. Production paths must use the target user's real home. The code refuses an existing private config rather than rotating keys silently.

```powershell
# Values are placeholders, not a discovered VM.
.\scripts\New-ServerConfig.ps1 `
  -PublicOrigin 'https://jarvis.your-domain.example' `
  -RemoteHome '/home/your-deploy-user' `
  -OAuthCallback 'https://exact-client-callback.example/path'

# Default mode is read-only. -Install is a separate, explicit decision.
.\scripts\Deploy-Oci.ps1 `
  -HostName '<verified-host>' -UserName '<deploy-user>' -Port 22 `
  -KeyPath '<private-key-outside-repo>' -KnownHostsPath '<verified-known-hosts>' `
  -Archive '.\artifacts\jarvis-mcp-server-1.0.16-linux-arm64.tar.gz' `
  -PrivateConfigDirectory '<private-config-directory>'
```

Review output before adding `-Install`. The script uploads only this app's artifacts/config and calls the supplied installer. It does not overwrite an existing private server configuration. The scripts themselves have been statically checked, not exercised against a VM; first run in a staging VM is mandatory.

## Isolation model

Application releases: `~/.local/share/jarvis-mcp-server/releases/<release>`; active symlink `current`; persistent data sibling `data`; configuration/PFX `~/.config/jarvis-mcp-server`; user unit `~/.config/systemd/user/jarvis-mcp-server.service`. Bind only `127.0.0.1:18765`, not the public NIC. User unit has memory768MiB, CPU100% (one CPU capacity), TasksMax256 and mode0077. Review resource limits against VM size; no benchmark or capacity guarantee is implied.

Tar entries are validated (no traversal, link or special-device entries), checksum checked, release IDs constrained, and an existing unrelated unit is not overwritten. A health failure attempts rollback of the Jarvis binary symlink only. It cannot roll back data already changed by the application; this version uses only schema1 with no destructive upgrade.

## Shared ingress

`deploy/nginx-location.example.conf` is only a reviewed fragment for a **new dedicated TLS virtual host**. It is not a replacement nginx.conf. It supports HTTP streaming without buffering and WebSocket upgrade. Set the real server_name/certificates separately. Check your existing proxy configuration before adding it. Run nginx config validation and reload only after administrator review; no script in this package automatically reloads shared nginx, Caddy, Docker, firewall or OCI networking.

The application trusts forwarded headers from loopback by default. A proxy in another container/network requires an explicit trusted-proxy configuration change; never trust all proxy IPs. Public HTTPS must have a valid certificate and DNS. Do not open port18765 to the Internet. OCI inbound 443 requirements and TLS certificate issuance are operator tasks.

## Backup, rollback and cleanup

Back up SQLite with a consistent SQLite backup operation or stop **only** this user service before copying its DB/WAL/key data. Back up OAuth PFX + configuration + Data Protection keys encrypted together. Do not copy only `jarvis.db` while active WAL transactions exist. Keep at least the previous verified release. After first successful admin creation, remove Bootstrap from the private configuration and retain only approved credentials in your password manager.

Inspect `journalctl --user -u jarvis-mcp-server.service`. To roll back a reviewed release, stop only the Jarvis user unit, point `current` to a known previous application release, and start the same unit. Do not delete data as a troubleshooting step. A service restart disconnects agents/MCP calls and clears in-memory routing. Verify user status, local agent arm and job completion before retrying work.
