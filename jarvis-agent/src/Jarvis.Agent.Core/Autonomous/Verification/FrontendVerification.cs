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
        ".cshtml", ".razor", ".astro", ".mdx"
    };

    private static readonly HashSet<string> VisualExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".scss", ".sass", ".less", ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".ico", ".avif", ".woff", ".woff2", ".ttf", ".otf"
    };

    private static readonly string[] FrontendTerms =
    [
        "frontend", "front-end", "front end", "ui", "user interface", "dashboard", "modal", "dialog", "button", "form", "web page", "web form",
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
        // Actual changes supersede argument hints. Reading an image, mentioning a source path in
        // a command, or editing backend code beside a web package does not make a task frontend.
        var values = (changedPaths ?? steps.Where(IsFileMutation).SelectMany(step => MutationPaths(step.Arguments)).ToArray())
            .Select(Normalize).Where(path => !IsTestPath(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = (projectFiles ?? []).Select(Normalize).ToArray();
        var packages = files.Where(file => file == "package.json" || file.EndsWith("/package.json", StringComparison.Ordinal))
            .Select(Parent).OrderByDescending(root => root.Length).ToArray();
        var webRoots = WebRoots(files, packages);
        var frontendPaths = values.Where(path => IsFrontendPath(path, packages, webRoots)).ToArray();
        var databaseNormalForm = ContainsTerm(goal, "normal form") &&
            (ContainsTerm(goal, "database") || ContainsTerm(goal, "sql") || ContainsTerm(goal, "relational"));
        var frontend = spec is not null || frontendPaths.Length > 0 ||
            FrontendTerms.Any(term => !(term == "form" && databaseNormalForm) && ContainsTerm(goal, term));
        if (!frontend) return FrontendVerificationRequirement.None;
        var visual = spec?.RequireVisualReview == true || !string.IsNullOrWhiteSpace(spec?.ReferenceId) ||
            frontendPaths.Any(path => VisualExtensions.Contains(Path.GetExtension(path))) || VisualTerms.Any(term => ContainsTerm(goal, term));
        return FrontendVerificationRequirement.ForFrontend(visual);
    }

    private static bool ContainsTerm(string goal, string term) => System.Text.RegularExpressions.Regex.IsMatch(goal,
        @"(?<![\p{L}\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(term) + @"(?![\p{L}\p{N}_])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('.', '/').ToLowerInvariant();
    private static bool Within(string path, string root) => root.Length == 0 || path.StartsWith(root + "/", StringComparison.Ordinal);
    private static string Parent(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : "";
    private static bool IsTestPath(string path) => path.Split('/').Any(part => part is "test" or "tests" or "__tests__" or "fixtures") ||
        path.Contains(".test.", StringComparison.Ordinal) || path.Contains(".spec.", StringComparison.Ordinal);
    private static string? Package(string path, IReadOnlyList<string> packages) => packages.FirstOrDefault(root => Within(path, root));
    private static IReadOnlyList<string> WebRoots(IReadOnlyCollection<string> files, IReadOnlyList<string> packages)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => !IsTestPath(file)))
        {
            var ext = Path.GetExtension(file);
            if (ext is ".tsx" or ".jsx" or ".vue" or ".svelte" or ".astro" or ".razor" or ".cshtml")
            {
                var package = Package(file, packages);
                if (package is not null) roots.Add(package);
                else
                {
                    var parts = file.Split('/');
                    var boundary = Array.FindIndex(parts, part => part is "src" or "wwwroot" or "client" or "frontend" or "web" or "ui" or "components" or "pages" or "views");
                    roots.Add(boundary >= 0 ? string.Join('/', parts.Take(boundary)) : Parent(file));
                }
            }
            else if (Path.GetFileName(file) == "index.html" && !file.Split('/').Any(part => part is "docs" or "samples" or "examples"))
            {
                var root = Parent(file);
                if (Path.GetFileName(root) is "public" or "src" or "wwwroot") root = Parent(root);
                roots.Add(root);
            }
        }
        return roots.ToArray();
    }

    private static bool IsFrontendPath(string path, IReadOnlyList<string> packages, IReadOnlyList<string> webRoots)
    {
        var extension = Path.GetExtension(path);
        if (FrontendExtensions.Contains(extension)) return true;
        if (extension is not (".js" or ".mjs" or ".cjs" or ".ts") && !VisualExtensions.Contains(extension)) return false;
        var parts = path.Split('/');
        var package = Package(path, packages);
        var relative = package is { Length: > 0 } ? path[(package.Length + 1)..] : path;
        if (parts.Any(part => part is "server" or "backend" or "database" or "migrations" or "workers" or "scripts" or "data" or "datasets" or "models") ||
            relative.StartsWith("api/", StringComparison.Ordinal) || relative.Contains("/app/api/", StringComparison.Ordinal) ||
            relative.StartsWith("app/api/", StringComparison.Ordinal) || relative.Contains("/pages/api/", StringComparison.Ordinal) || relative.StartsWith("pages/api/", StringComparison.Ordinal) ||
            Path.GetFileNameWithoutExtension(path) is "server" or "worker") return false;
        if (package is null && parts.Any(part => part is "wwwroot" or "client" or "frontend" or "ui" or "components" or "pages" or "views")) return true;
        return webRoots.Any(root => Within(path, root) && (package is null || Package(root + "/placeholder", packages) == package));
    }

    private static bool IsFileMutation(RemoteTaskStep step)
    {
        var id = step.ToolId.ToLowerInvariant();
        return id.StartsWith("filesystem.", StringComparison.Ordinal) &&
            (id.Contains("write", StringComparison.Ordinal) || id.Contains("edit", StringComparison.Ordinal) || id.Contains("patch", StringComparison.Ordinal));
    }

    private static IEnumerable<string> MutationPaths(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    if (property.Name is "file_path" or "path" or "filePath" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) yield return value;
                    }
                    else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        foreach (var item in MutationPaths(property.Value)) yield return item;
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    foreach (var item in MutationPaths(child)) yield return item;
                break;
        }
    }
}
