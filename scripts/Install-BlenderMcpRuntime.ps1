[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PythonExe,
    [string]$TargetDirectory = (Join-Path $env:LOCALAPPDATA 'JarvisAgent\blender-runtimes\2.1.1-jarvis.1'),
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
$python = (Resolve-Path -LiteralPath $PythonExe).Path
if ([IO.Path]::GetFileName($python) -ine 'python.exe') { throw 'Use a trusted Python 3.11+ python.exe.' }
$arguments = @((Join-Path $PSScriptRoot 'blender\install_runtime.py'), '--target', [IO.Path]::GetFullPath($TargetDirectory))
if ($ArchivePath) { $arguments += @('--archive', (Resolve-Path -LiteralPath $ArchivePath).Path) }
& $python @arguments
if ($LASTEXITCODE -ne 0) { throw 'Runtime installation failed; the live Blender/Agent profile was not changed.' }
