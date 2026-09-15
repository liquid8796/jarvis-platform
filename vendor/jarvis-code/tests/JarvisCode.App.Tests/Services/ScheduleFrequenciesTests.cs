using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The Scheduled editor's cron validation and its frequency ⇄ cron mapping,
/// against the reference's own ladder (Claude Code Desktop 1.40609.1.0, its
/// routine form <c>c0243d234</c> and the first-pass validator in
/// <c>c0958e5bf</c>).
/// </summary>
public class ScheduleFrequenciesTests
{
    [Theory]
    // Accepted.
    [InlineData("0 9 * * *", CronProblem.None)]
    [InlineData("30 8 * * 1", CronProblem.None)]
    [InlineData("0 9 * * 1-5", CronProblem.None)]
    [InlineData("15 * * * *", CronProblem.None)]
    [InlineData("0 0 1 * *", CronProblem.None)]
    [InlineData("0 9,17 * * *", CronProblem.None)]
    [InlineData("0 */2 * * *", CronProblem.None)]
    [InlineData("", CronProblem.None)]
    // A minute field that is not one minute of the hour runs more than hourly.
    [InlineData("* * * * *", CronProblem.SubHourly)]
    [InlineData("*/5 * * * *", CronProblem.SubHourly)]
    [InlineData("0,30 * * * *", CronProblem.SubHourly)]
    // Shape.
    [InlineData("0 9 * *", CronProblem.Invalid)]
    [InlineData("0 9 * * * *", CronProblem.Invalid)]
    [InlineData("/5 9 * * *", CronProblem.Invalid)]
    [InlineData("60 9 * * *", CronProblem.Invalid)]
    [InlineData("0 24 * * *", CronProblem.Invalid)]
    [InlineData("0 9 32 * *", CronProblem.Invalid)]
    [InlineData("0 9 * 13 *", CronProblem.Invalid)]
    // Named fields and the crontab extensions this evaluator does not take.
    [InlineData("0 9 * * MON", CronProblem.NumericOnly)]
    [InlineData("0 9 * JAN *", CronProblem.NumericOnly)]
    [InlineData("0 9 ? * *", CronProblem.NumericOnly)]
    [InlineData("0 9 L * *", CronProblem.NumericOnly)]
    [InlineData("0 9 15W * *", CronProblem.NumericOnly)]
    [InlineData("0 9 * * 5#2", CronProblem.NumericOnly)]
    // Sunday.
    [InlineData("0 9 * * 7", CronProblem.SundayIsZero)]
    [InlineData("0 9 * * 1-7", CronProblem.SundayIsZero)]
    [InlineData("0 9 * * 0,7", CronProblem.SundayIsZero)]
    public void ValidateFollowsTheReferenceLadder(string cron, CronProblem expected) =>
        Assert.Equal(expected, ScheduleFrequencies.Validate(cron));

    [Fact]
    public void GibberishIsInvalidRatherThanNamed() =>
        Assert.Equal(CronProblem.Invalid, ScheduleFrequencies.Validate("0 9 * * zzz"));

    [Theory]
    [InlineData(CronProblem.Invalid, "Invalid cron expression. Check the format (for example: 0 9 * * *).")]
    [InlineData(CronProblem.SubHourly, "Schedules must run at most once per hour.")]
    [InlineData(CronProblem.SundayIsZero, "Use 0 for Sunday (7 isn’t supported here).")]
    public void ProblemMessagesAreTheReferenceSentences(CronProblem problem, string expected) =>
        Assert.Equal(expected, ScheduleFrequencies.ProblemMessage(problem));

    [Fact]
    public void NoProblemHasNoMessage() =>
        Assert.Null(ScheduleFrequencies.ProblemMessage(CronProblem.None));

    [Theory]
    [InlineData(ScheduleFrequency.Hourly, 9, 15, 1, "15 * * * *")]
    [InlineData(ScheduleFrequency.Daily, 9, 0, 1, "0 9 * * *")]
    [InlineData(ScheduleFrequency.Weekdays, 8, 30, 1, "30 8 * * 1-5")]
    [InlineData(ScheduleFrequency.Weekly, 16, 0, 5, "0 16 * * 5")]
    public void BuildCronMatchesThePresets(
        ScheduleFrequency frequency, int hour, int minute, int day, string expected) =>
        Assert.Equal(expected, ScheduleFrequencies.BuildCron(frequency, hour, minute, day));

    [Theory]
    [InlineData(ScheduleFrequency.Manual)]
    [InlineData(ScheduleFrequency.OneTime)]
    [InlineData(ScheduleFrequency.Custom)]
    public void FrequenciesWithoutACronBuildNone(ScheduleFrequency frequency) =>
        Assert.Null(ScheduleFrequencies.BuildCron(frequency, 9, 0, 1));

    [Theory]
    [InlineData(ScheduleFrequency.Hourly, 9, 15, 1)]
    [InlineData(ScheduleFrequency.Daily, 9, 0, 1)]
    [InlineData(ScheduleFrequency.Weekdays, 8, 30, 1)]
    [InlineData(ScheduleFrequency.Weekly, 16, 0, 5)]
    public void PresetsRoundTrip(ScheduleFrequency frequency, int hour, int minute, int day)
    {
        var cron = ScheduleFrequencies.BuildCron(frequency, hour, minute, day);
        var parsed = ScheduleFrequencies.ParseCron(cron);
        Assert.NotNull(parsed);
        Assert.Equal(frequency, parsed!.Frequency);
        Assert.Equal(minute, parsed.Minute);
        if (frequency != ScheduleFrequency.Hourly)
        {
            Assert.Equal(hour, parsed.Hour);
        }

        if (frequency == ScheduleFrequency.Weekly)
        {
            Assert.Equal(day, parsed.DayOfWeek);
        }
    }

    [Theory]
    [InlineData("0 0 1 * *")]
    [InlineData("0 9 * * 2-4")]
    [InlineData("0 9,17 * * *")]
    [InlineData(null)]
    [InlineData("")]
    public void ACronThatIsNotAPresetStaysCustom(string? cron) =>
        Assert.Null(ScheduleFrequencies.ParseCron(cron));

    [Fact]
    public void OneTimeIsOfferedOnlyForATaskThatHasOne()
    {
        Assert.DoesNotContain(ScheduleFrequency.OneTime, ScheduleFrequencies.Offered(hasFireAt: false));
        Assert.Equal(ScheduleFrequency.OneTime, ScheduleFrequencies.Offered(hasFireAt: true)[0]);
    }

    [Fact]
    public void OnlyTheDatedFrequenciesShowTheTimeControls()
    {
        Assert.True(ScheduleFrequencies.NeedsTimeOfDay(ScheduleFrequency.Daily));
        Assert.True(ScheduleFrequencies.NeedsTimeOfDay(ScheduleFrequency.Weekdays));
        Assert.True(ScheduleFrequencies.NeedsTimeOfDay(ScheduleFrequency.Weekly));
        Assert.False(ScheduleFrequencies.NeedsTimeOfDay(ScheduleFrequency.Hourly));
        Assert.False(ScheduleFrequencies.NeedsTimeOfDay(ScheduleFrequency.Custom));
        Assert.False(ScheduleFrequencies.NeedsTimeOfDay(ScheduleFrequency.Manual));
    }

    [Fact]
    public void TheStaggerNoteRidesEveryRecurringFrequency()
    {
        Assert.False(ScheduleFrequencies.ShowsStaggerNote(ScheduleFrequency.Manual));
        Assert.False(ScheduleFrequencies.ShowsStaggerNote(ScheduleFrequency.OneTime));
        Assert.True(ScheduleFrequencies.ShowsStaggerNote(ScheduleFrequency.Hourly));
        Assert.True(ScheduleFrequencies.ShowsStaggerNote(ScheduleFrequency.Custom));
    }

    [Theory]
    [InlineData("*", 0, 59, true)]
    [InlineData("0", 0, 59, true)]
    [InlineData("59", 0, 59, true)]
    [InlineData("60", 0, 59, false)]
    [InlineData("1-5", 0, 6, true)]
    [InlineData("5-1", 0, 6, false)]
    [InlineData("0-7", 0, 6, false)]
    [InlineData("*/2", 0, 23, true)]
    [InlineData("*/0", 0, 23, false)]
    [InlineData("1,2,3", 0, 6, true)]
    [InlineData("1,9", 0, 6, false)]
    public void FieldRangesFollowTheReferenceChecker(string field, int min, int max, bool expected) =>
        Assert.Equal(expected, ScheduleFrequencies.FieldInRange(field, min, max));
}
