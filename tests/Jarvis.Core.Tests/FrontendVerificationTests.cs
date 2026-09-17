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
    public void Latest_evidence_wins_and_visual_gate_adds_desktop_mobile_and_overflow_checks()
    {
        var requirement = FrontendVerificationRequirement.ForFrontend(isVisual: true);
        var evidence = BaseEvidence().Concat(new[]
        {
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveDesktop, true, "desktop 1440x900"),
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveMobile, false, "mobile clipped"),
            new FrontendEvidence(FrontendEvidenceKind.ResponsiveMobile, true, "mobile 390x844 fixed"),
            new FrontendEvidence(FrontendEvidenceKind.Overflow, true, "no clipping")
        }).ToArray();

        var result = FrontendVerificationGate.Evaluate(requirement, evidence);

        Assert.True(result.Passed);
        Assert.Empty(result.Missing);
        Assert.Empty(result.Failed);
    }

    public static FrontendEvidence[] BaseEvidence() =>
    [
        new(FrontendEvidenceKind.TargetIdentity, true, "URL/title matched"),
        new(FrontendEvidenceKind.RenderedDom, true, "rendered DOM present"),
        new(FrontendEvidenceKind.FrameworkOverlay, true, "no framework overlay"),
        new(FrontendEvidenceKind.ConsoleHealth, true, "no unexpected console errors/warnings"),
        new(FrontendEvidenceKind.Screenshot, true, "screenshot captured", "shot.png"),
        new(FrontendEvidenceKind.Interaction, true, "primary interaction changed expected state")
    ];
}
