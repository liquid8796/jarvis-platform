using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The palette's rules of order. The grouping and the filter are what a wrong
/// port gets wrong, and neither is visible in a screenshot.
/// </summary>
public class CommandPaletteTests
{
    private static PaletteAction Action(
        string key, PaletteGroup group, string description, bool queryOnly = false,
        params string[] hints) =>
        new(key, group, description, () => { }) { QueryOnly = queryOnly, SearchHints = hints };

    private static readonly PaletteAction[] Sample =
    [
        Action("toggle_sidebar", PaletteGroup.General, "Toggle sidebar"),
        Action("rename", PaletteGroup.Session, "Rename session"),
        Action("archive", PaletteGroup.Session, "Archive session", hints: "hide"),
        Action("code", PaletteGroup.Navigation, "Code"),
        Action("setting", PaletteGroup.Settings, "Settings → Extra → Themes", queryOnly: true),
    ];

    [Fact]
    public void AnEmptyQueryKeepsRegistryOrderAndDropsTheQueryOnlyRows()
    {
        var filtered = CommandPalette.Filter(Sample, "");

        Assert.Equal(
            ["toggle_sidebar", "rename", "archive", "code"],
            filtered.Select(static a => a.Key));
    }

    [Fact]
    public void AQueryOverFiftyCharactersMatchesNothing() =>
        Assert.Empty(CommandPalette.Filter(Sample, new string('x', 51)));

    [Fact]
    public void AQueryMatchesTheDescription()
    {
        var filtered = CommandPalette.Filter(Sample, "rename");

        Assert.Equal("Rename session", filtered[0].Description);
    }

    [Fact]
    public void AHiddenHintMatchesToo()
    {
        // "hide" appears in no description; it is the archive row's search hint.
        var filtered = CommandPalette.Filter(Sample, "hide");

        Assert.Contains(filtered, a => a.Key == "archive");
    }

    [Fact]
    public void AQueryOnlyRowAppearsOnceSomethingIsTyped()
    {
        var filtered = CommandPalette.Filter(Sample, "themes");

        Assert.Contains(filtered, a => a.Key == "setting");
    }

    [Fact]
    public void GroupsComeOutInTheReferencesOrderAndEmptyOnesAreDropped()
    {
        var groups = CommandPalette.GroupActions(Sample);

        Assert.Equal(["navigation", "session", "settings", "general"], groups.Select(static g => g.Id));
        Assert.Equal(["Navigation", "Session", "Settings", "General"], groups.Select(static g => g.Title));
    }

    [Fact]
    public void TheOtherGroupCarriesNoHeader()
    {
        var groups = CommandPalette.GroupActions([Action("k", PaletteGroup.Other, "Command menu")]);

        Assert.Null(groups[0].Title);
    }

    [Fact]
    public void TheDefaultModeLeadsWithQuickActionsWhenNothingIsTyped()
    {
        PaletteRow[] quick = [new PaletteActionRow(Action("new", PaletteGroup.Other, "New session"))];
        PaletteRow[] recents = [new PaletteSessionRow("s1", "A session", null, false, 0, null, () => { })];

        var groups = CommandPalette.GroupDefault(Sample, quick, recents, "");

        Assert.Equal(["send_message_group", "recents"], groups.Select(static g => g.Id));
    }

    [Fact]
    public void TypingPutsTheMatchedActionsFirst()
    {
        PaletteRow[] quick = [new PaletteActionRow(Action("new", PaletteGroup.Other, "New session"))];
        PaletteRow[] recents = [new PaletteSessionRow("s1", "A session", null, false, 0, null, () => { })];

        var groups = CommandPalette.GroupDefault(Sample, quick, recents, "rename");

        Assert.Equal(["actions", "send_message_group", "recents"], groups.Select(static g => g.Id));
        Assert.Equal("Actions", groups[0].Title);
    }

    [Fact]
    public void FlattenWalksTheRowsInRenderOrder()
    {
        PaletteRow[] quick = [new PaletteActionRow(Action("new", PaletteGroup.Other, "New session"))];
        var groups = CommandPalette.GroupDefault([], quick, [], "");

        Assert.Equal(["new"], CommandPalette.Flatten(groups).Select(static r => r.Id));
    }

    [Fact]
    public void TheRegistryHasNoDuplicateKeys()
    {
        var keys = CommandPalette.Registry.Select(static e => e.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryRegistryRowSaysSomething()
    {
        foreach (var entry in CommandPalette.Registry)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        }
    }

    [Fact]
    public void TheSettingsRowNamesItsGroupAndSection() =>
        Assert.Equal("Settings → Extra → Themes", CommandPalette.SettingsRowLabel("Extra", "Themes"));

    [Fact]
    public void TheCustomizeRowNamesItsSection() =>
        Assert.Equal("Customize → Skills", CommandPalette.CustomizeRowLabel("Skills"));

    [Theory]
    [InlineData(0, "No results found")]
    [InlineData(1, "1 result available")]
    [InlineData(4, "4 results available")]
    public void TheAnnouncementCounts(int count, string expected) =>
        Assert.Equal(expected, CommandPalette.ResultsAnnouncement(count));

    [Theory]
    [InlineData(1, "1 run")]
    [InlineData(3, "3 runs")]
    public void TheRunCountPluralises(int runs, string expected) =>
        Assert.Equal(expected, CommandPalette.RunCountLabel(runs));
}
