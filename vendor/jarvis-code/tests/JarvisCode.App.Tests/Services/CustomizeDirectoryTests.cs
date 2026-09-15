using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>The Directory's facets, sort and unified search.</summary>
public sealed class CustomizeDirectoryTests
{
    private static DirectoryItem Item(
        string title,
        DirectorySection section = DirectorySection.Skills,
        bool installed = false,
        string source = "anthropic",
        string? category = null,
        DateTimeOffset? updated = null,
        string? description = null,
        string? author = null,
        params string[] skills) =>
        new(section, title, title, description, author, source, category, installed, updated, skills);

    [Fact]
    public void TheStatusFacetKeepsWhatItNames()
    {
        var items = new[] { Item("a", installed: true), Item("b") };

        Assert.Equal(2, CustomizeDirectory.ByStatus(items, DirectoryStatus.All).Count);
        Assert.Equal(["a"], CustomizeDirectory.ByStatus(items, DirectoryStatus.Installed).Select(i => i.Title));
        Assert.Equal(["b"], CustomizeDirectory.ByStatus(items, DirectoryStatus.NotInstalled).Select(i => i.Title));
    }

    [Fact]
    public void SourceAndCategoryNarrowOrPassEverythingThrough()
    {
        var items = new[]
        {
            Item("a", source: "anthropic", category: "docs"),
            Item("b", source: "acme", category: "build"),
        };

        Assert.Equal(2, CustomizeDirectory.BySource(items, null).Count);
        Assert.Equal(["b"], CustomizeDirectory.BySource(items, "acme").Select(i => i.Title));
        Assert.Equal(["a"], CustomizeDirectory.ByCategory(items, "docs").Select(i => i.Title));
        Assert.Equal(["acme", "anthropic"], CustomizeDirectory.Sources(items));
        Assert.Equal(["build", "docs"], CustomizeDirectory.Categories(items));
    }

    [Fact]
    public void ATitleHitCarriesNoMetadataLine()
    {
        var items = new[] { Item("weekly-status", description: "Weekly reports") };

        var found = Assert.Single(CustomizeDirectory.Search(items, "weekly"));

        Assert.Null(found.MatchedSkill);
        Assert.Null(found.MatchedOn);
    }

    [Fact]
    public void ASkillHitSaysWhichSkillMatched()
    {
        var items = new[] { Item("devkit", description: "A plugin", skills: ["ship", "deploy"]) };

        var found = Assert.Single(CustomizeDirectory.Search(items, "deploy"));

        Assert.Equal("deploy", found.MatchedSkill);
        Assert.Null(found.MatchedOn);
    }

    [Fact]
    public void ADescriptionHitSaysWhatItMatchedOn()
    {
        var items = new[] { Item("devkit", description: "Ships containers") };

        var found = Assert.Single(CustomizeDirectory.Search(items, "containers"));

        Assert.Null(found.MatchedSkill);
        Assert.Equal("Ships containers", found.MatchedOn);
    }

    [Fact]
    public void AnAuthorHitIsFoundLast()
    {
        var items = new[] { Item("devkit", description: "Ships", author: "Ada Lovelace") };

        var found = Assert.Single(CustomizeDirectory.Search(items, "lovelace"));

        Assert.Equal("Ada Lovelace", found.MatchedOn);
    }

    [Fact]
    public void AnEmptyQueryKeepsEverything() =>
        Assert.Equal(2, CustomizeDirectory.Search([Item("a"), Item("b")], "   ").Count);

    [Fact]
    public void NameSortsAlphabeticallyAndRecentlyUpdatedByDate()
    {
        var now = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
        var items = new[]
        {
            Item("zeta", updated: now.AddDays(-1)),
            Item("alpha", updated: now.AddDays(-9)),
            Item("mid"),
        };

        Assert.Equal(
            ["alpha", "mid", "zeta"],
            CustomizeDirectory.Sort(items, DirectorySort.NameAsc).Select(i => i.Title));
        Assert.Equal(
            ["zeta", "alpha", "mid"],
            CustomizeDirectory.Sort(items, DirectorySort.RecentlyUpdated).Select(i => i.Title));
    }

    [Fact]
    public void MostPopularPutsWhatIsAlreadyInstalledFirst()
    {
        var items = new[] { Item("zeta", installed: true), Item("alpha") };

        Assert.Equal(
            ["zeta", "alpha"],
            CustomizeDirectory.Sort(items, DirectorySort.MostPopular).Select(i => i.Title));
    }

    [Fact]
    public void TheUnifiedSearchPreviewsFivePerSectionAndKeepsTheTotal()
    {
        var items = Enumerable.Range(1, 7)
            .Select(n => Item($"skill-{n}"))
            .Concat([Item("skill-connector", DirectorySection.Connectors)])
            .ToList();

        var sections = CustomizeDirectory.SearchAll(items, "skill");

        Assert.Equal(
            [DirectorySection.Skills, DirectorySection.Connectors, DirectorySection.Plugins],
            sections.Select(s => s.Section));
        Assert.Equal(7, sections[0].Total);
        Assert.Equal(CustomizeDirectory.SectionPreview, sections[0].Entries.Count);
        Assert.Equal(1, sections[1].Total);
        Assert.Equal(0, sections[2].Total);
    }

    [Fact]
    public void TheSectionLabelsAndSeeAllLinesAreTheReferencesOwn()
    {
        Assert.Equal("Skills", CustomizeDirectory.SectionLabel(DirectorySection.Skills));
        Assert.Equal("Connectors", CustomizeDirectory.SectionLabel(DirectorySection.Connectors));
        Assert.Equal("Plugins", CustomizeDirectory.SectionLabel(DirectorySection.Plugins));
        Assert.Equal("See all 12 skills", CustomizeDirectory.SeeAllLabel(DirectorySection.Skills, 12));
        Assert.Equal("See all 3 connectors", CustomizeDirectory.SeeAllLabel(DirectorySection.Connectors, 3));
        Assert.Equal("See all 9 plugins", CustomizeDirectory.SeeAllLabel(DirectorySection.Plugins, 9));
    }

    [Fact]
    public void EachSectionHasItsOwnPlaceholderAndTheUnifiedOneCoversAllThree()
    {
        Assert.Equal("Search skills…", CustomizeDirectory.SearchPlaceholder(DirectorySection.Skills));
        Assert.Equal("Search connectors…", CustomizeDirectory.SearchPlaceholder(DirectorySection.Connectors));
        Assert.Equal("Search plugins…", CustomizeDirectory.SearchPlaceholder(DirectorySection.Plugins));
        Assert.Equal("Search skills, connectors, and plugins…", CustomizeDirectory.SearchPlaceholder(null));
    }

    [Fact]
    public void ResultCountSingularizes()
    {
        Assert.Equal("1 result", CustomizeDirectory.ResultCount(1));
        Assert.Equal("4 results", CustomizeDirectory.ResultCount(4));
        Assert.Equal("1,200 results", CustomizeDirectory.ResultCount(1200));
    }
}
