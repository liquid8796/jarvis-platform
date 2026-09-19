using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrontendQaSpec
{
    public string Url { get; init; } = "";
    public string? ExpectedUrl { get; init; }
    public FrontendQaLocator? Ready { get; init; }
    public IReadOnlyList<FrontendQaViewport> Viewports { get; init; } = [];
    public IReadOnlyList<FrontendQaStep> Steps { get; init; } = [];
    public int TimeoutMs { get; init; } = 15000;
    public bool FullPage { get; init; } = true;
    public bool RequireVisualReview { get; init; } = true;
    public string? ReferenceId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrontendQaViewport
{
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Mobile { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrontendQaLocator
{
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? TestId { get; init; }
    public string? Css { get; init; }
    public string? Frame { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrontendQaStep
{
    public string Action { get; init; } = "";
    public FrontendQaLocator? Locator { get; init; }
    public JsonElement? Value { get; init; }
    public string? Key { get; init; }
    public FrontendQaExpectation? Expect { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrontendQaExpectation
{
    public string Kind { get; init; } = "";
    public FrontendQaLocator? Locator { get; init; }
    public JsonElement? Value { get; init; }
}

public sealed record FrontendQaProvenance
{
    public string TaskId { get; init; } = "";
    public string SourceRevision { get; init; } = "";
    public string VerificationRunId { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string ObservationId { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public string Producer { get; init; } = "";
    public string? ViewportId { get; init; }
    public string? ScenarioId { get; init; }
}

public sealed record FrontendQaCapture
{
    public string CaptureId { get; init; } = "";
    public string ArtifactPath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    // Observed CSS viewport dimensions; a full-page PNG can have a larger pixel height.
    public int Width { get; init; }
    public int Height { get; init; }
    public string SourceRevision { get; init; } = "";
    public string VerificationRunId { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string ViewportId { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrontendQaVisualReview
{
    public string VerificationRunId { get; init; } = "";
    public string SourceRevision { get; init; } = "";
    public string Reviewer { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? ReferenceId { get; init; }
    public IReadOnlyList<FrontendQaReviewCheck> Checks { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FrontendQaReviewCheck
{
    public string CaptureId { get; init; } = "";
    public string ScreenshotSha256 { get; init; } = "";
    public string Category { get; init; } = "";
    public bool Passed { get; init; }
    public string Observation { get; init; } = "";
}

public static class FrontendQaRules
{
    public const int MaxRepairRounds = 3;
    public const int MaxWorkflowAttempts = 32;
    public static readonly string[] ReviewCategories = ["layout", "typography", "color", "iconography", "overflow", "interaction"];
    public static void Validate(FrontendQaSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!ValidUrl(spec.Url) || spec.ExpectedUrl is not null && !ValidUrl(spec.ExpectedUrl))
            throw new ArgumentException("Frontend QA requires an explicit HTTP(S) URL and a valid optional expectedUrl.");
        if (spec.TimeoutMs is < 1000 or > 30000 || spec.ReferenceId?.Length > 2048)
            throw new ArgumentException("Invalid frontend QA timeout or reference identity.");
        if (spec.Viewports is null || spec.Viewports.Count is < 1 or > 4 || spec.Steps is null || spec.Steps.Count is < 1 or > 24)
            throw new ArgumentException("Frontend QA requires 1..4 viewports and 1..24 ordered steps.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var viewport in spec.Viewports)
            if (viewport is null || string.IsNullOrWhiteSpace(viewport.Name) || viewport.Name.Length > 80 || !names.Add(viewport.Name) ||
                viewport.Width is < 240 or > 3840 || viewport.Height is < 240 or > 2160)
                throw new ArgumentException("Frontend QA viewport names must be unique; width is 240..3840 and height is 240..2160.");
        if (spec.Ready is not null) ValidateLocator(spec.Ready);
        var meaningfulInteraction = false;
        foreach (var step in spec.Steps)
        {
            if (step is null || step.Action is not ("click" or "fill" or "select" or "check" or "press" or "assert"))
                throw new ArgumentException("Unsupported frontend QA action.");
            if (step.Action != "assert")
            {
                ValidateLocator(step.Locator);
                if (step.Expect is null) throw new ArgumentException("Every frontend interaction requires an expected postcondition.");
                meaningfulInteraction = true;
            }
            if (step.Action is "fill" or "select" && step.Value?.ValueKind != JsonValueKind.String ||
                step.Action == "check" && step.Value?.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                step.Action == "press" && (string.IsNullOrWhiteSpace(step.Key) || step.Key.Length > 80))
                throw new ArgumentException("Frontend QA action value/key is invalid.");
            if (step.Value?.ValueKind == JsonValueKind.String && step.Value.Value.GetString()!.Length > 10000) throw new ArgumentException("Frontend QA action value is too long.");
            var expectation = step.Expect ?? throw new ArgumentException("An assert step requires an expectation.");
            if (expectation.Kind is not ("visible" or "hidden" or "text" or "value" or "checked" or "count" or "url"))
                throw new ArgumentException("Unsupported frontend QA assertion.");
            if (expectation.Kind != "url") ValidateLocator(expectation.Locator ?? step.Locator);
            if (expectation.Kind is "text" or "value" or "url" && expectation.Value?.ValueKind != JsonValueKind.String ||
                expectation.Kind == "checked" && expectation.Value?.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                expectation.Kind == "count" && (expectation.Value?.ValueKind != JsonValueKind.Number || !expectation.Value.Value.TryGetInt32(out var count) || count is < 0 or > 1000))
                throw new ArgumentException("Frontend QA assertion value is invalid.");
            if (expectation.Value?.ValueKind == JsonValueKind.String && expectation.Value.Value.GetString()!.Length > 10000) throw new ArgumentException("Frontend QA assertion value is too long.");
        }
        if (!meaningfulInteraction) throw new ArgumentException("Frontend QA requires a target interaction with a postcondition, not assertions alone.");
        if (JsonSerializer.SerializeToUtf8Bytes(spec, WireJson.Options).Length > 65536) throw new ArgumentException("Frontend QA specification exceeds 64 KiB.");
    }

    public static void ValidateReview(FrontendQaVisualReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (string.IsNullOrWhiteSpace(review.VerificationRunId) || review.VerificationRunId.Length > 100 ||
            !IsSha256(review.SourceRevision) || string.IsNullOrWhiteSpace(review.Reviewer) || review.Reviewer.Length > 200 ||
            string.IsNullOrWhiteSpace(review.Summary) || review.Summary.Length > 4000 || review.ReferenceId?.Length > 2048 ||
            review.Checks is null || review.Checks.Count is < 1 or > 128)
            throw new ArgumentException("Visual review requires the current run/revision, reviewer, summary and bounded image observations.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in review.Checks)
            if (check is null || string.IsNullOrWhiteSpace(check.CaptureId) || check.CaptureId.Length > 200 ||
                !IsSha256(check.ScreenshotSha256) || !ReviewCategories.Contains(check.Category) ||
                !ids.Add(check.CaptureId + "|" + check.Category) || string.IsNullOrWhiteSpace(check.Observation) || check.Observation.Length > 2000)
                throw new ArgumentException("Visual review checks require unique capture/category pairs, screenshot hashes and concrete observations.");
    }

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidUrl(string? value) => value is { Length: > 0 and <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
    private static void ValidateLocator(FrontendQaLocator? locator)
    {
        if (locator is null || new[] { locator.Role, locator.Text, locator.TestId, locator.Css }.All(string.IsNullOrWhiteSpace) ||
            new[] { locator.Role, locator.Name, locator.Text, locator.TestId, locator.Css, locator.Frame }.Any(value => value?.Length > 500))
            throw new ArgumentException("Frontend QA requires a bounded target locator.");
    }
}
