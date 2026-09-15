namespace JarvisCode.Core.Checkpoints;

/// <summary>Receives "about to change this file" notifications from mutating tools.</summary>
public interface ICheckpointRecorder
{
    /// <summary>
    /// Captures the file's current state (or its absence) the first time it is
    /// touched in the current turn. Must be safe to call from parallel tools.
    /// </summary>
    Task RecordBeforeChangeAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Snapshot of one file before the turn changed it.</summary>
public sealed record CheckpointFile(string Path, bool Existed, string? OriginalContent);

/// <summary>All files captured for one user turn.</summary>
public sealed class CheckpointRecord
{
    public required int TurnNumber { get; init; }
    public string UserPrompt { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Where the conversation stood when the turn started (index of its user message).</summary>
    public int MessageIndexAtTurnStart { get; init; }
    public List<CheckpointFile> Files { get; set; } = [];
}

public sealed record CheckpointSummary(int TurnNumber, string UserPrompt, DateTimeOffset CreatedAt, int FileCount)
{
    public int MessageIndexAtTurnStart { get; init; }
}

public sealed record RewindResult(
    int ToTurnNumber,
    int MessageIndexAtTurnStart,
    string UserPrompt,
    IReadOnlyList<string> RestoredFiles,
    IReadOnlyList<string> Skipped);
