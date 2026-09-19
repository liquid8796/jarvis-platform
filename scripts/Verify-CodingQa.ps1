[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$resultRoot = Join-Path $repoRoot ('artifacts/verification/coding-qa/' + $runId)
New-Item -ItemType Directory -Path $resultRoot | Out-Null
$filter = 'FullyQualifiedName~CodingTaskAcceptanceTests|FullyQualifiedName~DeveloperVerificationCorpusTests|FullyQualifiedName~BoundedDeveloperTestRunnerTests|FullyQualifiedName~StructuredTestReportParserTests|FullyQualifiedName~VerificationResourceGroupTests|FullyQualifiedName~CodingWorkflowContractTests'
$testArgs = @('test', (Join-Path $repoRoot 'tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj'), '-c', 'Release', '--no-restore', '--filter', $filter, '--logger', 'trx;LogFileName=coding-qa.trx', '--results-directory', $resultRoot)
if ($NoBuild) { $testArgs += '--no-build' }
Push-Location $repoRoot
try {
    $started = [DateTimeOffset]::UtcNow
    & dotnet @testArgs *> (Join-Path $resultRoot 'run.log')
    $exitCode = $LASTEXITCODE
    $trxPath = Join-Path $resultRoot 'coding-qa.trx'
    if (-not (Test-Path -LiteralPath $trxPath)) { throw "No TRX was produced. Inspect $resultRoot/run.log" }
    [xml]$trx = Get-Content -Raw -LiteralPath $trxPath
    $counts = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if ($null -eq $counts) { throw 'The test report has no recognized counters.' }
    foreach ($name in @('total','executed','passed','failed','error','aborted')) {
        if (-not $counts.HasAttribute($name)) { throw "Missing test counter: $name" }
    }
    $passed = $exitCode -eq 0 -and [int]$counts.total -gt 0 -and [int]$counts.executed -gt 0 -and
        [int]$counts.passed -gt 0 -and [int]$counts.failed -eq 0 -and [int]$counts.error -eq 0 -and [int]$counts.aborted -eq 0
    $report = [pscustomobject]@{
        version = (Get-Content -LiteralPath (Join-Path $repoRoot 'VERSION') -Raw).Trim()
        startedAt = $started; finishedAt = [DateTimeOffset]::UtcNow
        passed = $passed; exitCode = $exitCode
        total = [int]$counts.total; executed = [int]$counts.executed
        passedTests = [int]$counts.passed; failedTests = [int]$counts.failed
        reportSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $trxPath).Hash.ToLowerInvariant()
        scope = 'Real executable task/API/SQLite/CLI/package fixtures, measured probes, process runners and parser/permission contracts. No model-quality or universal Codex parity claim.'
        resultsDirectory = $resultRoot
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resultRoot 'results.json') -Encoding UTF8
    $report | ConvertTo-Json -Depth 4
    if (-not $passed) { Get-Content -LiteralPath (Join-Path $resultRoot 'run.log') -Tail 50; exit 1 }
} finally { Pop-Location }
