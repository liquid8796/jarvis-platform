[CmdletBinding()]
param(
  [string]$InstallDirectory = (Join-Path $HOME 'Downloads\Jarvis Agent')
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repository 'VERSION') -Raw).Trim()
$buildDirectory = Join-Path $repository ("artifacts\agent\" + $version + "\desktop")
$source = Join-Path $buildDirectory 'Jarvis.Agent.Core.dll'
$target = Join-Path $InstallDirectory 'Jarvis.Agent.Core.dll'
$desktop = Join-Path $InstallDirectory 'Jarvis.Agent.Desktop.exe'

foreach ($file in @($source,$target,$desktop)) {
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw "Required file is missing: $file"
  }
}

$fullInstall = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$running = @(Get-Process -Name 'Jarvis.Agent.Desktop','jarvis-agent','jarvis-browser-service' -ErrorAction SilentlyContinue |
  Where-Object {
    try {
      $_.Path -and ([IO.Path]::GetFullPath($_.Path).StartsWith($fullInstall + '\',[StringComparison]::OrdinalIgnoreCase))
    } catch { $false }
  })
if ($running.Count -gt 0) {
  throw "Jarvis Agent or its browser integration is still running. Exit Jarvis Agent fully (including the tray icon) and close Jarvis browser-service processes before rerunning this script."
}

$expected = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
if ($expected -eq $actual) {
  Write-Host "Jarvis Core is already using the UTF-8-fixed build."
  Start-Process -FilePath $desktop -WorkingDirectory $InstallDirectory
  exit 0
}

$backup = Join-Path $InstallDirectory ("Jarvis.Agent.Core.dll.before-utf8-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".bak")
$temporary = Join-Path $InstallDirectory ("Jarvis.Agent.Core.dll.utf8-" + [guid]::NewGuid().ToString("N") + ".tmp")
try {
  [IO.File]::Copy($source,$temporary,$false)
  [IO.File]::Replace($temporary,$target,$backup)
  $installed = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
  if ($installed -ne $expected) {
    [IO.File]::Copy($backup,$target,$true)
    throw "Hash mismatch after installation; old DLL restored."
  }
  Write-Host "Applied UTF-8 fix to: $target"
  Write-Host "Original DLL backup: $backup"
  Start-Process -FilePath $desktop -WorkingDirectory $InstallDirectory
  Write-Host "Jarvis Agent restart requested."
} finally {
  if (Test-Path -LiteralPath $temporary) {
    Remove-Item -LiteralPath $temporary -Force
  }
}
