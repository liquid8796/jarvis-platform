[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PythonExe,
    [ValidateRange(1,65535)][int]$Port = 9876,
    [string]$ConfigPath = (Join-Path $env:LOCALAPPDATA 'JarvisAgent\mcp.json')
)
$ErrorActionPreference = 'Stop'
$python = (Resolve-Path -LiteralPath $PythonExe).Path
if ([IO.Path]::GetFileName($python) -ine 'python.exe') { throw 'Use the trusted Blender MCP environment python.exe.' }
$config = [IO.Path]::GetFullPath($ConfigPath)
$directory = [IO.Path]::GetDirectoryName($config)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$lock = $null
try {
    # This lock coordinates instances of this setup helper. Other clients may not honor it.
    $lock = [IO.File]::Open($config + '.lock', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    & $python (Join-Path $PSScriptRoot 'blender\configure.py') --config $config --python $python --port $Port
    if ($LASTEXITCODE -ne 0) { throw 'Blender MCP configuration failed; inspect the setup error above.' }
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
}
