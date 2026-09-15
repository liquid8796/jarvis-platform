using JarvisCode.App.Infrastructure;

namespace JarvisCode.App.ViewModels;

/// <summary>What the running turn is doing right now, as the status line phrases it.</summary>
public enum TurnPhase
{
    /// <summary>No turn in flight — the line is hidden.</summary>
    Idle,

    /// <summary>The request is out and no delta has arrived: "Waiting for Jarvis…".</summary>
    WaitingForModel,

    /// <summary>Thinking deltas are streaming: the elapsed-bucketed "Thinking…" ladder.</summary>
    Thinking,

    /// <summary>Thinking ended and text is streaming: "Thought for {n}s".</summary>
    ThoughtFor,

    /// <summary>Text is streaming with no preceding thinking — no status label.</summary>
    Streaming,

    /// <summary>Tool calls are executing: "Running tools…".</summary>
    RunningTools,

    /// <summary>/compact is summarizing history: "Compacting session…".</summary>
    Compacting,

    /// <summary>Cancel was requested and the turn is draining: "Stopping…".</summary>
    Stopping,
}

/// <summary>
/// Live state behind the turn status line at the transcript tail — the reference
/// app's progress indicator (spinner · elapsed · tokens · phase label). The view
/// model narrates the turn into it; <see cref="Controls.TurnStatusLine"/> renders
/// and animates it.
/// </summary>
public sealed class TurnStatus : ObservableObject
{
    private TurnPhase _phase = TurnPhase.Idle;
    private DateTimeOffset? _startedAt;
    private long _outputTokens;
    private int _thoughtForSeconds;
    private DateTimeOffset? _thinkingStartedAt;
    private int _runningCalls;

    public TurnPhase Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    /// <summary>When the turn started; the elapsed timer counts from here.</summary>
    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        private set => SetProperty(ref _startedAt, value);
    }

    public long OutputTokens
    {
        get => _outputTokens;
        set => SetProperty(ref _outputTokens, value);
    }

    /// <summary>Seconds the last thinking block ran, for "Thought for {n}s".</summary>
    public int ThoughtForSeconds
    {
        get => _thoughtForSeconds;
        private set => SetProperty(ref _thoughtForSeconds, value);
    }

    public void BeginTurn(DateTimeOffset now)
    {
        _thinkingStartedAt = null;
        _runningCalls = 0;
        OutputTokens = 0;
        ThoughtForSeconds = 0;
        StartedAt = now;
        Phase = TurnPhase.WaitingForModel;
    }

    public void BeginCompacting(DateTimeOffset now)
    {
        _thinkingStartedAt = null;
        _runningCalls = 0;
        OutputTokens = 0;
        ThoughtForSeconds = 0;
        StartedAt = now;
        Phase = TurnPhase.Compacting;
    }

    public void EndTurn()
    {
        Phase = TurnPhase.Idle;
        StartedAt = null;
        _thinkingStartedAt = null;
        _runningCalls = 0;
    }

    public void OnThinkingDelta(DateTimeOffset now)
    {
        if (Phase == TurnPhase.Stopping)
        {
            return;
        }

        if (Phase != TurnPhase.Thinking)
        {
            _thinkingStartedAt = now;
            Phase = TurnPhase.Thinking;
        }
    }

    public void OnTextDelta(DateTimeOffset now)
    {
        switch (Phase)
        {
            case TurnPhase.Thinking:
                ThoughtForSeconds = _thinkingStartedAt is { } start
                    ? Math.Max(0, (int)(now - start).TotalSeconds)
                    : 0;
                Phase = TurnPhase.ThoughtFor;
                break;
            case TurnPhase.WaitingForModel or TurnPhase.RunningTools:
                Phase = TurnPhase.Streaming;
                break;
        }
    }

    /// <summary>The assistant message closed; any "Thought for" label retires.</summary>
    public void OnMessageCompleted()
    {
        if (Phase is TurnPhase.Thinking or TurnPhase.ThoughtFor or TurnPhase.Streaming)
        {
            Phase = TurnPhase.Streaming;
        }
    }

    public void OnToolStarted()
    {
        _runningCalls++;
        if (Phase != TurnPhase.Stopping)
        {
            Phase = TurnPhase.RunningTools;
        }
    }

    public void OnToolFinished()
    {
        if (_runningCalls > 0 && --_runningCalls == 0 && Phase == TurnPhase.RunningTools)
        {
            // The loop feeds the results straight back to the model.
            Phase = TurnPhase.WaitingForModel;
        }
    }

    /// <summary>A provider-side tool (web search) reported activity.</summary>
    public void OnServerToolActivity()
    {
        if (Phase != TurnPhase.Stopping)
        {
            Phase = TurnPhase.RunningTools;
        }
    }

    /// <summary>Mid-turn auto-compaction started; the elapsed timer keeps running.</summary>
    public void OnCompacting()
    {
        if (Phase != TurnPhase.Idle && Phase != TurnPhase.Stopping)
        {
            Phase = TurnPhase.Compacting;
        }
    }

    /// <summary>Mid-turn auto-compaction finished; the request goes back out.</summary>
    public void OnCompacted()
    {
        if (Phase == TurnPhase.Compacting)
        {
            Phase = TurnPhase.WaitingForModel;
        }
    }

    /// <summary>Dev/verification hook: poses the line without a live turn.</summary>
    public void ShowSample(TurnPhase phase, DateTimeOffset startedAt, long tokens)
    {
        StartedAt = startedAt;
        OutputTokens = tokens;
        Phase = phase;
    }

    public void OnStopping()
    {
        if (Phase != TurnPhase.Idle)
        {
            Phase = TurnPhase.Stopping;
        }
    }
}
