using JarvisCode.App.Services;
using Xunit;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// How a turn promoted to a chapter is titled. The rule is the reference's own
/// (its kT over DT = 40, desktop 1.46388.2.0): the first line with anything on
/// it, trimmed, cut at forty characters with an ellipsis.
/// </summary>
public class ChapterTitlesTests
{
    [Fact]
    public void AShortLineIsTheTitleAsItStands()
    {
        Assert.Equal("Fixed the parser", ChapterTitles.FromSeed("Fixed the parser"));
    }

    [Fact]
    public void OnlyTheFirstLineWithSomethingOnItIsUsed()
    {
        Assert.Equal("Second line", ChapterTitles.FromSeed("\n   \nSecond line\nthird"));
    }

    [Fact]
    public void ALongLineIsCutAtFortyWithAnEllipsis()
    {
        var seed = new string('x', 60);
        var title = ChapterTitles.FromSeed(seed);

        Assert.Equal(new string('x', 40) + "…", title);
        Assert.Equal(41, title.Length);
    }

    [Fact]
    public void ExactlyFortyIsNotCut()
    {
        var seed = new string('x', 40);

        Assert.Equal(seed, ChapterTitles.FromSeed(seed));
    }

    [Fact]
    public void SurroundingSpaceIsTrimmedBeforeTheCut()
    {
        Assert.Equal("hello", ChapterTitles.FromSeed("   hello   "));
    }

    [Fact]
    public void AllBlankFallsBackToTheTrimmedSeed()
    {
        Assert.Equal("", ChapterTitles.FromSeed("   \n  \n "));
    }

    [Fact]
    public void NothingIsAnEmptyTitleRatherThanAThrow()
    {
        Assert.Equal("", ChapterTitles.FromSeed(null));
        Assert.Equal("", ChapterTitles.FromSeed(""));
    }

    [Fact]
    public void TheSeedJoinsATurnsRunsWithTwoSpaces()
    {
        Assert.Equal("one  two", ChapterTitles.Seed(["one", "two"]));
    }

    [Fact]
    public void ASeedBuiltFromSeveralRunsStillTitlesFromItsFirstLine()
    {
        var seed = ChapterTitles.Seed(["Reading the file", "and then editing it"]);

        Assert.Equal("Reading the file  and then editing it", ChapterTitles.FromSeed(seed));
    }
}
