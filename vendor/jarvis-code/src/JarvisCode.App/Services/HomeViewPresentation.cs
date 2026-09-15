namespace JarvisCode.App.Services;

/// <summary>Why a session sits in the home view's Sessions section (the reference's `bM` kinds).</summary>
public enum HomeSessionKind
{
    Blocked,
    Review,
    Unread,
    Working,
}

/// <summary>What the home view reads about one session before deciding whether to list it.</summary>
public sealed record HomeSessionInput(
    string Id,
    string Title,
    DateTimeOffset UpdatedAt,
    bool IsArchived,
    bool IsPinned,
    bool IsRunning,
    bool NeedsInput,
    bool IsUnread,
    DateTimeOffset? DismissedAt = null,
    string? RepoName = null,
    string? Summary = null);

/// <summary>One row of the Sessions section.</summary>
public sealed record HomeSessionRow(HomeSessionInput Session, HomeSessionKind Kind)
{
    /// <summary>The reference's "Untitled session" for a row with no title.</summary>
    public string Title =>
        string.IsNullOrWhiteSpace(Session.Title) ? HomeViewPresentation.UntitledSession : Session.Title;
}

/// <summary>Where a PR row files (the reference's `vM` category).</summary>
public enum HomePrCategory
{
    ReadyToMerge,
    NeedsAttention,
    InReview,
    Draft,
}

/// <summary>The tone of a PR row's pill (the reference's `gM`/`hM` keys).</summary>
public enum HomePrTone
{
    Green,
    Yellow,
    Orange,
    Red,
    Closed,
    Neutral,
}

/// <summary>A PR row's pill: its tone and text (`vM`).</summary>
public sealed record HomePrPill(HomePrTone Tone, string Label);

/// <summary>What the home view reads about one session's pull request.</summary>
public sealed record HomePrInput(
    string SessionId,
    string? SessionTitle,
    DateTimeOffset UpdatedAt,
    PrInfo Pr,
    string RepoSlug,
    string Branch);

/// <summary>One row of the Pull requests section.</summary>
public sealed record HomePrRow(HomePrInput Entry, HomePrCategory Category, HomePrPill Pill)
{
    public string Title => Entry.Pr.Title ?? Entry.SessionTitle ?? HomeViewPresentation.UntitledSession;
}

/// <summary>
/// The reference's home view — its "action center" (desktop 1.40609.1.0, ccd chunk
/// `c11959232`: the layout `LM`@86400, the selector `NM`@82419, the greeting `jM`@86800,
/// the session row `OM`@93200, the PR row `yM`@76751 with its classifier `vM`@74400,
/// and the kind pills `bM`/`kM`/`xM`@78905) as the rules that decide what it lists.
/// Pure so all of it is unit-testable; the rows are drawn by <c>Views/HomeView.xaml</c>.
/// </summary>
public static class HomeViewPresentation
{
    public const string UntitledSession = "Untitled session";
    public const string SessionsTitle = "Sessions";
    public const string ProjectsTitle = "Projects";
    public const string PullRequestsTitle = "Pull requests";
    public const string MarkAllRead = "Mark all as read";
    public const string ShowLess = "Show less";
    public const string Dismiss = "Dismiss";
    public const string DismissSession = "Dismiss session";

    public static string ShowMore(int count) => $"Show {count} more";

    public static string OpenSession(string title) => $"Open session {title}";

    /// <summary>
    /// The reference's `R`: three rows plus one for each of the 800/900/1000/1100px
    /// height thresholds the window clears. Sessions and folders stop at five (its
    /// `A = Math.min(R, 5)`); pull requests take the whole budget.
    /// </summary>
    public static int RowBudget(double windowHeight)
    {
        var rows = 3;
        foreach (var threshold in new[] { 800, 900, 1000, 1100 })
        {
            if (windowHeight >= threshold)
            {
                rows++;
            }
        }

        return rows;
    }

    public static int SessionLimit(double windowHeight) => Math.Min(RowBudget(windowHeight), 5);

    /// <summary>
    /// The greeting (`jM`): "What's up next" while every section is empty — the
    /// reference's `landingClear`, which is also what puts the usage stats card on
    /// screen — and "Welcome back" once there is something to act on.
    /// </summary>
    public static string Greeting(bool landingClear, string? name)
    {
        var known = !string.IsNullOrWhiteSpace(name);
        return landingClear
            ? known ? $"What’s up next, {name}?" : "What’s up next?"
            : known ? $"Welcome back, {name}" : "Welcome back";
    }

    /// <summary>
    /// The name the greeting uses: the local part of an e-mail address, else the
    /// first word of a display name — the reference's own derivation.
    /// </summary>
    public static string? GreetingName(string? email, string? fullName)
    {
        var trimmed = email?.Trim();
        if (!string.IsNullOrEmpty(trimmed) &&
            System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[^@\s]+@[^@\s]+$"))
        {
            return trimmed.Split('@')[0];
        }

        var first = fullName?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    /// <summary>The pill each kind carries (`xM`).</summary>
    public static string KindLabel(HomeSessionKind kind) => kind switch
    {
        HomeSessionKind.Blocked => "Needs input",
        HomeSessionKind.Review => "Ready for review",
        HomeSessionKind.Unread => "Unread",
        _ => "Working",
    };

    /// <summary>The theme token the kind's dot and text take (`kM`).</summary>
    public static string KindBrushKey(HomeSessionKind kind) => kind switch
    {
        HomeSessionKind.Blocked => "Warning100Brush",
        HomeSessionKind.Review or HomeSessionKind.Unread => "AccentBrandBrush",
        _ => "Text500Brush",
    };

    /// <summary>
    /// The reference's `NM`, its no-board branch: archived and pinned (its `starredIds`)
    /// sessions never list, a running one never lists, and a dismissal at or after the
    /// session's last activity hides it. What is left is blocked when it waits on the
    /// user and unread otherwise; blocked rows lead, each group newest first.
    /// </summary>
    public static IReadOnlyList<HomeSessionRow> AttentionRows(IEnumerable<HomeSessionInput> sessions)
    {
        var blocked = new List<HomeSessionRow>();
        var rest = new List<HomeSessionRow>();
        foreach (var s in sessions)
        {
            if (s.IsArchived || s.IsPinned)
            {
                continue;
            }

            var dismissed = s.DismissedAt is { } at && at >= s.UpdatedAt;
            if (s.IsRunning || dismissed)
            {
                continue;
            }

            if (s.NeedsInput)
            {
                blocked.Add(new HomeSessionRow(s, HomeSessionKind.Blocked));
            }
            else if (s.IsUnread)
            {
                rest.Add(new HomeSessionRow(s, HomeSessionKind.Unread));
            }
        }

        static int Newest(HomeSessionRow a, HomeSessionRow b) => b.Session.UpdatedAt.CompareTo(a.Session.UpdatedAt);
        blocked.Sort(Newest);
        rest.Sort(Newest);
        return [.. blocked, .. rest];
    }

    /// <summary>
    /// The reference's `vM`, branch for branch in its order: merged, closed and draft
    /// first, then conflicts and requested changes, then a failing check, then the
    /// queue, then an approval — which only reads "Ready to merge" once nothing is
    /// still running — then a running check count, then plain review.
    /// </summary>
    public static (HomePrCategory Category, HomePrPill Pill) Classify(PrDisplayState state, CiCounts? checks)
    {
        var failing = checks is { Failed: > 0 };
        var pending = checks is { Pending: > 0 };
        var allPassed = checks is { Failed: 0, Pending: 0 };
        return state switch
        {
            PrDisplayState.Merged => (HomePrCategory.InReview, new HomePrPill(HomePrTone.Neutral, "Merged")),
            PrDisplayState.Closed => (HomePrCategory.InReview, new HomePrPill(HomePrTone.Neutral, "Closed")),
            PrDisplayState.Draft => (HomePrCategory.Draft, new HomePrPill(HomePrTone.Neutral, "Draft")),
            PrDisplayState.Conflicting =>
                (HomePrCategory.NeedsAttention, new HomePrPill(HomePrTone.Orange, "Merge conflicts")),
            PrDisplayState.ChangesRequested =>
                (HomePrCategory.NeedsAttention, new HomePrPill(HomePrTone.Closed, "Changes requested")),
            _ when failing => (HomePrCategory.NeedsAttention, new HomePrPill(HomePrTone.Red, "CI failing")),
            PrDisplayState.Queued => (HomePrCategory.ReadyToMerge, new HomePrPill(HomePrTone.Yellow, "Queued")),
            PrDisplayState.Approved when allPassed || checks is null =>
                (HomePrCategory.ReadyToMerge, new HomePrPill(HomePrTone.Green, "Ready to merge")),
            PrDisplayState.Approved => (HomePrCategory.InReview, new HomePrPill(HomePrTone.Green, "Approved")),
            _ when pending => (HomePrCategory.InReview,
                new HomePrPill(HomePrTone.Neutral, $"CI {checks!.Passed + checks.Failed}/{checks.Total}")),
            _ => (HomePrCategory.InReview, new HomePrPill(HomePrTone.Neutral, "Ready for review")),
        };
    }

    /// <summary>
    /// The Pull requests section (`k` and `z`): only the active states and never a
    /// draft, newest session first, one row per PR url (falling back to
    /// "{repo}#{number}" when there is none), then the ready-to-merge and
    /// needs-attention rows ahead of the rest.
    /// </summary>
    public static IReadOnlyList<HomePrRow> PrRows(IEnumerable<HomePrInput> entries)
    {
        var active = entries
            .Where(static e => e.Pr.State
                is PrDisplayState.Open
                or PrDisplayState.Approved
                or PrDisplayState.ChangesRequested
                or PrDisplayState.Conflicting
                or PrDisplayState.Queued)
            .OrderByDescending(static e => e.UpdatedAt)
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<HomePrRow>();
        foreach (var entry in active)
        {
            var key = string.IsNullOrEmpty(entry.Pr.Url)
                ? $"{entry.RepoSlug.ToLowerInvariant()}#{entry.Pr.Number}"
                : entry.Pr.Url;
            if (!seen.Add(key))
            {
                continue;
            }

            var (category, pill) = Classify(entry.Pr.State, entry.Pr.Checks);
            rows.Add(new HomePrRow(entry, category, pill));
        }

        static bool Urgent(HomePrRow r) => r.Category is HomePrCategory.ReadyToMerge or HomePrCategory.NeedsAttention;
        return [.. rows.Where(Urgent), .. rows.Where(static r => !Urgent(r))];
    }

    /// <summary>The theme token a pill tone paints with (`gM`/`hM`).</summary>
    public static string ToneBrushKey(HomePrTone tone) => tone switch
    {
        HomePrTone.Green => "GitOpenedBrush",
        HomePrTone.Yellow => "GitQueuedBrush",
        HomePrTone.Orange => "GitConflictingBrush",
        HomePrTone.Red => "GitRemovedBrush",
        HomePrTone.Closed => "GitClosedBrush",
        _ => "Text500Brush",
    };

    /// <summary>The units the reference's clock steps through, largest first (its `Qd`).</summary>
    private static readonly (string Unit, long Seconds)[] Units =
    [
        ("year", 31536000),
        ("month", 2592000),
        ("week", 604800),
        ("day", 86400),
        ("hour", 3600),
        ("minute", 60),
    ];

    /// <summary>
    /// The narrow relative clock beside each row — the reference's `Gd` with
    /// <c>relativeFormat:"narrow"</c>: under a minute it reads "now", otherwise the
    /// largest unit the gap clears carries the rounded count. The wordings are
    /// <c>Intl.RelativeTimeFormat("en-US", {numeric:"auto", style:"narrow"})</c>'s own,
    /// measured by running it, which is why "yesterday" and "last wk." are here and
    /// "1d ago" and "1w ago" are not.
    /// </summary>
    public static string NarrowRelative(DateTimeOffset at, DateTimeOffset now)
    {
        // JavaScript's Math.round is half-up rather than half-to-even, and this
        // rounds a negative offset, where the two disagree.
        static long Round(double value) => (long)Math.Floor(value + 0.5);

        var seconds = Round((at - now).TotalSeconds);
        var magnitude = Math.Abs(seconds);
        if (magnitude < 60)
        {
            return "now";
        }

        var unit = "minute";
        long length = 60;
        foreach (var (candidate, secs) in Units)
        {
            if (magnitude >= secs)
            {
                (unit, length) = (candidate, secs);
                break;
            }
        }

        var count = Round((double)seconds / length);
        return Narrow(count, unit);
    }

    private static string Narrow(long count, string unit)
    {
        if (count == -1)
        {
            switch (unit)
            {
                case "day": return "yesterday";
                case "week": return "last wk.";
                case "month": return "last mo.";
                case "year": return "last yr.";
            }
        }

        if (count == 1)
        {
            switch (unit)
            {
                case "day": return "tomorrow";
                case "week": return "next wk.";
                case "month": return "next mo.";
                case "year": return "next yr.";
            }
        }

        var suffix = unit switch
        {
            "year" => "y",
            "month" => "mo",
            "week" => "w",
            "day" => "d",
            "hour" => "h",
            _ => "m",
        };
        return count < 0 ? $"{-count}{suffix} ago" : $"in {count}{suffix}";
    }
}
