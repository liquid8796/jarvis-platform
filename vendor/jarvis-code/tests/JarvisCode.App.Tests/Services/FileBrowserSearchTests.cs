using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The Files pane's name search, which is the reference's own subsequence matcher
/// (ion-dist chunk cd089cf92-CPpbZ5h_.js, its Aa/Pa), and the "?" content query.
/// </summary>
public class FileBrowserSearchTests
{
    private static FileNameIndex Index(params string[] paths)
    {
        var index = new FileNameIndex();
        index.Load(paths);
        return index;
    }

    [Fact]
    public void AQueryMatchesInOrderRatherThanAsASubstring()
    {
        var hits = Index("src/App/ChatSurface.xaml.cs").Search("chsurf", 10);
        Assert.Single(hits);
        Assert.Equal("src/App/ChatSurface.xaml.cs", hits[0].Path);
    }

    [Fact]
    public void APathMissingOneOfTheQueryLettersIsSkipped()
        => Assert.Empty(Index("src/App/Chat.cs").Search("zzz", 10));

    [Fact]
    public void ThePositionsAreTheIndexesThatMatched()
    {
        var hits = Index("abc").Search("ac", 10);
        Assert.Equal([0, 2], hits[0].Positions);
    }

    [Fact]
    public void AnExactRunScoresAboveAScatteredOne()
    {
        var hits = Index("aXbXcX", "abc").Search("abc", 10);
        Assert.Equal("abc", hits[0].Path);
    }

    [Fact]
    public void AMatchAfterASeparatorOutranksOneInTheMiddleOfAWord()
    {
        var hits = Index("zzzzzzfoo.cs", "src/foo.cs").Search("foo", 10);
        Assert.Equal("src/foo.cs", hits[0].Path);
    }

    [Fact]
    public void AQueryWithACapitalIsMatchedCaseSensitively()
    {
        var index = Index("panel.cs", "Panel.cs");
        var hits = index.Search("Panel", 10);
        Assert.Single(hits);
        Assert.Equal("Panel.cs", hits[0].Path);
    }

    [Fact]
    public void AnAllLowercaseQueryMatchesEitherCase()
        => Assert.Equal(2, Index("panel.cs", "Panel.cs").Search("panel", 10).Count);

    [Fact]
    public void TheLimitIsHonouredAndTheBestHitComesFirst()
    {
        var index = Index("a/one.cs", "b/two.cs", "c/three.cs", "one.cs");
        var hits = index.Search("one", 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal("one.cs", hits[0].Path);
    }

    [Fact]
    public void ALimitOfZeroAnswersNothing()
        => Assert.Empty(Index("a.cs").Search("a", 0));

    [Fact]
    public void AnEmptyQueryAnswersTheTopLevelSegments()
    {
        var hits = Index("src/a.cs", "src/b.cs", "tests/c.cs", "README.md").Search("", 10);
        Assert.Equal(["src", "tests", "README.md"], hits.Select(h => h.Path));
        Assert.All(hits, hit => Assert.Empty(hit.Positions));
    }

    [Fact]
    public void ThePathWithTestInItIsRankedDown()
    {
        // Two equally good hits: the reference multiplies the "test" one's rank by 1.05,
        // so it can never rank above its neighbour.
        var index = Index("app/thing.cs", "test/thing.cs");
        var hits = index.Search("thing", 10);
        var test = hits.Single(h => h.Path.Contains("test"));
        var app = hits.Single(h => h.Path.Contains("app"));
        Assert.True(test.Score >= app.Score);
    }

    [Fact]
    public void ADuplicatePathIsIndexedOnce()
        => Assert.Single(Index("a.cs", "a.cs").Search("a", 10));

    [Theory]
    [InlineData("Foo.cs", 0, true, 8)]
    [InlineData("Foo.cs", 0, false, 0)]
    [InlineData("src/Foo.cs", 4, false, 8)]
    [InlineData("fooBar.cs", 3, false, 6)]
    [InlineData("foobar.cs", 3, false, 0)]
    public void TheBonusIsTheReferenceTable(string path, int index, bool first, int expected)
        => Assert.Equal(expected, FileNameIndex.Bonus(path, index, first));

    [Theory]
    [InlineData("?todo", true, "todo")]
    [InlineData("  ? todo", true, "todo")]
    [InlineData("todo", false, "")]
    [InlineData("?", true, "")]
    public void AQueryOpeningWithAQuestionMarkIsAContentSearch(string query, bool isContent, string term)
    {
        Assert.Equal(isContent, FileContentSearch.IsContentQuery(query));
        Assert.Equal(term, FileContentSearch.ContentTerm(query));
    }

    [Fact]
    public void ContentHitsGroupByFileInFirstSeenOrder()
    {
        var groups = FileContentSearch.Group(
        [
            new FileContentHit("b.cs", "/b.cs", 1, 1, "x"),
            new FileContentHit("a.cs", "/a.cs", 2, 1, "x"),
            new FileContentHit("b.cs", "/b.cs", 9, 1, "x"),
        ]);
        Assert.Equal(["b.cs", "a.cs"], groups.Select(g => g.RelativePath));
        Assert.Equal(2, groups[0].Matches.Count);
    }

    [Fact]
    public void AnEmptyTermFindsNothing()
        => Assert.Empty(FileContentSearch.Search("/root", ["a.cs"], ""));

    [Fact]
    public void TheReferenceCapsAndDebouncesAreTheMeasuredNumbers()
    {
        Assert.Equal(200, FileContentSearch.MaxHits);
        Assert.Equal(250, FileContentSearch.ContentDebounceMs);
        Assert.Equal(120, FileContentSearch.NameDebounceMs);
        Assert.Equal(64, FileNameIndex.MaxQueryLength);
        Assert.Equal(100, FileNameIndex.TopLevelCacheSize);
    }
}
