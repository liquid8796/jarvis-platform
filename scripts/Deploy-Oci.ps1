# Explicit invocation is required. SSH host verification is never disabled.
[CmdletBinding()]param(
 [Parameter(Mandatory)][string]$HostName,
 [Parameter(Mandatory)][string]$UserName,
 [Parameter(Mandatory)][string]$KeyPath,
 [Parameter(Mandatory)][string]$KnownHostsPath,
 [Parameter(Mandatory)][string]$Archive,
 [Parameter(Mandatory)][string]$PrivateConfigDirectory,
 [int]$Port=22,
 [switch]$Install)
$ErrorActionPreference='Stop'
if($HostName -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$' -or $UserName -notmatch '^[A-Za-z_][A-Za-z0-9_-]*$' -or $Port -lt 1 -or $Port -gt 65535){throw 'Invalid SSH target.'}
foreach($path in $KeyPath,$KnownHostsPath,$Archive){if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing file: $path"}}
$ssh=@('-i',(Resolve-Path $KeyPath).Path,'-p',"$Port",'-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',"UserKnownHostsFile=$((Resolve-Path $KnownHostsPath).Path)")
$scp=@('-i',(Resolve-Path $KeyPath).Path,'-P',"$Port",'-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o',"UserKnownHostsFile=$((Resolve-Path $KnownHostsPath).Path)")
$target="$UserName@$HostName"; $root=Split-Path $PSScriptRoot -Parent
# No remote writes for the default read-only mode.
Get-Content -Raw "$root/deploy/preflight.sh" | & ssh @ssh $target 'bash -s'
if($LASTEXITCODE -ne 0){throw 'Read-only preflight failed. Nothing deployed.'}
if(-not $Install){Write-Host 'Read-only preflight completed. Add -Install only after reviewing the result.';return}
foreach($name in 'server.private.json','Signing.pfx','Encryption.pfx'){if(-not(Test-Path "$PrivateConfigDirectory/$name")){throw "Missing private file: $name"}}
$version=(Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid VERSION.'}
$release=$version+'-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$stage=".cache/jarvis-deploy/$release"
& ssh @ssh $target "umask 077; mkdir -p '$stage' .config/jarvis-mcp-server; test ! -e .config/jarvis-mcp-server/server.private.json"
$firstConfig=($LASTEXITCODE -eq 0)
if($firstConfig){
 foreach($name in 'server.private.json','Signing.pfx','Encryption.pfx'){
  & scp @scp "$PrivateConfigDirectory/$name" "${target}:.config/jarvis-mcp-server/$name";if($LASTEXITCODE -ne 0){throw 'Private config upload failed.'}
 }
 & ssh @ssh $target 'chmod 600 .config/jarvis-mcp-server/server.private.json .config/jarvis-mcp-server/Signing.pfx .config/jarvis-mcp-server/Encryption.pfx'
 if($LASTEXITCODE -ne 0){throw 'Could not restrict secret permissions.'}
}
foreach($name in 'preflight.sh','install-release.sh','jarvis-mcp-server.service'){
 & scp @scp "$root/deploy/$name" "${target}:${stage}/$name";if($LASTEXITCODE -ne 0){throw 'Deployment script upload failed.'}
}
& scp @scp $Archive "${target}:${stage}/server.tar.gz";if($LASTEXITCODE -ne 0){throw 'Release upload failed.'}
$hash=(Get-FileHash $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
& ssh @ssh $target "bash '$stage/install-release.sh' '$stage/server.tar.gz' '$hash' '$release'"
if($LASTEXITCODE -ne 0){throw 'Installation or health check failed. Review the Jarvis user-service journal.'}
