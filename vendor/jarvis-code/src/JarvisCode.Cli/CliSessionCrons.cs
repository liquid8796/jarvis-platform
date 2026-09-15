using JarvisCode.App.Services;
using JarvisCode.Core.Routines;

namespace JarvisCode.Cli;

/// <summary>Session cron dispatch remains alive across print/REPL turns and never fires mid-query.</summary>
internal sealed class CliSessionCrons : IDisposable
{
    private readonly RoutineStore _store;
    private readonly string _sessionId;
    private readonly Func<bool> _isIdle;
    private readonly Action<string> _enqueue;
    private readonly string? _workingDirectory;
    private readonly Timer? _timer;
    private int _ticking;
    private volatile bool _disposed;

    public CliSessionCrons(RoutineStore store, string sessionId, Func<bool> isIdle, Action<string> enqueue,
        bool automaticTick = true, string? workingDirectory = null)
    {
        _store = store; _sessionId = sessionId; _isIdle = isIdle; _enqueue = enqueue;
        _workingDirectory = workingDirectory;
        if (automaticTick) _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public bool HasPendingWork => !_disposed && (Volatile.Read(ref _ticking) != 0 || _store.Load().Any(Eligible));
    private bool Eligible(Routine job) => job.Enabled && job.CronSessionId is not null &&
        CronSchedule.TryParse(job.CronExpression) is not null &&
        (job.OwnerSessionId == _sessionId || (job.OwnerSessionId is null &&
            (job.CronSessionId == _sessionId || (_workingDirectory is not null &&
                CronTools.SameWorkingDirectory(job.WorkingDirectory, _workingDirectory)))));

    public void Tick(DateTime? at = null)
    {
        if (_disposed || !_isIdle() || Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;
        try
        {
            var now = at ?? DateTime.Now;
            foreach (var job in _store.Load().Where(Eligible))
            {
                if (_disposed || !_isIdle()) return;
                var boundary = job.LastRunSlot ?? job.CreatedAt;
                if (SessionCronTiming.NextDispatch(job, boundary) is not { } due || due > now) continue;
                var expired = job.ExpiresAt is { } expiry && now >= expiry;
                string? prompt = null;
                _store.Update(rows =>
                {
                    var current = rows.FirstOrDefault(r => r.Id == job.Id && Eligible(r));
                    if (current is null || current.LastRunSlot != job.LastRunSlot) return;
                    current.LastRunSlot = now;
                    current.LastRunAt = now;
                    prompt = current.Instruction + (expired
                        ? "\n[This recurring cron job has expired after seven days. This is its final run.]" : "");
                    if (current.OneShot || expired) rows.Remove(current);
                });
                if (prompt is not null && !_disposed)
                {
                    _enqueue(prompt);
                    return; // next tick checks that the first dispatch has become idle again
                }
            }
        }
        catch (Exception ex) { JarvisCode.Core.Utilities.DiagnosticLog.Write("cron dispatch failed: " + ex.Message); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _store.RemoveOwnedBy(_sessionId);
        SessionCronDispatch.Unregister(_sessionId);
    }
}
