using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Routines;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Services;

/// <summary>
/// The scheduler the engine leaves to the host: while the app is open, a
/// minute timer fires due routines; each run is a fresh headless Code session
/// saved into the normal session store. Unattended runs use a promptless gate,
/// so anything that would need a human is denied instead of hanging.
/// </summary>
public sealed class RoutineRunner : IDisposable
{
    private readonly AppServices _services;
    private readonly RoutineStore _store;
    private readonly ScheduledTaskStore _tasks;
    private readonly RoutineRunStore _runs;
    private readonly TurnContextFactory _factory;
    private readonly DispatcherTimer _timer;
    private bool _running;
    private string? _runningRoutineId;

    public event Action<Routine, Session>? RoutineCompleted;

    /// <summary>A run that ended in anything but a completed turn, for the failure notification.</summary>
    public event Action<Routine, Session>? RoutineFailed;

    /// <summary>
    /// A slot that had already passed when the app got to it. The reference
    /// says so and runs it anyway: "Routine “{name}” missed at {time}. Running now."
    /// </summary>
    public event Action<Routine, DateTime>? RoutineMissed;

    /// <summary>A due slot that was dropped, with one of <see cref="RoutineSkipReasons"/>.</summary>
    public event Action<Routine, string>? RoutineSkipped;

    /// <summary>A run that started, for the "Routine “{name}” started." notice.</summary>
    public event Action<Routine>? RoutineStarted;

    public RoutineRunner(AppServices services)
    {
        _services = services;
        _store = new RoutineStore(services.Paths.RoutinesFile);
        _tasks = new ScheduledTaskStore(services.Paths.ScheduledTasksDirectory);
        _runs = new RoutineRunStore(services.Paths.RoutineRunsDirectory);
        _factory = new TurnContextFactory(services);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    /// <summary>
    /// A CronCreate job that was not durable lived in the session that made it;
    /// this process is a new one, so whatever it left in the store is gone now —
    /// the reference's in-memory job simply does not survive.
    /// </summary>
    private void SweepNonDurable()
    {
        var routines = _store.Load().ToList();
        if (routines.RemoveAll(static r => r.OwnerSessionId is not null) > 0)
        {
            _store.Save(routines);
        }
    }

    /// <summary>
    /// The due slot of a cron routine: the first match after its last run (or its
    /// creation) that has already passed. Like the presets, one catch-up at most.
    /// </summary>
    internal static DateTime? CronDueSlot(Routine routine, DateTime now)
    {
        if (!routine.Enabled || CronSchedule.TryParse(routine.CronExpression) is not { } cron)
        {
            return null;
        }

        var boundary = routine.LastRunSlot ?? routine.CreatedAt;
        var next = routine.CronSessionId is null ? cron.NextAfter(boundary) : SessionCronTiming.NextDispatch(routine, boundary);
        return next is { } slot && slot <= now ? slot : null;
    }

    public RoutineStore Store => _store;

    /// <summary>The desktop's scheduled tasks, fired by the same minute tick.</summary>
    public ScheduledTaskStore ScheduledTasks => _tasks;

    /// <summary>The History behind the Scheduled detail page.</summary>
    public RoutineRunStore Runs => _runs;

    /// <summary>True while any routine run holds the single-run gate.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// A slot older than this counts as missed rather than as this minute's
    /// tick: the timer fires once a minute, so anything past two of them was
    /// waiting while the app was closed or the machine asleep.
    /// </summary>
    internal static readonly TimeSpan MissedThreshold = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Why a routine cannot run right now, or null when it can: the reference's
    /// two skip reasons, its own run first.
    /// </summary>
    internal static string? SkipReason(bool anyRunning, bool thisRunning) =>
        thisRunning ? RoutineSkipReasons.PerTaskLimit
        : anyRunning ? RoutineSkipReasons.GlobalLimit
        : null;

    /// <summary>
    /// The reason a stored routine can no longer be scheduled, or null. These
    /// are the two of the reference's twenty-three auto-disable reasons a local
    /// scheduler can determine for itself; both are fixed by editing the schedule.
    /// </summary>
    internal static string? AutoDisableReason(Routine routine)
    {
        if (routine.CronExpression is not { Length: > 0 } cron)
        {
            return null;
        }

        if (routine.CronSessionId is not null)
            return CronSchedule.TryParse(cron) is null ? RoutineEndedReasons.InvalidCron : null;

        return ScheduleFrequencies.Validate(cron) switch
        {
            CronProblem.SubHourly => RoutineEndedReasons.SubHourly,
            CronProblem.None => null,
            _ => RoutineEndedReasons.InvalidCron,
        };
    }

    /// <summary>A one-time routine whose moment has come and which has not run yet.</summary>
    internal static bool OneTimeDue(Routine routine, DateTimeOffset now) =>
        routine.Enabled && routine.EndedReason is null &&
        routine.FireAt is { } fireAt && fireAt <= now;

    /// <summary>Raised when a scheduled task finishes, for notifyOnCompletion.</summary>
    public event Action<ScheduledTask, Session>? ScheduledTaskCompleted;

    public async Task TickAsync()
    {
        var routines = _store.Load().ToList();
        var now = DateTime.Now;
        var dirty = routines.RemoveAll(r => r.CronSessionId is null && r.ExpiresAt is { } expires && now >= expires) > 0;

        // A routine whose schedule stopped being usable is disabled with the
        // reason, rather than silently never firing again.
        foreach (var routine in routines.Where(static r => r.Enabled && r.EndedReason is null))
        {
            if (AutoDisableReason(routine) is { } reason)
            {
                routine.Enabled = false;
                routine.EndedReason = reason;
                dirty = true;
            }
        }

        if (dirty)
        {
            _store.Save(routines);
        }

        foreach (var routine in routines.ToList())
        {
            var expiredCron = routine.CronSessionId is not null && routine.ExpiresAt is { } expiry && now >= expiry;
            if (routine.CronSessionId is { } origin)
            {
                if (SessionCronDispatch.IsRegistered(origin) ? !SessionCronDispatch.IsIdle(origin) : SessionCronDispatch.AnyBusy)
                    continue;
                if (routine.OwnerSessionId is not null && !SessionCronDispatch.IsRegistered(origin))
                {
                    routines.Remove(routine);
                    _store.Update(rows => rows.RemoveAll(r => r.Id == routine.Id));
                    continue;
                }
            }
            var oneTime = OneTimeDue(routine, now);
            var slot = oneTime
                ? routine.FireAt!.Value.LocalDateTime
                : routine.CronExpression is { Length: > 0 }
                    ? CronDueSlot(routine, now)
                    : RoutineScheduler.DueSlot(routine, now);
            if (slot is not { } due)
            {
                continue;
            }

            if (routine.CronSessionId is { } liveId && SessionCronDispatch.IsRegistered(liveId))
            {
                SessionCronDispatch.TryEnqueue(liveId, routine.Instruction +
                    (expiredCron ? "\n[This recurring cron job has expired after seven days. This is its final run.]" : ""), () =>
                    _store.Update(rows =>
                    {
                        if (rows.FirstOrDefault(r => r.Id == routine.Id) is not { } current) return;
                        current.LastRunSlot = due;
                        current.LastRunAt = now;
                        if (current.OneShot || expiredCron) rows.Remove(current);
                    }));
                continue;
            }

            if (SkipReason(_running, _runningRoutineId == routine.Id) is { } skip)
            {
                // The slot is spent either way — the reference records the miss
                // and moves on rather than queueing a second run behind the first.
                routine.LastRunSlot = due;
                _store.Save(routines);
                _runs.Append(routine.Id, new RoutineRunEntry
                {
                    Kind = RoutineRunEntry.MissedKind,
                    Time = new DateTimeOffset(due),
                    Reason = skip,
                });
                RoutineSkipped?.Invoke(routine, skip);
                continue;
            }

            routine.LastRunSlot = due;
            routine.LastRunAt = now;
            if (oneTime)
            {
                // "runs once at the given moment, then auto-disables" — kept in
                // the list as Completed, which is what the reference shows.
                routine.Enabled = false;
                routine.EndedReason = RoutineEndedReasons.RunOnceFired;
            }
            else if (routine.OneShot || expiredCron)
            {
                // recurring: false — fire once at the next match, then auto-delete.
                routines.Remove(routine);
            }

            _store.Save(routines);
            if (now - due > MissedThreshold)
            {
                RoutineMissed?.Invoke(routine, due);
            }

            await RunAsync(routine);
        }

        await TickScheduledTasksAsync(DateTimeOffset.Now);
    }

    /// <summary>
    /// Fires the scheduled tasks that are due. A task is due when its next run
    /// from the last one it took has passed — so a task whose moment went by
    /// while the app was closed runs on this tick, which is what its tool doc
    /// promises ("If the app is closed when a task is due, it runs on next
    /// launch"). A one-shot disables itself after firing.
    /// </summary>
    public async Task TickScheduledTasksAsync(DateTimeOffset now)
    {
        foreach (var task in _tasks.Load())
        {
            if (!task.Enabled || task.IsAdHoc || !IsDue(task, now))
            {
                continue;
            }

            task.LastRunAt = now;
            if (task.FireAt is not null)
            {
                // "runs once at the given moment, then auto-disables"
                task.Enabled = false;
            }

            _tasks.Save(task);
            await RunScheduledTaskAsync(task);
        }
    }

    /// <summary>
    /// Whether a task's moment has passed since it last ran. For a one-shot that
    /// is simply its fireAt; for a cron it is the first slot after the last run,
    /// or after now-minus-a-minute for a task that has never run, so starting the
    /// app does not replay every slot in its history.
    /// </summary>
    internal static bool IsDue(ScheduledTask task, DateTimeOffset now)
    {
        if (task.FireAt is { } once)
        {
            return once <= now;
        }

        if (CronSchedule.TryParse(task.CronExpression) is not { } cron)
        {
            return false;
        }

        var since = task.LastRunAt ?? now.AddMinutes(-1);
        if (cron.NextAfter(since.LocalDateTime) is not { } next)
        {
            return false;
        }

        // The reference delays a recurring task's dispatch by a deterministic
        // few minutes derived from its id, which is what its tool docs call
        // "a small deterministic delay … to balance server load". A one-time
        // task fires without delay, and so does one here: the branch above
        // already returned.
        var jitter = ScheduledTaskJitter.SecondsFor(task.TaskId, task.CronExpression);
        return next.AddSeconds(jitter) <= now.LocalDateTime;
    }

    /// <summary>Runs one scheduled task now — the detail page's "Run now".</summary>
    public async Task RunScheduledTaskAsync(ScheduledTask task)
    {
        var routine = new Routine
        {
            Id = task.TaskId,
            Name = task.Description is { Length: > 0 } d ? d : task.TaskId,
            Instruction = task.Prompt,
            Schedule = RoutineSchedule.Manual,
        };

        Session? finished = null;
        void Capture(Routine _, Session session) => finished = session;
        RoutineCompleted += Capture;
        try
        {
            await RunAsync(routine);
        }
        finally
        {
            RoutineCompleted -= Capture;
        }

        if (task.NotifyOnCompletion && finished is not null)
        {
            ScheduledTaskCompleted?.Invoke(task, finished);
        }
    }

    /// <summary>Runs one routine immediately ("Run now" or a due slot).</summary>
    public async Task RunAsync(Routine routine)
    {
        if (_running || string.IsNullOrWhiteSpace(routine.Instruction))
        {
            return;
        }

        _running = true;
        _runningRoutineId = routine.Id;
        var failed = false;
        try
        {
            var cwd = routine.WorkingDirectory is { Length: > 0 } dir && System.IO.Directory.Exists(dir)
                ? dir
                : _services.Settings.Current.LastWorkingDirectory is { Length: > 0 } last && System.IO.Directory.Exists(last)
                    ? last
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var session = Session.CreateNew(cwd);
            session.Title = $"{routine.Name} · {DateTime.Now:HH:mm}";
            session.ModelId = routine.ModelId;
            // The Runs pane finds a routine's other runs by this link; without it a
            // scheduled session is indistinguishable from one the user started.
            session.RoutineId = routine.Id;
            session.RoutineName = routine.Name;
            session.Messages.Add(ChatMessage.FromUserText(routine.Instruction));

            RoutineStarted?.Invoke(routine);

            // Promptless gate: benign calls run, everything else is denied.
            var gate = new UiPermissionGate
            {
                WorkingDirectory = cwd,
                GlobalRuleLines = _services.Settings.Current.PermissionRuleLines,
                PromptAsync = null,
            };

            // The routine's own mode, when it named one; otherwise the Settings
            // default, which is what its "Settings default" row means.
            if (routine.PermissionModeName is { Length: > 0 } modeName &&
                Enum.TryParse<PermissionMode>(modeName, ignoreCase: true, out var mode))
            {
                gate.Mode = mode;
            }

            // The tools a run was allowed are kept on the routine and re-applied to
            // later ones, which is the reference's approvedPermissions: the stored
            // keys become the session rule lines the gate already speaks.
            if (routine.ApprovedPermissions.Count > 0)
            {
                gate.AddSessionRuleLines(RoutineApprovals.RuleLines(routine.ApprovedPermissions));
            }

            // The sites a run was allowed, likewise (the reference's
            // chromePermissionMode / chromeAllowedDomains). The reference also
            // records the grant at dispatch for a task whose mode stops it asking:
            // an Auto or Bypass run is given every site.
            if (gate.Mode is PermissionMode.Auto or PermissionMode.Bypass &&
                RoutineApprovals.UpdateChrome(
                    routine, ChromePermissionModes.SkipAllPermissionChecks, routine.ChromeAllowedDomains))
            {
                PersistRoutineGrants(routine);
            }

            _services.BrowserOrigins.ApplyRoutineGrant(
                routine.ChromePermissionMode, routine.ChromeAllowedDomains);
            _grantWriteBack = (grantMode, domains) =>
            {
                if (RoutineApprovals.UpdateChrome(routine, grantMode, domains))
                {
                    PersistRoutineGrants(routine);
                }
            };
            _services.BrowserOrigins.RoutineGrantChanged += _grantWriteBack;

            var setup = _factory.CreateForCode(session, gate, routine.Instruction, todoSink: null, subagentActivity: null);
            if (setup is null)
            {
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            try
            {
                await foreach (var e in _services.Orchestrator.RunTurnAsync(setup.Context, cts.Token))
                {
                    // A run nobody watched is reported by how its turn ended;
                    // anything but a completed turn is a failure worth notifying.
                    if (e is TurnCompleted completed && completed.Reason != TurnEndReason.Completed)
                    {
                        failed = true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                session.Messages.Add(ChatMessage.FromUserText("[routine run timed out]"));
                failed = true;
            }

            session.UpdatedAt = DateTimeOffset.Now;
            await _services.Sessions.SaveAsync(session);
            _runs.Append(routine.Id, new RoutineRunEntry
            {
                Kind = RoutineRunEntry.RunKind,
                Time = session.UpdatedAt,
                SessionId = session.Id,
                Summary = LastAssistantText(session),
                Failed = failed,
            });

            RoutineCompleted?.Invoke(routine, session);
            if (failed)
            {
                RoutineFailed?.Invoke(routine, session);
            }
        }
        finally
        {
            if (_grantWriteBack is not null)
            {
                _services.BrowserOrigins.RoutineGrantChanged -= _grantWriteBack;
                _grantWriteBack = null;
            }

            _services.BrowserOrigins.ClearRoutineGrant();
            _running = false;
            _runningRoutineId = null;
        }
    }

    private Action<string?, IReadOnlyList<string>>? _grantWriteBack;

    /// <summary>Writes the run's grants back onto the stored routine.</summary>
    private void PersistRoutineGrants(Routine routine)
    {
        var routines = _store.Load();
        if (routines.FirstOrDefault(r => r.Id == routine.Id) is not { } stored)
        {
            return;
        }

        stored.ChromePermissionMode = routine.ChromePermissionMode;
        stored.ChromeAllowedDomains = [.. routine.ChromeAllowedDomains];
        stored.ApprovedPermissions = [.. routine.ApprovedPermissions];
        _store.Save(routines);
    }

    /// <summary>
    /// What the run reported, for History's "Run reported: {summary}" tooltip.
    /// The reference caps the summary at 500 characters where it renders it; the
    /// same cap is applied here so the stored history cannot grow without bound.
    /// </summary>
    internal static string? LastAssistantText(Session session)
    {
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            if (session.Messages[i].Role != Role.Assistant)
            {
                continue;
            }

            var text = session.Messages[i].GetText().Trim();
            if (text.Length > 0)
            {
                return text.Length > 500 ? text[..500] : text;
            }
        }

        return null;
    }

    public void Dispose() => _timer.Stop();
}
