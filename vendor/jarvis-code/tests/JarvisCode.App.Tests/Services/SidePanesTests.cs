using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The pane titles and icons (the reference's qH and NP), its default keymap (LI), and
/// the pieces of pane chrome whose wording is the reference's own.
/// </summary>
public class SidePanesTests
{
    [Theory]
    [InlineData(SidePanes.Preview, "Browser")]
    [InlineData(SidePanes.Artifact, "Artifacts")]
    [InlineData(SidePanes.Diff, "Changes")]
    [InlineData(SidePanes.Terminal, "Terminal")]
    [InlineData(SidePanes.File, "File")]
    [InlineData(SidePanes.FileBrowser, "Files")]
    [InlineData(SidePanes.Plan, "Plan")]
    [InlineData(SidePanes.BackgroundTasks, "Background tasks")]
    [InlineData(SidePanes.Subagent, "Agent")]
    [InlineData(SidePanes.Session, "Session")]
    [InlineData(SidePanes.Transcript, "Transcript")]
    [InlineData(SidePanes.RunHistory, "Runs")]
    [InlineData(SidePanes.PullRequest, "Pull request")]
    public void EveryPaneCarriesTheReferenceTitle(string pane, string title)
        => Assert.Equal(title, SidePanes.Title(pane));

    [Theory]
    [InlineData(SidePanes.Plan, "ChecklistGlyph")]
    [InlineData(SidePanes.RunHistory, "ClockGlyph")]
    [InlineData(SidePanes.PullRequest, "GitPullRequestGlyph")]
    [InlineData(SidePanes.BackgroundTasks, "AgentsSimpleGlyph")]
    [InlineData(SidePanes.FileBrowser, "FolderGlyph")]
    public void ThePaneIconsFollowTheReferenceMap(string pane, string glyph)
        => Assert.Equal(glyph, SidePanes.Icon(pane));

    [Fact]
    public void APaneWithNoRailToggleHasNoIcon()
        => Assert.Null(SidePanes.Icon(SidePanes.Session));

    [Theory]
    [InlineData(PaneCommand.ToggleTerminal, "Ctrl `")]
    [InlineData(PaneCommand.ToggleDiff, "Ctrl ⇧ D")]
    [InlineData(PaneCommand.ToggleFileBrowser, "Ctrl ⇧ F")]
    [InlineData(PaneCommand.ClosePane, "Ctrl \\")]
    [InlineData(PaneCommand.ToggleSideChat, "Ctrl ;")]
    [InlineData(PaneCommand.BackgroundTasks, "Ctrl B")]
    [InlineData(PaneCommand.ToggleSelectionMode, "Ctrl ⇧ S")]
    [InlineData(PaneCommand.NewPreviewTab, "Ctrl T")]
    [InlineData(PaneCommand.CycleTranscriptMode, "Ctrl O")]
    public void TheChordsAreSpelledFromTheOneKeymap(PaneCommand command, string chord)
        => Assert.Equal(chord, PaneShortcuts.Display(command));

    [Fact]
    public void TogglePreviewShowsTheBrowserChordOnlyWhereAnExternalPreviewExists()
    {
        Assert.Equal("Ctrl ⇧ P", PaneShortcuts.Display(PaneCommand.TogglePreview));
        Assert.Equal("Ctrl ⇧ B", PaneShortcuts.Display(PaneCommand.TogglePreview, externalPreviewAvailable: true));
    }

    [Theory]
    [InlineData(ModifierKeys.Control, Key.OemTilde, PaneCommand.ToggleTerminal)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.D, PaneCommand.ToggleDiff)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.F, PaneCommand.ToggleFileBrowser)]
    [InlineData(ModifierKeys.Control, Key.T, PaneCommand.NewPreviewTab)]
    [InlineData(ModifierKeys.Control, Key.B, PaneCommand.BackgroundTasks)]
    [InlineData(ModifierKeys.Control, Key.Oem5, PaneCommand.ClosePane)]
    [InlineData(ModifierKeys.Control, Key.OemSemicolon, PaneCommand.ToggleSideChat)]
    public void APressedChordNamesItsCommand(ModifierKeys modifiers, Key key, PaneCommand command)
        => Assert.Equal(command, PaneKeymap.Match(modifiers, key));

    [Fact]
    public void AChordNobodyBoundMatchesNothing()
        => Assert.Null(PaneKeymap.Match(ModifierKeys.Control | ModifierKeys.Alt, Key.Q));

    [Fact]
    public void EveryPaneCommandIsAlsoAKeybindingsAction()
        => Assert.All(
            Enum.GetNames<PaneCommand>(),
            name => Assert.Contains(name, UserKeybindings.KnownActions));

    [Fact]
    public void ARailSpecReadsItsActivityLabelOnlyWhileSomethingIsWaiting()
    {
        var quiet = new PaneRailSpec(SidePanes.Diff, "ChangesGlyph", "Changes", "Ctrl ⇧ D", false);
        var busy = quiet with { Activity = true, ActiveLabel = SidePanes.DiffActivityLabel };
        Assert.Equal("Changes", quiet.Tooltip);
        Assert.Equal("Changes (uncommitted changes)", busy.Tooltip);
    }

    [Theory]
    [InlineData(null, 1, 1, "Terminal")]
    [InlineData(null, 1, 2, "Terminal 1")]
    [InlineData(null, 3, 4, "Terminal 3")]
    [InlineData("build", 2, 5, "build")]
    public void ATerminalTabIsNumberedOnlyOnceThereAreTwo(string? name, int ordinal, int count, string label)
        => Assert.Equal(label, TerminalTabs.Label(name, ordinal, count));

    [Fact]
    public void TheBrowserCloseTitleFollowsWhatIsBeingClosed()
    {
        Assert.Equal("Close localhost:5173?",
            BrowserCloseDialogText.Title(BrowserCloseMode.Single, "localhost:5173"));
        Assert.Equal("Close other tabs?",
            BrowserCloseDialogText.Title(BrowserCloseMode.Others, "localhost:5173"));
    }

    [Fact]
    public void TheBrowserCloseBodyHasOneSentencePerCase()
    {
        Assert.StartsWith("2 dev servers are still running.",
            BrowserCloseDialogText.Body(BrowserCloseMode.Single, 2, "web", "localhost:5173"));
        Assert.Contains("re-open this tab",
            BrowserCloseDialogText.Body(BrowserCloseMode.Single, 1, "web", "localhost:5173"));
        Assert.Contains("re-open a tab",
            BrowserCloseDialogText.Body(BrowserCloseMode.Others, 1, "web", "localhost:5173"));
        Assert.StartsWith("the dev server is still running.",
            BrowserCloseDialogText.Body(BrowserCloseMode.Single, 1, "", "localhost:5173"));
    }

    [Fact]
    public void TheStopButtonNamesHowManyServersWouldStop()
    {
        Assert.Equal("Stop server", BrowserCloseDialogText.StopLabel(1));
        Assert.Equal("Stop servers", BrowserCloseDialogText.StopLabel(2));
        Assert.Equal("Stop server and close", BrowserCloseDialogText.StopAccessibleName(1));
        Assert.Equal("Stop 3 servers and close", BrowserCloseDialogText.StopAccessibleName(3));
    }

    [Fact]
    public void TheHostLabelIsTheServerPort()
    {
        Assert.Equal("localhost:5173", BrowserCloseDialogText.HostLabel(5173));
        Assert.Equal("", BrowserCloseDialogText.HostLabel(null));
    }
}
