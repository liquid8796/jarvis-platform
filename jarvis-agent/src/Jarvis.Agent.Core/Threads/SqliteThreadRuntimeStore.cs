using System.Globalization;
using System.Text.Json;
using Jarvis.Protocol;
using Microsoft.Data.Sqlite;

namespace Jarvis.Agent.Core.Threads;

public sealed record ThreadArtifactInput(string Kind, string Uri, string? MetadataJson = null);
public sealed record ThreadItemInput(string Type, string Content, IReadOnlyList<ThreadArtifactInput>? Artifacts = null);
public sealed record ThreadEventRecord(long EventId, string ThreadId, string Kind, string PayloadJson, DateTimeOffset CreatedAt);
public sealed record ThreadQueueItem(string QueueId, int Position, string PayloadJson, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ThreadSearchHit(string ThreadId, string Project, string Title, long? EventId, string? Kind, string Snippet);
public sealed record ThreadSnapshot(
    string ThreadId, string Project, string Title, string? ParentThreadId, long? ForkEventId,
    string? Goal, string? Section, long? CompactThroughEventId, string? CompactSummary, long? RollbackTargetEventId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<ThreadEventRecord> Events, IReadOnlyList<ThreadQueueItem> Queue);

/// <summary>
/// Durable append-only thread journal with materialized queue/checkpoint metadata.
/// Compaction and rollback never delete journal events; they only change the default projection.
/// </summary>
public sealed class SqliteThreadRuntimeStore : IDisposable
{
    private readonly string _connectionString;
    private int _disposed;

    public SqliteThreadRuntimeStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public ThreadSnapshot Create(string project, string title, string? goal = null, string? section = null)
    {
        ThrowIfDisposed();
        ValidateProject(project); ValidateTitle(title); ValidateMetadata(goal, section);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertThread(connection, transaction, id, project, title, null, null, goal, section, now);
        AppendEvent(connection, transaction, id, "thread.created", JsonSerializer.Serialize(new { title, goal, section }, WireJson.Options), now);
        transaction.Commit();
        return Get(id, includeCompacted: true, maxEvents: 100);
    }

    public ThreadSnapshot Fork(string parentThreadId, string title)
    {
        ThrowIfDisposed(); ValidateId(parentThreadId, nameof(parentThreadId)); ValidateTitle(title);
        var parent = GetHeader(parentThreadId);
        var childId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        EnsureThreadExists(connection, transaction, parentThreadId);
        var forkEvent = AppendEvent(connection, transaction, parentThreadId, "thread.fork.created",
            JsonSerializer.Serialize(new { childThreadId = childId, title }, WireJson.Options), now);
        InsertThread(connection, transaction, childId, parent.Project, title, parentThreadId, forkEvent.EventId,
            parent.Goal, parent.Section, now);
        AppendEvent(connection, transaction, childId, "thread.forked",
            JsonSerializer.Serialize(new { parentThreadId, forkEventId = forkEvent.EventId }, WireJson.Options), now);
        transaction.Commit();
        return Get(childId, includeCompacted: true, maxEvents: 100);
    }

    public ThreadEventRecord AppendTurn(string threadId, string role, string? summary, IReadOnlyList<ThreadItemInput> items)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId));
        if (string.IsNullOrWhiteSpace(role) || role.Length > 64) throw new ArgumentException("Turn role must contain 1..64 characters.", nameof(role));
        if (summary?.Length > 10_000) throw new ArgumentException("Turn summary exceeds 10000 characters.", nameof(summary));
        if (items is null || items.Count is < 1 or > 128) throw new ArgumentException("A turn must contain 1..128 items.", nameof(items));
        foreach (var item in items) ValidateItem(item);
        var turnId = Guid.NewGuid().ToString("N");
        var normalized = items.Select(item => new
        {
            itemId = Guid.NewGuid().ToString("N"), item.Type, item.Content,
            artifacts = (item.Artifacts ?? []).Select(a => new { artifactId = Guid.NewGuid().ToString("N"), a.Kind, a.Uri, metadataJson = a.MetadataJson }).ToArray()
        }).ToArray();
        var payload = JsonSerializer.Serialize(new { turnId, role, summary, items = normalized }, WireJson.Options);
        return Append(threadId, "turn.appended", payload);
    }

    public ThreadEventRecord UpdateMetadata(string threadId, string? goal = null, string? section = null)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId)); ValidateMetadata(goal, section);
        if (goal is null && section is null) throw new ArgumentException("At least one metadata field is required.");
        var now = DateTimeOffset.UtcNow;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        EnsureThreadExists(connection, transaction, threadId);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE threads SET goal=COALESCE($goal, goal), section=COALESCE($section, section), updated_utc=$now WHERE thread_id=$id;";
            command.Parameters.AddWithValue("$goal", (object?)goal ?? DBNull.Value);
            command.Parameters.AddWithValue("$section", (object?)section ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$id", threadId);
            command.ExecuteNonQuery();
        }
        var result = AppendEvent(connection, transaction, threadId, "thread.metadata",
            JsonSerializer.Serialize(new { goal, section }, WireJson.Options), now);
        transaction.Commit();
        return result;
    }

    public ThreadQueueItem QueueAdd(string threadId, string payloadJson)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId)); ValidateJsonPayload(payloadJson, 100_000, "queue payload");
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        EnsureThreadExists(connection, transaction, threadId);
        int position;
        using (var positionCommand = connection.CreateCommand())
        {
            positionCommand.Transaction = transaction;
            positionCommand.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM thread_queue WHERE thread_id=$thread;";
            positionCommand.Parameters.AddWithValue("$thread", threadId);
            position = Convert.ToInt32(positionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO thread_queue(queue_id,thread_id,position,payload_json,status,created_utc,updated_utc) VALUES($id,$thread,$position,$payload,'queued',$now,$now);";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$thread", threadId);
            command.Parameters.AddWithValue("$position", position); command.Parameters.AddWithValue("$payload", payloadJson); command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery();
        }
        AppendEvent(connection, transaction, threadId, "queue.added", JsonSerializer.Serialize(new { queueId = id, position, payloadJson }, WireJson.Options), now);
        transaction.Commit();
        return new(id, position, payloadJson, "queued", now, now);
    }

    public void QueueUpdate(string threadId, string queueId, string payloadJson)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId)); ValidateId(queueId, nameof(queueId)); ValidateJsonPayload(payloadJson, 100_000, "queue payload");
        ChangeQueue(threadId, queueId, "queue.updated", payloadJson, null);
    }

    public void QueueDelete(string threadId, string queueId)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId)); ValidateId(queueId, nameof(queueId));
        var now = DateTimeOffset.UtcNow;
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        EnsureThreadExists(connection, transaction, threadId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM thread_queue WHERE thread_id=$thread AND queue_id=$id;";
        command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$id", queueId);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Queue item not found.");
        NormalizeQueuePositions(connection, transaction, threadId);
        AppendEvent(connection, transaction, threadId, "queue.deleted", JsonSerializer.Serialize(new { queueId }, WireJson.Options), now);
        transaction.Commit();
    }

    public void QueueStart(string threadId, string queueId) => ChangeQueue(threadId, queueId, "queue.started", null, "running");

    public void QueueReorder(string threadId, IReadOnlyList<string> queueIds)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId));
        if (queueIds is null || queueIds.Count > 256 || queueIds.Distinct(StringComparer.Ordinal).Count() != queueIds.Count)
            throw new ArgumentException("Queue reorder IDs must be unique and bounded.", nameof(queueIds));
        var now = DateTimeOffset.UtcNow;
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        EnsureThreadExists(connection, transaction, threadId);
        var existing = ReadQueueIds(connection, transaction, threadId);
        if (existing.Count != queueIds.Count || existing.Except(queueIds, StringComparer.Ordinal).Any())
            throw new ArgumentException("Queue reorder must contain every current queue item exactly once.", nameof(queueIds));
        using (var shift = connection.CreateCommand())
        {
            shift.Transaction = transaction;
            shift.CommandText = "UPDATE thread_queue SET position=position+1000000 WHERE thread_id=$thread;";
            shift.Parameters.AddWithValue("$thread", threadId);
            shift.ExecuteNonQuery();
        }
        for (var i = 0; i < queueIds.Count; i++)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE thread_queue SET position=$position, updated_utc=$now WHERE thread_id=$thread AND queue_id=$id;";
            command.Parameters.AddWithValue("$position", i); command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$id", queueIds[i]); command.ExecuteNonQuery();
        }
        AppendEvent(connection, transaction, threadId, "queue.reordered", JsonSerializer.Serialize(new { queueIds }, WireJson.Options), now);
        transaction.Commit();
    }

    public ThreadEventRecord Compact(string threadId, long throughEventId, string summary)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId));
        if (throughEventId <= 0) throw new ArgumentOutOfRangeException(nameof(throughEventId));
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 100_000) throw new ArgumentException("Compaction summary must contain 1..100000 characters.", nameof(summary));
        var now = DateTimeOffset.UtcNow;
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        EnsureEventBelongs(connection, transaction, threadId, throughEventId);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE threads SET compact_through_event_id=$event, compact_summary=$summary, updated_utc=$now WHERE thread_id=$thread;";
            command.Parameters.AddWithValue("$event", throughEventId); command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$now", now.ToString("O")); command.Parameters.AddWithValue("$thread", threadId); command.ExecuteNonQuery();
        }
        var result = AppendEvent(connection, transaction, threadId, "checkpoint.compacted", JsonSerializer.Serialize(new { throughEventId, summary }, WireJson.Options), now);
        transaction.Commit(); return result;
    }

    public ThreadEventRecord Rollback(string threadId, long targetEventId, string? reason = null)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId));
        if (targetEventId <= 0) throw new ArgumentOutOfRangeException(nameof(targetEventId));
        if (reason?.Length > 10_000) throw new ArgumentException("Rollback reason exceeds 10000 characters.", nameof(reason));
        var now = DateTimeOffset.UtcNow;
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        EnsureEventBelongs(connection, transaction, threadId, targetEventId);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE threads SET rollback_target_event_id=$event, updated_utc=$now WHERE thread_id=$thread;";
            command.Parameters.AddWithValue("$event", targetEventId); command.Parameters.AddWithValue("$now", now.ToString("O")); command.Parameters.AddWithValue("$thread", threadId); command.ExecuteNonQuery();
        }
        var result = AppendEvent(connection, transaction, threadId, "checkpoint.rollback", JsonSerializer.Serialize(new { targetEventId, reason }, WireJson.Options), now);
        transaction.Commit(); return result;
    }

    public ThreadSnapshot Get(string threadId, bool includeCompacted = false, int maxEvents = 100)
    {
        ThrowIfDisposed(); ValidateId(threadId, nameof(threadId));
        if (maxEvents is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        using var connection = Open();
        var header = GetHeader(connection, threadId);
        var events = new List<ThreadEventRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = includeCompacted || header.CompactThroughEventId is null
                ? "SELECT event_id,thread_id,kind,payload_json,created_utc FROM thread_events WHERE thread_id=$thread ORDER BY event_id LIMIT $limit;"
                : "SELECT event_id,thread_id,kind,payload_json,created_utc FROM thread_events WHERE thread_id=$thread AND event_id>$compact ORDER BY event_id LIMIT $limit;";
            command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$limit", maxEvents);
            if (!includeCompacted && header.CompactThroughEventId is not null) command.Parameters.AddWithValue("$compact", header.CompactThroughEventId.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read()) events.Add(ReadEvent(reader));
        }
        var queue = ReadQueue(connection, threadId);
        return header with { Events = events, Queue = queue };
    }

    public IReadOnlyList<ThreadSearchHit> Search(string query, string? project = null, int limit = 20)
    {
        ThrowIfDisposed();
        if (query is null || query.Length > 1000) throw new ArgumentException("Search query must contain at most 1000 characters.", nameof(query));
        if (project?.Length > 1024) throw new ArgumentException("Project is too long.", nameof(project));
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.thread_id,t.project,t.title,e.event_id,e.kind,e.payload_json
            FROM threads t LEFT JOIN thread_events e ON e.thread_id=t.thread_id
            WHERE ($project IS NULL OR t.project=$project)
              AND ($query='' OR instr(lower(t.title),lower($query))>0 OR instr(lower(COALESCE(t.goal,'')),lower($query))>0 OR instr(lower(COALESCE(e.payload_json,'')),lower($query))>0)
            ORDER BY t.updated_utc DESC, e.event_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value); command.Parameters.AddWithValue("$query", query); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var result = new List<ThreadSearchHit>(limit);
        while (reader.Read())
        {
            var payload = reader.IsDBNull(5) ? "" : reader.GetString(5);
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), payload.Length <= 1000 ? payload : payload[..1000]));
        }
        return result;
    }

    private ThreadEventRecord Append(string threadId, string kind, string payload)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = Open(); using var transaction = connection.BeginTransaction(); EnsureThreadExists(connection, transaction, threadId);
        var result = AppendEvent(connection, transaction, threadId, kind, payload, now); transaction.Commit(); return result;
    }

    private void ChangeQueue(string threadId, string queueId, string kind, string? payloadJson, string? status)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = Open(); using var transaction = connection.BeginTransaction(); EnsureThreadExists(connection, transaction, threadId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE thread_queue SET payload_json=COALESCE($payload,payload_json), status=COALESCE($status,status), updated_utc=$now WHERE thread_id=$thread AND queue_id=$id;";
        command.Parameters.AddWithValue("$payload", (object?)payloadJson ?? DBNull.Value); command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O")); command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$id", queueId);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("Queue item not found.");
        AppendEvent(connection, transaction, threadId, kind, JsonSerializer.Serialize(new { queueId, payloadJson, status }, WireJson.Options), now);
        transaction.Commit();
    }

    private void Initialize()
    {
        using var connection = OpenCore(); using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS threads(
                thread_id TEXT PRIMARY KEY, project TEXT NOT NULL, title TEXT NOT NULL,
                parent_thread_id TEXT NULL, fork_event_id INTEGER NULL, goal TEXT NULL, section TEXT NULL,
                compact_through_event_id INTEGER NULL, compact_summary TEXT NULL, rollback_target_event_id INTEGER NULL,
                created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                FOREIGN KEY(parent_thread_id) REFERENCES threads(thread_id)
            );
            CREATE TABLE IF NOT EXISTS thread_events(
                event_id INTEGER PRIMARY KEY AUTOINCREMENT, thread_id TEXT NOT NULL, kind TEXT NOT NULL,
                payload_json TEXT NOT NULL, created_utc TEXT NOT NULL,
                FOREIGN KEY(thread_id) REFERENCES threads(thread_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS thread_queue(
                queue_id TEXT PRIMARY KEY, thread_id TEXT NOT NULL, position INTEGER NOT NULL,
                payload_json TEXT NOT NULL, status TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                FOREIGN KEY(thread_id) REFERENCES threads(thread_id) ON DELETE CASCADE,
                UNIQUE(thread_id, position)
            );
            CREATE INDEX IF NOT EXISTS ix_thread_events_thread_event ON thread_events(thread_id,event_id);
            CREATE INDEX IF NOT EXISTS ix_threads_project_updated ON threads(project,updated_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_thread_queue_thread_position ON thread_queue(thread_id,position);
            """;
        command.ExecuteNonQuery();
    }

    private static void InsertThread(SqliteConnection connection, SqliteTransaction transaction, string id, string project, string title,
        string? parent, long? forkEventId, string? goal, string? section, DateTimeOffset now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO threads(thread_id,project,title,parent_thread_id,fork_event_id,goal,section,created_utc,updated_utc) VALUES($id,$project,$title,$parent,$fork,$goal,$section,$now,$now);";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$project", project); command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value); command.Parameters.AddWithValue("$fork", (object?)forkEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$goal", (object?)goal ?? DBNull.Value); command.Parameters.AddWithValue("$section", (object?)section ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now.ToString("O")); command.ExecuteNonQuery();
    }

    private static ThreadEventRecord AppendEvent(SqliteConnection connection, SqliteTransaction transaction, string threadId, string kind, string payload, DateTimeOffset now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO thread_events(thread_id,kind,payload_json,created_utc) VALUES($thread,$kind,$payload,$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$now", now.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE threads SET updated_utc=$now WHERE thread_id=$thread;"; update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$thread", threadId); update.ExecuteNonQuery();
        return new(id, threadId, kind, payload, now);
    }

    private ThreadSnapshot GetHeader(string threadId) { using var connection = Open(); return GetHeader(connection, threadId); }

    private static ThreadSnapshot GetHeader(SqliteConnection connection, string threadId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT thread_id,project,title,parent_thread_id,fork_event_id,goal,section,compact_through_event_id,compact_summary,rollback_target_event_id,created_utc,updated_utc FROM threads WHERE thread_id=$id LIMIT 1;";
        command.Parameters.AddWithValue("$id", threadId); using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Thread not found.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), ParseTime(reader.GetString(10)), ParseTime(reader.GetString(11)), [], []);
    }

    private static IReadOnlyList<ThreadQueueItem> ReadQueue(SqliteConnection connection, string threadId)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT queue_id,position,payload_json,status,created_utc,updated_utc FROM thread_queue WHERE thread_id=$thread ORDER BY position;";
        command.Parameters.AddWithValue("$thread", threadId); using var reader = command.ExecuteReader(); var result = new List<ThreadQueueItem>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), ParseTime(reader.GetString(4)), ParseTime(reader.GetString(5))));
        return result;
    }

    private static IReadOnlyList<string> ReadQueueIds(SqliteConnection connection, SqliteTransaction transaction, string threadId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT queue_id FROM thread_queue WHERE thread_id=$thread ORDER BY position;"; command.Parameters.AddWithValue("$thread", threadId);
        using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }

    private static void NormalizeQueuePositions(SqliteConnection connection, SqliteTransaction transaction, string threadId)
    {
        var ids = ReadQueueIds(connection, transaction, threadId);
        // Shift away from the UNIQUE(thread_id,position) range first so swaps cannot collide.
        using (var shift = connection.CreateCommand()) { shift.Transaction = transaction; shift.CommandText = "UPDATE thread_queue SET position=position+1000000 WHERE thread_id=$thread;"; shift.Parameters.AddWithValue("$thread", threadId); shift.ExecuteNonQuery(); }
        for (var i = 0; i < ids.Count; i++)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE thread_queue SET position=$position WHERE thread_id=$thread AND queue_id=$id;";
            command.Parameters.AddWithValue("$position", i); command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$id", ids[i]); command.ExecuteNonQuery();
        }
    }

    private static void EnsureThreadExists(SqliteConnection connection, SqliteTransaction transaction, string threadId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT 1 FROM threads WHERE thread_id=$id LIMIT 1;"; command.Parameters.AddWithValue("$id", threadId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException("Thread not found.");
    }

    private static void EnsureEventBelongs(SqliteConnection connection, SqliteTransaction transaction, string threadId, long eventId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT 1 FROM thread_events WHERE thread_id=$thread AND event_id=$event LIMIT 1;";
        command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$event", eventId); if (command.ExecuteScalar() is null) throw new ArgumentException("Checkpoint event does not belong to this thread.");
    }

    private static ThreadEventRecord ReadEvent(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseTime(reader.GetString(4)));
    private static DateTimeOffset ParseTime(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        ThrowIfDisposed(); return OpenCore();
    }

    private SqliteConnection OpenCore()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery(); return connection;
    }

    private static void ValidateProject(string project) { if (string.IsNullOrWhiteSpace(project) || project.Length > 1024) throw new ArgumentException("Project must contain 1..1024 characters.", nameof(project)); }
    private static void ValidateTitle(string title) { if (string.IsNullOrWhiteSpace(title) || title.Length > 512) throw new ArgumentException("Thread title must contain 1..512 characters.", nameof(title)); }
    private static void ValidateId(string id, string name) { if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsWhiteSpace)) throw new ArgumentException("Identifier is invalid.", name); }
    private static void ValidateMetadata(string? goal, string? section) { if (goal?.Length > 20_000 || section?.Length > 512) throw new ArgumentException("Thread metadata exceeds bounded limits."); }
    private static void ValidateJsonPayload(string payload, int maxLength, string name) { if (payload is null || payload.Length > maxLength) throw new ArgumentException(name + " exceeds bounded size."); try { using var _ = JsonDocument.Parse(payload); } catch (JsonException ex) { throw new ArgumentException(name + " must be valid JSON.", ex); } }

    private static void ValidateItem(ThreadItemInput item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Type) || item.Type.Length > 128 || item.Content is null || item.Content.Length > 200_000) throw new ArgumentException("Thread item is invalid or too large.");
        if ((item.Artifacts?.Count ?? 0) > 32) throw new ArgumentException("Thread item artifact count exceeds 32.");
        foreach (var artifact in item.Artifacts ?? [])
        {
            if (string.IsNullOrWhiteSpace(artifact.Kind) || artifact.Kind.Length > 128 || string.IsNullOrWhiteSpace(artifact.Uri) || artifact.Uri.Length > 2048 || artifact.MetadataJson?.Length > 64_000)
                throw new ArgumentException("Thread artifact is invalid or too large.");
            if (artifact.MetadataJson is not null) ValidateJsonPayload(artifact.MetadataJson, 64_000, "artifact metadata");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

/// <summary>Production-safe tool facade over the durable thread journal.</summary>
public sealed class ThreadRuntimeToolSet : IDisposable
{
    private readonly SqliteThreadRuntimeStore _store;
    public ThreadRuntimeToolSet(string databasePath) => _store = new(databasePath);
    public IEnumerable<IAgentTool> Tools =>
    [
        new Tool(this, "create"), new Tool(this, "get"), new Tool(this, "append_turn"), new Tool(this, "queue"),
        new Tool(this, "search"), new Tool(this, "fork"), new Tool(this, "checkpoint")
    ];
    public void Dispose() => _store.Dispose();

    private sealed class Tool(ThreadRuntimeToolSet owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor => new("thread." + operation, "thread__" + operation, "thread",
            operation switch
            {
                "create" => "Create a durable project thread in the selected workspace.",
                "get" => "Read a durable thread projection, queue and bounded event timeline.",
                "append_turn" => "Append one turn with bounded items and artifact references to the thread journal.",
                "queue" => "Mutate the durable per-thread queue: add, update, delete, reorder or start an item.",
                "search" => "Search bounded durable thread titles, goals and event payloads inside a selected project.",
                "fork" => "Fork a durable thread while preserving parent/fork-event lineage.",
                _ => "Write a non-destructive compact or rollback checkpoint to a durable thread."
            }, Schema(operation), operation is "get" or "search", operation is not "get" and not "search");

        public Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                object result = operation switch
                {
                    "create" => owner._store.Create(Project(args, context), Required(args, "title", 512), Optional(args, "goal", 20_000), Optional(args, "section", 512)),
                    "get" => Get(owner._store, args, context),
                    "append_turn" => AppendTurn(owner._store, args, context),
                    "queue" => Queue(owner._store, args, context),
                    "search" => Search(owner._store, args, context),
                    "fork" => Fork(owner._store, args, context),
                    _ => Checkpoint(owner._store, args, context)
                };
                return Task.FromResult(new ToolReply(JsonSerializer.Serialize(result, WireJson.Options)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or SqliteException or JsonException)
            {
                return Task.FromResult(ToolReply.Error(ex.Message));
            }
        }

        private static object Get(SqliteThreadRuntimeStore store, JsonElement args, AgentExecutionContext context)
        {
            var snapshot = store.Get(Required(args, "threadId", 128), args.TryGetProperty("includeCompacted", out var compacted) && compacted.GetBoolean(),
                args.TryGetProperty("maxEvents", out var max) ? max.GetInt32() : 100);
            RequireProject(snapshot.Project, context); return snapshot;
        }

        private static object AppendTurn(SqliteThreadRuntimeStore store, JsonElement args, AgentExecutionContext context)
        {
            var threadId = Required(args, "threadId", 128); var snapshot = store.Get(threadId, false, 1); RequireProject(snapshot.Project, context);
            if (!args.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("items must be an array.");
            var items = itemsElement.EnumerateArray().Select(item =>
            {
                var artifacts = item.TryGetProperty("artifacts", out var array) && array.ValueKind == JsonValueKind.Array
                    ? array.EnumerateArray().Select(a => new ThreadArtifactInput(Required(a, "kind", 128), Required(a, "uri", 2048), Optional(a, "metadataJson", 64_000))).ToArray()
                    : [];
                return new ThreadItemInput(Required(item, "type", 128), Required(item, "content", 200_000), artifacts);
            }).ToArray();
            return store.AppendTurn(threadId, Required(args, "role", 64), Optional(args, "summary", 10_000), items);
        }

        private static object Queue(SqliteThreadRuntimeStore store, JsonElement args, AgentExecutionContext context)
        {
            var threadId = Required(args, "threadId", 128); var snapshot = store.Get(threadId, false, 1); RequireProject(snapshot.Project, context);
            var op = Required(args, "operation", 32);
            return op switch
            {
                "add" => store.QueueAdd(threadId, Required(args, "payloadJson", 100_000)),
                "update" => Do(() => store.QueueUpdate(threadId, Required(args, "queueId", 128), Required(args, "payloadJson", 100_000))),
                "delete" => Do(() => store.QueueDelete(threadId, Required(args, "queueId", 128))),
                "start" => Do(() => store.QueueStart(threadId, Required(args, "queueId", 128))),
                "reorder" => Do(() => store.QueueReorder(threadId, StringArray(args, "queueIds", 256))),
                _ => throw new ArgumentException("Unsupported queue operation.")
            };
        }

        private static object Search(SqliteThreadRuntimeStore store, JsonElement args, AgentExecutionContext context) =>
            store.Search(Optional(args, "query", 1000) ?? "", Project(args, context), args.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 20);

        private static object Fork(SqliteThreadRuntimeStore store, JsonElement args, AgentExecutionContext context)
        {
            var id = Required(args, "threadId", 128); var snapshot = store.Get(id, false, 1); RequireProject(snapshot.Project, context);
            return store.Fork(id, Required(args, "title", 512));
        }

        private static object Checkpoint(SqliteThreadRuntimeStore store, JsonElement args, AgentExecutionContext context)
        {
            var id = Required(args, "threadId", 128); var snapshot = store.Get(id, true, 1); RequireProject(snapshot.Project, context);
            var op = Required(args, "operation", 32); var eventId = args.GetProperty("eventId").GetInt64();
            return op switch
            {
                "compact" => store.Compact(id, eventId, Required(args, "summary", 100_000)),
                "rollback" => store.Rollback(id, eventId, Optional(args, "reason", 10_000)),
                _ => throw new ArgumentException("Unsupported checkpoint operation.")
            };
        }

        private static object Do(Action action) { action(); return new { ok = true }; }

        private static string Project(JsonElement args, AgentExecutionContext context)
        {
            var requested = Optional(args, "project", 1024);
            var full = WorkspaceDirectories.Normalize(string.IsNullOrWhiteSpace(requested) ? context.Workspace : Path.GetFullPath(requested, context.Workspace));
            RequireProject(full, context); return full;
        }

        private static void RequireProject(string project, AgentExecutionContext context)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
            foreach (var root in new[] { context.Workspace }.Concat(context.AdditionalDirectories ?? []))
            {
                var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (full.Equals(selected, comparison) || full.StartsWith(selected + Path.DirectorySeparatorChar, comparison) || full.StartsWith(selected + Path.AltDirectorySeparatorChar, comparison)) return;
            }
            throw new UnauthorizedAccessException("Thread project is outside selected workspaces.");
        }

        private static string Required(JsonElement args, string name, int max)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > max)
                throw new ArgumentException(name + " is required and bounded.");
            return value.GetString()!;
        }
        private static string? Optional(JsonElement args, string name, int max)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > max) throw new ArgumentException(name + " exceeds bounded size.");
            return value.GetString();
        }
        private static string[] StringArray(JsonElement args, string name, int max)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) throw new ArgumentException(name + " must be an array.");
            var values = value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : throw new ArgumentException(name + " items must be strings.")).ToArray();
            if (values.Length > max || values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException(name + " exceeds bounded size."); return values;
        }

        private static JsonElement Schema(string op)
        {
            object schema = op switch
            {
                "create" => new { type = "object", properties = new Dictionary<string, object> { ["project"] = new { type = "string", maxLength = 1024 }, ["title"] = new { type = "string", minLength = 1, maxLength = 512 }, ["goal"] = new { type = "string", maxLength = 20000 }, ["section"] = new { type = "string", maxLength = 512 } }, required = new[] { "title" }, additionalProperties = false },
                "get" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = new { type = "string" }, ["includeCompacted"] = new { type = "boolean", @default = false }, ["maxEvents"] = new { type = "integer", minimum = 1, maximum = 500, @default = 100 } }, required = new[] { "threadId" }, additionalProperties = false },
                "append_turn" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = new { type = "string" }, ["role"] = new { type = "string", minLength = 1, maxLength = 64 }, ["summary"] = new { type = "string", maxLength = 10000 }, ["items"] = new { type = "array", minItems = 1, maxItems = 128, items = new { type = "object" } } }, required = new[] { "threadId", "role", "items" }, additionalProperties = false },
                "queue" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = new { type = "string" }, ["operation"] = new { type = "string", @enum = new[] { "add", "update", "delete", "reorder", "start" } }, ["queueId"] = new { type = "string" }, ["payloadJson"] = new { type = "string", maxLength = 100000 }, ["queueIds"] = new { type = "array", maxItems = 256, items = new { type = "string" } } }, required = new[] { "threadId", "operation" }, additionalProperties = false },
                "search" => new { type = "object", properties = new Dictionary<string, object> { ["query"] = new { type = "string", maxLength = 1000 }, ["project"] = new { type = "string", maxLength = 1024 }, ["limit"] = new { type = "integer", minimum = 1, maximum = 50, @default = 20 } }, additionalProperties = false },
                "fork" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = new { type = "string" }, ["title"] = new { type = "string", minLength = 1, maxLength = 512 } }, required = new[] { "threadId", "title" }, additionalProperties = false },
                _ => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = new { type = "string" }, ["operation"] = new { type = "string", @enum = new[] { "compact", "rollback" } }, ["eventId"] = new { type = "integer", minimum = 1 }, ["summary"] = new { type = "string", maxLength = 100000 }, ["reason"] = new { type = "string", maxLength = 10000 } }, required = new[] { "threadId", "operation", "eventId" }, additionalProperties = false }
            };
            return WireJson.Element(schema);
        }
    }
}
