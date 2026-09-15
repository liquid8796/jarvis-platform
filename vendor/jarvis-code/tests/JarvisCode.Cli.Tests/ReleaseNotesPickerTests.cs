using JarvisCode.App.Services;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.Cli.Repl.Render;

namespace JarvisCode.Cli.Tests;

/// <summary>
/// The REPL's /release-notes picker, against the reference CLI 2.1.257's own:
/// a "Show all" row carrying the version count over the versions newest first,
/// and the notes each row prints.
/// </summary>
public sealed class ReleaseNotesPickerTests
{
    private static readonly IReadOnlyList<ReleaseNote> Notes =
    [
        new("1.2.0", ["Older thing"]),
        new("1.10.0", ["Newer thing", "And another"]),
    ];

    private static ReleaseNotesPicker Picker() => new(Notes, Ansi.Plain);

    [Fact]
    public void It_opens_on_show_all_over_the_versions_newest_first()
    {
        var lines = Picker().Render(80);
        Assert.Equal("Release notes", lines[0]);
        Assert.Equal("Select a version to view its notes.", lines[2]);
        Assert.Contains(lines, l => l.EndsWith("Show all", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Trim() == "2 versions");
        var versions = lines.Where(l => l.Contains("1.", StringComparison.Ordinal)).ToList();
        Assert.EndsWith("1.10.0", versions[0], StringComparison.Ordinal);
        Assert.EndsWith("1.2.0", versions[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Show_all_prints_every_version_oldest_first()
    {
        var all = Picker().Chosen(ReleaseNotesPicker.ShowAllValue);
        Assert.NotNull(all);
        Assert.StartsWith("Version 1.2.0:\n· Older thing", all, StringComparison.Ordinal);
        Assert.Contains("\n\nVersion 1.10.0:\n· Newer thing\n· And another", all, StringComparison.Ordinal);
    }

    [Fact]
    public void One_row_prints_only_its_own_notes()
    {
        Assert.Equal("Version 1.2.0:\n· Older thing", Picker().Chosen("1.2.0"));
        Assert.Null(Picker().Chosen("9.9.9"));
    }

    [Fact]
    public void Escape_cancels_and_enter_accepts_the_highlighted_row()
    {
        var picker = Picker();
        Assert.Equal(DialogOutcome.Cancelled, picker.Handle("select:cancel", new KeyPress("escape")).Outcome);

        var accepted = picker.Handle("select:accept", new KeyPress("return"));
        Assert.Equal(DialogOutcome.Accepted, accepted.Outcome);
        Assert.Equal(ReleaseNotesPicker.ShowAllValue, accepted.Value);

        picker.Handle("select:next", new KeyPress("down"));
        var second = picker.Handle("select:accept", new KeyPress("return"));
        Assert.Equal("1.10.0", second.Value);
    }
}
