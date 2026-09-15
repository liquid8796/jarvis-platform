using JarvisCode.App.Services;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The three panes whose content is a model rather than a control: the Runs pane's run
/// list, the Pull request pane's badge and parsing, and the diff pane's findings stepper.
/// </summary>
public class PaneContentTests
{
    private static SessionSummary Run(string id, string? routine, int minutesAgo) =>
        new(id, id, DateTimeOffset.Now.AddMinutes(-minutesAgo), 1, "", routine);

    [Fact]
    public void TheRunsPaneListsOnlyItsOwnRoutineNewestFirst()
    {
        var rows = RunHistoryPresentation.Build(
            [Run("a", "r1", 30), Run("b", "r2", 5), Run("c", "r1", 1)],
            "r1", "c", [], [], DateTimeOffset.Now);
        Assert.Equal(["c", "a"], rows.Select(r => r.SessionId));
        Assert.True(rows[0].IsCurrent);
    }

    [Fact]
    public void ARunReadsRunningOnlyWhileItsSessionIsLive()
    {
        var rows = RunHistoryPresentation.Build(
            [Run("a", "r1", 1)], "r1", "a", ["a"], [], DateTimeOffset.Now);
        Assert.Equal(RunStatus.Running, rows[0].Status);
    }

    [Fact]
    public void ARunTheRunnerAbandonedReadsFailed()
    {
        var rows = RunHistoryPresentation.Build(
            [Run("a", "r1", 1)], "r1", "a", [], ["a"], DateTimeOffset.Now);
        Assert.Equal(RunStatus.Failed, rows[0].Status);
    }

    [Theory]
    [InlineData(RunStatus.Running, "Running")]
    [InlineData(RunStatus.Failed, "Failed")]
    [InlineData(RunStatus.Completed, "Completed")]
    public void TheStatusWordsAreTheReferenceThree(RunStatus status, string label)
        => Assert.Equal(label, RunHistoryPresentation.StatusLabel(status));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(5, "5 minutes ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(1440, "1 day ago")]
    public void TheRunLabelIsARelativeTime(int minutesAgo, string label)
    {
        var now = DateTimeOffset.Now;
        Assert.Equal(label, RunHistoryPresentation.RelativeTime(now.AddMinutes(-minutesAgo), now));
    }

    // ---- pull request ----

    [Theory]
    [InlineData("MERGED", false, null, false, PrBadge.Merged)]
    [InlineData("CLOSED", false, null, false, PrBadge.Closed)]
    [InlineData("OPEN", true, null, false, PrBadge.Draft)]
    [InlineData("OPEN", false, "CHANGES_REQUESTED", false, PrBadge.ChangesRequested)]
    [InlineData("OPEN", false, null, true, PrBadge.Conflicting)]
    [InlineData("OPEN", false, "APPROVED", false, PrBadge.Approved)]
    [InlineData("OPEN", false, null, false, PrBadge.Open)]
    [InlineData("", false, null, false, PrBadge.None)]
    public void TheBadgeResolvesTheReferenceWay(
        string state, bool draft, string? decision, bool conflicting, PrBadge expected)
        => Assert.Equal(expected, PullRequestPresentation.Resolve(state, draft, decision, conflicting));

    [Fact]
    public void ChangesRequestedWinsOverAConflict()
        => Assert.Equal(
            PrBadge.ChangesRequested,
            PullRequestPresentation.Resolve("OPEN", false, "CHANGES_REQUESTED", conflicting: true));

    [Theory]
    [InlineData(PrBadge.None, "No pull request")]
    [InlineData(PrBadge.Open, "Open")]
    [InlineData(PrBadge.Draft, "Draft")]
    [InlineData(PrBadge.Approved, "Approved")]
    [InlineData(PrBadge.ChangesRequested, "Changes requested")]
    [InlineData(PrBadge.Conflicting, "Merge conflicts")]
    [InlineData(PrBadge.Queued, "Queued")]
    [InlineData(PrBadge.Merged, "Merged")]
    [InlineData(PrBadge.Closed, "Closed")]
    public void TheBadgeLabelsAreTheReferenceNine(PrBadge badge, string label)
        => Assert.Equal(label, PullRequestPresentation.BadgeLabel(badge));

    [Fact]
    public void TheFileCountIsPluralised()
    {
        Assert.Equal("1 file", PullRequestPresentation.FileCountLabel(1));
        Assert.Equal("4 files", PullRequestPresentation.FileCountLabel(4));
    }

    [Fact]
    public void GhOutputParsesIntoTheView()
    {
        const string json = """
            {
              "number": 42,
              "title": "Add the Runs pane",
              "body": "It lists the routine's runs.",
              "author": { "login": "liquid8796" },
              "state": "OPEN",
              "isDraft": false,
              "reviewDecision": "APPROVED",
              "mergeable": "MERGEABLE",
              "updatedAt": "2026-09-01T10:00:00Z",
              "url": "https://github.com/o/r/pull/42",
              "headRefOid": "abcdef1234567890",
              "additions": 12,
              "deletions": 3,
              "files": [ { "path": "src/a.cs", "additions": 10, "deletions": 1 } ],
              "reviews": [ { "author": { "login": "someone" }, "state": "APPROVED" } ],
              "statusCheckRollup": [
                { "status": "COMPLETED", "conclusion": "SUCCESS" },
                { "status": "COMPLETED", "conclusion": "FAILURE" },
                { "status": "IN_PROGRESS", "conclusion": "" }
              ]
            }
            """;
        var view = PullRequestPresentation.Parse(json);
        Assert.NotNull(view);
        Assert.Equal(42, view!.Number);
        Assert.Equal(PrBadge.Approved, view.Badge);
        Assert.Equal("liquid8796", view.Author);
        Assert.Single(view.Files);
        Assert.Equal("someone · APPROVED", view.Reviews[0]);
        Assert.Equal(1, view.ChecksPassed);
        Assert.Equal(1, view.ChecksFailed);
        Assert.Equal(1, view.ChecksPending);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void UnusableGhOutputReadsAsNoPullRequest(string json)
        => Assert.Null(PullRequestPresentation.Parse(json));

    // ---- review findings ----

    private static ReviewFinding Finding(string id, ReviewFindingState state = ReviewFindingState.Open) =>
        new() { Id = id, File = "a.cs", Line = 1, Summary = id, State = state };

    [Fact]
    public void TheStepperCountsTheFindingItIsOn()
        => Assert.Equal("Finding 2 of 3",
            ReviewFindingsPresentation.Status([Finding("a"), Finding("b"), Finding("c")], 1));

    [Fact]
    public void TheStepperSaysApplyingWhileNothingIsOpen()
        => Assert.Equal("Applying fixes…",
            ReviewFindingsPresentation.Status([Finding("a", ReviewFindingState.Fixing)], 0));

    [Fact]
    public void TheStepperSaysAppliedOnceEveryFindingIsAddressed()
        => Assert.Equal("Fixes applied",
            ReviewFindingsPresentation.Status([Finding("a", ReviewFindingState.Addressed)], 0));

    [Fact]
    public void TheArrowsAppearOnlyWithMoreThanOneFinding()
    {
        Assert.False(ReviewFindingsPresentation.ShowArrows([Finding("a")]));
        Assert.True(ReviewFindingsPresentation.ShowArrows([Finding("a"), Finding("b")]));
    }

    [Fact]
    public void ApplyFixesShowsWhileSomethingIsStillOpen()
    {
        Assert.True(ReviewFindingsPresentation.ShowApplyFixes([Finding("a")]));
        Assert.False(ReviewFindingsPresentation.ShowApplyFixes([Finding("a", ReviewFindingState.Addressed)]));
    }

    [Fact]
    public void TheApplyMessageNamesEveryFindingAndItsLine()
    {
        var message = ReviewFindingsPresentation.ApplyFixesMessage(
            [new ReviewFinding { Id = "1", File = "a.cs", Line = 7, Summary = "off by one", Detail = "n+1" }]);
        Assert.Contains("a.cs:7 — off by one", message);
        Assert.Contains("n+1", message);
    }

    [Fact]
    public void TheStoreMovesFindingsFromOpenToFixingToAddressed()
    {
        const string session = "session-under-test";
        ReviewFindingsStore.Set(session, [Finding("a"), Finding("b")]);
        ReviewFindingsStore.MarkFixing(session);
        Assert.All(ReviewFindingsStore.Get(session), f => Assert.Equal(ReviewFindingState.Fixing, f.State));
        ReviewFindingsStore.MarkAddressed(session);
        Assert.True(ReviewFindingsPresentation.AllAddressed(ReviewFindingsStore.Get(session)));
        ReviewFindingsStore.Clear(session);
        Assert.Empty(ReviewFindingsStore.Get(session));
    }
}
