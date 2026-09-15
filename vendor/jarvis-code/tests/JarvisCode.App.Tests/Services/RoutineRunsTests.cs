using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Routines;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The run history behind the Scheduled detail page, and the rules the runner
/// uses to decide that a slot was missed, skipped or no longer schedulable.
/// </summary>
public class RoutineRunsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "jarvis-routine-runs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory the OS still holds open is not a test failure.
        }
    }

    private RoutineRunStore Store() => new(_root);

    [Fact]
    public void AnAbsentHistoryIsEmptyRatherThanAFailure() =>
        Assert.Empty(Store().Load("never-ran"));

    [Fact]
    public void EntriesRoundTrip()
    {
        var store = Store();
        var when = DateTimeOffset.Now;
        store.Append("brief", new RoutineRunEntry
        {
            Time = when,
            SessionId = "session-1",
            Summary = "Nothing changed",
        });

        var entry = Assert.Single(store.Load("brief"));
        Assert.Equal(RoutineRunEntry.RunKind, entry.Kind);
        Assert.Equal("session-1", entry.SessionId);
        Assert.Equal("Nothing changed", entry.Summary);
        Assert.False(entry.IsMissed);
        Assert.Equal(when.ToUnixTimeSeconds(), entry.Time.ToUnixTimeSeconds());
    }

    [Fact]
    public void HistoryIsNewestFirst()
    {
        var store = Store();
        var now = DateTimeOffset.Now;
        store.Append("brief", new RoutineRunEntry { Time = now.AddHours(-2) });
        store.Append("brief", new RoutineRunEntry { Time = now });
        store.Append("brief", new RoutineRunEntry { Time = now.AddHours(-1) });

        var times = store.Load("brief").Select(static e => e.Time).ToList();
        Assert.Equal([.. times.OrderByDescending(static t => t)], times);
    }

    [Fact]
    public void OnlyRunsAreCounted()
    {
        var store = Store();
        var now = DateTimeOffset.Now;
        store.Append("brief", new RoutineRunEntry { Time = now });
        store.Append("brief", new RoutineRunEntry
        {
            Kind = RoutineRunEntry.MissedKind,
            Time = now.AddMinutes(-1),
            Reason = RoutineSkipReasons.GlobalLimit,
        });

        Assert.Equal(1, store.RunCount("brief"));
        Assert.Equal(2, store.Load("brief").Count);
    }

    [Fact]
    public void TheOldestEntriesAreDroppedPastTheCap()
    {
        var store = Store();
        var now = DateTimeOffset.Now;
        for (var i = 0; i < RoutineRunStore.MaxEntries + 25; i++)
        {
            store.Append("brief", new RoutineRunEntry { Time = now.AddMinutes(i) });
        }

        var entries = store.Load("brief");
        Assert.Equal(RoutineRunStore.MaxEntries, entries.Count);
        // The newest survived; the first 25 minutes are gone.
        Assert.Equal(now.AddMinutes(25).ToUnixTimeSeconds(), entries[^1].Time.ToUnixTimeSeconds());
    }

    [Fact]
    public void DeletingDropsTheHistory()
    {
        var store = Store();
        store.Append("brief", new RoutineRunEntry { Time = DateTimeOffset.Now });
        store.Delete("brief");
        Assert.Empty(store.Load("brief"));
    }

    [Fact]
    public void DeletingSomethingThatIsNotThereIsFine() => Store().Delete("never-existed");

    [Theory]
    [InlineData("daily-brief", "daily-brief")]
    [InlineData("../escape", "escape")]
    [InlineData("a/b\\c", "a-b-c")]
    [InlineData("!!!", "routine")]
    public void AnIdCannotEscapeTheDirectory(string id, string expected) =>
        Assert.Equal(expected, RoutineRunStore.SanitizeId(id));

    [Fact]
    public void AGuidIdSurvivesSanitising()
    {
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(id, RoutineRunStore.SanitizeId(id));
    }

    // ---- the runner's rules ------------------------------------------------

    [Fact]
    public void NothingRunningMeansNothingIsSkipped() =>
        Assert.Null(RoutineRunner.SkipReason(anyRunning: false, thisRunning: false));

    [Fact]
    public void AnotherRoutineHoldingTheGateIsTheGlobalLimit() =>
        Assert.Equal(
            RoutineSkipReasons.GlobalLimit,
            RoutineRunner.SkipReason(anyRunning: true, thisRunning: false));

    [Fact]
    public void ARoutineStillRunningItsOwnTurnIsThePerTaskLimit() =>
        Assert.Equal(
            RoutineSkipReasons.PerTaskLimit,
            RoutineRunner.SkipReason(anyRunning: true, thisRunning: true));

    [Fact]
    public void AGoodCronIsNotAutoDisabled() =>
        Assert.Null(RoutineRunner.AutoDisableReason(new Routine { CronExpression = "0 9 * * *" }));

    [Fact]
    public void ARoutineWithNoCronIsNotAutoDisabled() =>
        Assert.Null(RoutineRunner.AutoDisableReason(new Routine()));

    [Fact]
    public void ABrokenCronNamesItsReason() =>
        Assert.Equal(
            RoutineEndedReasons.InvalidCron,
            RoutineRunner.AutoDisableReason(new Routine { CronExpression = "0 9 * *" }));

    [Fact]
    public void ASubHourlyCronNamesItsOwnReason() =>
        Assert.Equal(
            RoutineEndedReasons.SubHourly,
            RoutineRunner.AutoDisableReason(new Routine { CronExpression = "*/5 * * * *" }));

    [Fact]
    public void AOneTimeRoutineIsDueOnceItsMomentArrives()
    {
        var now = DateTimeOffset.Now;
        Assert.True(RoutineRunner.OneTimeDue(new Routine { FireAt = now.AddMinutes(-1) }, now));
        Assert.False(RoutineRunner.OneTimeDue(new Routine { FireAt = now.AddMinutes(1) }, now));
    }

    [Fact]
    public void AOneTimeRoutineThatAlreadyFiredIsNotDueAgain()
    {
        var now = DateTimeOffset.Now;
        var routine = new Routine
        {
            FireAt = now.AddMinutes(-1),
            EndedReason = RoutineEndedReasons.RunOnceFired,
            Enabled = false,
        };
        Assert.False(RoutineRunner.OneTimeDue(routine, now));
    }

    [Fact]
    public void APausedOneTimeRoutineIsNotDue() =>
        Assert.False(RoutineRunner.OneTimeDue(
            new Routine { FireAt = DateTimeOffset.Now.AddMinutes(-1), Enabled = false }, DateTimeOffset.Now));
}
