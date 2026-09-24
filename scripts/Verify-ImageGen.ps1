param([string]$Configuration = 'Release', [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$version = (Get-Content VERSION -Raw).Trim()
$report = Join-Path $PWD "artifacts\verification\imagegen-$version"
New-Item -ItemType Directory -Force $report | Out-Null
function Check([string]$Name, [string]$Command, [string[]]$CommandArgs) {
    $log = Join-Path $report ($Name + '.log')
    Write-Output "START $Name"
    $old = $ErrorActionPreference
    try { $ErrorActionPreference = 'Continue'; & $Command @CommandArgs *> $log; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $old }
    Get-Content $log -Tail 14
    if ($code -ne 0) { throw "$Name failed with exit code $code. See $log" }
    Write-Output "PASS $Name"
}
foreach ($suite in @('Jarvis.Core.Tests','Jarvis.Agent.Windows.Tests','Jarvis.Server.Tests')) {
    $argsList = @('test', "tests/$suite", '-c', $Configuration, '--logger', "trx;LogFileName=$suite.trx", '--results-directory', $report, '-v:q')
    if ($NoRestore) { $argsList += '--no-restore' }
    Check $suite 'dotnet' $argsList
}
Check 'browser-tests' 'node' @('--test','tests/browser-session-isolation.test.cjs','tests/imagegen-extension.test.cjs')
Check 'python-tests' 'python' @('-B','-m','unittest','discover','-s','tests','-p','test_*.py')
Check 'current-version' 'python' @('-B','scripts/Verify-CurrentVersion.py')
foreach ($file in @('background.js','imagegen.js','imagegen-page.js','imagegen-popup.js')) {
    Check ('syntax-' + $file) 'node' @('--check', "jarvis-agent/src/Jarvis.Agent.Windows/Assets/Browser/$file")
}
@{ version=$version; status='passed'; completedUtc=[DateTime]::UtcNow.ToString('o');
   liveImageGenerationTested=$false; scopes=@('Core','Windows','Server','extension fixtures','Python repository checks','version','JavaScript syntax') } |
    ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $report 'verification-complete.json')
Write-Output 'IMAGEGEN_VERIFICATION_PASSED'
