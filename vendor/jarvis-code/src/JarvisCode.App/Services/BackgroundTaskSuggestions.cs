using System.Security.Cryptography;

namespace JarvisCode.App.Services;

/// <summary>One flagged out-of-scope issue, as spawn_task queued it.</summary>
public sealed record TaskSuggestion(string Id, string? Title, string Prompt, string? Tldr, string? Cwd)
{
    public TaskSuggestionState State { get; set; } = TaskSuggestionState.Pending;

    /// <summary>The chip label, as the reference renders it in its listings: id plus a quoted title.</summary>
    public string Label => Title is { Length: > 0 } title ? $"{Id} \"{title}\"" : Id;
}

public enum TaskSuggestionState
{
    Pending,

    /// <summary>The user clicked the chip; the spawn is under way and cannot be withdrawn.</summary>
    SpawnInFlight,

    Started,

    Dismissed,
}

/// <summary>Why a dismiss_task call did or did not withdraw a chip — the reference's own six outcomes.</summary>
public enum TaskDismissOutcome
{
    Dismissed,
    AlreadyStarted,
    AlreadyDismissed,
    SpawnInFlight,
    NotFound,
    SessionNotFound,
}

/// <summary>The result of queueing one suggestion.</summary>
public sealed record TaskEnqueueResult(
    int Position,
    IReadOnlyList<TaskSuggestion> Pending,
    IReadOnlyList<TaskSuggestion> Evicted);

/// <summary>
/// The queue behind mcp__ccd_session__spawn_task / dismiss_task, ported from the
/// reference desktop's background-task suggestions (1.40609.0.0): at most
/// <see cref="Capacity"/> pending chips, the oldest evicted to make room, and a
/// dismiss that reports precisely why it could not withdraw. Kept free of WPF so
/// the eviction and state rules are unit-testable.
/// </summary>
public sealed class BackgroundTaskSuggestions
{
    /// <summary>"The queue holds at most 20 pending suggestions".</summary>
    public const int Capacity = 20;

    private readonly List<TaskSuggestion> _all = [];
    private readonly Lock _lock = new();

    /// <summary>Raised whenever the pending set changes, so the chip strip can repaint.</summary>
    public event Action? Changed;

    /// <summary>The reference's id shape: <c>task_</c> plus four random bytes as hex.</summary>
    public static string NewId() => $"task_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

    /// <summary>Whether an id could have come from <see cref="NewId"/> — the reference's own regex.</summary>
    public static bool IsWellFormedId(string id) =>
        id.Length == 13 &&
        id.StartsWith("task_", StringComparison.Ordinal) &&
        id.AsSpan(5).ContainsOnlyHexLower();

    public IReadOnlyList<TaskSuggestion> Pending
    {
        get
        {
            lock (_lock)
            {
                return [.. _all.Where(static s => s.State is TaskSuggestionState.Pending)];
            }
        }
    }

    public TaskEnqueueResult Enqueue(TaskSuggestion suggestion)
    {
        List<TaskSuggestion> evicted = [];
        TaskEnqueueResult result;
        lock (_lock)
        {
            _all.Add(suggestion);

            // Oldest pending first out, so the newest suggestion always survives.
            var pending = _all.Where(static s => s.State is TaskSuggestionState.Pending).ToList();
            while (pending.Count > Capacity)
            {
                var oldest = pending[0];
                oldest.State = TaskSuggestionState.Dismissed;
                evicted.Add(oldest);
                pending.RemoveAt(0);
            }

            result = new TaskEnqueueResult(pending.Count, pending, evicted);
        }

        Changed?.Invoke();
        return result;
    }

    public TaskDismissOutcome Dismiss(string id)
    {
        TaskDismissOutcome outcome;
        lock (_lock)
        {
            if (_all.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) is not { } found)
            {
                return TaskDismissOutcome.NotFound;
            }

            outcome = found.State switch
            {
                TaskSuggestionState.Started => TaskDismissOutcome.AlreadyStarted,
                TaskSuggestionState.Dismissed => TaskDismissOutcome.AlreadyDismissed,
                TaskSuggestionState.SpawnInFlight => TaskDismissOutcome.SpawnInFlight,
                _ => TaskDismissOutcome.Dismissed,
            };

            if (outcome == TaskDismissOutcome.Dismissed)
            {
                found.State = TaskSuggestionState.Dismissed;
            }
        }

        if (outcome == TaskDismissOutcome.Dismissed)
        {
            Changed?.Invoke();
        }

        return outcome;
    }

    /// <summary>Moves a chip the user clicked out of the pending set before the spawn begins.</summary>
    public TaskSuggestion? BeginSpawn(string id)
    {
        TaskSuggestion? found;
        lock (_lock)
        {
            found = _all.FirstOrDefault(s =>
                string.Equals(s.Id, id, StringComparison.Ordinal) && s.State is TaskSuggestionState.Pending);
            if (found is not null)
            {
                found.State = TaskSuggestionState.SpawnInFlight;
            }
        }

        if (found is not null)
        {
            Changed?.Invoke();
        }

        return found;
    }

    public void CompleteSpawn(string id, bool started)
    {
        lock (_lock)
        {
            if (_all.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) is { } found)
            {
                found.State = started ? TaskSuggestionState.Started : TaskSuggestionState.Pending;
            }
        }

        Changed?.Invoke();
    }
}

file static class HexSpanExtensions
{
    public static bool ContainsOnlyHexLower(this ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return !span.IsEmpty;
    }
}
