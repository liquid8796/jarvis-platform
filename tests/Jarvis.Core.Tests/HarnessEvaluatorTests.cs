using System.Text.Json;
using Jarvis.Agent.Core.Evaluation;

namespace Jarvis.Core.Tests;

public sealed class HarnessEvaluatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-eval-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Evaluator_records_success_failure_and_security_metrics()
    {
        var evaluator = new HarnessEvaluator();
        var report = await evaluator.RunAsync([
            new DelegateHarnessScenario("success", _ => Task.FromResult(new HarnessScenarioResult(true, ToolCalls: 3, UnauthorizedRejections: 2, Notes: "ok"))),
            new DelegateHarnessScenario("exception", _ => throw new InvalidOperationException("synthetic"))
        ]);

        Assert.Equal(1, report.Passed);
        Assert.Equal(1, report.Failed);
        Assert.Equal(3, report.ToolCalls);
        Assert.Equal(2, report.UnauthorizedRejections);
        Assert.Contains(report.Scenarios, s => s.Name == "exception" && !s.Success && s.Notes!.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Evaluator_rejects_duplicate_names_and_preserves_cancellation()
    {
        var evaluator = new HarnessEvaluator();
        var scenario = new DelegateHarnessScenario("same", _ => Task.FromResult(new HarnessScenarioResult(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => evaluator.RunAsync([scenario, scenario]));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluator.RunAsync([scenario], cts.Token));
    }

    [Fact]
    public async Task Evaluator_reports_bounded_tool_latency_percentiles()
    {
        var samples = Enumerable.Range(1, 100).Select(value => (double)value).ToArray();
        var report = await new HarnessEvaluator().RunAsync([
            new DelegateHarnessScenario("latency", _ => Task.FromResult(new HarnessScenarioResult(
                true, ToolCalls: 100, ToolLatencyMs: samples)))
        ]);

        var latency = report.Scenarios.Single().ToolLatency;
        Assert.NotNull(latency);
        Assert.Equal(100, latency!.Samples);
        Assert.Equal(50, latency.P50Ms);
        Assert.Equal(95, latency.P95Ms);
        Assert.Equal(100, latency.MaxMs);
    }

    [Fact]
    public async Task Json_report_is_machine_readable()
    {
        Directory.CreateDirectory(_root);
        var report = await new HarnessEvaluator().RunAsync([
            new DelegateHarnessScenario("fixture", _ => Task.FromResult(new HarnessScenarioResult(true, ToolCalls: 1)))
        ]);
        var path = Path.Combine(_root, "report.json");
        HarnessEvaluator.WriteJson(report, path);
        var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
        Assert.Equal(1, json.GetProperty("passed").GetInt32());
        Assert.Equal("fixture", json.GetProperty("scenarios")[0].GetProperty("name").GetString());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
