using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using JarvisCode.Core.Routines;

namespace JarvisCode.App.Services;

/// <summary>Hosts register their live conversation; cron prompts enter only while it is idle.</summary>
public static class SessionCronDispatch
{
    private sealed record Host(Func<bool> Idle, Action<string> Enqueue)
    {
        public int Reserved;
    }
    private static readonly ConcurrentDictionary<string, Host> Hosts = new(StringComparer.Ordinal);
    public static void Register(string sessionId, Func<bool> isIdle, Action<string> enqueue)
        => Hosts[sessionId] = new Host(isIdle, enqueue);
    public static void Unregister(string sessionId) => Hosts.TryRemove(sessionId, out _);
    public static bool IsRegistered(string sessionId) => Hosts.ContainsKey(sessionId);
    public static bool IsIdle(string sessionId) => Hosts.TryGetValue(sessionId, out var host) && host.Reserved == 0 && host.Idle();
    public static bool AnyBusy => Hosts.Values.Any(host => host.Reserved != 0 || !host.Idle());
    public static bool TryEnqueue(string sessionId, string prompt, Action? accepted = null)
    {
        if (!Hosts.TryGetValue(sessionId, out var host) || !host.Idle() || Interlocked.CompareExchange(ref host.Reserved, 1, 0) != 0)
            return false;
        try
        {
            accepted?.Invoke();
            host.Enqueue(prompt);
        }
        catch
        {
            host.Reserved = 0;
            throw;
        }
        return true;
    }
}

/// <summary>
/// CLI 2.1.260 H5e/Abt defaults, read from the installed binary. The implementation
/// uses 0.5 / 30 minutes even though that build's tool prose still says 0.1 / 15.
/// </summary>
public static class SessionCronTiming
{
    public static DateTime? NextDispatch(Routine routine, DateTime boundary)
    {
        if (CronSchedule.TryParse(routine.CronExpression) is not { } cron || cron.NextAfter(boundary) is not { } next)
            return null;
        var seed = uint.TryParse(routine.Id[..Math.Min(8, routine.Id.Length)], NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var prefix) ? prefix / 4294967296d : 0;
        if (routine.OneShot)
            return next.Minute % 30 == 0 ? new DateTime(Math.Max(boundary.Ticks, next.AddMilliseconds(-seed * 90000).Ticks), next.Kind) : next;
        if (cron.NextAfter(next) is not { } following) return next;
        var period = following - next;
        if (Regex.IsMatch(routine.CronExpression!, @"^\*/\d+ \* \* \* \*$") &&
            period.TotalMilliseconds >= 300000 && period.TotalMilliseconds - 15000 < 300000)
            return boundary + period - TimeSpan.FromSeconds(15);
        return next.AddMilliseconds(Math.Min(seed * 0.5 * period.TotalMilliseconds, 1800000));
    }
}
