[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Desktop','Cli','Windows')][string]$Component = 'Desktop',
    [switch]$RequireSymbols,
    [switch]$PublishedWinX64
)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $OutputDirectory).Path
if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
    throw 'OutputDirectory must be a directory.'
}
# Keep this list independent of Directory.Build.targets to detect regressions.
$forbidden = @('JarvisCode.App.exe','JarvisCode.App.deps.json','JarvisCode.App.runtimeconfig.json')
$required = @('JarvisCode.App.dll','JarvisCode.Core.dll','JarvisCode.Host.dll',
              'JarvisCode.Providers.dll','Jarvis.Agent.Windows.dll','Jarvis.Agent.Core.dll','Jarvis.Protocol.dll')
if ($Component -ne 'Windows') {
    $entryPoint = if ($Component -eq 'Desktop') { 'Jarvis.Agent.Desktop' } else { 'jarvis-agent' }
    $required += @("$entryPoint.exe", "$entryPoint.dll", "$entryPoint.deps.json", "$entryPoint.runtimeconfig.json")
    $required += @('browser/jarvis-browser-host.exe','jarvis-browser-service.exe','jarvis-browser-service.dll','jarvis-browser-service.deps.json','jarvis-browser-service.runtimeconfig.json')
}
if ($RequireSymbols) { $required += 'JarvisCode.App.pdb' }
# The official ConPTY host is architecture-specific, not OpenConsole.exe at the root.
# Windows x64 also supports execution on ARM64 Windows via the native ARM64 console host.
if ($PublishedWinX64) {
    $required += @('conpty.dll', 'x64/OpenConsole.exe', 'arm64/OpenConsole.exe',
                   'licenses/Microsoft.Windows.Console.ConPTY-LICENSE.txt')
}
$unexpected = @($forbidden | Where-Object { Test-Path -LiteralPath (Join-Path $directory $_) })
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $directory $_) -PathType Leaf) })
if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
    throw "Invalid $Component output. Unexpected standalone files: $($unexpected -join ', '); missing dependencies: $($missing -join ', ')."
}
[pscustomobject]@{
    Component = $Component
    OutputDirectory = $directory
    StandaloneBaselineHost = 'Absent'
    RequiredDependencies = 'Present'
    SymbolsChecked = [bool]$RequireSymbols
    Result = 'PASS'
}
