using JarvisCode.Core.Models;

namespace JarvisCode.Core.Sessions;

/// <summary>A persisted conversation.</summary>
public sealed class Session
{
    public required string Id { get; init; }
    public string Title { get; set; } = "New session";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Extra source directories attached to this session besides the main one.</summary>
    public List<string> AdditionalDirectories { get; set; } = [];

    public string? ModelId { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];

    /// <summary>Messages replaced by compaction summaries, kept so no history is lost.</summary>
    public List<ChatMessage> ArchivedMessages { get; set; } = [];

    /// <summary>
    /// The system prompt recorded on the conversation's first request and reused
    /// verbatim on every later request and resume — the reference's
    /// systemPromptSnapshot (on by default for the built-in prompt). Null until a
    /// turn records it.
    /// </summary>
    public string? SystemPromptSnapshot { get; set; }

    /// <summary>What the snapshot was recorded for (model and working directory); a change re-records.</summary>
    public string? SystemPromptSnapshotKey { get; set; }

    /// <summary>
    /// The scheduled routine this session was started by, when it was started by one.
    /// The Runs pane reads it to find the other sessions of the same routine; a session
    /// the user started carries null and the pane says so.
    /// </summary>
    public string? RoutineId { get; set; }

    /// <summary>The routine's name at the time it ran, for the Runs pane header.</summary>
    public string? RoutineName { get; set; }

    public static Session CreateNew(string workingDirectory) => new()
    {
        Id = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24],
        WorkingDirectory = workingDirectory,
    };
}

/// <summary>Lightweight session descriptor for list views (no messages loaded).</summary>
public sealed record SessionSummary(
    string Id, string Title, DateTimeOffset UpdatedAt, int MessageCount, string WorkingDirectory = "",
    string? RoutineId = null);
