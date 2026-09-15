using System.Security.Cryptography;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// The deterministic dispatch delay the reference applies to recurring tasks,
/// and which its create/update tool docs describe as "a small deterministic
/// delay of several minutes at dispatch time to balance server load".
///
/// Ported from <c>getJitterSecondsForTask</c> in <c>index.chunk-C5__TEgr.js</c>
/// (desktop 1.40609.0.0) and the two helpers it calls, <c>W3n</c> and
/// <c>R3n</c>:
///
/// <code>
/// t = dispatchJitterMaxMinutes (10)
/// if (t &lt;= 0 || !task.cronExpression || task.disableJitter) return 0
/// r = interval-between-firings-in-minutes(cron) or null
/// i = r === null ? t : min(t, r - 1)
/// return i &lt;= 0 ? 0 : sha256(id).readUInt32BE(0) % (i * 60)
/// </code>
///
/// A one-time or ad-hoc task gets no jitter — the reference's own note says
/// "One-time tasks fire without delay" — and the bound on <c>r</c> is what keeps
/// a task that fires every five minutes from being delayed by ten.
/// </summary>
internal static class ScheduledTaskJitter
{
    /// <summary>The reference's <c>dispatchJitterMaxMinutes</c> default.</summary>
    public const int MaxMinutes = 10;

    /// <summary>
    /// How long after its scheduled moment a task actually dispatches. Zero for
    /// anything without a cron expression.
    /// </summary>
    public static int SecondsFor(string taskId, string? cronExpression, int maxMinutes = MaxMinutes)
    {
        if (maxMinutes <= 0 || string.IsNullOrWhiteSpace(cronExpression))
        {
            return 0;
        }

        var bound = IntervalMinutes(cronExpression) is { } interval
            ? Math.Min(maxMinutes, interval - 1)
            : maxMinutes;
        return bound <= 0 ? 0 : (int)(Hash(taskId) % (uint)(bound * 60));
    }

    /// <summary>
    /// <c>W3n</c>: the first four bytes of the id's SHA-256, read big-endian.
    /// Deterministic per task, so a task's delay does not move between runs.
    /// </summary>
    internal static uint Hash(string taskId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(taskId));
        return ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];
    }

    /// <summary>
    /// <c>R3n</c>: minutes between two consecutive firings, by walking the next
    /// 60 minutes from the top of the next minute and measuring the gap between
    /// the first two that fall in different hours. Null when the expression does
    /// not fire twice inside that window, which is the common case and means
    /// "no tighter bound than the maximum".
    /// </summary>
    internal static int? IntervalMinutes(string cronExpression, int horizonMinutes = 60)
    {
        if (CronSchedule.TryParse(cronExpression) is not { } cron)
        {
            return null;
        }

        var cursor = DateTime.Now;
        cursor = new DateTime(
            cursor.Year, cursor.Month, cursor.Day, cursor.Hour, cursor.Minute, 0, cursor.Kind)
            .AddMinutes(1);

        DateTime? first = null;
        var firstHour = 0;
        for (var i = 0; i < horizonMinutes; i++, cursor = cursor.AddMinutes(1))
        {
            if (!cron.Matches(cursor))
            {
                continue;
            }

            if (first is null)
            {
                first = cursor;
                firstHour = cursor.Hour;
            }
            else if (cursor.Hour != firstHour)
            {
                return (int)Math.Round((cursor - first.Value).TotalMinutes);
            }
        }

        return null;
    }
}
