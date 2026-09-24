[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BlenderExe,
    [Parameter(Mandatory=$true)][string]$AddonPath,
    [ValidateRange(1,65535)][int]$Port = 9876
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $BlenderExe).Path
$addon = (Resolve-Path -LiteralPath $AddonPath).Path
$bootstrap = Join-Path $PSScriptRoot 'blender\bootstrap.py'
if ([IO.Path]::GetFileName($exe) -ine 'blender.exe') { throw 'Specify the installed blender.exe.' }
if (-not (Test-Path -LiteralPath $addon -PathType Leaf)) { throw 'AddonPath must name the trusted addon.py file.' }
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw "Port $Port already has a listener. No process was started or stopped. Select a separate free port or use the existing addon intentionally."
}
$run = Join-Path $env:LOCALAPPDATA ('JarvisAgent\blender-run\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run) | Out-Null
$stdout = Join-Path $run 'stdout.log'
$stderr = Join-Path $run 'stderr.log'
# Windows paths cannot contain quotes; all path arguments are independently quoted for spaces.
$arguments = '--factory-startup --disable-autoexec --python "' + $bootstrap + '" -- --addon "' + $addon + '" --port ' + $Port
$process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Minimized -RedirectStandardOutput $stdout -RedirectStandardError $stderr
[IO.File]::WriteAllText((Join-Path $run 'process.json'), (@{ pid=$process.Id; executable=$exe; port=$Port; startedUtc=$process.StartTime.ToUniversalTime().ToString('o') } | ConvertTo-Json))
$ready = $false
for ($attempt=0; $attempt -lt 60; $attempt++) {
    $process.Refresh()
    if ($process.HasExited) { throw "Dedicated Blender exited ($($process.ExitCode)). Logs: $run" }
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Where-Object { $_.OwningProcess -eq $process.Id -and $_.LocalAddress -eq '127.0.0.1' }
    if ($listener) { $ready=$true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $ready) {
    # Do not kill anything here: preserve the owned window/log for diagnosis, and never touch another Blender.
    throw "Dedicated Blender has not opened the expected loopback listener. PID $($process.Id), logs: $run"
}
[pscustomobject]@{ pid=$process.Id; host='127.0.0.1'; port=$Port; logDirectory=$run; factoryStartup=$true; autoexecDisabled=$true } | ConvertTo-Json -Compress
