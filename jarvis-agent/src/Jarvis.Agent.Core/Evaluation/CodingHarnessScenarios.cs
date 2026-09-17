namespace Jarvis.Agent.Core.Evaluation;

public sealed record EngineeringEvidence(
    bool BuildPassed,
    bool TestsPassed,
    bool BrowserPassed,
    bool ConsolePassed,
    bool InteractionPassed,
    bool VisualPassed,
    int CorrectiveLoops,
    int EvidenceCount)
{
    public static EngineeringEvidence Passed(int correctiveLoops = 0, int evidenceCount = 6) =>
        new(true, true, true, true, true, true, correctiveLoops, evidenceCount);
}

public sealed record CodingHarnessScenario(
    string Id,
    string Description,
    bool RequiresBuild = true,
    bool RequiresTests = true,
    bool RequiresBrowser = false,
    bool RequiresConsole = false,
    bool RequiresInteraction = false,
    bool RequiresVisual = false);

public sealed record CodingHarnessResult(
    string ScenarioId,
    bool Passed,
    EngineeringEvidence Evidence,
    IReadOnlyList<string> MissingOrFailed);

public sealed record CodingHarnessMetrics(
    int ScenarioTotal,
    double SuccessRate,
    double BuildPassRate,
    double TestPassRate,
    double BrowserPassRate,
    double ConsolePassRate,
    double InteractionPassRate,
    double VisualPassRate,
    double AverageCorrectiveLoops,
    double AverageEvidenceCount);

/// <summary>
/// Stable engineering scenarios used to compare Jarvis coding-harness releases on evidence quality,
/// not only throughput and tool latency.
/// </summary>
public static class CodingHarnessScenarios
{
    public static IReadOnlyList<CodingHarnessScenario> All { get; } =
    [
        new("fe.modal-repair", "Repair a broken modal flow and prove the primary interaction/post-state.",
            RequiresBrowser: true, RequiresConsole: true, RequiresInteraction: true, RequiresVisual: true),
        new("fe.responsive-clipping", "Repair desktop/mobile responsive clipping and prove visual fidelity.",
            RequiresBrowser: true, RequiresConsole: true, RequiresInteraction: true, RequiresVisual: true),
        new("fe.api-error-state", "Repair an API error path and prove the rendered error/retry interaction.",
            RequiresBrowser: true, RequiresConsole: true, RequiresInteraction: true),
        new("fe.stale-loading", "Repair stale loading UI and prove the transition to the expected rendered state.",
            RequiresBrowser: true, RequiresConsole: true, RequiresInteraction: true),
        new("fe.visual-regression", "Repair a reference-driven visual regression with a resolved fidelity ledger.",
            RequiresBrowser: true, RequiresConsole: true, RequiresInteraction: true, RequiresVisual: true),
        new("be.regression", "Repair a backend regression while preserving focused and broader tests.",
            RequiresBrowser: false, RequiresConsole: false, RequiresInteraction: false, RequiresVisual: false)
    ];
}

public static class CodingHarnessEvaluator
{
    public static CodingHarnessResult Evaluate(CodingHarnessScenario scenario, EngineeringEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.CorrectiveLoops < 0) throw new ArgumentOutOfRangeException(nameof(evidence), "Corrective loops cannot be negative.");
        if (evidence.EvidenceCount < 0) throw new ArgumentOutOfRangeException(nameof(evidence), "Evidence count cannot be negative.");

        var missing = new List<string>();
        if (scenario.RequiresBuild && !evidence.BuildPassed) missing.Add("build");
        if (scenario.RequiresTests && !evidence.TestsPassed) missing.Add("tests");
        if (scenario.RequiresBrowser && !evidence.BrowserPassed) missing.Add("browser");
        if (scenario.RequiresConsole && !evidence.ConsolePassed) missing.Add("console");
        if (scenario.RequiresInteraction && !evidence.InteractionPassed) missing.Add("interaction");
        if (scenario.RequiresVisual && !evidence.VisualPassed) missing.Add("visual");
        return new(scenario.Id, missing.Count == 0, evidence, missing);
    }

    public static CodingHarnessMetrics Aggregate(IReadOnlyList<CodingHarnessResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new(
            results.Count,
            Rate(results, result => result.Passed),
            Rate(results, result => result.Evidence.BuildPassed),
            Rate(results, result => result.Evidence.TestsPassed),
            Rate(results, result => result.Evidence.BrowserPassed),
            Rate(results, result => result.Evidence.ConsolePassed),
            Rate(results, result => result.Evidence.InteractionPassed),
            Rate(results, result => result.Evidence.VisualPassed),
            results.Average(result => result.Evidence.CorrectiveLoops),
            results.Average(result => result.Evidence.EvidenceCount));
    }

    private static double Rate(IReadOnlyList<CodingHarnessResult> results, Func<CodingHarnessResult, bool> predicate) =>
        results.Count(result => predicate(result)) / (double)results.Count;
}
