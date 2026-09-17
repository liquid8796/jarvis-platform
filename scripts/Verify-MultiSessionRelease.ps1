[CmdletBinding()]
param([string]$VerificationDirectory = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION must contain major.minor.patch.' }
if ([string]::IsNullOrWhiteSpace($VerificationDirectory)) {
    $VerificationDirectory = Join-Path $root "artifacts/verification/multi-session-$version/resumed"
}
$base = [IO.Path]::GetFullPath($VerificationDirectory)
$build = Join-Path $base 'build'
$agent = Join-Path $root "artifacts/agent/$version"
$server = Join-Path $root "artifacts/server/$version-linux-arm64"
New-Item -ItemType Directory -Force -Path $base | Out-Null

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    Write-Host ('VERIFY> ' + $File + ' ' + ($Arguments -join ' '))
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with code $LASTEXITCODE" }
}
Push-Location $root
try {
    # Isolate compiler output from the running desktop/native messaging host.
    Invoke-Checked dotnet @('build', 'Jarvis.slnx', '-c', 'Release', '--artifacts-path', $build, '--verbosity', 'minimal')
    foreach ($project in @('Jarvis.Core.Tests', 'Jarvis.Agent.Windows.Tests', 'Jarvis.Server.Tests')) {
        Invoke-Checked dotnet @('test', "tests/$project/$project.csproj", '-c', 'Release', '--artifacts-path', $build,
            '--no-build', '--no-restore', '--logger', 'trx', '--results-directory', "$base/tests", '--verbosity', 'minimal')
    }
    Invoke-Checked node @('--test', 'tests/browser-session-isolation.test.cjs')
    Invoke-Checked python @('tests/test_session_ui_assets.py')
    Invoke-Checked python @('tests/test_agent_publish_assets.py')
    Invoke-Checked python @('scripts/Verify-CurrentVersion.py')

    foreach ($target in @(@('Jarvis.Agent.Desktop', 'desktop', 'Desktop'), @('Jarvis.Agent.Cli', 'cli', 'Cli'))) {
        Invoke-Checked dotnet @('publish', "jarvis-agent/src/$($target[0])/$($target[0]).csproj", '-c', 'Release', '-r', 'win-x64',
            '--self-contained', 'true', '--artifacts-path', $build, '-o', "$agent/$($target[1])", '--verbosity', 'minimal')
        & (Join-Path $PSScriptRoot 'Verify-AgentOutput.ps1') -OutputDirectory "$agent/$($target[1])" -Component $target[2] -PublishedWinX64
    }
    Invoke-Checked dotnet @('publish', 'jarvis-mcp-server/src/Jarvis.McpServer/Jarvis.McpServer.csproj', '-c', 'Release',
        '-r', 'linux-arm64', '--self-contained', 'true', '--artifacts-path', $build, '-o', $server, '--verbosity', 'minimal')

    $smoke = @('run', '--project', 'tests/Jarvis.Agent.UiSmoke/Jarvis.Agent.UiSmoke.csproj', '-c', 'Release', '--artifacts-path', $build)
    Invoke-Checked dotnet ($smoke + @('--', "$agent/desktop", "$base/ui"))
    foreach ($part in @('desktop', 'cli')) {
        Invoke-Checked dotnet ($smoke + @('--no-build', '--', "$agent/$part", "$base/pty-$part", '--pty-only'))
    }

    $frameworkPrivate = @('System.Private.CoreLib.dll', 'System.Private.DataContractSerialization.dll', 'System.Private.Uri.dll',
        'System.Private.Windows.Core.dll', 'System.Private.Windows.GdiPlus.dll', 'System.Private.Xml.dll', 'System.Private.Xml.Linq.dll')
    $bad = Get-ChildItem $agent, $server -Recurse -File | Where-Object {
        $_.Extension -in '.pfx', '.p12', '.key', '.pem', '.ttf', '.otf', '.woff', '.woff2', '.eot', '.ttc', '.db', '.sqlite' -or
        $_.Name -in 'agent.local.json', 'tool-permissions.json', 'computer-settings.json', 'execution-settings.json' -or
        ($_.Name -like '*.private.*' -and $_.Name -notin $frameworkPrivate)
    }
    if ($bad) { throw ('Private state or font binaries found in release: ' + ($bad.FullName -join ', ')) }
    foreach ($binary in @("$agent/desktop/Jarvis.Agent.Desktop.dll", "$agent/cli/jarvis-agent.dll", "$server/jarvis-mcp-server.dll")) {
        $assembly = [Reflection.AssemblyName]::GetAssemblyName($binary).Version.ToString()
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileVersion
        if ($assembly -ne "$version.0" -or $fileVersion -ne "$version.0") { throw "Assembly/file version mismatch: $binary" }
        Write-Host "ASSEMBLY_VERIFIED $binary $assembly $fileVersion"
    }
    $agentZip = Join-Path $root "artifacts/Jarvis-Agent-$version-win-x64.zip"
    $serverArchive = Join-Path $root "artifacts/jarvis-mcp-server-$version-linux-arm64.tar.gz"
    Compress-Archive -Path "$agent/*" -DestinationPath $agentZip -Force
    Invoke-Checked tar @('-czf', $serverArchive, '-C', $server, '.')
    & tar -tzf $serverArchive > $null
    if ($LASTEXITCODE -ne 0) { throw 'Server archive integrity check failed.' }
    Invoke-Checked python @('-c', 'import sys,zipfile; z=zipfile.ZipFile(sys.argv[1]); assert z.testzip() is None; print(''ZIP_CRC_VERIFIED'',len(z.infolist()))', $agentZip)
    Get-FileHash $agentZip, $serverArchive -Algorithm SHA256 |
        Select-Object Path, Hash | ConvertTo-Json | Set-Content -Encoding UTF8 "$base/package-hashes.json"
    Write-Host "RELEASE_VERIFICATION_PASSED version=$version evidence=$base"
    Write-Host 'No live agent enrollment, Arm/Pause change, server deployment or production restart was performed.'
}
finally { Pop-Location }
