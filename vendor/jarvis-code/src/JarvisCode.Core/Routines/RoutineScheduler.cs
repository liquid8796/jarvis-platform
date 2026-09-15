namespace JarvisCode.Core.Routines;

/// <summary>
/// Pure slot math for <see cref="Routine"/> schedules. A "slot" is the exact
/// local time an occurrence is meant to fire. Missed slots (app closed, machine
/// asleep) are caught up at most once: only the most recent missed slot runs,
/// and only when it is at most <see cref="MissedRunWindow"/> old.
/// </summary>
public static class RoutineScheduler
{
    /// <summary>Missed slots older than this are discarded instead of retried.</summary>
    public static readonly TimeSpan MissedRunWindow = TimeSpan.FromDays(7);

    /// <summary>The next slot strictly after <paramref name="after"/>, or null for Manual.</summary>
    public static DateTime? NextSlot(Routine routine, DateTime after)
    {
        switch (routine.Schedule)
        {
            case RoutineSchedule.Manual:
                return null;
            case RoutineSchedule.Hourly:
            {
                var hour = new DateTime(after.Year, after.Month, after.Day, after.Hour, 0, 0, after.Kind);
                return hour > after ? hour : hour.AddHours(1);
            }
            case RoutineSchedule.Daily:
            {
                var slot = after.Date.AddMinutes(routine.TimeOfDayMinutes);
                return slot > after ? slot : slot.AddDays(1);
            }
            case RoutineSchedule.Weekdays:
            {
                var slot = after.Date.AddMinutes(routine.TimeOfDayMinutes);
                if (slot <= after)
                    slot = slot.AddDays(1);
                while (slot.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    slot = slot.AddDays(1);
                return slot;
            }
            case RoutineSchedule.Weekly:
            {
                var slot = after.Date.AddMinutes(routine.TimeOfDayMinutes);
                while (slot.DayOfWeek != routine.Day || slot <= after)
                    slot = slot.AddDays(1);
                return slot;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(routine), routine.Schedule, "Unknown schedule");
        }
    }

    /// <summary>The most recent slot at or before <paramref name="at"/>, or null for Manual.</summary>
    public static DateTime? PreviousSlot(Routine routine, DateTime at)
    {
        switch (routine.Schedule)
        {
            case RoutineSchedule.Manual:
                return null;
            case RoutineSchedule.Hourly:
                return new DateTime(at.Year, at.Month, at.Day, at.Hour, 0, 0, at.Kind);
            case RoutineSchedule.Daily:
            {
                var slot = at.Date.AddMinutes(routine.TimeOfDayMinutes);
                return slot <= at ? slot : slot.AddDays(-1);
            }
            case RoutineSchedule.Weekdays:
            {
                var slot = at.Date.AddMinutes(routine.TimeOfDayMinutes);
                if (slot > at)
                    slot = slot.AddDays(-1);
                while (slot.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    slot = slot.AddDays(-1);
                return slot;
            }
            case RoutineSchedule.Weekly:
            {
                var slot = at.Date.AddMinutes(routine.TimeOfDayMinutes);
                while (slot.DayOfWeek != routine.Day || slot > at)
                    slot = slot.AddDays(-1);
                return slot;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(routine), routine.Schedule, "Unknown schedule");
        }
    }

    /// <summary>
    /// The slot that should run now, if any: the most recent slot at or before
    /// <paramref name="now"/> that is newer than the last run (or the creation
    /// time) and no older than <see cref="MissedRunWindow"/>. Yields at most one
    /// catch-up run no matter how many slots were missed.
    /// </summary>
    public static DateTime? DueSlot(Routine routine, DateTime now)
    {
        if (!routine.Enabled)
            return null;
        var slot = PreviousSlot(routine, now);
        if (slot is null)
            return null;
        var boundary = routine.LastRunSlot ?? routine.CreatedAt;
        if (slot <= boundary)
            return null;
        // Defensive: the built-in presets are at most a week apart, so their most
        // recent slot can never exceed the window; this only bites for schedules
        // sparser than seven days, should one ever be added.
        if (now - slot.Value > MissedRunWindow)
            return null;
        return slot;
    }

    /// <summary>Human-readable schedule summary for list rows.</summary>
    public static string Describe(Routine routine)
    {
        if (routine.CronExpression is { Length: > 0 } cron)
            return routine.OneShot ? $"Once, at the next match of {cron}" : $"Cron {cron}";
        var time = TimeSpan.FromMinutes(routine.TimeOfDayMinutes);
        var clock = $"{(DateTime.Today + time):HH:mm}";
        return routine.Schedule switch
        {
            RoutineSchedule.Manual => "Manual",
            RoutineSchedule.Hourly => "Hourly, on the hour",
            RoutineSchedule.Daily => $"Daily at {clock}",
            RoutineSchedule.Weekdays => $"Weekdays at {clock}",
            RoutineSchedule.Weekly => $"{routine.Day}s at {clock}",
            _ => routine.Schedule.ToString(),
        };
    }
}
