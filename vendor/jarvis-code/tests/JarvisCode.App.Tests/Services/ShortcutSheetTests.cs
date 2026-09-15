using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The shortcut sheet and the keymap it reads its chords from. The counts and
/// the chord spellings are the reference's, measured off its own sheet.
/// </summary>
public class ShortcutSheetTests
{
    [Fact]
    public void TheSheetHasTheReferencesThreeSections() =>
        Assert.Equal(["General", "Panes", "Composer"], ShortcutSheet.Sections.Select(static s => s.Title));

    /// <summary>
    /// The reference's own sheet shows 41 rows on a build where every gate is on
    /// (1.44121.2.0 grew it from 36: it split the sidebar-tab row in two and
    /// added "Search or start a session", "Toggle sidebar", "Settings" and the
    /// changed-file row, which 1.46388 rewords to "Toggle file list in changes").
    /// One is absent here — "Toggle fast mode", gated on an IPC method this build
    /// has no equivalent for — so the count is 40, and that row is declared in the
    /// parity suite's surface manifest.
    /// </summary>
    [Fact]
    public void TheSheetHasTheRowsThisBuildCanOffer() =>
        Assert.Equal(40, ShortcutSheet.AllRows.Count);

    [Fact]
    public void EveryRowCarriesAtLeastOneChord()
    {
        foreach (var row in ShortcutSheet.AllRows)
        {
            Assert.NotEmpty(row.Chords);
            Assert.False(string.IsNullOrWhiteSpace(row.Description));
        }
    }

    [Fact]
    public void TheCommandKeyIsDrawnAsControlHere() =>
        Assert.Equal(["Ctrl", "M"], ShortcutSheet.Keys("cmd+m"));

    [Fact]
    public void ModifiersSortTheReferencesWayWithShiftAsItsGlyph() =>
        Assert.Equal(["Ctrl", "Alt", "⇧", "K"], ShortcutSheet.Keys("shift+alt+cmd+k"));

    [Theory]
    [InlineData("esc", "Esc")]
    [InlineData("tab", "Tab")]
    [InlineData("space", "Space")]
    [InlineData("enter", "Enter")]
    [InlineData("backspace", "Backspace")]
    [InlineData("up", "Up")]
    [InlineData("down", "Down")]
    [InlineData("left", "Left")]
    [InlineData("right", "Right")]
    [InlineData("f5", "F5")]
    [InlineData("b", "B")]
    [InlineData("click", "Click")]
    public void KeysAreNamedTheReferencesWay(string key, string expected) =>
        Assert.Equal([expected], ShortcutSheet.Keys(key));

    [Fact]
    public void ThePromptJumpsAreOnTheArrowKeysWithAlt()
    {
        var previous = ShortcutSheet.AllRows.Single(static r => r.Description == "Jump to previous prompt");
        var next = ShortcutSheet.AllRows.Single(static r => r.Description == "Jump to next prompt");

        Assert.Equal(["Alt", "Up"], ShortcutSheet.Keys(previous.Chords[0]));
        Assert.Equal(["Alt", "Down"], ShortcutSheet.Keys(next.Chords[0]));
    }

    [Fact]
    public void ArchiveOffersBothOfTheReferencesChords()
    {
        var archive = ShortcutSheet.AllRows.Single(static r => r.Description == "Archive session");

        Assert.Equal(2, archive.Chords.Count);
        Assert.Equal(["Ctrl", "Alt", "A"], ShortcutSheet.Keys(archive.Chords[0]));
        Assert.Equal(["Ctrl", "⇧", "Backspace"], ShortcutSheet.Keys(archive.Chords[1]));
    }

    [Fact]
    public void TheStopRowNamesThisAssistant() =>
        Assert.Contains(ShortcutSheet.AllRows, r => r.Description == "Stop Jarvis’s response");

    [Fact]
    public void TheKeymapMatchesTheChordsTheSheetAdvertises()
    {
        Assert.Equal(PaneCommand.BackgroundTasks, PaneKeymap.Match(ModifierKeys.Control, Key.B));
        Assert.Equal(PaneCommand.CycleChipLevel, PaneKeymap.Match(ModifierKeys.Shift, Key.Tab));
        Assert.Equal(PaneCommand.JumpPrevPrompt, PaneKeymap.Match(ModifierKeys.Alt, Key.Up));
        Assert.Equal(PaneCommand.JumpNextPrompt, PaneKeymap.Match(ModifierKeys.Alt, Key.Down));
        Assert.Equal(PaneCommand.NewPreviewTab, PaneKeymap.Match(ModifierKeys.Control, Key.T));
        Assert.Equal(
            PaneCommand.ToggleSelectionMode,
            PaneKeymap.Match(ModifierKeys.Control | ModifierKeys.Shift, Key.S));
    }

    [Fact]
    public void TheFirstMatchingRowWinsForAPreviewToggle() =>
        Assert.Equal(
            PaneCommand.TogglePreview,
            PaneKeymap.Match(ModifierKeys.Control | ModifierKeys.Shift, Key.B));

    [Fact]
    public void AnUnclaimedChordMatchesNothing() =>
        Assert.Null(PaneKeymap.Match(ModifierKeys.Control, Key.Q));
}
