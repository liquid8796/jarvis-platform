using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// /release-notes, against the reference CLI 2.1.257's own picker module: its
/// changelog parser, its two formatters and the two opposite orders they use.
/// </summary>
public sealed class ReleaseNotesTests
{
    private const string Changelog = """
        # Changelog

        ## 2.1.2 - 2026-08-01
        - Fixed the second thing
        - And another

        ## 2.1.10
        - Fixed the newest thing

        ## 2.0.0
        no bullets here, so this section is dropped
        """;

    [Fact]
    public void The_parser_takes_the_version_before_the_dash_and_the_dash_bullets()
    {
        var notes = ReleaseNotes.Parse(Changelog);
        Assert.Equal(["2.1.2", "2.1.10"], notes.Select(n => n.Version));
        Assert.Equal(["Fixed the second thing", "And another"], notes[0].Bullets);
        Assert.Equal(["Fixed the newest thing"], notes[1].Bullets);
    }

    [Fact]
    public void Nothing_parses_to_nothing()
    {
        Assert.Empty(ReleaseNotes.Parse(null));
        Assert.Empty(ReleaseNotes.Parse("   "));
        Assert.Empty(ReleaseNotes.Parse("no headings at all"));
    }

    [Fact]
    public void One_version_is_formatted_with_the_references_middle_dot()
    {
        var note = new ReleaseNote("1.2.3", ["first", "second"]);
        Assert.Equal("Version 1.2.3:\n· first\n· second", ReleaseNotes.FormatVersion(note));
    }

    [Fact]
    public void The_picker_lists_newest_first_and_show_all_prints_oldest_first()
    {
        var notes = ReleaseNotes.Parse(Changelog);
        Assert.Equal(["2.1.10", "2.1.2"], ReleaseNotes.Newest(notes).Select(n => n.Version));

        var all = ReleaseNotes.FormatAll(notes);
        Assert.StartsWith("Version 2.1.2:", all, StringComparison.Ordinal);
        Assert.Contains("\n\nVersion 2.1.10:", all, StringComparison.Ordinal);
    }

    [Fact]
    public void The_show_all_row_counts_the_versions() =>
        Assert.Equal("3 versions", ReleaseNotes.VersionCount(3));

    [Fact]
    public void An_empty_feed_points_at_this_products_own_releases()
    {
        Assert.Equal(
            "See the full changelog at: https://github.com/liquid8796/jarvis-code/releases",
            ReleaseNotes.NothingToShow());
        Assert.Empty(ReleaseNotes.FromFeed("[]"));
        Assert.Empty(ReleaseNotes.FromFeed("not json"));
    }

    [Fact]
    public void A_release_contributes_its_tag_and_its_bullets()
    {
        const string feed = """
            [
              {"tag_name": "v1.4.0", "body": "- Added a pane\n- Fixed a crash\n"},
              {"tag_name": "v1.3.0", "body": "nothing itemised"},
              {"tag_name": "nightly", "body": "- ignored, no version in the tag"},
              {"tag_name": "v1.2.0", "draft": true, "body": "- a draft is not published"}
            ]
            """;
        var notes = ReleaseNotes.FromFeed(feed);
        Assert.Single(notes);
        Assert.Equal("1.4.0", notes[0].Version);
        Assert.Equal(["Added a pane", "Fixed a crash"], notes[0].Bullets);
    }

    [Fact]
    public void A_release_body_written_as_changelog_sections_is_read_as_those()
    {
        const string feed = """
            [{"tag_name": "v2.0.0", "body": "## 2.0.0 - 2026-09-01\n- One\n\n## 1.9.0\n- Two\n"}]
            """;
        Assert.Equal(["2.0.0", "1.9.0"], ReleaseNotes.FromFeed(feed).Select(n => n.Version));
    }
}
