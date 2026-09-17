using Jarvis.Agent.Core.Evaluation;

namespace Jarvis.Core.Tests;

public sealed class CodingHarnessScenarioTests
{
    [Fact]
    public void Codex_parity_suite_contains_frontend_and_backend_regression_scenarios()
    {
        var scenarios = CodingHarnessScenarios.All;

        Assert.True(scenarios.Count >= 6);
        Assert.Contains(scenarios, scenario => scenario.Id == "fe.modal-repair" && scenario.RequiresBrowser && scenario.RequiresInteraction);
        Assert.Contains(scenarios, scenario => scenario.Id == "fe.responsive-clipping" && scenario.RequiresVisual);
        Assert.Contains(scenarios, scenario => scenario.Id == "be.regression" && scenario.RequiresTests && !scenario.RequiresBrowser);
    }

    [Fact]
    public void Scenario_fails_when_required_engineering_evidence_is_missing()
    {
        var scenario = CodingHarnessScenarios.All.Single(item => item.Id == "fe.api-error-state");
        var evidence = new EngineeringEvidence(
            BuildPassed: true,
            TestsPassed: true,
            BrowserPassed: false,
            ConsolePassed: true,
            InteractionPassed: false,
            VisualPassed: true,
            CorrectiveLoops: 1,
            EvidenceCount: 4);

        var result = CodingHarnessEvaluator.Evaluate(scenario, evidence);

        Assert.False(result.Passed);
        Assert.Contains("browser", result.MissingOrFailed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("interaction", result.MissingOrFailed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scenario_passes_when_all_required_evidence_is_present()
    {
        var scenario = CodingHarnessScenarios.All.Single(item => item.Id == "fe.responsive-clipping");
        var evidence = EngineeringEvidence.Passed(correctiveLoops: 2, evidenceCount: 9);

        var result = CodingHarnessEvaluator.Evaluate(scenario, evidence);

        Assert.True(result.Passed);
        Assert.Empty(result.MissingOrFailed);
    }

    [Fact]
    public void Aggregate_reports_quality_rates_and_repair_cost()
    {
        var results = new[]
        {
            new CodingHarnessResult("one", true, new EngineeringEvidence(true, true, true, true, true, true, 1, 8), []),
            new CodingHarnessResult("two", false, new EngineeringEvidence(true, false, false, true, false, false, 3, 3), ["tests", "browser"])
        };

        var metrics = HarnessEvaluator.EvaluateEngineering(results);

        Assert.Equal(2, metrics.ScenarioTotal);
        Assert.Equal(0.5, metrics.SuccessRate);
        Assert.Equal(1.0, metrics.BuildPassRate);
        Assert.Equal(0.5, metrics.TestPassRate);
        Assert.Equal(0.5, metrics.BrowserPassRate);
        Assert.Equal(2.0, metrics.AverageCorrectiveLoops);
        Assert.Equal(5.5, metrics.AverageEvidenceCount);
    }
}
