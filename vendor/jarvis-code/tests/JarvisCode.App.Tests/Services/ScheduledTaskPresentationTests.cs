using System.Globalization;
using JarvisCode.App.Services;
using JarvisCode.Core.Routines;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The Scheduled page's own logic against the reference's (Claude Code Desktop
/// 1.40609.1.0): the status ladder, the schedule sentences, the relative dates,
/// and how the list is searched, filtered and sorted.
/// </summary>
public class ScheduledTaskPresentationTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>
    /// Noon on a Wednesday, so the weekday and weekly cases have somewhere to
    /// run to. It is built at the machine's own offset because the page renders
    /// every time in local time, as the reference's <c>toLocaleTimeString</c>
    /// does — a fixture pinned to UTC would only pass in one timezone.
    /// </summary>
    private static readonly DateTimeOffset Now = Local(new DateTime(2026, 9, 2, 12, 0, 0));

    private static DateTimeOffset Local(DateTime moment) =>
        new(DateTime.SpecifyKind(moment, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(moment));

    private static ScheduledItem Item(
        bool enabled = true,
        string? cron = null,
        DateTimeOffset? fireAt = null,
        DateTimeOffset? lastRunAt = null,
        string? ended = null,
        string? suspension = null,
        string name = "daily-brief",
        string description = "",
        int jitterSeconds = 0) => new()
    {
        Id = name,
        Name = "",
        Description = description,
        Enabled = enabled,
        CronExpression = cron,
        FireAt = fireAt,
        LastRunAt = lastRunAt,
        EndedReason = ended,
        SuspensionReason = suspension,
        JitterSeconds = jitterSeconds,
    };

    // ---- the status ladder -------------------------------------------------

    [Fact]
    public void EnabledIsActive() =>
        Assert.Equal(ScheduledStatus.Active, ScheduledTaskPresentation.BaseStatus(true, null));

    [Fact]
    public void StoppedWithNoReasonIsPaused() =>
        Assert.Equal(ScheduledStatus.Paused, ScheduledTaskPresentation.BaseStatus(false, null));

    [Fact]
    public void TheOneTimeReasonIsCompletedRatherThanDisabled() =>
        Assert.Equal(
            ScheduledStatus.Completed,
            ScheduledTaskPresentation.BaseStatus(false, RoutineEndedReasons.RunOnceFired));

    [Theory]
    [InlineData(RoutineEndedReasons.InvalidCron)]
    [InlineData(RoutineEndedReasons.SubHourly)]
    [InlineData(RoutineEndedReasons.ConfigRejected)]
    public void EveryOtherReasonIsAutoDisabled(string reason) =>
        Assert.Equal(ScheduledStatus.AutoDisabled, ScheduledTaskPresentation.BaseStatus(false, reason));

    [Theory]
    [InlineData(ScheduledStatus.Active, ScheduledStatus.OnHold)]
    [InlineData(ScheduledStatus.Paused, ScheduledStatus.OnHold)]
    [InlineData(ScheduledStatus.Completed, ScheduledStatus.Completed)]
    [InlineData(ScheduledStatus.AutoDisabled, ScheduledStatus.AutoDisabled)]
    public void AHoldOnlyCatchesTheTwoLiveStates(ScheduledStatus given, ScheduledStatus expected) =>
        Assert.Equal(expected, ScheduledTaskPresentation.WithSuspension(given, "subscription_paused"));

    [Fact]
    public void NoSuspensionLeavesTheStatusAlone() =>
        Assert.Equal(
            ScheduledStatus.Active,
            ScheduledTaskPresentation.WithSuspension(ScheduledStatus.Active, null));

    [Fact]
    public void ADeviceAbsentHoldIsDroppedByTheList() =>
        Assert.Equal(
            ScheduledStatus.Active,
            ScheduledTaskPresentation.Status(Item(suspension: "device_absent")));

    [Fact]
    public void AOneTimeItemThatRanReadsCompletedWithoutAStoredReason() =>
        Assert.Equal(
            ScheduledStatus.Completed,
            ScheduledTaskPresentation.Status(Item(enabled: false, fireAt: Now, lastRunAt: Now)));

    [Fact]
    public void AOneTimeItemThatHasNotRunIsStillPaused() =>
        Assert.Equal(
            ScheduledStatus.Paused,
            ScheduledTaskPresentation.Status(Item(enabled: false, fireAt: Now.AddDays(1))));

    [Fact]
    public void APausedRecurringItemStaysPaused() =>
        Assert.Equal(ScheduledStatus.Paused, ScheduledTaskPresentation.Status(Item(enabled: false, cron: "0 9 * * *")));

    [Theory]
    [InlineData(ScheduledStatus.Active, "Active")]
    [InlineData(ScheduledStatus.Paused, "Paused")]
    [InlineData(ScheduledStatus.Completed, "Ran")]
    [InlineData(ScheduledStatus.OnHold, "On hold")]
    [InlineData(ScheduledStatus.AutoDisabled, "Auto-disabled")]
    public void StatusLabelsAreTheReferenceWords(ScheduledStatus status, string expected) =>
        Assert.Equal(expected, ScheduledTaskPresentation.StatusLabel(status));

    // ---- the enable switch -------------------------------------------------

    [Fact]
    public void ARunningRoutineCanAlwaysBePaused() =>
        Assert.True(ScheduledTaskPresentation.CanToggleEnabled(Item(cron: "0 9 * * *"), Now));

    [Fact]
    public void ACompletedOneTimeRoutineOffersNoSwitch() =>
        Assert.False(ScheduledTaskPresentation.CanToggleEnabled(
            Item(enabled: false, fireAt: Now, lastRunAt: Now, ended: RoutineEndedReasons.RunOnceFired), Now));

    [Theory]
    [InlineData(RoutineEndedReasons.InvalidCron)]
    [InlineData(RoutineEndedReasons.SubHourly)]
    public void AScheduleProblemMustBeEditedRatherThanSwitchedBackOn(string reason) =>
        Assert.False(ScheduledTaskPresentation.CanToggleEnabled(Item(enabled: false, ended: reason), Now));

    [Fact]
    public void AConfigurationProblemCanBeSwitchedBackOn() =>
        Assert.True(ScheduledTaskPresentation.CanToggleEnabled(
            Item(enabled: false, ended: RoutineEndedReasons.ConfigRejected), Now));

    [Fact]
    public void AOneTimeMomentThatPassedOffersNoSwitch() =>
        Assert.False(ScheduledTaskPresentation.CanToggleEnabled(
            Item(enabled: false, fireAt: Now.AddDays(-1)), Now));

    [Fact]
    public void AOneTimeMomentStillAheadDoesOfferOne() =>
        Assert.True(ScheduledTaskPresentation.CanToggleEnabled(
            Item(enabled: false, fireAt: Now.AddDays(1)), Now));

    // ---- reasons -----------------------------------------------------------

    [Fact]
    public void AnUnknownReasonStillGetsASentence() =>
        Assert.Equal(
            "This routine was automatically disabled. Review your settings and re-enable it.",
            ScheduledTaskPresentation.EndedReasonMessage("auto_disabled_something_new"));

    [Fact]
    public void NoReasonHasNoSentence()
    {
        Assert.Null(ScheduledTaskPresentation.EndedReasonMessage(null));
        Assert.Null(ScheduledTaskPresentation.EndedReasonShortLabel(null));
    }

    [Fact]
    public void TheSubHourlyReasonNamesTheFrequency() =>
        Assert.Equal("schedule too frequent",
            ScheduledTaskPresentation.EndedReasonShortLabel(RoutineEndedReasons.SubHourly));

    // ---- names -------------------------------------------------------------

    [Theory]
    [InlineData("Daily code review", "daily-code-review")]
    [InlineData("  Spaced   Out  ", "spaced-out")]
    [InlineData("Ünïcode!", "ncode")]
    [InlineData("--edges--", "edges")]
    [InlineData("!!!", "")]
    public void SlugMatchesTheReference(string name, string expected) =>
        Assert.Equal(expected, ScheduledTaskPresentation.Slug(name));

    [Fact]
    public void SlugIsCappedAtTheReferenceLength() =>
        Assert.Equal(120, ScheduledTaskPresentation.Slug(new string('a', 200)).Length);

    [Fact]
    public void AnIdReadsBackAsWords() =>
        Assert.Equal("Daily code review", ScheduledTaskPresentation.NameFromId("daily-code-review"));

    [Fact]
    public void AStoredNameWinsOverTheId() =>
        Assert.Equal("My brief", ScheduledTaskPresentation.DisplayName(Item() with { Name = "My brief" }));

    [Fact]
    public void AnEmptyNameIsNotAnErrorUntilItIsTyped() =>
        Assert.Null(ScheduledTaskPresentation.NameError("", new HashSet<string>(), isEditing: false));

    [Fact]
    public void ANameWithNothingUsableIsRefused() =>
        Assert.Equal(
            "Name must contain at least one letter or number.",
            ScheduledTaskPresentation.NameError("!!!", new HashSet<string>(), isEditing: false));

    [Theory]
    [InlineData("new")]
    [InlineData("New")]
    [InlineData("new-local")]
    public void TheReferencesReservedNamesAreRefused(string name) =>
        Assert.Equal(
            "This name is reserved. Choose a different name.",
            ScheduledTaskPresentation.NameError(name, new HashSet<string>(), isEditing: false));

    [Fact]
    public void ACollisionNamesTheRoutine() =>
        Assert.Equal(
            "A routine named “Daily brief” already exists.",
            ScheduledTaskPresentation.NameError(
                "Daily brief", new HashSet<string> { "daily-brief" }, isEditing: false));

    [Fact]
    public void AnExistingRoutineKeepsWhateverNameItHas() =>
        Assert.Null(ScheduledTaskPresentation.NameError(
            "new", new HashSet<string> { "new" }, isEditing: true));

    // ---- schedule in words -------------------------------------------------

    [Fact]
    public void TimeOfDayIsAClockNotADate() =>
        Assert.Equal("9:05 AM", ScheduledTaskPresentation.TimeOfDay(9, 5, Culture));

    [Theory]
    [InlineData(0, "Sunday")]
    [InlineData(1, "Monday")]
    [InlineData(6, "Saturday")]
    public void DayZeroIsSunday(int day, string expected) =>
        Assert.Equal(expected, ScheduledTaskPresentation.DayName(day, Culture));

    [Theory]
    [InlineData("0 9 * * *", "Every day at 9:00 AM")]
    [InlineData("30 8 * * 1-5", "Weekdays at 8:30 AM")]
    [InlineData("0 16 * * 5", "Every Friday at 4:00 PM")]
    [InlineData("15 * * * *", "Hourly")]
    public void PresetsAreDescribedTheReferenceWay(string cron, string expected) =>
        Assert.Equal(expected, ScheduledTaskPresentation.Describe(Item(cron: cron), Now, Culture));

    [Fact]
    public void JitterMarksTheTimeAsApproximate() =>
        Assert.Equal(
            "Every day at ~9:00 AM",
            ScheduledTaskPresentation.Describe(Item(cron: "0 9 * * *", jitterSeconds: 300), Now, Culture));

    [Fact]
    public void NoScheduleHasNoDescription() =>
        Assert.Null(ScheduledTaskPresentation.Describe(Item(), Now, Culture));

    [Fact]
    public void ACronThatDoesNotParseAtAllReadsInvalid() =>
        Assert.Equal("Invalid schedule", ScheduledTaskPresentation.Describe(Item(cron: "nope"), Now, Culture));

    [Fact]
    public void AValidNonPresetCronShowsItsExpression() =>
        Assert.Equal("0 0 1 * *", ScheduledTaskPresentation.Describe(Item(cron: "0 0 1 * *"), Now, Culture));

    [Fact]
    public void AOneTimeItemIsDescribedByItsMoment() =>
        Assert.StartsWith(
            "Once — ",
            ScheduledTaskPresentation.Describe(Item(fireAt: Now.AddHours(3)), Now, Culture),
            StringComparison.Ordinal);

    // ---- relative dates ----------------------------------------------------

    [Fact]
    public void TodayIsNamed() =>
        Assert.Equal("today at 3:00 PM", ScheduledTaskPresentation.Relative(
            Now.AddHours(3), Now, false, ScheduledTaskPresentation.RelativeDirection.Future, Culture));

    [Fact]
    public void TomorrowIsNamedOnlyLookingForward() =>
        Assert.Equal("tomorrow at 12:00 PM", ScheduledTaskPresentation.Relative(
            Now.AddDays(1), Now, false, ScheduledTaskPresentation.RelativeDirection.Future, Culture));

    [Fact]
    public void YesterdayIsNamedOnlyLookingBack() =>
        Assert.Equal("yesterday at 12:00 PM", ScheduledTaskPresentation.Relative(
            Now.AddDays(-1), Now, false, ScheduledTaskPresentation.RelativeDirection.Past, Culture));

    [Fact]
    public void ANeighbouringDayTheWrongWayFallsBackToADate() =>
        Assert.Equal("Sep 1 at 12:00 PM", ScheduledTaskPresentation.Relative(
            Now.AddDays(-1), Now, false, ScheduledTaskPresentation.RelativeDirection.Future, Culture));

    [Fact]
    public void AnotherYearCarriesIt() =>
        Assert.Equal("Sep 2, 2027 at 12:00 PM", ScheduledTaskPresentation.Relative(
            Now.AddYears(1), Now, false, ScheduledTaskPresentation.RelativeDirection.Future, Culture));

    [Fact]
    public void ApproximateDatesCarryTheTilde() =>
        Assert.Equal("today at ~3:00 PM", ScheduledTaskPresentation.Relative(
            Now.AddHours(3), Now, true, ScheduledTaskPresentation.RelativeDirection.Future, Culture));

    // ---- next occurrence ---------------------------------------------------

    [Fact]
    public void HourlyRollsToTheNextHourOnceItsMinutePassed()
    {
        var next = ScheduledTaskPresentation.NextOccurrence(
            new ParsedSchedule(ScheduleFrequency.Hourly, 9, 0, 1), Now);
        Assert.Equal(13, next.Hour);
        Assert.Equal(0, next.Minute);
    }

    [Fact]
    public void HourlyStaysInTheHourWhileItsMinuteIsAhead()
    {
        var next = ScheduledTaskPresentation.NextOccurrence(
            new ParsedSchedule(ScheduleFrequency.Hourly, 9, 30, 1), Now);
        Assert.Equal(12, next.Hour);
        Assert.Equal(30, next.Minute);
    }

    [Fact]
    public void DailyRollsToTomorrowOnceItsTimePassed() =>
        Assert.Equal(3, ScheduledTaskPresentation.NextOccurrence(
            new ParsedSchedule(ScheduleFrequency.Daily, 9, 0, 1), Now).Day);

    [Fact]
    public void DailyStaysTodayWhileItsTimeIsAhead() =>
        Assert.Equal(2, ScheduledTaskPresentation.NextOccurrence(
            new ParsedSchedule(ScheduleFrequency.Daily, 18, 0, 1), Now).Day);

    [Fact]
    public void WeekdaysSkipsTheWeekend()
    {
        // From Friday 2026-09-04 at noon, the 9am slot lands on Monday the 7th.
        var friday = Local(new DateTime(2026, 9, 4, 12, 0, 0));
        var next = ScheduledTaskPresentation.NextOccurrence(
            new ParsedSchedule(ScheduleFrequency.Weekdays, 9, 0, 1), friday);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(7, next.Day);
    }

    [Fact]
    public void WeeklyRollsAWholeWeekWhenTodayIsAlreadyPast()
    {
        // Now is a Wednesday at noon; a Wednesday 9am slot is next week.
        var next = ScheduledTaskPresentation.NextOccurrence(
            new ParsedSchedule(ScheduleFrequency.Weekly, 9, 0, 3), Now);
        Assert.Equal(DayOfWeek.Wednesday, next.DayOfWeek);
        Assert.Equal(9, next.Day);
    }

    [Fact]
    public void APausedItemHasNoNextRun() =>
        Assert.Null(ScheduledTaskPresentation.NextRunText(Item(enabled: false, cron: "0 9 * * *"), Now, Culture));

    [Fact]
    public void ASpentOneTimeItemHasNoNextRun() =>
        Assert.Null(ScheduledTaskPresentation.NextRunText(
            Item(fireAt: Now.AddDays(-1), lastRunAt: Now.AddDays(-1)), Now, Culture));

    [Fact]
    public void ANonPresetCronUsesTheEvaluatorsAnswer() =>
        Assert.Equal("tomorrow at 12:00 AM", ScheduledTaskPresentation.NextRunText(
            Item(cron: "0 0 1 * *") with { NextRunAt = Now.AddDays(1).AddHours(-12) }, Now, Culture));

    // ---- list --------------------------------------------------------------

    [Fact]
    public void TheDescriptionPreviewPrefersTheDescription() =>
        Assert.Equal("A brief", ScheduledTaskPresentation.DescriptionPreview(
            Item(description: "A brief") with { Prompt = "the prompt" }));

    [Fact]
    public void ThePreviewFallsBackToThePrompt() =>
        Assert.Equal("the prompt", ScheduledTaskPresentation.DescriptionPreview(
            Item() with { Prompt = "the prompt" }));

    [Fact]
    public void ThePreviewCollapsesWhitespace() =>
        Assert.Equal("one two", ScheduledTaskPresentation.DescriptionPreview(
            Item(description: "  one \n\t two  ")));

    [Fact]
    public void ThePreviewIsCutAtTheReferenceLength()
    {
        var preview = ScheduledTaskPresentation.DescriptionPreview(Item(description: new string('x', 400)));
        Assert.Equal(280, preview!.Length);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingToPreviewIsNull() =>
        Assert.Null(ScheduledTaskPresentation.DescriptionPreview(Item()));

    [Theory]
    [InlineData(1, "1 run")]
    [InlineData(0, "0 runs")]
    [InlineData(12, "12 runs")]
    public void TheRunCountIsPluralised(int count, string expected) =>
        Assert.Equal(expected, ScheduledTaskPresentation.RunCountLabel(count));

    [Fact]
    public void SearchMatchesNameAndDescription()
    {
        var items = new[]
        {
            Item(name: "morning-brief"),
            Item(name: "nightly", description: "A morning summary"),
            Item(name: "other"),
        };

        Assert.Equal(2, ScheduledTaskPresentation.Filter(items, "morning", null).Count);
        Assert.Equal(3, ScheduledTaskPresentation.Filter(items, "  ", null).Count);
        Assert.Empty(ScheduledTaskPresentation.Filter(items, "nothing", null));
    }

    [Fact]
    public void SearchIsCaseInsensitive() =>
        Assert.Single(ScheduledTaskPresentation.Filter([Item(name: "morning-brief")], "MORNING", null));

    [Fact]
    public void TheStatusFilterKeepsOnlyThatStatus()
    {
        var items = new[]
        {
            Item(name: "live"),
            Item(name: "stopped", enabled: false),
            Item(name: "broken", enabled: false, ended: RoutineEndedReasons.InvalidCron),
        };

        Assert.Equal("live", ScheduledTaskPresentation.Filter(items, "", ScheduledStatus.Active).Single().Id);
        Assert.Equal("stopped", ScheduledTaskPresentation.Filter(items, "", ScheduledStatus.Paused).Single().Id);
        Assert.Equal("broken", ScheduledTaskPresentation.Filter(items, "", ScheduledStatus.AutoDisabled).Single().Id);
    }

    [Fact]
    public void SortByNameIsAlphabetical()
    {
        var items = new[] { Item(name: "zulu"), Item(name: "alpha") };
        var sorted = ScheduledTaskPresentation.Sort(items, ScheduledSort.Name, Now);
        Assert.Equal(["alpha", "zulu"], sorted.Select(static i => i.Id));
    }

    [Fact]
    public void SortByNextRunPutsEverythingStoppedLast()
    {
        var items = new[]
        {
            Item(name: "paused", enabled: false, cron: "0 9 * * *"),
            Item(name: "later", cron: "0 18 * * *"),
            Item(name: "sooner", cron: "0 13 * * *"),
        };

        Assert.Equal(
            ["sooner", "later", "paused"],
            ScheduledTaskPresentation.Sort(items, ScheduledSort.NextRun, Now).Select(static i => i.Id));
    }

    [Theory]
    [InlineData(true, true, "No routines match your search and filters.")]
    [InlineData(true, false, "No routines match your search.")]
    [InlineData(false, true, "No routines match these filters.")]
    public void TheEmptyResultNamesWhatEmptiedIt(bool searching, bool filtering, string expected) =>
        Assert.Equal(expected, ScheduledTaskPresentation.EmptyResultMessage(searching, filtering));
}
