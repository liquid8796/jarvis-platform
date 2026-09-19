using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Autonomous.Verification;

public enum FrontendEvidenceKind
{
    TargetIdentity,
    RenderedDom,
    FrameworkOverlay,
    ConsoleHealth,
    NetworkHealth,
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
    string? Artifact = null,
    FrontendQaProvenance? Provenance = null,
    FrontendQaCapture? Capture = null);

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
        FrontendEvidenceKind.NetworkHealth,
        FrontendEvidenceKind.Screenshot,
        FrontendEvidenceKind.Interaction,
        FrontendEvidenceKind.Overflow
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
        IReadOnlyList<FrontendEvidence> evidence,
        FrontendQaSpec? spec = null,
        FrontendQaVisualReview? review = null,
        string? currentRunId = null,
        string? currentSourceRevision = null)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!requirement.IsFrontend) return new(true, [], [], evidence.ToArray());

        var measured = new List<FrontendEvidence>();
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.Summary))
                throw new ArgumentException("Frontend evidence summaries must be non-empty.", nameof(evidence));
            if (item.Kind == FrontendEvidenceKind.VisualFidelity) continue; // Only a bound visual review can satisfy this kind.
            if (IsMeasured(item, currentRunId, currentSourceRevision)) measured.Add(item);
        }
        var required = requirement.Required.ToHashSet();
        if (RequiresVisualReview(requirement, spec)) required.Add(FrontendEvidenceKind.VisualFidelity);
        var missing = new HashSet<FrontendEvidenceKind>();
        var failed = new HashSet<FrontendEvidenceKind>();
        foreach (var kind in required.Where(kind => kind != FrontendEvidenceKind.VisualFidelity))
        {
            var items = measured.Where(item => item.Kind == kind).ToArray();
            if (items.Length == 0) missing.Add(kind);
            if (items.Any(item => !item.Success)) failed.Add(kind); // A later assertion cannot erase an observed failure.
            if (kind == FrontendEvidenceKind.Screenshot && items.Any(item => !ValidCapture(item))) failed.Add(kind);
            if (kind == FrontendEvidenceKind.Screenshot && spec is not null && items.Any(item => item.Capture is { } capture &&
                !spec.Viewports.Any(viewport => viewport.Name == capture.ViewportId && viewport.Width == capture.Width && viewport.Height == capture.Height)))
                failed.Add(kind);
            if (spec is not null && PerViewport(kind))
                foreach (var viewport in spec.Viewports)
                    if (!items.Any(item => item.Provenance!.ViewportId == viewport.Name)) missing.Add(kind);
        }
        if (required.Contains(FrontendEvidenceKind.VisualFidelity))
        {
            if (review is null) missing.Add(FrontendEvidenceKind.VisualFidelity);
            else
            {
                try
                {
                    var taskId = measured.FirstOrDefault()?.Provenance?.TaskId ?? "";
                    ValidateReview(review, measured, taskId, currentSourceRevision ?? "", currentRunId, spec);
                    if (review.Checks.Any(check => !check.Passed)) failed.Add(FrontendEvidenceKind.VisualFidelity);
                }
                catch (ArgumentException) { failed.Add(FrontendEvidenceKind.VisualFidelity); }
            }
        }
        return new(missing.Count == 0 && failed.Count == 0, missing.OrderBy(kind => kind).ToArray(), failed.OrderBy(kind => kind).ToArray(), evidence.ToArray());
    }

    public static bool RequiresVisualReview(FrontendVerificationRequirement requirement, FrontendQaSpec? spec) =>
        requirement.IsVisual || spec?.RequireVisualReview == true || !string.IsNullOrWhiteSpace(spec?.ReferenceId);

    private static bool PerViewport(FrontendEvidenceKind kind) => kind is FrontendEvidenceKind.TargetIdentity or FrontendEvidenceKind.RenderedDom or
        FrontendEvidenceKind.FrameworkOverlay or FrontendEvidenceKind.ConsoleHealth or FrontendEvidenceKind.NetworkHealth or FrontendEvidenceKind.Screenshot or
        FrontendEvidenceKind.Interaction or FrontendEvidenceKind.Overflow;

    private static bool IsMeasured(FrontendEvidence evidence, string? currentRunId, string? currentSourceRevision)
    {
        var p = evidence.Provenance;
        return p is not null && p.Producer == "collector" && !string.IsNullOrWhiteSpace(p.TaskId) &&
            !string.IsNullOrWhiteSpace(p.ObservationId) && !string.IsNullOrWhiteSpace(currentRunId) &&
            p.VerificationRunId == currentRunId && FrontendQaRules.IsSha256(currentSourceRevision) && p.SourceRevision == currentSourceRevision &&
            p.CapturedAt != default && p.CapturedAt <= DateTimeOffset.UtcNow.AddMinutes(1) &&
            Uri.TryCreate(p.TargetUrl, UriKind.Absolute, out var target) && target.Scheme is "http" or "https";
    }

    private static bool ValidCapture(FrontendEvidence evidence)
    {
        var capture = evidence.Capture;
        var p = evidence.Provenance;
        return capture is not null && p is not null && !string.IsNullOrWhiteSpace(capture.CaptureId) &&
            Path.IsPathFullyQualified(capture.ArtifactPath) && FrontendQaRules.IsSha256(capture.Sha256) && capture.Width > 0 && capture.Height > 0 &&
            capture.SourceRevision == p.SourceRevision && capture.VerificationRunId == p.VerificationRunId &&
            capture.TargetUrl == p.TargetUrl && capture.ViewportId == p.ViewportId && capture.CapturedAt == p.CapturedAt;
    }

    public static void ValidateReview(FrontendQaVisualReview review, IReadOnlyList<FrontendEvidence> evidence,
        string taskId, string sourceRevision, string? verificationRunId = null, FrontendQaSpec? spec = null)
    {
        FrontendQaRules.ValidateReview(review);
        if (review.SourceRevision != sourceRevision || review.VerificationRunId != verificationRunId ||
            !string.Equals(review.ReferenceId, spec?.ReferenceId, StringComparison.Ordinal))
            throw new ArgumentException("Visual review must match the current source revision, verification run and reference.");
        var captures = evidence.Where(item => item.Kind == FrontendEvidenceKind.Screenshot && item.Success &&
                IsMeasured(item, verificationRunId, sourceRevision) && item.Provenance!.TaskId == taskId && ValidCapture(item))
            .Select(item => item.Capture!).DistinctBy(item => item.CaptureId).ToArray();
        if (captures.Length == 0) throw new ArgumentException("No verified screenshot captures are available for review.");
        foreach (var check in review.Checks)
            if (!captures.Any(capture => capture.CaptureId == check.CaptureId && string.Equals(capture.Sha256, check.ScreenshotSha256, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Visual review references an unknown or stale screenshot capture.");
        foreach (var capture in captures)
            foreach (var category in FrontendQaRules.ReviewCategories)
                if (!review.Checks.Any(check => check.CaptureId == capture.CaptureId && check.Category == category))
                    throw new ArgumentException("Every screenshot requires layout, typography, color, iconography, overflow and interaction observations.");
        if (spec is not null && spec.Viewports.Any(viewport => !captures.Any(capture => capture.ViewportId == viewport.Name)))
            throw new ArgumentException("Visual review is missing a required viewport capture.");
    }

    public static IReadOnlyList<string> DescribeDebt(FrontendVerificationRequirement requirement, IReadOnlyList<FrontendEvidence> evidence,
        FrontendQaSpec? spec = null, FrontendQaVisualReview? review = null, string? currentRunId = null, string? currentSourceRevision = null)
    {
        var result = Evaluate(requirement, evidence, spec, review, currentRunId, currentSourceRevision);
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
        ".tsx", ".jsx", ".vue", ".svelte", ".css", ".scss", ".sass", ".less", ".html", ".htm",
        ".cshtml", ".razor", ".astro", ".mdx", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico", ".avif", ".woff", ".woff2", ".ttf", ".otf"
    };

    private static readonly HashSet<string> VisualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".scss", ".sass", ".less", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico", ".avif", ".woff", ".woff2", ".ttf", ".otf"
    };

    private static readonly string[] FrontendTerms =
    [
        "frontend", "front-end", " ui ", "user interface", "component", "dashboard", "modal", "dialog", "button", "form", "page", "browser",
        "giao diện", "trang web", "nút bấm", "biểu mẫu", "trình duyệt"
    ];

    private static readonly string[] VisualTerms =
    [
        "layout", "responsive", "visual", "design", "style", "styling", "desktop", "mobile", "spacing", "typography", "color", "icon", "overflow", "clipping",
        "bố cục", "màu", "phông", "khoảng cách", "ảnh mẫu", "giống ảnh", "thiết kế", "điện thoại"
    ];

    public static FrontendVerificationRequirement Classify(string goal, IReadOnlyList<RemoteTaskStep> steps,
        IReadOnlyCollection<string>? changedPaths = null, FrontendQaSpec? spec = null, IReadOnlyCollection<string>? projectFiles = null)
    {
        goal ??= string.Empty;
        ArgumentNullException.ThrowIfNull(steps);
        var values = steps.SelectMany(step => Strings(step.Arguments)).Concat(changedPaths ?? []).ToArray();
        var extensions = values.Select(value => Path.GetExtension(value.Trim())).Where(ext => !string.IsNullOrEmpty(ext)).ToArray();
        var normalizedGoal = " " + goal.ToLowerInvariant() + " ";
        var renderedProject = (projectFiles ?? []).Any(path => Path.GetExtension(path).ToLowerInvariant() is ".tsx" or ".jsx" or ".html" or ".vue" or ".svelte" or ".astro" or ".cshtml" or ".razor");
        var renderedScript = values.Any(path => Path.GetExtension(path.Trim()).ToLowerInvariant() is ".js" or ".mjs" or ".cjs" or ".ts" &&
            (renderedProject || path.Replace('\\', '/').Split('/').Any(segment => segment.ToLowerInvariant() is "wwwroot" or "client" or "frontend" or "ui" or "components" or "pages" or "views")));
        var frontend = spec is not null || renderedScript || extensions.Any(FrontendExtensions.Contains) || FrontendTerms.Any(term => normalizedGoal.Contains(term, StringComparison.Ordinal));
        if (!frontend) return FrontendVerificationRequirement.None;
        var visual = spec?.RequireVisualReview == true || !string.IsNullOrWhiteSpace(spec?.ReferenceId) || extensions.Any(VisualExtensions.Contains) || VisualTerms.Any(term => normalizedGoal.Contains(term, StringComparison.Ordinal));
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
