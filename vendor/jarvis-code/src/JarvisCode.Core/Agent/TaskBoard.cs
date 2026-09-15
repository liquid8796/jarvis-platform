using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JarvisCode.Core.Agent;

/// <summary>Lifecycle of a board task; <c>deleted</c> is an operation, not a stored state.</summary>
public enum TaskState
{
    Pending,
    InProgress,
    Completed,
}

/// <summary>
/// One task on the session's shared board. Teammates claim tasks by setting
/// <see cref="Owner"/> and gate their work on <see cref="BlockedBy"/>.
/// </summary>
public sealed record BoardTask
{
    public required string Id { get; init; }

    public required string Subject { get; init; }

    public string Description { get; init; } = "";

    public TaskState Status { get; init; } = TaskState.Pending;

    /// <summary>Present-continuous form shown while the task is in progress ("Running tests").</summary>
    public string? ActiveForm { get; init; }

    /// <summary>Agent name that claimed the task; null while it is available.</summary>
    public string? Owner { get; init; }

    /// <summary>Tasks that cannot start until this one completes.</summary>
    public IReadOnlyList<string> Blocks { get; init; } = [];

    /// <summary>Tasks that must complete before this one can start.</summary>
    public IReadOnlyList<string> BlockedBy { get; init; } = [];

    public IReadOnlyDictionary<string, JsonNode?> Metadata { get; init; } =
        new Dictionary<string, JsonNode?>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Board-internal bookkeeping tasks are hidden from TaskList, like the reference.</summary>
    public bool IsInternal =>
        Metadata.TryGetValue("_internal", out var flag) && flag is not null &&
        flag.GetValueKind() is JsonValueKind.True or JsonValueKind.String or JsonValueKind.Number;
}

/// <summary>Fields a TaskUpdate call may change; null means "leave alone".</summary>
public sealed record TaskUpdateRequest
{
    public string? Subject { get; init; }

    public string? Description { get; init; }

    public string? ActiveForm { get; init; }

    public string? Owner { get; init; }

    public TaskState? Status { get; init; }

    /// <summary>Merged into the task's metadata; a null value deletes the key.</summary>
    public IReadOnlyDictionary<string, JsonNode?>? Metadata { get; init; }

    public IReadOnlyList<string>? AddBlocks { get; init; }

    public IReadOnlyList<string>? AddBlockedBy { get; init; }
}

/// <summary>What an update changed, in the reference's field order.</summary>
public sealed record TaskUpdateOutcome(
    bool Success,
    string TaskId,
    IReadOnlyList<string> UpdatedFields,
    string? Error = null,
    TaskState? StatusFrom = null,
    /// <summary>Null for an ordinary status change; "deleted" when the task was removed.</summary>
    string? StatusTo = null);

/// <summary>
/// The session's structured task list — the reference's TaskCreate/TaskGet/
/// TaskList/TaskUpdate board, shared between the main session and its
/// teammates. Ids are sequential decimal strings and are never reused.
///
/// The reference locks its file because its teammates are separate processes;
/// ours run in-process against this instance, so a monitor is enough. The file
/// exists so a board survives a restart, and is written whole on each change.
/// </summary>
public sealed class TaskBoard
{
    private readonly object _gate = new();
    private readonly List<BoardTask> _tasks = [];
    private readonly string? _path;
    private int _nextId = 1;

    public TaskBoard(string? path = null)
    {
        _path = path;
        Load();
    }

    /// <summary>Raised after any mutation so a host can re-render the task panel.</summary>
    public event Action? Changed;

    public IReadOnlyList<BoardTask> Snapshot()
    {
        lock (_gate)
            return _tasks.ToList();
    }

    /// <summary>Tasks a model should see: board-internal bookkeeping is hidden.</summary>
    public IReadOnlyList<BoardTask> Visible()
    {
        lock (_gate)
            return _tasks.Where(t => !t.IsInternal).ToList();
    }

    public BoardTask? Get(string id)
    {
        lock (_gate)
            return _tasks.FirstOrDefault(t => t.Id == id);
    }

    public BoardTask Create(
        string subject,
        string description,
        string? activeForm = null,
        IReadOnlyDictionary<string, JsonNode?>? metadata = null)
    {
        BoardTask task;
        lock (_gate)
        {
            task = new BoardTask
            {
                Id = _nextId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Subject = subject,
                Description = description,
                ActiveForm = activeForm,
                Status = TaskState.Pending,
                Metadata = metadata is null
                    ? new Dictionary<string, JsonNode?>()
                    : CloneMetadata(metadata),
            };
            _nextId++;
            _tasks.Add(task);
            Save();
        }

        Changed?.Invoke();
        return task;
    }

    /// <summary>Removes a task and drops it from every other task's dependency lists.</summary>
    public bool Delete(string id)
    {
        bool removed;
        lock (_gate)
        {
            int index = _tasks.FindIndex(t => t.Id == id);
            if (index < 0)
                return false;
            _tasks.RemoveAt(index);
            for (int i = 0; i < _tasks.Count; i++)
            {
                var other = _tasks[i];
                if (!other.Blocks.Contains(id) && !other.BlockedBy.Contains(id))
                    continue;
                _tasks[i] = other with
                {
                    Blocks = other.Blocks.Where(x => x != id).ToList(),
                    BlockedBy = other.BlockedBy.Where(x => x != id).ToList(),
                    UpdatedAt = DateTimeOffset.Now,
                };
            }

            removed = true;
            Save();
        }

        Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Applies one update. <paramref name="claimingAgent"/> is the teammate name
    /// that becomes the owner when a task moves to in_progress unowned — the
    /// reference's implicit claim.
    /// </summary>
    public TaskUpdateOutcome Update(string id, TaskUpdateRequest request, string? claimingAgent = null)
    {
        var fields = new List<string>();
        TaskState from;
        lock (_gate)
        {
            int index = _tasks.FindIndex(t => t.Id == id);
            if (index < 0)
                return new TaskUpdateOutcome(false, id, [], "Task not found");

            var task = _tasks[index];
            from = task.Status;

            if (request.Subject is not null && request.Subject != task.Subject)
            {
                task = task with { Subject = request.Subject };
                fields.Add("subject");
            }

            if (request.Description is not null && request.Description != task.Description)
            {
                task = task with { Description = request.Description };
                fields.Add("description");
            }

            if (request.ActiveForm is not null && request.ActiveForm != task.ActiveForm)
            {
                task = task with { ActiveForm = request.ActiveForm };
                fields.Add("activeForm");
            }

            if (request.Owner is not null && request.Owner != task.Owner)
            {
                task = task with { Owner = request.Owner };
                fields.Add("owner");
            }
            else if (request.Status == TaskState.InProgress && request.Owner is null &&
                     string.IsNullOrEmpty(task.Owner) && !string.IsNullOrEmpty(claimingAgent))
            {
                task = task with { Owner = claimingAgent };
                fields.Add("owner");
            }

            if (request.Metadata is not null)
            {
                var merged = CloneMetadata(task.Metadata);
                foreach (var (key, value) in request.Metadata)
                {
                    if (value is null)
                        merged.Remove(key);
                    else
                        merged[key] = value.DeepClone();
                }

                task = task with { Metadata = merged };
                fields.Add("metadata");
            }

            if (request.Status is { } status && status != task.Status)
            {
                task = task with { Status = status };
                fields.Add("status");
            }

            if (request.AddBlocks is { Count: > 0 })
            {
                var added = request.AddBlocks.Where(x => !task.Blocks.Contains(x)).ToList();
                if (added.Count > 0)
                {
                    task = task with { Blocks = task.Blocks.Concat(added).ToList() };
                    fields.Add("blocks");
                    LinkReverse(added, id, blockedBy: true);
                }
            }

            if (request.AddBlockedBy is { Count: > 0 })
            {
                var added = request.AddBlockedBy.Where(x => !task.BlockedBy.Contains(x)).ToList();
                if (added.Count > 0)
                {
                    task = task with { BlockedBy = task.BlockedBy.Concat(added).ToList() };
                    fields.Add("blockedBy");
                    LinkReverse(added, id, blockedBy: false);
                }
            }

            if (fields.Count > 0)
            {
                task = task with { UpdatedAt = DateTimeOffset.Now };
                // The reverse-link pass above may have rewritten the list, so
                // re-find the slot rather than trusting the captured index.
                int slot = _tasks.FindIndex(t => t.Id == id);
                _tasks[slot] = task;
                Save();
            }
        }

        if (fields.Count > 0)
            Changed?.Invoke();

        var statusChanged = fields.Contains("status");
        return new TaskUpdateOutcome(
            true, id, fields, null,
            statusChanged ? from : null,
            statusChanged ? StatusName(Get(id)?.Status ?? from) : null);
    }

    /// <summary>Mirrors a dependency onto the other task, so both ends agree.</summary>
    private void LinkReverse(IReadOnlyList<string> ids, string sourceId, bool blockedBy)
    {
        foreach (var otherId in ids)
        {
            int index = _tasks.FindIndex(t => t.Id == otherId);
            if (index < 0)
                continue;
            var other = _tasks[index];
            if (blockedBy)
            {
                if (other.BlockedBy.Contains(sourceId))
                    continue;
                _tasks[index] = other with { BlockedBy = other.BlockedBy.Append(sourceId).ToList() };
            }
            else
            {
                if (other.Blocks.Contains(sourceId))
                    continue;
                _tasks[index] = other with { Blocks = other.Blocks.Append(sourceId).ToList() };
            }
        }
    }

    /// <summary>Open blockers only — a completed blocker no longer gates the task.</summary>
    public IReadOnlyList<string> OpenBlockers(BoardTask task)
    {
        lock (_gate)
        {
            var completed = _tasks
                .Where(t => t.Status == TaskState.Completed)
                .Select(t => t.Id)
                .ToHashSet(StringComparer.Ordinal);
            return task.BlockedBy.Where(id => !completed.Contains(id)).ToList();
        }
    }

    public static string StatusName(TaskState status) => status switch
    {
        TaskState.Pending => "pending",
        TaskState.InProgress => "in_progress",
        TaskState.Completed => "completed",
        _ => "pending",
    };

    public static TaskState? ParseStatus(string? text) => text switch
    {
        "pending" => TaskState.Pending,
        "in_progress" => TaskState.InProgress,
        "completed" => TaskState.Completed,
        _ => null,
    };

    private static Dictionary<string, JsonNode?> CloneMetadata(IReadOnlyDictionary<string, JsonNode?> source)
    {
        var copy = new Dictionary<string, JsonNode?>(source.Count, StringComparer.Ordinal);
        foreach (var (key, value) in source)
            copy[key] = value?.DeepClone();
        return copy;
    }

    private sealed record PersistedTask(
        string Id,
        string Subject,
        string Description,
        string Status,
        string? ActiveForm,
        string? Owner,
        List<string> Blocks,
        List<string> BlockedBy,
        Dictionary<string, JsonNode?>? Metadata,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record PersistedBoard(int NextId, List<PersistedTask> Tasks);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private void Load()
    {
        if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            return;
        try
        {
            var board = JsonSerializer.Deserialize<PersistedBoard>(File.ReadAllText(_path), SerializerOptions);
            if (board is null)
                return;
            foreach (var stored in board.Tasks)
            {
                _tasks.Add(new BoardTask
                {
                    Id = stored.Id,
                    Subject = stored.Subject,
                    Description = stored.Description,
                    Status = ParseStatus(stored.Status) ?? TaskState.Pending,
                    ActiveForm = stored.ActiveForm,
                    Owner = stored.Owner,
                    Blocks = stored.Blocks,
                    BlockedBy = stored.BlockedBy,
                    Metadata = stored.Metadata ?? new Dictionary<string, JsonNode?>(),
                    CreatedAt = stored.CreatedAt,
                    UpdatedAt = stored.UpdatedAt,
                });
            }

            _nextId = Math.Max(board.NextId, _tasks.Count == 0 ? 1 : _tasks.Max(ParseId) + 1);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable board must not take the session down with
            // it; the session starts with an empty list and overwrites on save.
            _tasks.Clear();
        }
    }

    private static int ParseId(BoardTask task) =>
        int.TryParse(task.Id, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;

    private void Save()
    {
        if (string.IsNullOrEmpty(_path))
            return;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var board = new PersistedBoard(_nextId, _tasks.Select(t => new PersistedTask(
                t.Id, t.Subject, t.Description, StatusName(t.Status), t.ActiveForm, t.Owner,
                t.Blocks.ToList(), t.BlockedBy.ToList(),
                t.Metadata.Count == 0 ? null : new Dictionary<string, JsonNode?>(t.Metadata),
                t.CreatedAt, t.UpdatedAt)).ToList());

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(board, SerializerOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is a convenience: the in-memory board stays authoritative.
        }
    }
}
