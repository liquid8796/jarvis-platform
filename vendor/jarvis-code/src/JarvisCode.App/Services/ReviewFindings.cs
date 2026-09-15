namespace JarvisCode.App.Services;

/// <summary>Where one finding stands, in the reference's own three states.</summary>
public enum ReviewFindingState
{
    Open,
    Fixing,
    Addressed,
}

/// <summary>One finding the review reported, as the diff pane's stepper walks it.</summary>
public sealed class ReviewFinding
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public int? Line { get; init; }
    public required string Summary { get; init; }
    public string? Detail { get; init; }
    public ReviewFindingState State { get; set; } = ReviewFindingState.Open;
}

/// <summary>
/// The findings a review reported, per session. The reference keeps them beside the diff
/// so its stepper can walk them, apply them, and re-run the review; this is the same
/// store, filled by the ReportFindings tool and read by the Changes pane.
/// </summary>
public static class ReviewFindingsStore
{
    private static readonly Dictionary<string, List<ReviewFinding>> Findings =
        new(StringComparer.Ordinal);

    /// <summary>Raised with the session id whose findings changed.</summary>
    public static event EventHandler<string>? Changed;

    public static IReadOnlyList<ReviewFinding> Get(string sessionId) =>
        Findings.TryGetValue(sessionId, out var list) ? list : [];

    public static void Set(string sessionId, IEnumerable<ReviewFinding> findings)
    {
        Findings[sessionId] = [.. findings];
        Changed?.Invoke(null, sessionId);
    }

    public static void Clear(string sessionId)
    {
        Findings.Remove(sessionId);
        Changed?.Invoke(null, sessionId);
    }

    /// <summary>The reference's markReviewFindingsFixing: every open finding is being fixed.</summary>
    public static void MarkFixing(string sessionId)
    {
        foreach (var finding in Get(sessionId).Where(f => f.State == ReviewFindingState.Open))
        {
            finding.State = ReviewFindingState.Fixing;
        }

        Changed?.Invoke(null, sessionId);
    }

    /// <summary>The turn finished: everything that was being fixed now counts as addressed.</summary>
    public static void MarkAddressed(string sessionId)
    {
        foreach (var finding in Get(sessionId).Where(f => f.State == ReviewFindingState.Fixing))
        {
            finding.State = ReviewFindingState.Addressed;
        }

        Changed?.Invoke(null, sessionId);
    }
}

/// <summary>
/// The findings stepper's own wording and button states, ported from the reference's fC
/// (ion-dist chunk c360a9e1c-DUoNQd2W.js at the "Apply fixes" literal).
/// </summary>
public static class ReviewFindingsPresentation
{
    /// <summary>The status line: applying, applied, or which finding of how many.</summary>
    public static string Status(IReadOnlyList<ReviewFinding> findings, int current)
    {
        var fixing = findings.Count(f => f.State == ReviewFindingState.Fixing);
        var addressed = findings.Count(f => f.State == ReviewFindingState.Addressed);
        var open = findings.Count - fixing - addressed;
        if (fixing > 0 && open == 0)
        {
            return "Applying fixes…";
        }

        if (findings.Count > 0 && addressed == findings.Count)
        {
            return "Fixes applied";
        }

        return $"Finding {current + 1} of {findings.Count}";
    }

    /// <summary>Whether every finding has been addressed — the bar then offers a re-run.</summary>
    public static bool AllAddressed(IReadOnlyList<ReviewFinding> findings) =>
        findings.Count > 0 && findings.All(f => f.State == ReviewFindingState.Addressed);

    /// <summary>The reference shows the arrows only once there is more than one finding.</summary>
    public static bool ShowArrows(IReadOnlyList<ReviewFinding> findings) => findings.Count > 1;

    /// <summary>"Apply fixes" appears while something is still open and nothing is applied.</summary>
    public static bool ShowApplyFixes(IReadOnlyList<ReviewFinding> findings) =>
        !AllAddressed(findings) && findings.Any(f => f.State == ReviewFindingState.Open);

    /// <summary>The message Apply fixes sends: one line per finding, file, line and summary.</summary>
    public static string ApplyFixesMessage(IReadOnlyList<ReviewFinding> findings)
    {
        var lines = new List<string>();
        foreach (var finding in findings)
        {
            var where = finding.Line is { } line ? $"{finding.File}:{line}" : finding.File;
            lines.Add(finding.Detail is { Length: > 0 } detail
                ? $"- {where} — {finding.Summary}\n  {detail}"
                : $"- {where} — {finding.Summary}");
        }

        return "Apply these review findings:\n" + string.Join("\n", lines);
    }
}
