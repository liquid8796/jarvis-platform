using System.Text.Json;
using Jarvis.Protocol;
using Microsoft.Data.Sqlite;

namespace Jarvis.Agent.Core.Sessions;

/// <summary>
/// Local, durable metadata and coordination mailboxes. No handles, transcripts or permission grants
/// are stored here. Every remote operation is partitioned by authenticated owner AND enrolled agent.
/// </summary>
public sealed class AgentSessionStore : IDisposable
{
    private const int MaxSessions = 512;
    private const int RetainedEvents = 128;
    private readonly object _sync = new();
    private readonly SqliteConnection _db;
    private bool _disposed;
    public event Action? Changed;

    public AgentSessionStore(string databasePath)
    {
        var path = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        using var command = _db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS agent_sessions (
                owner_id TEXT NOT NULL, device_id TEXT NOT NULL, session_id TEXT NOT NULL,
                label TEXT NOT NULL, workspace TEXT NOT NULL, additional_json TEXT NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0, created_ms INTEGER NOT NULL, last_active_ms INTEGER NOT NULL,
                closed_ms INTEGER NULL, parent_id TEXT NULL, read_cursor INTEGER NOT NULL DEFAULT 0,
                pruned_cursor INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(owner_id, device_id, session_id)
            );
            CREATE TABLE IF NOT EXISTS session_events (
                cursor INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_id TEXT NOT NULL, device_id TEXT NOT NULL, session_id TEXT NOT NULL,
                kind TEXT NOT NULL, sender_id TEXT NULL, created_ms INTEGER NOT NULL, text TEXT NOT NULL,
                FOREIGN KEY(owner_id, device_id, session_id)
                    REFERENCES agent_sessions(owner_id, device_id, session_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_session_mailbox ON session_events(owner_id, device_id, session_id, cursor);
            CREATE INDEX IF NOT EXISTS ix_session_activity ON agent_sessions(owner_id, device_id, last_active_ms);
            """;
        command.ExecuteNonQuery();
    }

    public AgentSessionSnapshot Open(AgentSessionIdentity identity, string label, WorkspaceDirectories defaults, string? parentSessionId = null)
    {
        Validate(identity);
        label = string.IsNullOrWhiteSpace(label) ? "Untitled session" : label.Trim();
        if (label.Length > 120) throw new ArgumentException("Session label must be at most 120 characters.");
        AgentSessionSnapshot result;
        lock (_sync)
        {
            CheckOpen();
            using var tx = _db.BeginTransaction();
            var existing = Find(identity, tx);
            if (existing is not null)
            {
                RequireActive(existing);
                tx.Commit();
                return existing;
            }
            if (parentSessionId is not null) Require(identity with { SessionId = parentSessionId }, tx);
            // Closed tombstones outlive the server handle lifetime (30 days). Never resurrect a valid closed handle.
            using (var prune = Command("DELETE FROM agent_sessions WHERE closed_ms IS NOT NULL AND closed_ms < $before", tx,
                ("$before", DateTimeOffset.UtcNow.AddDays(-35).ToUnixTimeMilliseconds()))) prune.ExecuteNonQuery();
            using (var count = Bound("SELECT COUNT(*) FROM agent_sessions WHERE owner_id=$owner AND device_id=$device AND closed_ms IS NULL", identity, tx))
                if (Convert.ToInt64(count.ExecuteScalar()) >= MaxSessions)
                    throw new AgentRequestException("SESSION_CAPACITY", "Close unused sessions before creating another one.");
            var now = Now();
            using (var insert = Bound("""
                INSERT INTO agent_sessions(owner_id,device_id,session_id,label,workspace,additional_json,created_ms,last_active_ms,parent_id)
                VALUES($owner,$device,$session,$label,$workspace,$additional,$now,$now,$parent)
                """, identity, tx, ("$label", label), ("$workspace", defaults.Primary),
                ("$additional", JsonSerializer.Serialize(defaults.Additional, WireJson.Options)), ("$now", now), ("$parent", parentSessionId)))
                insert.ExecuteNonQuery();
            AddEvent(identity, "session.opened", null, "Session opened.", tx);
            result = Require(identity, tx);
            tx.Commit();
        }
        NotifyChanged();
        return result;
    }

    public AgentSessionSnapshot Get(AgentSessionIdentity identity, bool allowClosed = false)
    {
        Validate(identity);
        lock (_sync)
        {
            CheckOpen();
            return allowClosed ? Find(identity) ?? throw Missing() : Require(identity);
        }
    }

    public void Touch(AgentSessionIdentity identity)
    {
        Validate(identity);
        lock (_sync)
        {
            CheckOpen(); Require(identity);
            var now = Now();
            using var command = Bound("""
                UPDATE agent_sessions SET last_active_ms=$now
                WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND last_active_ms < $threshold
                """, identity, null, ("$now", now), ("$threshold", now - 5000));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AgentSessionSnapshot> List(string ownerId, string deviceId, int offset = 0, int limit = 100, bool includeClosed = false)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Session page limit must be 1..200 and offset nonnegative.");
        lock (_sync)
        {
            CheckOpen();
            using var command = Command(Select + """
                 WHERE s.owner_id=$owner AND s.device_id=$device AND ($closed=1 OR s.closed_ms IS NULL)
                 ORDER BY s.last_active_ms DESC,s.session_id LIMIT $limit OFFSET $offset
                """, null, ("$owner", ownerId), ("$device", deviceId), ("$closed", includeClosed ? 1 : 0), ("$limit", limit), ("$offset", offset));
            using var reader = command.ExecuteReader();
            var result = new List<AgentSessionSnapshot>();
            while (reader.Read()) result.Add(Read(reader));
            return result;
        }
    }

    /// <summary>Local operator projection only; not used by remote session.list.</summary>
    public IReadOnlyList<LocalSessionEntry> ListLocal(string? deviceId = null)
    {
        lock (_sync)
        {
            CheckOpen();
            using var command = Command(Select +
                " WHERE ($device IS NULL OR s.device_id=$device) ORDER BY s.last_active_ms DESC LIMIT 512", null, ("$device", deviceId));
            using var reader = command.ExecuteReader();
            var result = new List<LocalSessionEntry>();
            while (reader.Read())
            {
                var snapshot = Read(reader);
                result.Add(new(new(reader.GetString(12), snapshot.DeviceId, snapshot.SessionId), snapshot));
            }
            return result;
        }
    }

    public AgentSessionSnapshot SetWorkspace(AgentSessionIdentity identity, string? path, IReadOnlyList<string>? additionalDirectories, long expectedRevision)
    {
        Validate(identity);
        if (expectedRevision < 0) throw new ArgumentException("expectedRevision must be nonnegative.");
        var extra = additionalDirectories ?? [];
        if (extra.Count > 16) throw new ArgumentException("At most 16 additional workspace directories are supported.");
        if (string.IsNullOrWhiteSpace(path) && extra.Count != 0) throw new ArgumentException("Clearing the workspace also clears additional directories.");
        foreach (var directory in new[] { path }.Concat(extra))
            if (!string.IsNullOrWhiteSpace(directory) && !Path.IsPathFullyQualified(directory))
                throw new ArgumentException("Workspace selection requires absolute paths on the enrolled agent.");
        var folders = new WorkspaceDirectories(path, extra);
        AgentSessionSnapshot result;
        lock (_sync)
        {
            CheckOpen();
            using var tx = _db.BeginTransaction();
            var before = Require(identity, tx);
            if (before.WorkspaceRevision != expectedRevision)
                throw new AgentRequestException("WORKSPACE_REVISION_CONFLICT", "The workspace changed. Read workspace__get and retry with its current revision.");
            using (var update = Bound("""
                UPDATE agent_sessions SET workspace=$workspace,additional_json=$additional,revision=revision+1,last_active_ms=$now
                WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND revision=$revision AND closed_ms IS NULL
                """, identity, tx, ("$workspace", folders.Primary), ("$additional", JsonSerializer.Serialize(folders.Additional, WireJson.Options)),
                ("$now", Now()), ("$revision", expectedRevision)))
                if (update.ExecuteNonQuery() != 1) throw new AgentRequestException("WORKSPACE_REVISION_CONFLICT", "Workspace changed concurrently.");
            AddEvent(identity, "workspace.changed", identity.SessionId,
                folders.HasWorkspace ? "Session workspace selected. Previously accepted calls retain their original workspace." : "Session workspace cleared.", tx);
            result = Require(identity, tx);
            tx.Commit();
        }
        NotifyChanged();
        return result;
    }

    public AgentSessionEvent SendMessage(AgentSessionIdentity sender, string targetSessionId, string text)
    {
        Validate(sender);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new ArgumentException("Message must contain 1..4000 characters.");
        var target = sender with { SessionId = targetSessionId };
        Validate(target);
        AgentSessionEvent result;
        lock (_sync)
        {
            CheckOpen();
            using var tx = _db.BeginTransaction();
            Require(sender, tx); Require(target, tx);
            result = AddEvent(target, "message", sender.SessionId, text, tx);
            tx.Commit();
        }
        NotifyChanged();
        return result;
    }

    public AgentSessionEventPage ReadEvents(AgentSessionIdentity identity, long cursor = 0, int limit = 50)
    {
        Validate(identity);
        if (cursor < 0 || limit is < 1 or > 100) throw new ArgumentException("Event cursor must be nonnegative and limit must be 1..100.");
        AgentSessionEventPage result;
        lock (_sync)
        {
            CheckOpen();
            using var tx = _db.BeginTransaction();
            Require(identity, tx);
            long pruned;
            using (var state = Bound("SELECT pruned_cursor FROM agent_sessions WHERE owner_id=$owner AND device_id=$device AND session_id=$session", identity, tx))
                pruned = Convert.ToInt64(state.ExecuteScalar());
            var events = new List<AgentSessionEvent>();
            using (var command = Bound("""
                SELECT cursor,kind,sender_id,created_ms,text FROM session_events
                WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND cursor>$cursor ORDER BY cursor LIMIT $limit
                """, identity, tx, ("$cursor", cursor), ("$limit", limit + 1)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) events.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetString(4)));
            var hasMore = events.Count > limit;
            if (hasMore) events.RemoveAt(events.Count - 1);
            var next = events.Count == 0 ? Math.Max(cursor, pruned) : events[^1].Cursor;
            using (var ack = Bound("""
                UPDATE agent_sessions SET read_cursor=MAX(read_cursor,$cursor)
                WHERE owner_id=$owner AND device_id=$device AND session_id=$session
                """, identity, tx, ("$cursor", next))) ack.ExecuteNonQuery();
            tx.Commit();
            result = new(events, next, hasMore, cursor < pruned);
        }
        NotifyChanged();
        return result;
    }

    public AgentSessionSnapshot Close(AgentSessionIdentity identity)
    {
        Validate(identity);
        AgentSessionSnapshot result;
        lock (_sync)
        {
            CheckOpen();
            using var tx = _db.BeginTransaction();
            var before = Find(identity, tx) ?? throw Missing();
            if (before.ClosedAt is not null) { tx.Commit(); return before; }
            using (var update = Bound("""
                UPDATE agent_sessions SET closed_ms=$now,last_active_ms=$now
                WHERE owner_id=$owner AND device_id=$device AND session_id=$session
                """, identity, tx, ("$now", Now()))) update.ExecuteNonQuery();
            AddEvent(identity, "session.closed", null, "Session closed. Owned work is cancelled without pausing other sessions.", tx);
            result = Find(identity, tx)!;
            tx.Commit();
        }
        NotifyChanged();
        return result;
    }

    private AgentSessionEvent AddEvent(AgentSessionIdentity identity, string kind, string? sender, string text, SqliteTransaction tx)
    {
        var now = Now();
        long cursor;
        using (var insert = Bound("""
            INSERT INTO session_events(owner_id,device_id,session_id,kind,sender_id,created_ms,text)
            VALUES($owner,$device,$session,$kind,$sender,$now,$text); SELECT last_insert_rowid();
            """, identity, tx, ("$kind", kind), ("$sender", sender), ("$now", now), ("$text", text)))
            cursor = Convert.ToInt64(insert.ExecuteScalar());
        long pruneThrough;
        using (var threshold = Bound("""
            SELECT COALESCE(MAX(cursor),0) FROM (
                SELECT cursor FROM session_events WHERE owner_id=$owner AND device_id=$device AND session_id=$session
                ORDER BY cursor DESC LIMIT -1 OFFSET $retained)
            """, identity, tx, ("$retained", RetainedEvents))) pruneThrough = Convert.ToInt64(threshold.ExecuteScalar());
        if (pruneThrough > 0)
        {
            using var prune = Bound("""
                DELETE FROM session_events WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND cursor <= $cursor;
                UPDATE agent_sessions SET pruned_cursor=MAX(pruned_cursor,$cursor)
                WHERE owner_id=$owner AND device_id=$device AND session_id=$session;
                """, identity, tx, ("$cursor", pruneThrough));
            prune.ExecuteNonQuery();
        }
        return new(cursor, kind, sender, DateTimeOffset.FromUnixTimeMilliseconds(now), text);
    }

    private const string Select = """
        SELECT s.session_id,s.device_id,s.label,s.workspace,s.additional_json,s.revision,s.created_ms,s.last_active_ms,
            s.closed_ms,s.parent_id,(SELECT COUNT(*) FROM session_events e WHERE e.owner_id=s.owner_id AND e.device_id=s.device_id
                AND e.session_id=s.session_id AND e.cursor>s.read_cursor) AS unread,s.pruned_cursor,s.owner_id
        FROM agent_sessions s
        """;
    private AgentSessionSnapshot? Find(AgentSessionIdentity identity, SqliteTransaction? tx = null)
    {
        using var command = Bound(Select + " WHERE s.owner_id=$owner AND s.device_id=$device AND s.session_id=$session", identity, tx);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }
    private AgentSessionSnapshot Require(AgentSessionIdentity identity, SqliteTransaction? tx = null)
    {
        var value = Find(identity, tx) ?? throw Missing();
        RequireActive(value);
        return value;
    }
    private static AgentSessionSnapshot Read(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), Array.AsReadOnly(JsonSerializer.Deserialize<string[]>(reader.GetString(4), WireJson.Options) ?? []), reader.GetInt64(5),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
        reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10));
    private static void RequireActive(AgentSessionSnapshot value)
    {
        if (value.ClosedAt is not null) throw new AgentRequestException("SESSION_CLOSED", "Open a new session; this session has been closed.");
    }
    private static AgentRequestException Missing() => new("SESSION_NOT_FOUND", "Session does not exist for this authenticated owner and agent.");
    private static void Validate(AgentSessionIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || identity.OwnerId.Length > 256 || string.IsNullOrWhiteSpace(identity.DeviceId) ||
            identity.DeviceId.Length > 100 || !AgentSessionRules.IsSessionId(identity.SessionId))
            throw new AgentRequestException("SESSION_REQUIRED", "A valid authenticated application session is required.");
    }
    private SqliteCommand Bound(string sql, AgentSessionIdentity identity, SqliteTransaction? tx, params (string Name, object? Value)[] values) =>
        Command(sql, tx, new[] { ("$owner", (object?)identity.OwnerId), ("$device", identity.DeviceId), ("$session", identity.SessionId) }.Concat(values).ToArray());
    private SqliteCommand Command(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] values)
    {
        var command = _db.CreateCommand(); command.CommandText = sql; command.Transaction = tx;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private void CheckOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void NotifyChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action)handler)(); } catch (Exception) { /* A UI observer cannot undo committed state. */ }
    }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _db.Dispose(); }
    }
}

public sealed record LocalSessionEntry(AgentSessionIdentity Identity, AgentSessionSnapshot Snapshot);
