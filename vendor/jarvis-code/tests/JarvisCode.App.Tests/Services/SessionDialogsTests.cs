using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The confirm dialogs' copy: the plural forms, the ten-path cap and the draft
/// preview are all rules rather than literals, and each is the reference's.
/// </summary>
public class SessionDialogsTests
{
    [Theory]
    [InlineData(1, "Delete session?")]
    [InlineData(3, "Delete 3 sessions?")]
    public void TheDeleteTitleCounts(int count, string expected) =>
        Assert.Equal(expected, SessionDialogs.DeleteTitle(count));

    [Fact]
    public void TheDeleteBodyQuotesTheSession() =>
        Assert.Equal(
            "“Fix the parser” will be permanently deleted. This can’t be undone.",
            SessionDialogs.DeleteBody("Fix the parser"));

    [Theory]
    [InlineData(1, "This session’s worktree has 1 uncommitted change that will be permanently discarded.")]
    [InlineData(4, "This session’s worktree has 4 uncommitted changes that will be permanently discarded.")]
    public void TheUncommittedBodyCounts(int count, string expected) =>
        Assert.Equal(expected, SessionDialogs.UncommittedBody(count));

    [Fact]
    public void TheConfirmSaysAnywayOnceItKnowsWhatWouldBeLost()
    {
        Assert.Equal("Delete anyway", SessionDialogs.UncommittedConfirmLabel(SessionDialogAction.Delete));
        Assert.Equal("Archive anyway", SessionDialogs.UncommittedConfirmLabel(SessionDialogAction.Archive));
    }

    [Fact]
    public void WhileItIsStillCheckingTheConfirmIsNotDangerous()
    {
        var copy = SessionDialogs.Checking(SessionDialogAction.Delete);

        Assert.Equal("Checking for uncommitted changes…", copy.Description);
        Assert.Equal("Delete", copy.ConfirmLabel);
        Assert.False(copy.Danger);
    }

    [Fact]
    public void ThePathListStopsAtTenAndCountsTheRest()
    {
        var paths = Enumerable.Range(1, 13).Select(i => $"src/file{i}.cs").ToList();

        var listed = SessionDialogs.PathList(paths);

        Assert.Equal(11, listed.Split('\n').Length);
        Assert.EndsWith("… and 3 more", listed);
        Assert.Contains("src/file10.cs", listed);
        Assert.DoesNotContain("src/file11.cs", listed);
    }

    [Fact]
    public void AShortPathListIsShownWhole() =>
        Assert.Equal("a.cs\nb.cs", SessionDialogs.PathList(["a.cs", "b.cs"]));

    [Fact]
    public void TheUncommittedCopyCarriesTheFootnoteOnlyWhenMoreFollow()
    {
        Assert.Null(SessionDialogs.Uncommitted(SessionDialogAction.Delete, ["a"], false).Footnote);
        Assert.Equal(
            SessionDialogs.MoreSessionsFollow,
            SessionDialogs.Uncommitted(SessionDialogAction.Delete, ["a"], true).Footnote);
    }

    [Theory]
    [InlineData(0, "“Reviews” will be removed. It contains no sessions.")]
    [InlineData(1, "“Reviews” will be removed. The 1 session in it will no longer be grouped.")]
    [InlineData(5, "“Reviews” will be removed. The 5 sessions in it will no longer be grouped.")]
    public void TheDeleteGroupBodySaysWhatItReleases(int count, string expected) =>
        Assert.Equal(expected, SessionDialogs.DeleteGroupBody("Reviews", count));

    [Fact]
    public void TheDraftPreviewCollapsesWhitespaceAndCutsAtSixty()
    {
        Assert.Equal("hello there", SessionDialogs.DraftPreview("  hello\n\n  there  "));

        var long_ = new string('x', 80);
        var preview = SessionDialogs.DraftPreview(long_);
        Assert.Equal(61, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void TheDiscardDraftBodyQuotesThePreviewWhenThereIsOne()
    {
        Assert.Equal(
            "“fix the tests” hasn’t been sent. Start a session from it, or discard it?",
            SessionDialogs.DiscardDraftBody("fix the tests"));
        Assert.Equal(
            "This draft hasn’t been sent. Start a session from it, or discard it?",
            SessionDialogs.DiscardDraftBody(""));
    }

    [Theory]
    [InlineData(1, false, "This will archive 1 session older than a week. Pinned sessions are not affected.")]
    [InlineData(4, false, "This will archive 4 sessions older than a week. Pinned sessions are not affected.")]
    public void TheBulkArchiveBodyCounts(int count, bool truncated, string expected) =>
        Assert.Equal(expected, SessionDialogs.ArchiveOlderBody(count, truncated));

    [Fact]
    public void ATruncatedListingSaysSoOnTheEnd() =>
        Assert.EndsWith(
            "Not all sessions could be loaded, so some older sessions will remain.",
            SessionDialogs.ArchiveOlderBody(2, truncated: true));

    [Fact]
    public void TheBranchDialogNamesBothBranches()
    {
        Assert.Equal("Uncommitted changes on main", SessionDialogs.UncommittedOnBranchTitle("main"));
        Assert.Equal("Handle them before switching to feature.", SessionDialogs.HandleThemBody("feature"));
        Assert.Equal(
            "All uncommitted changes on main will be permanently lost.",
            SessionDialogs.DiscardConfirmBody("main"));
    }

    [Theory]
    [InlineData(1, "1 other session is using this folder. Switching branches will affect it too.")]
    [InlineData(2, "2 other sessions are using this folder. Switching branches will affect them too.")]
    public void TheOtherSessionsLinePluralises(int count, string expected) =>
        Assert.Equal(expected, SessionDialogs.OtherSessionsUsingFolder(count));

    [Fact]
    public void TheGitRequiredBodyNamesTheVariable() =>
        Assert.Contains("CLAUDE_CODE_GIT_BASH_PATH", SessionDialogs.GitRequiredBody("CLAUDE_CODE_GIT_BASH_PATH"));
}
