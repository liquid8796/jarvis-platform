using System.Diagnostics;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Evaluation;

public sealed record HarnessScenarioResult(
    bool Success,
    int ToolCalls = 0,
    int UnauthorizedRejections = 0,
    string? Notes = null,
    IReadOnlyList<double>? ToolLatencyMs = null);

public sealed record HarnessLatencyMetric(int Samples, double P50Ms, double P95Ms, double MaxMs);

public sealed record HarnessScenarioMetric(
    string Name,
    bool Success,
    long DurationMs,
    int ToolCalls,
    int UnauthorizedRejections,
    string? Notes,
    HarnessLatencyMetric? ToolLatency = null,
    double ToolCallsPerSecond = 0);

public sealed record HarnessEvaluationReport(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<HarnessScenarioMetric> Scenarios)
{
    public int Passed => Scenarios.Count(s => s.Success);
    public int Failed => Scenarios.Count - Passed;
    public int ToolCalls => Scenarios.Sum(s => s.ToolCalls);
    public int UnauthorizedRejections => Scenarios.Sum(s => s.UnauthorizedRejections);
    public int ToolLatencySamples => Scenarios.Sum(s => s.ToolLatency?.Samples ?? 0);
    public long DurationMs => Math.Max(0, (long)(FinishedAt - StartedAt).TotalMilliseconds);
}

public interface IAgentHarnessScenario
{
    string Name { get; }
    Task<HarnessScenarioResult> RunAsync(CancellationToken cancellationToken);
}

public sealed class DelegateHarnessScenario(string name, Func<CancellationToken, Task<HarnessScenarioResult>> run) : IAgentHarnessScenario
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Scenario name is required.", nameof(name)) : name;
    public Task<HarnessScenarioResult> RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
}

/// <summary>
/// Offline deterministic scenario runner. It records bounded security, throughput and
/// latency metrics; it does not grant permissions or execute tools by itself.
/// </summary>
public sealed class HarnessEvaluator
{
    public async Task<HarnessEvaluationReport> RunAsync(IEnumerable<IAgentHarnessScenario> scenarios, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        var items = scenarios.ToArray();
        if (items.Length is < 1 or > 64) throw new ArgumentException("Evaluation must contain 1..64 scenarios.", nameof(scenarios));
        if (items.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Evaluation scenario names must be unique.", nameof(scenarios));
        var started = DateTimeOffset.UtcNow;
        var metrics = new List<HarnessScenarioMetric>(items.Length);
        foreach (var scenario in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await scenario.RunAsync(cancellationToken).ConfigureAwait(false);
                watch.Stop();
                var toolCalls = Math.Max(0, result.ToolCalls);
                metrics.Add(new(
                    scenario.Name,
                    result.Success,
                    watch.ElapsedMilliseconds,
                    toolCalls,
                    Math.Max(0, result.UnauthorizedRejections),
                    Clip(result.Notes),
                    Latency(result.ToolLatencyMs),
                    toolCalls / Math.Max(0.001, watch.Elapsed.TotalSeconds)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                watch.Stop();
                metrics.Add(new(scenario.Name, false, watch.ElapsedMilliseconds, 0, 0,
                    "Scenario exception: " + ex.GetType().Name));
            }
        }
        return new(started, DateTimeOffset.UtcNow, metrics);
    }

    public static void WriteJson(HarnessEvaluationReport report, string path)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(report, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
    }

    private static HarnessLatencyMetric? Latency(IReadOnlyList<double>? samples)
    {
        if (samples is null || samples.Count == 0) return null;
        if (samples.Count > 10_000 || samples.Any(value => double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 600_000))
            throw new InvalidDataException("Tool latency samples must contain at most 10,000 finite values between 0 and 600000 ms.");
        var sorted = samples.Order().ToArray();
        return new(sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[^1]);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Count));
        return sorted[rank - 1];
    }

    private static string? Clip(string? value) => value is null || value.Length <= 2000 ? value : value[..2000];
}