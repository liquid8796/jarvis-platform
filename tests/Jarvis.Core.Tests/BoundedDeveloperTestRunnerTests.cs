using System.Collections.Concurrent;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

/// <summary>Real owned process/report plumbing, not a claim about the fixture's test-framework quality.</summary>
public sealed class BoundedDeveloperTestRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-test-runner-" + Guid.NewGuid().ToString("N"));
    public BoundedDeveloperTestRunnerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Real_argv_runner_streams_both_channels_and_captures_fresh_report_artifacts()
    {
        const string literal = "literal; $(not-executed) with spaces";
        var script = await ScriptAsync(
            "param([string]$report,[string]$value)\n[Console]::WriteLine($value)\n[Console]::Error.WriteLine('stderr-observed')\n[IO.File]::WriteAllText($report,\"<testsuite tests='1' failures='0'><testcase name='fixture'/></testsuite>\")\n",
            "printf '%s\\n' \"$2\"\nprintf 'stderr-observed\\n' >&2\nprintf \"<testsuite tests='1' failures='0'><testcase name='fixture'/></testsuite>\" > \"$1\"\n");
        var chunks = new ConcurrentQueue<(string Stream, string Text)>();
        var request = Request(script) with
        { Argv = [.. Command(script), "{report}", literal], ReportOutput = (stream, text) => { chunks.Enqueue((stream, text)); return Task.CompletedTask; } };
        var result = await BoundedDeveloperTestRunner.RunAsync(request, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(literal, result.Stdout);
        Assert.Contains(chunks, item => item.Stream == "stdout" && item.Text.Contains(literal));
        Assert.Contains(chunks, item => item.Stream == "stderr" && item.Text.Contains("stderr-observed"));
        Assert.True(File.Exists(result.StdoutPath)); Assert.True(File.Exists(result.StderrPath));
        Assert.Equal("passed", StructuredTestReportParser.Parse("junit", result.ReportContent!, result.ExitCode).State);
    }

    [Fact]
    public async Task Existing_unchanged_report_cannot_certify_a_new_run()
    {
        var stale = Path.Combine(_root, "old-report.xml");
        await File.WriteAllTextAsync(stale, "<testsuite tests='1'><testcase name='old'/></testsuite>");
        var script = await ScriptAsync("Write-Output 'no report written'", "printf 'no report written\\n'");
        var result = await BoundedDeveloperTestRunner.RunAsync(Request(script) with { ReportFile = stale }, CancellationToken.None);
        Assert.Equal(0, result.ExitCode); Assert.Null(result.ReportContent);
        Assert.Contains("not refreshed", result.ReportError);
    }

    [Fact]
    public async Task Timeout_returns_failed_execution_with_bounded_partial_logs()
    {
        var script = await ScriptAsync("Write-Output 'started'; Start-Sleep -Seconds 30", "printf 'started\\n'; sleep 30");
        var result = await BoundedDeveloperTestRunner.RunAsync(Request(script) with { TimeoutSeconds = 1 }, CancellationToken.None);
        Assert.Equal(-1, result.ExitCode); Assert.Null(result.ReportContent);
        Assert.Contains("timeout", result.ReportError);
        Assert.True(File.Exists(result.StdoutPath));
    }

    [Fact]
    public async Task Production_tool_does_not_turn_exit_zero_and_no_tests_into_success()
    {
        var tool = new DeveloperTestTool((_, _) => Task.FromResult(new DeveloperTestRunResult(0, "Build succeeded", "")
        { ReportContent = "<testsuite tests='0' failures='0'/>" }), Path.Combine(_root, "private-artifacts"));
        var reply = await tool.ExecuteAsync(WireJson.Element(new
        { framework = "custom", argv = new[] { "fixture", "{report}" }, reportFormat = "junit" }), new(_root, "call", "session"), default);
        Assert.True(reply.IsError);
        var result = JsonSerializer.Deserialize<StructuredTestSummary>(reply.Text, WireJson.Options)!;
        Assert.Equal("not_run", result.State); Assert.True(result.ReportParsed); Assert.Equal(0, result.Executed);
        Assert.True(File.Exists(result.ReportPath)); Assert.Equal(64, result.ReportSha256!.Length);
    }

    [Fact]
    public async Task Default_dotnet_tool_parses_real_trx_and_rejects_a_zero_match_filter()
    {
        // Run one pure parser case from the already-built assembly; no recursive runner case or rebuild.
        var assembly = typeof(StructuredTestReportParserTests).Assembly.Location;
        var context = new AgentExecutionContext(Path.GetDirectoryName(assembly)!, "real-trx", "fixture-session");
        var tool = new DeveloperTestTool(BoundedDeveloperTestRunner.RunAsync, Path.Combine(_root, "private-trx"));
        var pass = await tool.ExecuteAsync(WireJson.Element(new
        {
            project = assembly, timeoutSeconds = 30,
            filter = "FullyQualifiedName=Jarvis.Core.Tests.StructuredTestReportParserTests.Jest_and_vitest_assertions_have_real_executed_counts"
        }), context, default);
        var passed = JsonSerializer.Deserialize<StructuredTestSummary>(pass.Text, WireJson.Options)!;
        Assert.False(pass.IsError, pass.Text); Assert.True(passed.ReportParsed);
        Assert.Equal("passed", passed.State); Assert.Equal(1, passed.Executed); Assert.True(File.Exists(passed.ReportPath));
        var empty = await tool.ExecuteAsync(WireJson.Element(new
        { project = assembly, timeoutSeconds = 30, filter = "FullyQualifiedName=NoSuchTest_" + Guid.NewGuid().ToString("N") }), context, default);
        var rejected = JsonSerializer.Deserialize<StructuredTestSummary>(empty.Text, WireJson.Options)!;
        Assert.True(empty.IsError, empty.Text); Assert.NotEqual("passed", rejected.State); Assert.Equal(0, rejected.Executed);
    }

    [Fact]
    public async Task Host_environment_references_resolve_without_persisting_values_or_silently_accepting_missing_names()
    {
        var source = "JARVIS_QA_REFERENCE_" + Guid.NewGuid().ToString("N");
        var sensitiveFixtureValue = "fixture-value-" + Guid.NewGuid().ToString("N");
        DeveloperTestRunRequest? observed = null;
        var tool = new DeveloperTestTool((request, _) =>
        {
            observed = request;
            return Task.FromResult(new DeveloperTestRunResult(0, "ok", "")
            { ReportContent = "<testsuite tests='1'><testcase name='reference'/></testsuite>" });
        }, Path.Combine(_root, "private-environment"));
        try
        {
            Environment.SetEnvironmentVariable(source, sensitiveFixtureValue);
            var arguments = WireJson.Element(new { framework = "custom", argv = new[] { "fixture", "{report}" }, reportFormat = "junit",
                environmentFromProcess = new Dictionary<string, string> { ["TARGET_CREDENTIAL"] = source } });
            var reply = await tool.ExecuteAsync(arguments, new(_root, "reference", "session"), default);
            Assert.False(reply.IsError, reply.Text);
            Assert.Equal(sensitiveFixtureValue, observed!.Environment!["TARGET_CREDENTIAL"]);
            Assert.DoesNotContain(sensitiveFixtureValue, reply.Text);
            Assert.DoesNotContain(sensitiveFixtureValue, string.Join(" ", observed.Argv!));
            Environment.SetEnvironmentVariable(source, null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(arguments, new(_root, "missing-reference", "session"), default));
        }
        finally { Environment.SetEnvironmentVariable(source, null); }
    }

    private DeveloperTestRunRequest Request(string script) => new(_root, null, 15)
    {
        Framework = "custom", ReportFormat = "junit", Argv = [.. Command(script), "{report}"],
        ArtifactDirectory = Path.Combine(_root, "artifacts", Guid.NewGuid().ToString("N"))
    };
    private static string[] Command(string script) => OperatingSystem.IsWindows()
        ? ["powershell.exe", "-NoProfile", "-NonInteractive", "-File", script] : ["/bin/sh", script];
    private async Task<string> ScriptAsync(string windows, string unix)
    {
        var path = Path.Combine(_root, "fixture-" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".ps1" : ".sh"));
        await File.WriteAllTextAsync(path, OperatingSystem.IsWindows() ? windows : unix);
        return path;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
