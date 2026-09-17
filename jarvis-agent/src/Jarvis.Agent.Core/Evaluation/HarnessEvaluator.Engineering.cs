namespace Jarvis.Agent.Core.Evaluation;

public sealed partial class HarnessEvaluator
{
    /// <summary>
    /// Evaluates coding-harness engineering evidence separately from transport/tool throughput metrics.
    /// The additive entrypoint keeps historical HarnessEvaluator metrics stable while enabling
    /// release-over-release comparison of build/test/rendered/interaction/visual quality.
    /// </summary>
    public static CodingHarnessMetrics EvaluateEngineering(IReadOnlyList<CodingHarnessResult> results) =>
        CodingHarnessEvaluator.Aggregate(results);

    public static CodingHarnessMetrics EvaluateEngineering(
        IReadOnlyList<(CodingHarnessScenario Scenario, EngineeringEvidence Evidence)> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return CodingHarnessEvaluator.Aggregate(runs
            .Select(run => CodingHarnessEvaluator.Evaluate(run.Scenario, run.Evidence))
            .ToArray());
    }
}
