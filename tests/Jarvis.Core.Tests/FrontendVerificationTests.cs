using Jarvis.Agent.Core.Autonomous.Verification;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class FrontendVerificationTests
{
    [Fact]
    public void Classifier_detects_frontend_paths_and_visual_intent()
    {
        var tsx = new RemoteTaskStep
        {
            Id = "edit", ToolId = "filesystem.Edit", Stage = "EXECUTE",
            Arguments = WireJson.Element(new { file_path = "src/components/Dashboard.tsx" })
        };
        var css = tsx with { Arguments = WireJson.Element(new { file_path = "src/app.css" }) };

        var component = FrontendChangeClassifier.Classify("Fix dashboard behavior", [tsx]);
        var visual = FrontendChangeClassifier.Classify("Polish responsive dashboard layout", [css]);
        var backend = FrontendChangeClassifier.Classify("Fix database retry", [tsx with { Arguments = WireJson.Element(new { file_path = "src/Store.cs" }) }]);

        Assert.True(component.IsFrontend);
        Assert.False(component.IsVisual);
        Assert.True(visual.IsFrontend);
        Assert.True(visual.IsVisual);
        Assert.False(backend.IsFrontend);
    }

    [Fact]
    public void Base_frontend_gate_requires_rendered_identity_dom_health_screenshot_and_interaction()
    {
        var requirement = FrontendVerificationRequirement.ForFrontend(isVisual: false);
        var evidence = BaseEvidence().Where(item => item.Kind != FrontendEvidenceKind.Screenshot).ToArray();

        var result = FrontendVerificationGate.Evaluate(requirement, evidence);

        Assert.False(result.Passed);
        Assert.Contains(FrontendEvidenceKind.Screenshot, result.Missing);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public void Generic_success_assertions_and_empty_ledger_cannot_satisfy_rendered_verification()
    {
        var requirement = FrontendVerificationRequirement.ForFrontend(isVisual: true);
        var evidence = BaseEvidence().Concat(new[]
        {
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveDesktop, true, "desktop 1440x900"),
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveMobile, false, "mobile clipped"),
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveMobile, true, "mobile 390x844 fixed"),
            new FrontendEvidence(FrontendEvidenceKind.Overflow, true, "no clipping"),
            new FrontendEvidence(FrontendEvidenceKind.VisualFidelity, true, "reference fidelity matched")
        }).ToArray();

        var result = FrontendVerificationGate.Evaluate(requirement, evidence);

        Assert.False(result.Passed);
        Assert.Contains(FrontendEvidenceKind.Screenshot, result.Missing);
        Assert.Contains(FrontendEvidenceKind.VisualFidelity, result.Missing);
        Assert.False(VisualFidelityLedger.Create([]).Evaluate().Passed);
    }

    [Fact]
    public void Current_measured_failure_cannot_be_overwritten_by_later_success_or_old_revision()
    {
        var requirement = FrontendVerificationRequirement.ForFrontend(false);
        var provenance = new FrontendQaProvenance { TaskId = Guid.NewGuid().ToString("N"), VerificationRunId = "run", SourceRevision = new string('A', 64),
            TargetUrl = "http://localhost:4173/", ObservationId = "observation", Producer = "collector", CapturedAt = DateTimeOffset.UtcNow, ViewportId = "desktop" };
        var evidence = new[]
        {
            new FrontendEvidence(FrontendEvidenceKind.ConsoleHealth, false, "A real browser error", Provenance: provenance),
            new FrontendEvidence(FrontendEvidenceKind.ConsoleHealth, true, "No errors", Provenance: provenance),
            new FrontendEvidence(FrontendEvidenceKind.Screenshot, true, "Old screenshot", Provenance: provenance with { SourceRevision = new string('B',64) })
        };
        var result = FrontendVerificationGate.Evaluate(requirement, evidence, currentRunId: "run", currentSourceRevision: provenance.SourceRevision);
        Assert.Contains(FrontendEvidenceKind.ConsoleHealth, result.Failed);
        Assert.Contains(FrontendEvidenceKind.Screenshot, result.Missing);
    }

    [Fact]
    public void Classifier_observes_rendered_edits_but_does_not_treat_backend_typescript_as_frontend()
    {
        Assert.False(FrontendChangeClassifier.Classify("Repair database retry", [], ["src/store.ts"]).IsFrontend);
        Assert.True(FrontendChangeClassifier.Classify("Repair behavior", [], ["src/app.ts"], projectFiles: ["index.html", "src/app.ts"]).IsFrontend);
        Assert.True(FrontendChangeClassifier.Classify("Sửa màu giao diện", []).IsVisual);
        Assert.True(FrontendChangeClassifier.Classify("Repair behavior", [], ["src/components/Button.tsx"]).IsFrontend);
    }

    public static FrontendEvidence[] BaseEvidence() =>
    [
        new(FrontendEvidenceKind.TargetIdentity, true, "URL/title matched"),
        new(FrontendEvidenceKind.RenderedDom, true, "rendered DOM present"),
        new(FrontendEvidenceKind.FrameworkOverlay, true, "no framework overlay"),
        new(FrontendEvidenceKind.ConsoleHealth, true, "no unexpected console errors/warnings"),
        new(FrontendEvidenceKind.NetworkHealth, true, "no failed network responses"),
        new(FrontendEvidenceKind.Screenshot, true, "screenshot captured", "shot.png"),
        new(FrontendEvidenceKind.Interaction, true, "primary interaction changed expected state"),
        new(FrontendEvidenceKind.Overflow, true, "no clipping")
    ];
}
