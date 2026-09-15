using JarvisCode.App.Services;
using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The transcript affordances this round ported: the compaction row's three
/// forms, the worktree porcelain reader, the session link and the workspace
/// trust rule.
/// </summary>
public class TranscriptAffordanceTests
{
    [Fact]
    public void ACompactionSaysWhatItSavedWhenItKnowsBothSides() =>
        Assert.Equal(
            "Compacted session · saved 45k tokens",
            new CompactionItem { PreTokens = 60_000, PostTokens = 15_000 }.Text);

    [Fact]
    public void ACompactionSaysWhatItStartedFromWhenThatIsAllItKnows() =>
        Assert.Equal(
            "Compacted session · from 60k tokens",
            new CompactionItem { PreTokens = 60_000, PostTokens = 0 }.Text);

    [Fact]
    public void ACompactionThatGrewFallsBackToTheStartingCount() =>
        Assert.Equal(
            "Compacted session · from 10k tokens",
            new CompactionItem { PreTokens = 10_000, PostTokens = 12_000 }.Text);

    [Fact]
    public void ACompactionWithNoNumbersSaysOnlyThat() =>
        Assert.Equal("Compacted session", new CompactionItem().Text);

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1_500, "1.5k")]
    [InlineData(45_000, "45k")]
    [InlineData(1_200_000, "1.2M")]
    public void TokensAreFormattedTheTranscriptsWay(long tokens, string expected) =>
        Assert.Equal(expected, CompactionItem.FormatTokens(tokens));

    [Fact]
    public void PorcelainRowsBecomeThePathsTheyName() =>
        Assert.Equal(
            ["src/a.cs", "docs/b.md"],
            WorktreeChanges.Parse(" M src/a.cs\n?? docs/b.md\n"));

    [Fact]
    public void ARenameListsTheFileThatWouldBeLost() =>
        Assert.Equal(["new.cs"], WorktreeChanges.Parse("R  old.cs -> new.cs"));

    [Fact]
    public void QuotedPathsLoseTheirQuotes() =>
        Assert.Equal(["a b.cs"], WorktreeChanges.Parse("?? \"a b.cs\""));

    [Fact]
    public void EmptyPorcelainIsNoChanges() =>
        Assert.Empty(WorktreeChanges.Parse(""));

    [Fact]
    public void ASessionLinkRoundTrips()
    {
        var link = DeepLinks.ForSession("abc-123");

        Assert.Equal("jarvis-code://session/abc-123", link);
        Assert.Equal("abc-123", DeepLinks.SessionIdIn(link));
    }

    [Theory]
    [InlineData("https://example.com/session/x")]
    [InlineData("jarvis-code://other/x")]
    [InlineData("jarvis-code://session/")]
    [InlineData("not a link")]
    [InlineData("")]
    public void AnythingElseIsNotOneOfOurs(string argument) =>
        Assert.Null(DeepLinks.SessionIdIn(argument));

    [Fact]
    public void ALinkWithAnOddIdIsRefused() =>
        Assert.Null(DeepLinks.SessionIdIn("jarvis-code://session/../../etc"));

    [Fact]
    public void ASessionWithNoFolderIsNeverAskedAboutTrust() =>
        Assert.True(WorkspaceTrust.IsTrusted([], ""));

    [Fact]
    public void TrustingAFolderIsRememberedCaseInsensitively()
    {
        var trusted = new List<string>();
        WorkspaceTrust.Trust(trusted, @"C:\Projects\App");

        Assert.True(WorkspaceTrust.IsTrusted(trusted, @"c:\projects\app"));
        Assert.False(WorkspaceTrust.IsTrusted(trusted, @"C:\Projects\Other"));
    }

    [Fact]
    public void TrustingTheSameFolderTwiceStoresItOnce()
    {
        var trusted = new List<string>();
        WorkspaceTrust.Trust(trusted, @"C:\Projects\App");
        WorkspaceTrust.Trust(trusted, @"C:\Projects\App\");

        Assert.Single(trusted);
    }
}
