param(
    [string]$OutputPath = "artifacts/evaluation/harness-eval.json"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # Test-group coverage includes production acceptance and unit contracts. Timing is not target
    # API latency or an end-to-end model/Codex parity score.
    $scenarios = @(
        @{ name = "coding_acceptance_v3"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~CodingTaskAcceptanceTests|FullyQualifiedName~DeveloperVerificationCorpusTests|FullyQualifiedName~BoundedDeveloperTestRunnerTests|FullyQualifiedName~StructuredTestReportParserTests" },
        @{ name = "dynamic_catalog"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~DynamicToolRegistryTests|FullyQualifiedName~DynamicAgentConnectionTests" },
        @{ name = "permission_pause"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~InvocationPermissionTests|FullyQualifiedName~LocalControlLifecycleTests|FullyQualifiedName~ToolPermissionTests|FullyQualifiedName~ToolCapabilityLeaseTests" },
        @{ name = "adaptive_no_replay_parallel"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~AdaptiveAgentHarnessTests|FullyQualifiedName~RemoteTaskAdaptiveRulesTests" },
        @{ name = "safe_code_plugins"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~ToolProgramEngineTests|FullyQualifiedName~ToolProgramConnectionTests|FullyQualifiedName~SafeScriptToolTests|FullyQualifiedName~PluginCatalogTests|FullyQualifiedName~PluginProjectionTests|FullyQualifiedName~PluginRuntimeTests" },
        @{ name = "delegation_sqlite_memory"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~RemoteTaskDelegationTests|FullyQualifiedName~SqliteMemoryStoreTests" },
        @{ name = "process_runtime_v2"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~ProcessTests|FullyQualifiedName~WorkspaceDirectoryTests" },
        @{ name = "durable_thread_runtime"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~ThreadRuntimeTests" },
        @{ name = "developer_evaluator_v2"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~DeveloperToolsTests|FullyQualifiedName~HarnessEvaluatorTests" },
        @{ name = "doctor_redaction"; project = "tests/Jarvis.Core.Tests/Jarvis.Core.Tests.csproj"; filter = "FullyQualifiedName~AgentDoctorTests" },
        @{ name = "production_runtime_bootstrap"; project = "tests/Jarvis.Agent.Windows.Tests/Jarvis.Agent.Windows.Tests.csproj"; filter = "FullyQualifiedName~AgentRuntimeBootstrapTests" },
        @{ name = "stale_computer_state"; project = "tests/Jarvis.Agent.Windows.Tests/Jarvis.Agent.Windows.Tests.csproj"; filter = "FullyQualifiedName~ComputerStateTrackerTests|FullyQualifiedName~StatefulComputerToolAdapterTests|FullyQualifiedName~ComputerStateToolTests|FullyQualifiedName~ToolInventoryStateTests" },
        @{ name = "protocol_v2_backward_compat"; project = "tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj"; filter = "FullyQualifiedName~AgentProtocolV2Tests|FullyQualifiedName~AgentBridgeTests" },
        @{ name = "task_transport_no_replay"; project = "tests/Jarvis.Server.Tests/Jarvis.Server.Tests.csproj"; filter = "FullyQualifiedName~AgentTaskExecutionTests|FullyQualifiedName~AgentTaskGatewayTests|FullyQualifiedName~AgentTaskRobustnessTests|FullyQualifiedName~AgentTaskMcpTests" }
    )

    $started = [DateTimeOffset]::UtcNow
    $results = @()
    foreach ($scenario in $scenarios) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $text = (& dotnet test $scenario.project --filter $scenario.filter --nologo --no-restore 2>&1 | Out-String)
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
        $durationMs = [int64]$watch.ElapsedMilliseconds
        $rate = if ($durationMs -gt 0) { [math]::Round($total / ($durationMs / 1000.0), 3) } else { 0.0 }
        $results += [pscustomobject]@{
            name = $scenario.name
            success = ($exitCode -eq 0 -and $failed -eq 0 -and $total -gt 0)
            durationMs = $durationMs
            exitCode = $exitCode
            passed = $passed
            failed = $failed
            skipped = $skipped
            total = $total
            testsPerSecond = $rate
            project = $scenario.project
            filter = $scenario.filter
            notes = $tail
        }
    }

    $durations = @($results | ForEach-Object { [int64]$_.durationMs } | Sort-Object)
    $p95Duration = 0
    if ($durations.Count -gt 0) {
        $rank = [math]::Max(1, [math]::Ceiling($durations.Count * 0.95))
        $p95Duration = $durations[[int]$rank - 1]
    }
    $totalTests = [int](($results | Measure-Object -Property total -Sum).Sum)
    $totalDurationMs = [int64](($results | Measure-Object -Property durationMs -Sum).Sum)
    $throughput = if ($totalDurationMs -gt 0) { [math]::Round($totalTests / ($totalDurationMs / 1000.0), 3) } else { 0.0 }

    $report = [pscustomobject]@{
        schemaVersion = 2
        metricScope = 'Test-group outcomes and durations; not application request latency or model-quality parity.'
        version = (Get-Content VERSION -Raw).Trim()
        startedAt = $started
        finishedAt = [DateTimeOffset]::UtcNow
        passedScenarios = @($results | Where-Object success).Count
        failedScenarios = @($results | Where-Object { -not $_.success }).Count
        totalTargetedTests = $totalTests
        totalScenarioDurationMs = $totalDurationMs
        testsPerSecond = $throughput
        p95ScenarioDurationMs = $p95Duration
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
