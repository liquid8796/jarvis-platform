using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Services;

/// <summary>How a run ended, in the reference's own three states.</summary>
public enum RunStatus
{
    Running,
    Failed,
    Completed,
}

/// <summary>One row of the Runs pane.</summary>
/// <param name="SessionId">The session that run produced.</param>
/// <param name="Label">The row's own line — the reference shows the run's date, relatively.</param>
/// <param name="Status">Running, Failed or Completed.</param>
/// <param name="IsCurrent">Whether this is the session the pane is open in.</param>
public sealed record RunRow(string SessionId, string Label, RunStatus Status, bool IsCurrent);

/// <summary>
/// The Runs pane's model, ported from the reference's uN/pN/wN (ion-dist chunk
/// c360a9e1c-DUoNQd2W.js at the "Not a scheduled run" literal): the runs of the routine
/// this session belongs to, split into a Running and a Completed section, newest first.
/// </summary>
public static class RunHistoryPresentation
{
    /// <summary>The marker RoutineRunner writes into a run it had to cut short.</summary>
    public const string TimedOutMarker = "[routine run timed out]";

    public static string StatusLabel(RunStatus status) => status switch
    {
        RunStatus.Running => "Running",
        RunStatus.Failed => "Failed",
        _ => "Completed",
    };

    /// <summary>
    /// The rows for one routine. A session is Running only while the host says its turn is
    /// live; a run RoutineRunner had to abandon carries its timeout marker and reads Failed.
    /// </summary>
    public static IReadOnlyList<RunRow> Build(
        IEnumerable<SessionSummary> sessions,
        string routineId,
        string currentSessionId,
        IReadOnlyCollection<string> runningSessionIds,
        IReadOnlyCollection<string> failedSessionIds,
        DateTimeOffset now)
    {
        var rows = new List<RunRow>();
        foreach (var session in sessions.Where(s => s.RoutineId == routineId).OrderByDescending(s => s.UpdatedAt))
        {
            var status = runningSessionIds.Contains(session.Id)
                ? RunStatus.Running
                : failedSessionIds.Contains(session.Id)
                    ? RunStatus.Failed
                    : RunStatus.Completed;
            rows.Add(new RunRow(
                session.Id,
                RelativeTime(session.UpdatedAt, now),
                status,
                session.Id == currentSessionId));
        }

        return rows;
    }

    /// <summary>The reference's relative past label, in the spelling this app already uses.</summary>
    public static string RelativeTime(DateTimeOffset at, DateTimeOffset now)
    {
        var delta = now - at;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var minutes = (int)delta.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            var hours = (int)delta.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        var days = (int)delta.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }
}
