using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class GitBarExtraRowTests
{
    private static RelatedPr Row(int number, PrDisplayState state = PrDisplayState.Open) =>
        new(number, $"https://github.com/o/r/pull/{number}", state, $"branch-{number}");

    [Fact]
    public void RankIsTheReferencesOrder()
    {
        Assert.Equal(0, GitBarPresentation.StateRank(PrDisplayState.Open));
        Assert.Equal(1, GitBarPresentation.StateRank(PrDisplayState.Draft));
        Assert.Equal(2, GitBarPresentation.StateRank(PrDisplayState.Queued));
        Assert.Equal(3, GitBarPresentation.StateRank(PrDisplayState.Merged));
        Assert.Equal(4, GitBarPresentation.StateRank(PrDisplayState.Closed));
    }

    [Fact]
    public void TheDerivedOpenStatesRankWithOpen()
    {
        Assert.Equal(0, GitBarPresentation.StateRank(PrDisplayState.Approved));
        Assert.Equal(0, GitBarPresentation.StateRank(PrDisplayState.ChangesRequested));
        Assert.Equal(0, GitBarPresentation.StateRank(PrDisplayState.Conflicting));
    }

    [Fact]
    public void AnExtraRowIsMergedOrView()
    {
        Assert.Equal(PrMode.Merged, GitBarPresentation.ExtraMode(PrDisplayState.Merged));
        Assert.Equal(PrMode.View, GitBarPresentation.ExtraMode(PrDisplayState.Closed));
        Assert.Equal(PrMode.View, GitBarPresentation.ExtraMode(PrDisplayState.Open));
    }

    [Fact]
    public void TheBranchesOwnPullRequestIsNotRepeated()
    {
        var current = new PrInfo(7, "u", PrDisplayState.Open);
        var rows = GitBarPresentation.ExtraRows(current, [Row(7), Row(8)]);
        Assert.Equal([8], rows.Select(r => r.Number));
    }

    [Fact]
    public void DismissedRowsAreDropped()
    {
        var rows = GitBarPresentation.ExtraRows(null, [Row(1), Row(2)], dismissed: [2]);
        Assert.Equal([1], rows.Select(r => r.Number));
    }

    [Fact]
    public void RowsAreDeduplicatedByNumber()
    {
        var rows = GitBarPresentation.ExtraRows(null, [Row(3), Row(3)]);
        Assert.Single(rows);
    }

    [Fact]
    public void RowsSortByStateThenByNumberDescending()
    {
        var rows = GitBarPresentation.ExtraRows(null,
        [
            Row(1, PrDisplayState.Merged),
            Row(2, PrDisplayState.Open),
            Row(9, PrDisplayState.Open),
            Row(4, PrDisplayState.Draft),
        ]);
        Assert.Equal([9, 2, 4, 1], rows.Select(r => r.Number));
    }

    [Fact]
    public void AtMostFiveRowsAreKept()
    {
        var rows = GitBarPresentation.ExtraRows(null, Enumerable.Range(1, 9).Select(n => Row(n)));
        Assert.Equal(GitBarPresentation.MaxExtraRows, rows.Count);
    }

    [Fact]
    public void AStackParentIsABaseThatIsNeitherTheBranchNorTheRepoBase()
    {
        Assert.True(GitBarPresentation.HasStackParent("feature-a", "feature-b", "main"));
        Assert.False(GitBarPresentation.HasStackParent("main", "feature-b", "main"));
        Assert.False(GitBarPresentation.HasStackParent("feature-b", "feature-b", "main"));
        Assert.False(GitBarPresentation.HasStackParent(null, "feature-b", "main"));
    }

    [Fact]
    public void ABranchThatIsTheRepoBaseHasNoStack()
    {
        // The reference's own guard: baseBranch === branch rules the row out.
        Assert.False(GitBarPresentation.HasStackParent("feature-a", "main", "main"));
    }

    [Fact]
    public void OverflowShowsEverythingWhileItFits()
    {
        Assert.Equal((3, 0), GitBarPresentation.Overflow(3, 5, expanded: false));
        Assert.Equal((5, 2), GitBarPresentation.Overflow(7, 5, expanded: false));
        Assert.Equal((7, 0), GitBarPresentation.Overflow(7, 5, expanded: true));
    }

    [Fact]
    public void RecordingASessionsPullRequestKeepsItsStateCurrent()
    {
        var stored = new List<SessionPullRequest>();
        Assert.True(SessionPullRequestLog.Record(
            stored, new PrInfo(1, "u", PrDisplayState.Open, BaseRefName: "main"), "repo", "feature"));
        Assert.False(SessionPullRequestLog.Record(
            stored, new PrInfo(1, "u", PrDisplayState.Open, BaseRefName: "main"), "repo", "feature"));
        Assert.True(SessionPullRequestLog.Record(
            stored, new PrInfo(1, "u", PrDisplayState.Merged, BaseRefName: "main"), "repo", "feature"));
        Assert.Single(stored);
        Assert.Equal("merged", stored[0].State);
    }

    [Fact]
    public void OnlyThisRepositorysRowsAreRelated()
    {
        var stored = new List<SessionPullRequest>
        {
            new() { Number = 1, Repo = "a", State = "open", Branch = "x" },
            new() { Number = 2, Repo = "b", State = "open", Branch = "y" },
            new() { Number = 3, Repo = "a", State = "open", Branch = "z", Dismissed = true },
        };
        Assert.Equal([1], SessionPullRequestLog.Related(stored, "A").Select(r => r.Number));
    }

    [Fact]
    public void StateNamesRoundTrip()
    {
        foreach (var state in new[]
                 {
                     PrDisplayState.Open, PrDisplayState.Draft, PrDisplayState.Queued,
                     PrDisplayState.Merged, PrDisplayState.Closed,
                 })
        {
            Assert.Equal(state, SessionPullRequestLog.StateOf(SessionPullRequestLog.StateName(state)));
        }
    }
}
