namespace JarvisCode.App.Services;

/// <summary>
/// One pull request a session has been bound to — the reference's own bound-PR
/// record, whose five fields are what its extra bar rows read
/// (<c>{number, url, state, baseRef, branch}</c>) plus the repository it belongs
/// to and whether the user dismissed the row.
/// </summary>
public sealed class SessionPullRequest
{
    public int Number { get; set; }

    public string Url { get; set; } = "";

    /// <summary>One of the reference's five stored states: open, draft, queued, merged, closed.</summary>
    public string State { get; set; } = "";

    public string Branch { get; set; } = "";

    public string? BaseRefName { get; set; }

    /// <summary>The repository the row belongs to; a row from another one is not listed.</summary>
    public string Repo { get; set; } = "";

    public bool Dismissed { get; set; }
}

/// <summary>
/// The bar's "related" rows: the pull requests this session has already been bound
/// to. The reference records one whenever a session's branch resolves to a PR and
/// then lists every entry but the branch's current one; this does the same over
/// <see cref="UiSettings.SessionPullRequests"/>.
/// </summary>
public static class SessionPullRequestLog
{
    /// <summary>The reference keeps at most five extra rows, so nothing longer is worth storing.</summary>
    public const int MaxStored = 20;

    /// <summary>The state string stored for a display state, in the reference's own spelling.</summary>
    public static string StateName(PrDisplayState state) => state switch
    {
        PrDisplayState.Draft => "draft",
        PrDisplayState.Queued => "queued",
        PrDisplayState.Merged => "merged",
        PrDisplayState.Closed => "closed",
        _ => "open",
    };

    /// <summary>The display state a stored row reads back as.</summary>
    public static PrDisplayState StateOf(string name) => name switch
    {
        "draft" => PrDisplayState.Draft,
        "queued" => PrDisplayState.Queued,
        "merged" => PrDisplayState.Merged,
        "closed" => PrDisplayState.Closed,
        _ => PrDisplayState.Open,
    };

    /// <summary>
    /// Records the pull request the bar just saw, replacing an earlier entry for the
    /// same number so its state stays current. Returns true when the list changed.
    /// </summary>
    public static bool Record(List<SessionPullRequest> stored, PrInfo pr, string repo, string branch)
    {
        var state = StateName(pr.State);
        var existing = stored.FirstOrDefault(s => s.Number == pr.Number);
        if (existing is not null)
        {
            if (existing.State == state && existing.Branch == branch && existing.BaseRefName == pr.BaseRefName)
            {
                return false;
            }

            existing.State = state;
            existing.Branch = branch;
            existing.BaseRefName = pr.BaseRefName;
            existing.Url = pr.Url;
            return true;
        }

        stored.Add(new SessionPullRequest
        {
            Number = pr.Number,
            Url = pr.Url,
            State = state,
            Branch = branch,
            BaseRefName = pr.BaseRefName,
            Repo = repo,
        });
        if (stored.Count > MaxStored)
        {
            stored.RemoveRange(0, stored.Count - MaxStored);
        }

        return true;
    }

    /// <summary>
    /// The rows the bar draws beside the branch's own: this repository's entries,
    /// minus the dismissed ones. Ordering, the current-PR exclusion and the cap are
    /// <see cref="GitBarPresentation.ExtraRows"/>'s, as they are in the reference.
    /// </summary>
    public static IReadOnlyList<RelatedPr> Related(IEnumerable<SessionPullRequest> stored, string repo) =>
        [.. stored
            .Where(s => !s.Dismissed && string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase))
            .Select(s => new RelatedPr(s.Number, s.Url, StateOf(s.State), s.Branch, s.BaseRefName))];
}
