using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Autonomous.Verification;

public enum FrontendEvidenceKind
{
    TargetIdentity,
    RenderedDom,
    FrameworkOverlay,
    ConsoleHealth,
    Screenshot,
    Interaction,
    ResponsiveDesktop,
    ResponsiveMobile,
    Overflow,
    VisualFidelity
}

public sealed record FrontendEvidence(
    FrontendEvidenceKind Kind,
    bool Success,
    string Summary,
    string? Artifact = null);

public sealed record FrontendVerificationRequirement(
    bool IsFrontend,
    bool IsVisual,
    IReadOnlySet<FrontendEvidenceKind> Required)
{
    private static readonly FrontendEvidenceKind[] BaseKinds =
    [
        FrontendEvidenceKind.TargetIdentity,
        FrontendEvidenceKind.RenderedDom,
        FrontendEvidenceKind.FrameworkOverlay,
        FrontendEvidenceKind.ConsoleHealth,
        FrontendEvidenceKind.Screenshot,
        FrontendEvidenceKind.Interaction
    ];

    public static FrontendVerificationRequirement None { get; } = new(false, false, new HashSet<FrontendEvidenceKind>());

    public static FrontendVerificationRequirement ForFrontend(bool isVisual)
    {
        var required = new HashSet<FrontendEvidenceKind>(BaseKinds);
        if (isVisual)
        {
            required.Add(FrontendEvidenceKind.ResponsiveDesktop);
            required.Add(FrontendEvidenceKind.ResponsiveMobile);
            required.Add(FrontendEvidenceKind.Overflow);
            required.Add(FrontendEvidenceKind.VisualFidelity);
        }
        return new(true, isVisual, required);
    }
}

public sealed record FrontendVerificationResult(
    bool Passed,
    IReadOnlyList<FrontendEvidenceKind> Missing,
    IReadOnlyList<FrontendEvidenceKind> Failed,
    IReadOnlyList<FrontendEvidence> Evidence)
{
    public string DescribeFailure()
    {
        if (Passed) return "Rendered frontend verification passed.";
        var parts = new List<string>();
        if (Missing.Count > 0) parts.Add("missing: " + string.Join(", ", Missing));
        if (Failed.Count > 0) parts.Add("failed: " + string.Join(", ", Failed));
        return "Rendered frontend verification incomplete (" + string.Join("; ", parts) + ").";
    }
}

public static class FrontendVerificationGate
{
    public static FrontendVerificationResult Evaluate(
        FrontendVerificationRequirement requirement,
        IReadOnlyList<FrontendEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!requirement.IsFrontend) return new(true, [], [], evidence.ToArray());

        var latest = new Dictionary<FrontendEvidenceKind, FrontendEvidence>();
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.Summary))
                throw new ArgumentException("Frontend evidence summaries must be non-empty.", nameof(evidence));
            latest[item.Kind] = item;
        }

        var missing = requirement.Required.Where(kind => !latest.ContainsKey(kind)).OrderBy(kind => kind).ToArray();
        var failed = requirement.Required.Where(kind => latest.TryGetValue(kind, out var item) && !item.Success).OrderBy(kind => kind).ToArray();
        return new(missing.Length == 0 && failed.Length == 0, missing, failed, evidence.ToArray());
    }

    public static IReadOnlyList<string> DescribeDebt(FrontendVerificationRequirement requirement, IReadOnlyList<FrontendEvidence> evidence)
    {
        var result = Evaluate(requirement, evidence);
        if (!requirement.IsFrontend || result.Passed) return [];
        var debt = new List<string>();
        if (result.Missing.Count > 0) debt.Add("Required rendered evidence still missing: " + string.Join(", ", result.Missing));
        if (result.Failed.Count > 0) debt.Add("Rendered evidence currently failing: " + string.Join(", ", result.Failed));
        return debt;
    }
}

public static class FrontendChangeClassifier
{
    private static readonly HashSet<string> FrontendExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tsx", ".jsx", ".vue", ".svelte", ".css", ".scss", ".sass", ".less", ".html", ".htm"
    };

    private static readonly HashSet<string> VisualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".scss", ".sass", ".less"
    };

    private static readonly string[] FrontendTerms =
    [
        "frontend", "front-end", " ui ", "user interface", "component", "dashboard", "modal", "dialog", "button", "form", "page", "browser"
    ];

    private static readonly string[] VisualTerms =
    [
        "layout", "responsive", "visual", "design", "style", "styling", "desktop", "mobile", "spacing", "typography", "color", "icon", "overflow", "clipping"
    ];

    public static FrontendVerificationRequirement Classify(string goal, IReadOnlyList<RemoteTaskStep> steps)
    {
        goal ??= string.Empty;
        ArgumentNullException.ThrowIfNull(steps);
        var values = steps.SelectMany(step => Strings(step.Arguments)).ToArray();
        var extensions = values.Select(value => Path.GetExtension(value.Trim())).Where(ext => !string.IsNullOrEmpty(ext)).ToArray();
        var normalizedGoal = " " + goal.ToLowerInvariant() + " ";
        var frontend = extensions.Any(FrontendExtensions.Contains) || FrontendTerms.Any(term => normalizedGoal.Contains(term, StringComparison.Ordinal));
        if (!frontend) return FrontendVerificationRequirement.None;
        var visual = extensions.Any(VisualExtensions.Contains) || VisualTerms.Any(term => normalizedGoal.Contains(term, StringComparison.Ordinal));
        return FrontendVerificationRequirement.ForFrontend(visual);
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value)) yield return value;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    foreach (var item in Strings(property.Value)) yield return item;
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    foreach (var item in Strings(child)) yield return item;
                break;
        }
    }
}
