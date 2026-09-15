namespace JarvisCode.App.Services;

/// <summary>
/// The state the PR bar's icon shows — the reference's `prState`, which is the raw
/// GitHub state widened by the review decision and mergeability (`fh`'s eight keys).
/// </summary>
public enum PrDisplayState
{
    None,
    Open,
    Draft,
    Approved,
    ChangesRequested,
    Conflicting,
    Queued,
    Merged,
    Closed,
}

/// <summary>What the bar's trailing button does — the reference's `prMode` (`yt`).</summary>
public enum PrMode
{
    View,
    Create,
    Draft,
    Compose,
    Merged,
    Queued,
    Commit,
    None,
}

/// <summary>The CI dot's state (the reference's `hh`/`gh` keys).</summary>
public enum CiStatus
{
    Failed,
    Passed,
    Skipped,
    Unavailable,
    Pending,
    Loading,
}

/// <summary>The tint the merged / queued rows take (the reference's `ph`).</summary>
public enum GitBarAccent
{
    None,
    Purple,
    Yellow,
}

/// <summary>
/// The check runs on a PR, counted the reference's way (its `G` in `c590e764b`@4480:
/// four buckets, `total` the whole list, `allDone` when nothing is pending and
/// `allPassed` only once something actually passed).
/// </summary>
public sealed record CiCounts(int Passed, int Failed, int Pending, int Skipped = 0)
{
    public int Total { get; init; } = Passed + Failed + Pending + Skipped;

    /// <summary>Nothing is still running.</summary>
    public bool AllDone => Pending == 0;

    /// <summary>Everything finished, nothing failed, and at least one check passed.</summary>
    public bool AllPassed => AllDone && Failed == 0 && Passed > 0;
}

/// <summary>What `gh` reported about the branch's pull request.</summary>
public sealed record PrInfo(
    int Number,
    string Url,
    PrDisplayState State,
    string? Title = null,
    string? BaseRefName = null,
    CiCounts? Checks = null);

/// <summary>
/// One pull request beside the branch's own — the reference's extra bar row, whose
/// record carries exactly these five fields (its history entry's
/// <c>{number, url, state, baseRefName, branch}</c>).
/// </summary>
public sealed record RelatedPr(
    int Number,
    string Url,
    PrDisplayState State,
    string Branch,
    string? BaseRefName = null,
    bool Stacked = false);

/// <summary>Everything the bar reads about one session's repository.</summary>
public sealed record GitBarInput
{
    /// <summary>The folder is inside a git repository at all.</summary>
    public bool IsGitRepo { get; init; } = true;

    /// <summary>A GitHub remote exists — the reference's `sessionRepo.repo`.</summary>
    public bool HasGithubRepo { get; init; }

    /// <summary>Nothing on this machine can create a PR (no `gh`) — the reference's local-only bar.</summary>
    public bool LocalOnly { get; init; }

    public string RepoName { get; init; } = "";

    /// <summary>The folder the bar describes, which its repo chip's menu acts on.</summary>
    public string Cwd { get; init; } = "";

    public string BranchName { get; init; } = "";

    public string? BaseBranch { get; init; }

    public string? BranchUrl { get; init; }

    public string? RepoUrl { get; init; }

    public PrInfo? Pr { get; init; }

    public int Added { get; init; }

    public int Removed { get; init; }

    public bool HasChanges { get; init; }

    /// <summary>How far the branch is behind its base, when that is known.</summary>
    public int Behind { get; init; }

    public bool IsResponding { get; init; }

    public bool Busy { get; init; }

    public bool Dismissed { get; init; }

    /// <summary>The persisted create mode (the reference's `epitaxy-pr-create-mode`, default "create").</summary>
    public string CreateMode { get; init; } = "create";

    /// <summary>The branch has no upstream yet — the reference's `branchNotPushed`.</summary>
    public bool BranchNotPushed { get; init; }

    /// <summary>The checkout is sitting on the base branch — the reference's `St`.</summary>
    public bool OnBaseBranch { get; init; }
}

/// <summary>One row of the bar's "More PR options" menu.</summary>
public sealed record GitBarMenuItem(string Key, string Label, bool Checked, bool Disabled = false);

/// <summary>Everything the bar draws, resolved from a <see cref="GitBarInput"/>.</summary>
public sealed record GitBarRow(
    bool Visible,
    PrMode Mode,
    string ButtonLabel,
    bool ShowButton,
    bool ButtonDisabled,
    string? DisabledReason,
    string? BusyLabel,
    PrDisplayState State,
    string? StateLabel,
    GitBarAccent Accent,
    bool ShowDiff,
    int Behind,
    string RepoName,
    string BranchName,
    CiStatus? Ci,
    string? CiLabel,
    IReadOnlyList<GitBarMenuItem> MenuItems);

/// <summary>
/// The reference's PR bar as a state machine (desktop 1.40609.1.0: the renderer `bh`
/// at `ca80fca8d`@161933, its host's `yt`/`Mt`/`Lt`/`Tt`/`Ot` at ~196172-198600, the
/// label table `ih`@159130, the CI helpers `hh`/`gh`/`Nh` and the busy labels `vh`).
/// Pure so every branch is unit-testable; the row itself is drawn by
/// <c>Views/GitBarView.cs</c>.
/// </summary>
public static class GitBarPresentation
{
    public const string ViewPr = "View PR";
    public const string CreatePr = "Create PR";
    public const string CreateDraftPr = "Create draft PR";
    public const string ManuallyCreatePr = "Manually create PR";
    public const string CommitChanges = "Commit changes";
    public const string MorePrOptions = "More PR options";
    public const string ViewDiff = "View diff";
    public const string CopyPath = "Copy path";
    public const string ChangeDirectory = "Change directory";
    public const string OpenRepoOnGithub = "Open repository on GitHub";
    public const string OpenInTerminal = "Open in terminal";
    public const string CopyBranchName = "Copy branch name";
    public const string OpenBranchOnGithub = "Open branch on GitHub";
    public const string PathCopied = "Path copied to clipboard.";
    public const string BranchNameCopied = "Branch name copied to clipboard.";
    public const string ViewOnGithub = "View on GitHub";
    public const string OpenPullRequest = "Open pull request";
    public const string WorkingDirectory = "Working directory";
    public const string RepositoryControls = "Repository and pull request controls";

    /// <summary>
    /// The message the Commit button sends into the session, verbatim from the
    /// reference's `Ft` — text the model reads, so it keeps its own wording.
    /// </summary>
    public const string CommitPrompt = "Commit the working tree changes with a sensible message.";

    /// <summary>The reference's `bC` labels, shared by the state icon's tooltip and the row's pill.</summary>
    public static string StateLabel(PrDisplayState state) => state switch
    {
        PrDisplayState.None => "No pull request",
        PrDisplayState.Open => "Open",
        PrDisplayState.Draft => "Draft",
        PrDisplayState.Approved => "Approved",
        PrDisplayState.ChangesRequested => "Changes requested",
        PrDisplayState.Conflicting => "Merge conflicts",
        PrDisplayState.Queued => "Queued",
        PrDisplayState.Merged => "Merged",
        PrDisplayState.Closed => "Closed",
        _ => "",
    };

    /// <summary>The theme token the state icon paints with (the reference's `uh`).</summary>
    public static string StateBrushKey(PrDisplayState state) => state switch
    {
        PrDisplayState.Open or PrDisplayState.Approved => "GitOpenedBrush",
        PrDisplayState.Draft => "GitDraftBrush",
        PrDisplayState.Merged => "GitMergedBrush",
        PrDisplayState.Closed or PrDisplayState.ChangesRequested => "GitClosedBrush",
        PrDisplayState.Conflicting => "GitConflictingBrush",
        PrDisplayState.Queued => "GitQueuedBrush",
        _ => "Text500Brush",
    };

    /// <summary>The reference's `ph`: merged rows read purple, queued rows yellow, nothing else is accented.</summary>
    public static GitBarAccent Accent(PrDisplayState state) => state switch
    {
        PrDisplayState.Merged => GitBarAccent.Purple,
        PrDisplayState.Queued => GitBarAccent.Yellow,
        _ => GitBarAccent.None,
    };

    /// <summary>The theme token an accented trailing label paints with (the reference's `mh`).</summary>
    public static string AccentBrushKey(GitBarAccent accent) => accent switch
    {
        GitBarAccent.Purple => "GitMergedBrush",
        GitBarAccent.Yellow => "GitQueuedBrush",
        _ => "Text300Brush",
    };

    /// <summary>
    /// The CI dot, the reference's `k` inside `wh`: with no counts yet a live PR is
    /// loading and a settled one unavailable; then a failure wins, all-passed passes,
    /// all-done-with-nothing-passed is skipped, and a settled PR reads unavailable
    /// rather than pending.
    /// </summary>
    public static CiStatus? CiState(PrInfo? pr)
    {
        if (pr is null)
        {
            return null;
        }

        var terminal = pr.State is PrDisplayState.Merged or PrDisplayState.Closed;
        var counts = pr.Checks;
        if (counts is null)
        {
            return terminal ? CiStatus.Unavailable : CiStatus.Loading;
        }

        if (counts.Total == 0)
        {
            return CiStatus.Unavailable;
        }

        if (counts.Failed > 0)
        {
            return CiStatus.Failed;
        }

        if (counts.AllPassed)
        {
            return CiStatus.Passed;
        }

        if (counts.AllDone && counts.Passed == 0)
        {
            return CiStatus.Skipped;
        }

        return terminal ? CiStatus.Unavailable : CiStatus.Pending;
    }

    /// <summary>The reference's `gh`: the CI button's accessible label.</summary>
    public static string CiLabel(CiStatus status) => status switch
    {
        CiStatus.Failed => "CI: checks failing",
        CiStatus.Passed => "CI: all checks passed",
        CiStatus.Loading or CiStatus.Pending => "CI: checks in progress",
        CiStatus.Skipped => "CI: all checks skipped",
        _ => "CI: status unavailable",
    };

    /// <summary>
    /// The reference's `Nh`: the same label with a count, in its two plural forms
    /// ("CI: {count, plural, one {# check failing} other {# checks failing}}").
    /// </summary>
    public static string CiLabel(CiStatus status, CiCounts? counts)
    {
        if (status == CiStatus.Failed && counts is { Failed: > 0 })
        {
            return counts.Failed == 1 ? "CI: 1 check failing" : $"CI: {counts.Failed} checks failing";
        }

        if (status == CiStatus.Skipped && counts is not null)
        {
            var skipped = counts.Total - counts.Passed - counts.Failed - counts.Pending;
            if (skipped > 0)
            {
                return skipped == 1 ? "CI: 1 check skipped" : $"CI: {skipped} checks skipped";
            }
        }

        return CiLabel(status);
    }

    /// <summary>The reference's `vh`: what the busy button reads while it works.</summary>
    public static string? BusyLabel(PrMode mode) => mode switch
    {
        PrMode.Create or PrMode.Draft or PrMode.Compose => "Creating PR…",
        _ => null,
    };

    /// <summary>The reference's `ih`: the trailing button's text per mode.</summary>
    public static string ButtonLabel(PrMode mode) => mode switch
    {
        PrMode.View => ViewPr,
        PrMode.Create => CreatePr,
        PrMode.Draft => CreateDraftPr,
        PrMode.Compose => ManuallyCreatePr,
        PrMode.Merged => "Merged",
        PrMode.Queued => "Queued",
        PrMode.Commit => CommitChanges,
        _ => "",
    };

    /// <summary>The reference's `$m`: compose is only offered where there is a repository to compose against.</summary>
    public static string EffectiveCreateMode(string stored, bool hasRepo) => stored switch
    {
        "draft" => "draft",
        "compose" => hasRepo ? "compose" : "create",
        _ => "create",
    };

    /// <summary>The reference's `yt`: which mode the bar's trailing control is in.</summary>
    public static PrMode Mode(GitBarInput input)
    {
        if (input.Pr is null && input.LocalOnly)
        {
            return PrMode.Commit;
        }

        if (input.Pr is { } pr)
        {
            return pr.State switch
            {
                PrDisplayState.Merged => PrMode.Merged,
                PrDisplayState.Queued => PrMode.Queued,
                _ => PrMode.View,
            };
        }

        return EffectiveCreateMode(input.CreateMode, input.HasGithubRepo) switch
        {
            "draft" => PrMode.Draft,
            "compose" => PrMode.Compose,
            _ => PrMode.Create,
        };
    }

    /// <summary>
    /// The reference's `Tt`: why the button is off, checked in its order — no remote,
    /// then on the base branch, then unpushed, then a running turn, then no changes.
    /// </summary>
    public static string? DisabledReason(GitBarInput input, PrMode mode)
    {
        if (!input.HasGithubRepo && mode != PrMode.Commit)
        {
            return "Add a GitHub remote to create a pull request";
        }

        if (input.OnBaseBranch)
        {
            return "Already on the base branch — check out a feature branch first";
        }

        if (input.BranchNotPushed)
        {
            return "Branch hasn’t been pushed to GitHub";
        }

        if (input.HasChanges)
        {
            return input.IsResponding ? "Jarvis is working — wait for the turn to finish" : null;
        }

        return mode == PrMode.Commit ? "No changes to commit" : "No changes to create a PR from";
    }

    public static GitBarRow Resolve(GitBarInput input)
    {
        var pr = input.Pr;
        var state = pr?.State ?? PrDisplayState.None;
        var mode = Mode(input);
        var hasPr = pr is not null;

        var needsRemote = !input.HasGithubRepo && mode != PrMode.Commit;
        var enabled = !hasPr && input.HasChanges && !needsRemote && !input.OnBaseBranch && !input.BranchNotPushed;
        var disabled = !hasPr && (!enabled || input.IsResponding || input.Busy);

        var accent = Accent(state);
        var ci = CiState(pr);
        var createMode = EffectiveCreateMode(input.CreateMode, input.HasGithubRepo);
        var menu = !hasPr && mode != PrMode.Commit
            ? new List<GitBarMenuItem>
            {
                new("create", CreatePr, createMode == "create"),
                new("draft", CreateDraftPr, createMode == "draft"),
                new("compose", ManuallyCreatePr, createMode == "compose", Disabled: !input.HasGithubRepo),
            }
            : [];

        return new GitBarRow(
            Visible: input.IsGitRepo && !input.Dismissed && (hasPr || input.HasChanges),
            Mode: mode,
            ButtonLabel: ButtonLabel(mode),
            ShowButton: mode is not (PrMode.View or PrMode.None),
            ButtonDisabled: disabled,
            DisabledReason: disabled ? DisabledReason(input, mode) : null,
            BusyLabel: input.Busy ? BusyLabel(mode) : null,
            State: state,
            StateLabel: hasPr ? StateLabel(state) : null,
            Accent: accent,
            // The reference hides the diff, the behind count and the CI button behind
            // `!accent`: a merged or queued row carries its tinted label instead.
            ShowDiff: accent == GitBarAccent.None && (input.Added > 0 || input.Removed > 0),
            Behind: accent == GitBarAccent.None ? input.Behind : 0,
            RepoName: input.RepoName,
            BranchName: input.BranchName,
            Ci: accent == GitBarAccent.None ? ci : null,
            CiLabel: accent == GitBarAccent.None && ci is { } status ? CiLabel(status, pr?.Checks) : null,
            MenuItems: menu);
    }

    // ---- the rows beside the branch's own ----

    /// <summary>
    /// The reference's `UC`: the order the extra rows sort in. Its stored PR state is
    /// one of these five; the three this build derives at render time
    /// (approved, changes-requested, conflicting) are all open pull requests and take
    /// the open rank.
    /// </summary>
    public static int StateRank(PrDisplayState state) => state switch
    {
        PrDisplayState.Draft => 1,
        PrDisplayState.Queued => 2,
        PrDisplayState.Merged => 3,
        PrDisplayState.Closed => 4,
        _ => 0,
    };

    /// <summary>The reference's `rg`: an extra row is Merged or View, never a create mode.</summary>
    public static PrMode ExtraMode(PrDisplayState state) =>
        state == PrDisplayState.Merged ? PrMode.Merged : PrMode.View;

    /// <summary>How many extra rows the reference keeps (its history slice).</summary>
    public const int MaxExtraRows = 5;

    /// <summary>
    /// The reference's stack test: a second row is added for the current PR's base
    /// branch exactly when that base is neither the session's branch nor the
    /// repository's base branch — which is what a stacked pull request looks like.
    /// </summary>
    public static bool HasStackParent(string? prBaseRefName, string branch, string? baseBranch) =>
        prBaseRefName is { Length: > 0 } prBase &&
        !string.Equals(baseBranch, branch, StringComparison.Ordinal) &&
        !string.Equals(prBase, baseBranch, StringComparison.Ordinal) &&
        !string.Equals(prBase, branch, StringComparison.Ordinal);

    /// <summary>
    /// The extra rows, resolved the reference's way: drop the branch's own pull
    /// request and everything the user dismissed, keep the first row per number,
    /// sort by state rank and then by number descending, and cap the list.
    /// </summary>
    public static IReadOnlyList<RelatedPr> ExtraRows(
        PrInfo? current,
        IEnumerable<RelatedPr> candidates,
        IReadOnlyCollection<int>? dismissed = null)
    {
        var seen = new HashSet<int>();
        var rows = new List<RelatedPr>();
        foreach (var row in candidates)
        {
            if (row.Number == current?.Number ||
                dismissed?.Contains(row.Number) == true ||
                !seen.Add(row.Number))
            {
                continue;
            }

            rows.Add(row);
        }

        rows.Sort((a, b) =>
        {
            var rank = StateRank(a.State) - StateRank(b.State);
            return rank != 0 ? rank : b.Number - a.Number;
        });
        return rows.Count > MaxExtraRows ? rows[..MaxExtraRows] : rows;
    }

    /// <summary>
    /// The reference's row budget (`ig`): everything is shown while the bar is
    /// expanded or fits, otherwise the first <paramref name="max"/> rows are drawn
    /// and the rest are counted.
    /// </summary>
    public static (int Visible, int Hidden) Overflow(int total, int max, bool expanded) =>
        expanded || total <= max ? (total, 0) : (max, total - max);

    /// <summary>
    /// The reference truncates a long branch name from the front, keeping its last
    /// ten characters whole (`rh`'s head/tail split, which counts code points).
    /// </summary>
    public static (string Head, string Tail)? SplitBranch(string branch)
    {
        var runes = branch.EnumerateRunes().ToList();
        if (runes.Count <= 10)
        {
            return null;
        }

        var head = string.Concat(runes.Take(runes.Count - 10).Select(static r => r.ToString()));
        var tail = string.Concat(runes.Skip(runes.Count - 10).Select(static r => r.ToString()));
        return (head, tail);
    }
}
