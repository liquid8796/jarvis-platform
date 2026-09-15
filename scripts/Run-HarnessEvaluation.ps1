param(
    [string]$OutputPath = "artifacts/evaluation/harness-eval.json"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $scenarios = @(
        @{ name = "dynamic_catalog"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~DynamicToolRegistryTests|FullyQualifiedName~DynamicAgentConnectionTests" },
        @{ name = "permission_pause"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~InvocationPermissionTests|FullyQualifiedName~LocalControlLifecycleTests|FullyQualifiedName~ToolPermissionTests" },
        @{ name = "adaptive_no_replay"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~AdaptiveAgentHarnessTests|FullyQualifiedName~RemoteTaskAdaptiveRulesTests" },
        @{ name = "tool_code_mode_plugins"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~ToolProgramEngineTests|FullyQualifiedName~ToolProgramConnectionTests|FullyQualifiedName~PluginCatalogTests|FullyQualifiedName~PluginProjectionTests" },
        @{ name = "delegation_sqlite_memory"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~RemoteTaskDelegationTests|FullyQualifiedName~SqliteMemoryStoreTests" },
        @{ name = "developer_tools"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~DeveloperToolsTests|FullyQualifiedName~HarnessEvaluatorTests" },
        @{ name = "stale_computer_state"; project = "tests/Jarvis.Agent.Windows.Tests/Jarvis.Agent.Windows.Tests.csproj"; filter = "FullyQualifiedName~ComputerStateTrackerTests|FullyQualifiedName~StatefulComputerToolAdapterTests|FullyQualifiedName~ComputerStateToolTests|FullyQualifiedName~ToolInventoryStateTests" },
        @{ name = "task_transport_no_replay"; project = "tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj"; filter = "FullyQualifiedName~AgentTaskExecutionTests|FullyQualifiedName~AgentTaskGatewayTests|FullyQualifiedName~AgentTaskRobustnessTests|FullyQualifiedName~AgentTaskMcpTests" }
    )

    $started = [DateTimeOffset]::UtcNow
    $results = @()
    foreach ($scenario in $scenarios) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $text = (& dotnet test $scenario.project --filter $scenario.filter --nologo 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
        $watch.Stop()
        $failed = 0; $passed = 0; $skipped = 0; $total = 0
        $matches = [regex]::Matches($text, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)')
        foreach ($match in $matches) {
            $failed += [int]$match.Groups[1].Value
            $passed += [int]$match.Groups[2].Value
            $skipped += [int]$match.Groups[3].Value
            $total += [int]$match.Groups[4].Value
        }
        $tail = if ($text.Length -le 2000) { $text.Trim() } else { $text.Substring($text.Length - 2000).Trim() }
        $results += [pscustomobject]@{
            name = $scenario.name
            success = ($exitCode -eq 0 -and $failed -eq 0 -and $total -gt 0)
            durationMs = $watch.ElapsedMilliseconds
            exitCode = $exitCode
            passed = $passed
            failed = $failed
            skipped = $skipped
            total = $total
            project = $scenario.project
            filter = $scenario.filter
            notes = $tail
        }
    }

    $report = [pscustomobject]@{
        version = (Get-Content VERSION -Raw).Trim()
        startedAt = $started
        finishedAt = [DateTimeOffset]::UtcNow
        passedScenarios = @($results | Where-Object success).Count
        failedScenarios = @($results | Where-Object { -not $_.success }).Count
        scenarios = $results
    }
    $fullOutput = Join-Path $root $OutputPath
    $parent = Split-Path -Parent $fullOutput
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $json = $report | ConvertTo-Json -Depth 8
    Set-Content -Path $fullOutput -Value $json -Encoding UTF8
    Write-Output $json
    if ($report.failedScenarios -ne 0) { exit 1 }
}
finally {
    Pop-Location
}
