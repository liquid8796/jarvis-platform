using Jarvis.Agent.Core.Autonomous.Verification;

namespace Jarvis.Core.Tests;

public sealed class VisualFidelityTests
{
    [Fact]
    public void Visual_requirement_includes_fidelity_evidence()
    {
        var requirement = FrontendVerificationRequirement.ForFrontend(isVisual: true);

        Assert.Contains(FrontendEvidenceKind.VisualFidelity, requirement.Required);
    }

    [Fact]
    public void Unresolved_blocking_mismatch_fails_fidelity_evidence()
    {
        var ledger = VisualFidelityLedger.Create(
        [
            new("layout.sidebar", VisualFidelityCategory.Layout, "Sidebar width", "280px", "304px", Blocking: true),
            new("color.muted", VisualFidelityCategory.Color, "Muted label", "#667085", "#6b7280", Blocking: false)
        ]);

        var result = ledger.Evaluate();
        var evidence = ledger.ToEvidence();

        Assert.False(result.Passed);
        Assert.Single(result.UnresolvedBlocking);
        Assert.False(evidence.Success);
        Assert.Contains("layout.sidebar", evidence.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolving_blocking_mismatches_produces_passing_fidelity_evidence()
    {
        var ledger = VisualFidelityLedger.Create(
        [
            new("type.heading", VisualFidelityCategory.Typography, "Heading", "32/40 semibold", "30/38 semibold", Blocking: true),
            new("icon.search", VisualFidelityCategory.Iconography, "Search icon", "16px stroke", "16px stroke", Blocking: false, Resolved: true)
        ]).Resolve("type.heading", "Matched reference after CSS adjustment.");

        var result = ledger.Evaluate();

        Assert.True(result.Passed);
        Assert.Empty(result.UnresolvedBlocking);
        Assert.True(ledger.ToEvidence().Success);
    }

    [Fact]
    public void Ledger_is_bounded()
    {
        var entries = Enumerable.Range(0, VisualFidelityLedger.MaxEntries + 1)
            .Select(i => new VisualFidelityMismatch($"layout.{i}", VisualFidelityCategory.Layout, "target", "expected", "actual"))
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => VisualFidelityLedger.Create(entries));
    }
}
