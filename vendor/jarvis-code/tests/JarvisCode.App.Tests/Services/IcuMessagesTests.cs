using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The slice of ICU MessageFormat the reference's routines strings use. The app
/// carries those literals rather than restating them as interpolations, so that
/// the parity suite can pin them by message id — which only works if this
/// renders them the way the reference's formatter does.
/// </summary>
public class IcuMessagesTests
{
    [Theory]
    [InlineData(1, "1 run")]
    [InlineData(0, "0 runs")]
    [InlineData(7, "7 runs")]
    public void PluralPicksTheCategoryAndSubstitutesTheHash(int count, string expected) =>
        Assert.Equal(expected, IcuMessages.Format(
            ScheduledTaskPresentation.RunCountMessage, "count", count));

    [Theory]
    [InlineData(1, "1 routine")]
    [InlineData(3, "3 routines")]
    public void TheRoutineCountUsesTheSameShape(int count, string expected) =>
        Assert.Equal(expected, IcuMessages.Format(
            ScheduledTaskPresentation.RoutineCountMessage, "count", count));

    [Fact]
    public void AnExactCategoryBeatsTheGeneralOne() =>
        Assert.Equal("no runs", IcuMessages.Format(
            "{n, plural, =0 {no runs} one {# run} other {# runs}}", "n", 0));

    [Fact]
    public void PlainSubstitutionWorks() =>
        Assert.Equal("Routine “Daily” started.", IcuMessages.Format(
            "Routine “{name}” started.", "name", "Daily"));

    [Fact]
    public void TwoArgumentsAreBothSubstituted() =>
        Assert.Equal("Daily missed at 09:00.", IcuMessages.Format(
            "{name} missed at {time}.", "name", "Daily", "time", "09:00"));

    [Fact]
    public void SelectPicksTheNamedBranch() =>
        Assert.Equal("~9:00", IcuMessages.Format(
            "{approx, select, yes {~} other {}}{time}",
            new Dictionary<string, object?> { ["approx"] = "yes", ["time"] = "9:00" }));

    [Fact]
    public void SelectFallsBackToOther() =>
        Assert.Equal("9:00", IcuMessages.Format(
            "{approx, select, yes {~} other {}}{time}",
            new Dictionary<string, object?> { ["approx"] = "no", ["time"] = "9:00" }));

    [Fact]
    public void AnArgumentWithNoValueRendersEmpty() =>
        Assert.Equal("a  b", IcuMessages.Format("a {missing} b", "other", 1));

    [Fact]
    public void TextWithNoArgumentsIsUnchanged() =>
        Assert.Equal("No runs yet", IcuMessages.Format(
            "No runs yet", new Dictionary<string, object?>()));

    [Fact]
    public void ABranchCanCarryItsOwnArguments() =>
        Assert.Equal("1 run for Daily", IcuMessages.Format(
            "{n, plural, one {# run for {name}} other {# runs for {name}}}",
            new Dictionary<string, object?> { ["n"] = 1, ["name"] = "Daily" }));
}
