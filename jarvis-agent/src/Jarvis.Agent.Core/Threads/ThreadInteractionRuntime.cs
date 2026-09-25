using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Protocol;
using Microsoft.Data.Sqlite;

namespace Jarvis.Agent.Core.Threads;

public sealed record ThreadHeaderRecord(
    string ThreadId, string Project, string Title, string? Goal, string? Section,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ThreadAutomationRecord(
    string AutomationId, string OwnerId, string DeviceId, string SessionId, string ThreadId,
    string Name, string PayloadJson, DateTimeOffset? NextDueAt, int? IntervalSeconds,
    string Status, long Revision, long RunCount, DateTimeOffset? LastRunAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ThreadAutomationRun(
    string AutomationId, string ThreadId, string QueueId, DateTimeOffset ScheduledFor,
    DateTimeOffset FiredAt, int SkippedOccurrences, long Revision, string Status,
    DateTimeOffset? NextDueAt);

public sealed record ThreadAsyncInputRecord(
    string InputId, string OwnerId, string DeviceId, string SessionId, string ThreadId,
    string Prompt, string SchemaJson, string Status, string? AnswerJson, long Revision,
    DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? AnsweredAt);

public sealed record ThreadInteractionLimits(
    int MaxAutomationsPerSession = 128,
    int MaxPendingInputsPerSession = 128,
    int MaxNameChars = 240,
    int MaxPromptChars = 20_000,
    int MaxPayloadChars = 100_000,
    int MaxSchemaChars = 64_000,
    int MaxAnswerChars = 100_000,
    int MaxListItems = 200,
    int MinIntervalSeconds = 60,
    int MaxIntervalSeconds = 31_536_000,
    int MaxExpirationSeconds = 604_800);

/// <summary>
/// Durable local thread-adjacent state: deterministic queue automations and asynchronous structured
/// input requests. No automation execution calls a model or a machine-mutating tool; firing only
/// appends a queue item and journal event for later explicit processing.
/// </summary>
public sealed class SqliteThreadInteractionStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ThreadInteractionLimits _limits;
    private readonly object _mutation = new();
    private int _disposed;

    public SqliteThreadInteractionStore(string databasePath, ThreadInteractionLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _limits = limits ?? new ThreadInteractionLimits();
        ValidateLimits(_limits);
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

    public string GetThreadProject(string threadId)
    {
        ThrowIfDisposed();
        ValidateThreadId(threadId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT project FROM threads WHERE thread_id=$id LIMIT 1;";
        command.Parameters.AddWithValue("$id", threadId);
        return command.ExecuteScalar() as string ?? throw new KeyNotFoundException("Thread not found.");
    }

    public IReadOnlyList<ThreadHeaderRecord> ListThreads(string project, string? query, int limit)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(project) || project.Length > 1024) throw new ArgumentException("Project is invalid.");
        if (query?.Length > 1_000) throw new ArgumentException("Thread query exceeds 1000 characters.");
        if (limit is < 1 || limit > _limits.MaxListItems) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT thread_id,project,title,goal,section,created_utc,updated_utc
            FROM threads
            WHERE project=$project AND ($query IS NULL OR $query='' OR instr(lower(title),lower($query))>0 OR instr(lower(COALESCE(goal,'')),lower($query))>0)
            ORDER BY updated_utc DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$project", project);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<ThreadHeaderRecord>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseTime(reader.GetString(5)), ParseTime(reader.GetString(6))));
        return rows;
    }

    public IReadOnlyList<ThreadEventRecord> ReadEvents(string threadId, long afterEventId, int limit)
    {
        ThrowIfDisposed();
        ValidateThreadId(threadId);
        if (afterEventId < 0) throw new ArgumentOutOfRangeException(nameof(afterEventId));
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id,thread_id,kind,payload_json,created_utc FROM thread_events WHERE thread_id=$thread AND event_id>$cursor ORDER BY event_id LIMIT $limit;";
        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$cursor", afterEventId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<ThreadEventRecord>();
        while (reader.Read())
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseTime(reader.GetString(4))));
        return rows;
    }

    public ThreadAutomationRecord CreateAutomation(AgentSessionIdentity identity, string threadId,
        string name, string payloadJson, DateTimeOffset nextDueAt, int? intervalSeconds)
    {
        ThrowIfDisposed();
        ValidateThreadId(threadId);
        name = ValidateText(name, _limits.MaxNameChars, "Automation name");
        payloadJson = ValidateJson(payloadJson, _limits.MaxPayloadChars, "Automation payload", requireObject: false);
        ValidateInterval(intervalSeconds);
        nextDueAt = nextDueAt.ToUniversalTime();
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            EnsureThread(connection, transaction, threadId);
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM thread_automations WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND status IN ('active','paused');";
                AddIdentity(count, identity);
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= _limits.MaxAutomationsPerSession)
                    throw new AgentRequestException("AUTOMATION_LIMIT",
                        $"This session already has {_limits.MaxAutomationsPerSession} active or paused automations.");
            }
            var id = "automation_" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO thread_automations(automation_id,owner_id,device_id,session_id,thread_id,name,payload_json,next_due_utc,interval_seconds,status,revision,run_count,last_run_utc,created_utc,updated_utc)
                    VALUES($id,$owner,$device,$session,$thread,$name,$payload,$due,$interval,'active',1,0,NULL,$now,$now);
                    """;
                command.Parameters.AddWithValue("$id", id);
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$thread", threadId);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$payload", payloadJson);
                command.Parameters.AddWithValue("$due", Stamp(nextDueAt));
                command.Parameters.AddWithValue("$interval", (object?)intervalSeconds ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.ExecuteNonQuery();
            }
            AppendEvent(connection, transaction, threadId, "automation.created", JsonSerializer.Serialize(new
            {
                automationId = id, name, nextDueAt, intervalSeconds
            }, WireJson.Options), now);
            transaction.Commit();
            return new(id, identity.OwnerId, identity.DeviceId, identity.SessionId, threadId, name,
                payloadJson, nextDueAt, intervalSeconds, "active", 1, 0, null, now, now);
        }
    }

    public ThreadAutomationRecord UpdateAutomation(AgentSessionIdentity identity, string automationId,
        long expectedRevision, string? name, string? payloadJson, DateTimeOffset? nextDueAt,
        int? intervalSeconds, bool intervalSupplied, bool? paused)
    {
        ThrowIfDisposed();
        ValidateAutomationId(automationId);
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (name is null && payloadJson is null && nextDueAt is null && !intervalSupplied && paused is null)
            throw new ArgumentException("At least one automation field must be supplied.");
        if (name is not null) name = ValidateText(name, _limits.MaxNameChars, "Automation name");
        if (payloadJson is not null) payloadJson = ValidateJson(payloadJson, _limits.MaxPayloadChars, "Automation payload", false);
        if (intervalSupplied) ValidateInterval(intervalSeconds);
        nextDueAt = nextDueAt?.ToUniversalTime();
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var current = ReadAutomation(connection, transaction, identity, automationId);
            if (current.Revision != expectedRevision)
                throw Conflict("Automation", expectedRevision, current.Revision);
            if (current.Status is "cancelled" or "completed")
                throw new AgentRequestException("AUTOMATION_TERMINAL", "A cancelled or completed automation cannot be updated.");
            var nextStatus = paused switch { true => "paused", false => "active", _ => current.Status };
            var nextRevision = current.Revision + 1;
            var now = DateTimeOffset.UtcNow;
            var next = current with
            {
                Name = name ?? current.Name,
                PayloadJson = payloadJson ?? current.PayloadJson,
                NextDueAt = nextDueAt ?? current.NextDueAt,
                IntervalSeconds = intervalSupplied ? intervalSeconds : current.IntervalSeconds,
                Status = nextStatus,
                Revision = nextRevision,
                UpdatedAt = now
            };
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE thread_automations SET name=$name,payload_json=$payload,next_due_utc=$due,interval_seconds=$interval,
                        status=$status,revision=$next,updated_utc=$now
                    WHERE automation_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session AND revision=$expected;
                    """;
                command.Parameters.AddWithValue("$name", next.Name);
                command.Parameters.AddWithValue("$payload", next.PayloadJson);
                command.Parameters.AddWithValue("$due", (object?)(next.NextDueAt is { } due ? Stamp(due) : null) ?? DBNull.Value);
                command.Parameters.AddWithValue("$interval", (object?)next.IntervalSeconds ?? DBNull.Value);
                command.Parameters.AddWithValue("$status", next.Status);
                command.Parameters.AddWithValue("$next", nextRevision);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.Parameters.AddWithValue("$id", automationId);
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$expected", expectedRevision);
                if (command.ExecuteNonQuery() != 1) throw Conflict("Automation", expectedRevision, null);
            }
            AppendEvent(connection, transaction, current.ThreadId, "automation.updated", JsonSerializer.Serialize(new
            {
                automationId, previousRevision = expectedRevision, revision = nextRevision, status = nextStatus
            }, WireJson.Options), now);
            transaction.Commit();
            return next;
        }
    }

    public ThreadAutomationRecord CancelAutomation(AgentSessionIdentity identity, string automationId, long expectedRevision)
    {
        ThrowIfDisposed();
        ValidateAutomationId(automationId);
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var current = ReadAutomation(connection, transaction, identity, automationId);
            if (current.Revision != expectedRevision) throw Conflict("Automation", expectedRevision, current.Revision);
            if (current.Status == "cancelled") return current;
            var now = DateTimeOffset.UtcNow;
            var revision = current.Revision + 1;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE thread_automations SET status='cancelled',next_due_utc=NULL,revision=$next,updated_utc=$now WHERE automation_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session AND revision=$expected;";
                command.Parameters.AddWithValue("$next", revision);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.Parameters.AddWithValue("$id", automationId);
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$expected", expectedRevision);
                if (command.ExecuteNonQuery() != 1) throw Conflict("Automation", expectedRevision, null);
            }
            AppendEvent(connection, transaction, current.ThreadId, "automation.cancelled",
                JsonSerializer.Serialize(new { automationId, revision }, WireJson.Options), now);
            transaction.Commit();
            return current with { Status = "cancelled", NextDueAt = null, Revision = revision, UpdatedAt = now };
        }
    }

    public ThreadAutomationRecord GetAutomation(AgentSessionIdentity identity, string automationId)
    {
        ThrowIfDisposed();
        ValidateAutomationId(automationId);
        using var connection = Open();
        return ReadAutomation(connection, null, identity, automationId);
    }

    public IReadOnlyList<ThreadAutomationRecord> ListAutomations(AgentSessionIdentity identity,
        string? threadId, string? status, int limit)
    {
        ThrowIfDisposed();
        if (threadId is not null) ValidateThreadId(threadId);
        if (status is not null && status is not ("active" or "paused" or "completed" or "cancelled"))
            throw new ArgumentException("Automation status is invalid.");
        if (limit is < 1 || limit > _limits.MaxListItems) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT automation_id,owner_id,device_id,session_id,thread_id,name,payload_json,next_due_utc,interval_seconds,status,revision,run_count,last_run_utc,created_utc,updated_utc
            FROM thread_automations
            WHERE owner_id=$owner AND device_id=$device AND session_id=$session
              AND ($thread IS NULL OR thread_id=$thread) AND ($status IS NULL OR status=$status)
            ORDER BY updated_utc DESC LIMIT $limit;
            """;
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<ThreadAutomationRecord>();
        while (reader.Read()) rows.Add(ReadAutomationRecord(reader));
        return rows;
    }

    public IReadOnlyList<ThreadAutomationRun> RunDueAutomations(DateTimeOffset now, int maxRuns,
        AgentSessionIdentity? identity = null, string? threadId = null)
    {
        ThrowIfDisposed();
        if (maxRuns is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maxRuns));
        if (threadId is not null) ValidateThreadId(threadId);
        now = now.ToUniversalTime();
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = """
                SELECT automation_id,owner_id,device_id,session_id,thread_id,name,payload_json,next_due_utc,interval_seconds,status,revision,run_count,last_run_utc,created_utc,updated_utc
                FROM thread_automations
                WHERE status='active' AND next_due_utc IS NOT NULL AND next_due_utc<=$now
                  AND ($owner IS NULL OR owner_id=$owner) AND ($device IS NULL OR device_id=$device)
                  AND ($session IS NULL OR session_id=$session) AND ($thread IS NULL OR thread_id=$thread)
                ORDER BY next_due_utc,automation_id LIMIT $limit;
                """;
            select.Parameters.AddWithValue("$now", Stamp(now));
            select.Parameters.AddWithValue("$owner", (object?)identity?.OwnerId ?? DBNull.Value);
            select.Parameters.AddWithValue("$device", (object?)identity?.DeviceId ?? DBNull.Value);
            select.Parameters.AddWithValue("$session", (object?)identity?.SessionId ?? DBNull.Value);
            select.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
            select.Parameters.AddWithValue("$limit", maxRuns);
            var due = new List<ThreadAutomationRecord>();
            using (var reader = select.ExecuteReader()) while (reader.Read()) due.Add(ReadAutomationRecord(reader));
            var runs = new List<ThreadAutomationRun>(due.Count);
            foreach (var automation in due)
            {
                var scheduled = automation.NextDueAt!.Value;
                var skipped = 0;
                DateTimeOffset? nextDue = null;
                var nextStatus = "completed";
                if (automation.IntervalSeconds is { } seconds)
                {
                    nextDue = scheduled.AddSeconds(seconds);
                    while (nextDue <= now)
                    {
                        nextDue = nextDue.Value.AddSeconds(seconds);
                        skipped++;
                    }
                    nextStatus = "active";
                }
                var queueId = AddAutomationQueue(connection, transaction, automation, scheduled, now, skipped);
                var revision = automation.Revision + 1;
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE thread_automations SET next_due_utc=$due,status=$status,revision=$revision,run_count=run_count+1,last_run_utc=$now,updated_utc=$now WHERE automation_id=$id AND revision=$expected AND status='active';";
                    update.Parameters.AddWithValue("$due", (object?)(nextDue is { } stampedDue ? Stamp(stampedDue) : null) ?? DBNull.Value);
                    update.Parameters.AddWithValue("$status", nextStatus);
                    update.Parameters.AddWithValue("$revision", revision);
                    update.Parameters.AddWithValue("$now", Stamp(now));
                    update.Parameters.AddWithValue("$id", automation.AutomationId);
                    update.Parameters.AddWithValue("$expected", automation.Revision);
                    if (update.ExecuteNonQuery() != 1) throw Conflict("Automation", automation.Revision, null);
                }
                AppendEvent(connection, transaction, automation.ThreadId, "automation.fired", JsonSerializer.Serialize(new
                {
                    automationId = automation.AutomationId, queueId, scheduledFor = scheduled,
                    firedAt = now, skippedOccurrences = skipped, nextDueAt = nextDue, status = nextStatus
                }, WireJson.Options), now);
                runs.Add(new(automation.AutomationId, automation.ThreadId, queueId, scheduled, now, skipped,
                    revision, nextStatus, nextDue));
            }
            transaction.Commit();
            return runs;
        }
    }

    public ThreadAsyncInputRecord CreateInput(AgentSessionIdentity identity, string threadId,
        string prompt, string schemaJson, int? expiresInSeconds)
    {
        ThrowIfDisposed();
        ValidateThreadId(threadId);
        prompt = ValidateText(prompt, _limits.MaxPromptChars, "Input prompt");
        schemaJson = ValidateSchema(schemaJson);
        if (expiresInSeconds is < 1 || expiresInSeconds > _limits.MaxExpirationSeconds)
            throw new ArgumentOutOfRangeException(nameof(expiresInSeconds));
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            EnsureThread(connection, transaction, threadId);
            ExpireInputs(connection, transaction, DateTimeOffset.UtcNow, identity);
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM thread_async_inputs WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND status='pending';";
                AddIdentity(count, identity);
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= _limits.MaxPendingInputsPerSession)
                    throw new AgentRequestException("ASYNC_INPUT_LIMIT",
                        $"This session already has {_limits.MaxPendingInputsPerSession} pending input requests.");
            }
            var id = "input_" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var expiresAt = expiresInSeconds is null ? (DateTimeOffset?)null : now.AddSeconds(expiresInSeconds.Value);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO thread_async_inputs(input_id,owner_id,device_id,session_id,thread_id,prompt,schema_json,status,answer_json,revision,expires_utc,created_utc,updated_utc,answered_utc)
                    VALUES($id,$owner,$device,$session,$thread,$prompt,$schema,'pending',NULL,1,$expires,$now,$now,NULL);
                    """;
                command.Parameters.AddWithValue("$id", id);
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$thread", threadId);
                command.Parameters.AddWithValue("$prompt", prompt);
                command.Parameters.AddWithValue("$schema", schemaJson);
                command.Parameters.AddWithValue("$expires", (object?)(expiresAt is { } expires ? Stamp(expires) : null) ?? DBNull.Value);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.ExecuteNonQuery();
            }
            AppendEvent(connection, transaction, threadId, "async_input.requested", JsonSerializer.Serialize(new
            {
                inputId = id, prompt, schemaJson, expiresAt
            }, WireJson.Options), now);
            transaction.Commit();
            return new(id, identity.OwnerId, identity.DeviceId, identity.SessionId, threadId, prompt,
                schemaJson, "pending", null, 1, expiresAt, now, now, null);
        }
    }

    public ThreadAsyncInputRecord RespondInput(AgentSessionIdentity identity, string inputId,
        long expectedRevision, string answerJson)
    {
        ThrowIfDisposed();
        ValidateInputId(inputId);
        answerJson = ValidateJson(answerJson, _limits.MaxAnswerChars, "Input answer", requireObject: false);
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            ExpireInputs(connection, transaction, DateTimeOffset.UtcNow, identity);
            var current = ReadInput(connection, transaction, identity, inputId);
            if (current.Revision != expectedRevision) throw Conflict("Async input", expectedRevision, current.Revision);
            if (current.Status != "pending")
                throw new AgentRequestException("ASYNC_INPUT_TERMINAL", $"Async input is already {current.Status}.");
            using (var schemaDocument = JsonDocument.Parse(current.SchemaJson))
            using (var answerDocument = JsonDocument.Parse(answerJson))
            {
                var schema = SchemaGuard.Compile(schemaDocument.RootElement);
                if (!SchemaGuard.Matches(schema, answerDocument.RootElement))
                    throw new AgentRequestException("ASYNC_INPUT_SCHEMA_MISMATCH", "Answer does not match the requested JSON schema.");
            }
            var now = DateTimeOffset.UtcNow;
            var revision = current.Revision + 1;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE thread_async_inputs SET status='answered',answer_json=$answer,revision=$next,updated_utc=$now,answered_utc=$now WHERE input_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session AND revision=$expected AND status='pending';";
                command.Parameters.AddWithValue("$answer", answerJson);
                command.Parameters.AddWithValue("$next", revision);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.Parameters.AddWithValue("$id", inputId);
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$expected", expectedRevision);
                if (command.ExecuteNonQuery() != 1) throw Conflict("Async input", expectedRevision, null);
            }
            AppendEvent(connection, transaction, current.ThreadId, "async_input.answered", JsonSerializer.Serialize(new
            {
                inputId, answerJson, revision
            }, WireJson.Options), now);
            transaction.Commit();
            return current with { Status = "answered", AnswerJson = answerJson, Revision = revision, UpdatedAt = now, AnsweredAt = now };
        }
    }

    public ThreadAsyncInputRecord CancelInput(AgentSessionIdentity identity, string inputId, long expectedRevision)
    {
        ThrowIfDisposed();
        ValidateInputId(inputId);
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            ExpireInputs(connection, transaction, DateTimeOffset.UtcNow, identity);
            var current = ReadInput(connection, transaction, identity, inputId);
            if (current.Revision != expectedRevision) throw Conflict("Async input", expectedRevision, current.Revision);
            if (current.Status != "pending") return current;
            var now = DateTimeOffset.UtcNow;
            var revision = current.Revision + 1;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE thread_async_inputs SET status='cancelled',revision=$next,updated_utc=$now WHERE input_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session AND revision=$expected AND status='pending';";
                command.Parameters.AddWithValue("$next", revision);
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                command.Parameters.AddWithValue("$id", inputId);
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$expected", expectedRevision);
                if (command.ExecuteNonQuery() != 1) throw Conflict("Async input", expectedRevision, null);
            }
            AppendEvent(connection, transaction, current.ThreadId, "async_input.cancelled",
                JsonSerializer.Serialize(new { inputId, revision }, WireJson.Options), now);
            transaction.Commit();
            return current with { Status = "cancelled", Revision = revision, UpdatedAt = now };
        }
    }

    public ThreadAsyncInputRecord GetInput(AgentSessionIdentity identity, string inputId)
    {
        ThrowIfDisposed();
        ValidateInputId(inputId);
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            ExpireInputs(connection, transaction, DateTimeOffset.UtcNow, identity);
            var result = ReadInput(connection, transaction, identity, inputId);
            transaction.Commit();
            return result;
        }
    }

    public IReadOnlyList<ThreadAsyncInputRecord> ListInputs(AgentSessionIdentity identity,
        string? threadId, string? status, int limit)
    {
        ThrowIfDisposed();
        if (threadId is not null) ValidateThreadId(threadId);
        if (status is not null && status is not ("pending" or "answered" or "cancelled" or "expired"))
            throw new ArgumentException("Async input status is invalid.");
        if (limit is < 1 || limit > _limits.MaxListItems) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            ExpireInputs(connection, transaction, DateTimeOffset.UtcNow, identity);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT input_id,owner_id,device_id,session_id,thread_id,prompt,schema_json,status,answer_json,revision,expires_utc,created_utc,updated_utc,answered_utc
                FROM thread_async_inputs
                WHERE owner_id=$owner AND device_id=$device AND session_id=$session
                  AND ($thread IS NULL OR thread_id=$thread) AND ($status IS NULL OR status=$status)
                ORDER BY updated_utc DESC LIMIT $limit;
                """;
            AddIdentity(command, identity);
            command.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var rows = new List<ThreadAsyncInputRecord>();
            while (reader.Read()) rows.Add(ReadInputRecord(reader));
            transaction.Commit();
            return rows;
        }
    }

    public void ExpireDueInputs(DateTimeOffset now)
    {
        ThrowIfDisposed();
        now = now.ToUniversalTime();
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            ExpireInputs(connection, transaction, now, null);
            transaction.Commit();
        }
    }

    public void CloseSession(AgentSessionIdentity identity)
    {
        ThrowIfDisposed();
        lock (_mutation)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow;
            var automationThreads = ReadIdsForClose(connection, transaction, "thread_automations", "automation_id", identity, "status IN ('active','paused')");
            foreach (var (id, thread) in automationThreads)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE thread_automations SET status='cancelled',next_due_utc=NULL,revision=revision+1,updated_utc=$now WHERE automation_id=$id;";
                command.Parameters.AddWithValue("$now", now.ToString("O")); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
                AppendEvent(connection, transaction, thread, "automation.cancelled", JsonSerializer.Serialize(new { automationId = id, reason = "session_closed" }, WireJson.Options), now);
            }
            var inputThreads = ReadIdsForClose(connection, transaction, "thread_async_inputs", "input_id", identity, "status='pending'");
            foreach (var (id, thread) in inputThreads)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE thread_async_inputs SET status='cancelled',revision=revision+1,updated_utc=$now WHERE input_id=$id;";
                command.Parameters.AddWithValue("$now", now.ToString("O")); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
                AppendEvent(connection, transaction, thread, "async_input.cancelled", JsonSerializer.Serialize(new { inputId = id, reason = "session_closed" }, WireJson.Options), now);
            }
            transaction.Commit();
        }
    }

    private static IReadOnlyList<(string Id, string ThreadId)> ReadIdsForClose(SqliteConnection connection,
        SqliteTransaction transaction, string table, string idColumn, AgentSessionIdentity identity, string predicate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {idColumn},thread_id FROM {table} WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND {predicate};";
        AddIdentity(command, identity);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static string AddAutomationQueue(SqliteConnection connection, SqliteTransaction transaction,
        ThreadAutomationRecord automation, DateTimeOffset scheduled, DateTimeOffset fired, int skipped)
    {
        int position;
        using (var positionCommand = connection.CreateCommand())
        {
            positionCommand.Transaction = transaction;
            positionCommand.CommandText = "SELECT COALESCE(MAX(position),-1)+1 FROM thread_queue WHERE thread_id=$thread;";
            positionCommand.Parameters.AddWithValue("$thread", automation.ThreadId);
            position = Convert.ToInt32(positionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var payloadDocument = JsonDocument.Parse(automation.PayloadJson);
        var queueId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            type = "automation",
            automationId = automation.AutomationId,
            automation.Name,
            scheduledFor = scheduled,
            firedAt = fired,
            skippedOccurrences = skipped,
            payload = payloadDocument.RootElement.Clone()
        }, WireJson.Options);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO thread_queue(queue_id,thread_id,position,payload_json,status,created_utc,updated_utc) VALUES($id,$thread,$position,$payload,'queued',$now,$now);";
        command.Parameters.AddWithValue("$id", queueId);
        command.Parameters.AddWithValue("$thread", automation.ThreadId);
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$now", fired.ToString("O"));
        command.ExecuteNonQuery();
        return queueId;
    }

    private static void ExpireInputs(SqliteConnection connection, SqliteTransaction transaction,
        DateTimeOffset now, AgentSessionIdentity? identity)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT input_id,thread_id FROM thread_async_inputs
            WHERE status='pending' AND expires_utc IS NOT NULL AND expires_utc<=$now
              AND ($owner IS NULL OR owner_id=$owner) AND ($device IS NULL OR device_id=$device) AND ($session IS NULL OR session_id=$session);
            """;
        select.Parameters.AddWithValue("$now", Stamp(now));
        select.Parameters.AddWithValue("$owner", (object?)identity?.OwnerId ?? DBNull.Value);
        select.Parameters.AddWithValue("$device", (object?)identity?.DeviceId ?? DBNull.Value);
        select.Parameters.AddWithValue("$session", (object?)identity?.SessionId ?? DBNull.Value);
        var due = new List<(string Id, string Thread)>();
        using (var reader = select.ExecuteReader()) while (reader.Read()) due.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var item in due)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE thread_async_inputs SET status='expired',revision=revision+1,updated_utc=$now WHERE input_id=$id AND status='pending';";
            update.Parameters.AddWithValue("$now", now.ToString("O")); update.Parameters.AddWithValue("$id", item.Id);
            if (update.ExecuteNonQuery() == 1)
                AppendEvent(connection, transaction, item.Thread, "async_input.expired",
                    JsonSerializer.Serialize(new { inputId = item.Id }, WireJson.Options), now);
        }
    }

    private void Initialize()
    {
        using var connection = OpenCore();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS thread_automations(
                automation_id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, device_id TEXT NOT NULL, session_id TEXT NOT NULL,
                thread_id TEXT NOT NULL, name TEXT NOT NULL, payload_json TEXT NOT NULL, next_due_utc TEXT NULL,
                interval_seconds INTEGER NULL, status TEXT NOT NULL, revision INTEGER NOT NULL, run_count INTEGER NOT NULL,
                last_run_utc TEXT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                FOREIGN KEY(thread_id) REFERENCES threads(thread_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS thread_async_inputs(
                input_id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, device_id TEXT NOT NULL, session_id TEXT NOT NULL,
                thread_id TEXT NOT NULL, prompt TEXT NOT NULL, schema_json TEXT NOT NULL, status TEXT NOT NULL,
                answer_json TEXT NULL, revision INTEGER NOT NULL, expires_utc TEXT NULL, created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL, answered_utc TEXT NULL,
                FOREIGN KEY(thread_id) REFERENCES threads(thread_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_thread_automations_due ON thread_automations(status,next_due_utc);
            CREATE INDEX IF NOT EXISTS ix_thread_automations_scope ON thread_automations(owner_id,device_id,session_id,updated_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_thread_async_inputs_pending ON thread_async_inputs(status,expires_utc);
            CREATE INDEX IF NOT EXISTS ix_thread_async_inputs_scope ON thread_async_inputs(owner_id,device_id,session_id,updated_utc DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureThread(SqliteConnection connection, SqliteTransaction transaction, string threadId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM threads WHERE thread_id=$id LIMIT 1;";
        command.Parameters.AddWithValue("$id", threadId);
        if (command.ExecuteScalar() is null) throw new KeyNotFoundException("Thread not found.");
    }

    private static ThreadAutomationRecord ReadAutomation(SqliteConnection connection, SqliteTransaction? transaction,
        AgentSessionIdentity identity, string id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT automation_id,owner_id,device_id,session_id,thread_id,name,payload_json,next_due_utc,interval_seconds,status,revision,run_count,last_run_utc,created_utc,updated_utc FROM thread_automations WHERE automation_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session LIMIT 1;";
        command.Parameters.AddWithValue("$id", id); AddIdentity(command, identity);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Automation was not found in this session.");
        return ReadAutomationRecord(reader);
    }

    private static ThreadAutomationRecord ReadAutomationRecord(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11),
        reader.IsDBNull(12) ? null : ParseTime(reader.GetString(12)), ParseTime(reader.GetString(13)), ParseTime(reader.GetString(14)));

    private static ThreadAsyncInputRecord ReadInput(SqliteConnection connection, SqliteTransaction? transaction,
        AgentSessionIdentity identity, string id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT input_id,owner_id,device_id,session_id,thread_id,prompt,schema_json,status,answer_json,revision,expires_utc,created_utc,updated_utc,answered_utc FROM thread_async_inputs WHERE input_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session LIMIT 1;";
        command.Parameters.AddWithValue("$id", id); AddIdentity(command, identity);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Async input was not found in this session.");
        return ReadInputRecord(reader);
    }

    private static ThreadAsyncInputRecord ReadInputRecord(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt64(9), reader.IsDBNull(10) ? null : ParseTime(reader.GetString(10)), ParseTime(reader.GetString(11)),
        ParseTime(reader.GetString(12)), reader.IsDBNull(13) ? null : ParseTime(reader.GetString(13)));

    private static ThreadEventRecord AppendEvent(SqliteConnection connection, SqliteTransaction transaction,
        string threadId, string kind, string payload, DateTimeOffset now)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO thread_events(thread_id,kind,payload_json,created_utc) VALUES($thread,$kind,$payload,$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$thread", threadId); command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$now", now.ToString("O"));
        var eventId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var touch = connection.CreateCommand(); touch.Transaction = transaction;
        touch.CommandText = "UPDATE threads SET updated_utc=$now WHERE thread_id=$thread;";
        touch.Parameters.AddWithValue("$now", now.ToString("O")); touch.Parameters.AddWithValue("$thread", threadId); touch.ExecuteNonQuery();
        return new(eventId, threadId, kind, payload, now);
    }

    private static void AddIdentity(SqliteCommand command, AgentSessionIdentity identity)
    {
        command.Parameters.AddWithValue("$owner", identity.OwnerId);
        command.Parameters.AddWithValue("$device", identity.DeviceId);
        command.Parameters.AddWithValue("$session", identity.SessionId);
    }

    private string ValidateSchema(string schemaJson)
    {
        schemaJson = ValidateJson(schemaJson, _limits.MaxSchemaChars, "Input schema", requireObject: true);
        using var document = JsonDocument.Parse(schemaJson);
        _ = SchemaGuard.Compile(document.RootElement);
        return schemaJson;
    }

    private static string ValidateJson(string value, int max, string name, bool requireObject)
    {
        if (value is null || value.Length > max) throw new ArgumentException(name + " exceeds bounded size.");
        try
        {
            using var document = JsonDocument.Parse(value);
            if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(name + " must be a JSON object.");
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex) { throw new ArgumentException(name + " must be valid JSON.", ex); }
    }

    private static string ValidateText(string value, int max, string name)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length is < 1 || value.Length > max) throw new ArgumentException($"{name} must contain 1..{max} characters.");
        return value;
    }

    private void ValidateInterval(int? seconds)
    {
        if (seconds is not null && (seconds < _limits.MinIntervalSeconds || seconds > _limits.MaxIntervalSeconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), $"Interval must be {_limits.MinIntervalSeconds}..{_limits.MaxIntervalSeconds} seconds.");
    }

    private static void ValidateThreadId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Thread ID is invalid.");
    }
    private static void ValidateAutomationId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^automation_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Automation ID is invalid.");
    }
    private static void ValidateInputId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^input_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Async input ID is invalid.");
    }

    private static AgentRequestException Conflict(string subject, long expected, long? current) =>
        new(subject == "Automation" ? "AUTOMATION_REVISION_CONFLICT" : "ASYNC_INPUT_REVISION_CONFLICT",
            current is null
                ? $"{subject} changed concurrently. Read it again and reconcile the operation."
                : $"{subject} revision changed from {expected} to {current.Value}. Read it again and reconcile the operation.");

    private static void ValidateLimits(ThreadInteractionLimits limits)
    {
        if (limits.MaxAutomationsPerSession < 1 || limits.MaxPendingInputsPerSession < 1 ||
            limits.MaxNameChars < 1 || limits.MaxPromptChars < 1 || limits.MaxPayloadChars < 2 ||
            limits.MaxSchemaChars < 2 || limits.MaxAnswerChars < 1 || limits.MaxListItems < 1 ||
            limits.MinIntervalSeconds < 1 || limits.MaxIntervalSeconds < limits.MinIntervalSeconds ||
            limits.MaxExpirationSeconds < 1) throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private SqliteConnection Open() { ThrowIfDisposed(); return OpenCore(); }
    private SqliteConnection OpenCore()
    {
        var connection = new SqliteConnection(_connectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery();
        return connection;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

/// <summary>Public tools plus a local durable pump for thread events, queue automations and async input.</summary>
public sealed class ThreadInteractionRuntimeToolSet : IDisposable
{
    private readonly SqliteThreadInteractionStore _store;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _pump;
    private readonly TimeSpan _pumpInterval;

    public ThreadInteractionRuntimeToolSet(string databasePath, ThreadInteractionLimits? limits = null,
        bool startPump = true, TimeSpan? pumpInterval = null)
    {
        _store = new(databasePath, limits);
        _pumpInterval = pumpInterval ?? TimeSpan.FromSeconds(5);
        if (_pumpInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pumpInterval));
        if (startPump) _pump = Task.Run(PumpAsync);
    }

    public IEnumerable<IAgentTool> Tools =>
    [
        new Tool(this, "thread_list"), new WaitTool(this, "thread_wait"),
        new Tool(this, "automation_create"), new Tool(this, "automation_update"),
        new Tool(this, "automation_get"), new Tool(this, "automation_list"),
        new Tool(this, "automation_cancel"), new Tool(this, "automation_run_due"),
        new Tool(this, "async_input_request"), new Tool(this, "async_input_respond"),
        new WaitTool(this, "async_input_wait"), new Tool(this, "async_input_list"),
        new Tool(this, "async_input_cancel")
    ];

    public void CloseSession(AgentSessionIdentity identity) => _store.CloseSession(identity);
    public IReadOnlyList<ThreadAutomationRun> RunDueNow(DateTimeOffset now, int maxRuns = 100) =>
        _store.RunDueAutomations(now, maxRuns);

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(_pumpInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    _store.RunDueAutomations(DateTimeOffset.UtcNow, 100);
                    _store.ExpireDueInputs(DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_pump is not null) try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _store.Dispose();
        _stop.Dispose();
    }

    private async Task<ToolReply> ExecuteAsync(string operation, JsonElement args,
        AgentExecutionContext context, CancellationToken ct)
    {
        try
        {
            var identity = context.RequireSessionIdentity();
            object result = operation switch
            {
                "thread_list" => _store.ListThreads(Project(args, context), Optional(args, "query", 1_000),
                    Int(args, "limit", 50)),
                "thread_wait" => await WaitThread(args, context, ct).ConfigureAwait(false),
                "automation_create" => CreateAutomation(identity, args, context),
                "automation_update" => UpdateAutomation(identity, args, context),
                "automation_get" => GetAutomation(identity, args, context),
                "automation_list" => ListAutomations(identity, args, context),
                "automation_cancel" => CancelAutomation(identity, args, context),
                "automation_run_due" => RunDue(identity, args, context),
                "async_input_request" => RequestInput(identity, args, context),
                "async_input_respond" => RespondInput(identity, args, context),
                "async_input_wait" => await WaitInput(identity, args, context, ct).ConfigureAwait(false),
                "async_input_list" => ListInputs(identity, args, context),
                _ => CancelInput(identity, args, context)
            };
            return new ToolReply(JsonSerializer.Serialize(result, WireJson.Options));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or
                                   DirectoryNotFoundException or UnauthorizedAccessException or SqliteException or JsonException)
        {
            return ToolReply.Error(ex.Message);
        }
    }

    private async Task<object> WaitThread(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var threadId = Required(args, "threadId", 128);
        RequireThreadProject(threadId, context);
        var cursor = args.TryGetProperty("cursor", out var cursorNode) ? cursorNode.GetInt64() : 0;
        var limit = Int(args, "limit", 100);
        var yieldMs = Int(args, "yieldTimeMs", 5_000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(yieldMs);
        IReadOnlyList<ThreadEventRecord> events;
        while ((events = _store.ReadEvents(threadId, cursor, limit)).Count == 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, yieldMs))), ct).ConfigureAwait(false);
        var nextCursor = events.Count == 0 ? cursor : events[^1].EventId;
        var more = events.Count == limit && _store.ReadEvents(threadId, nextCursor, 1).Count > 0;
        return new { threadId, events, nextCursor, hasMore = more, timedOut = events.Count == 0 };
    }

    private ThreadAutomationRecord CreateAutomation(AgentSessionIdentity identity, JsonElement args, AgentExecutionContext context)
    {
        var threadId = Required(args, "threadId", 128); RequireThreadProject(threadId, context);
        return _store.CreateAutomation(identity, threadId, Required(args, "name", 240),
            Required(args, "payloadJson", 100_000), Date(args, "nextDueUtc"),
            args.TryGetProperty("intervalSeconds", out var interval) ? interval.GetInt32() : null);
    }

    private ThreadAutomationRecord UpdateAutomation(AgentSessionIdentity identity, JsonElement args, AgentExecutionContext context)
    {
        var id = Required(args, "automationId", 80);
        var current = _store.GetAutomation(identity, id); RequireThreadProject(current.ThreadId, context);
        var intervalSupplied = args.TryGetProperty("intervalSeconds", out var interval);
        var intervalValue = intervalSupplied ? interval.GetInt32() : (int?)null;
        if (intervalValue == 0) intervalValue = null;
        return _store.UpdateAutomation(identity, id, Long(args, "expectedRevision"),
            Optional(args, "name", 240), Optional(args, "payloadJson", 100_000),
            args.TryGetProperty("nextDueUtc", out _) ? Date(args, "nextDueUtc") : null,
            intervalValue, intervalSupplied,
            args.TryGetProperty("paused", out var paused) ? paused.GetBoolean() : null);
    }

    private ThreadAutomationRecord GetAutomation(AgentSessionIdentity identity, JsonElement args, AgentExecutionContext context)
    {
        var row = _store.GetAutomation(identity, Required(args, "automationId", 80));
        RequireThreadProject(row.ThreadId, context); return row;
    }

    private IReadOnlyList<ThreadAutomationRecord> ListAutomations(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context)
    {
        var threadId = Optional(args, "threadId", 128);
        if (threadId is not null) RequireThreadProject(threadId, context);
        var rows = _store.ListAutomations(identity, threadId, Optional(args, "status", 32), Int(args, "limit", 50));
        foreach (var projectThread in rows.Select(row => row.ThreadId).Distinct(StringComparer.Ordinal)) RequireThreadProject(projectThread, context);
        return rows;
    }

    private ThreadAutomationRecord CancelAutomation(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context)
    {
        var id = Required(args, "automationId", 80);
        var current = _store.GetAutomation(identity, id); RequireThreadProject(current.ThreadId, context);
        return _store.CancelAutomation(identity, id, Long(args, "expectedRevision"));
    }

    private IReadOnlyList<ThreadAutomationRun> RunDue(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context)
    {
        var threadId = Optional(args, "threadId", 128);
        if (threadId is not null) RequireThreadProject(threadId, context);
        var rows = _store.RunDueAutomations(DateTimeOffset.UtcNow, Int(args, "maxRuns", 100), identity, threadId);
        foreach (var projectThread in rows.Select(row => row.ThreadId).Distinct(StringComparer.Ordinal)) RequireThreadProject(projectThread, context);
        return rows;
    }

    private ThreadAsyncInputRecord RequestInput(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context)
    {
        var threadId = Required(args, "threadId", 128); RequireThreadProject(threadId, context);
        return _store.CreateInput(identity, threadId, Required(args, "prompt", 20_000),
            Optional(args, "schemaJson", 64_000) ?? "{}",
            args.TryGetProperty("expiresInSeconds", out var expires) ? expires.GetInt32() : null);
    }

    private ThreadAsyncInputRecord RespondInput(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context)
    {
        var id = Required(args, "inputId", 80);
        var current = _store.GetInput(identity, id); RequireThreadProject(current.ThreadId, context);
        return _store.RespondInput(identity, id, Long(args, "expectedRevision"), Required(args, "answerJson", 100_000, true));
    }

    private async Task<ThreadAsyncInputRecord> WaitInput(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context, CancellationToken ct)
    {
        var id = Required(args, "inputId", 80);
        var yieldMs = Int(args, "yieldTimeMs", 5_000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(yieldMs);
        while (true)
        {
            var current = _store.GetInput(identity, id); RequireThreadProject(current.ThreadId, context);
            if (current.Status != "pending" || DateTimeOffset.UtcNow >= deadline) return current;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(200, Math.Max(1, yieldMs))), ct).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<ThreadAsyncInputRecord> ListInputs(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context)
    {
        var threadId = Optional(args, "threadId", 128);
        if (threadId is not null) RequireThreadProject(threadId, context);
        var rows = _store.ListInputs(identity, threadId, Optional(args, "status", 32), Int(args, "limit", 50));
        foreach (var projectThread in rows.Select(row => row.ThreadId).Distinct(StringComparer.Ordinal)) RequireThreadProject(projectThread, context);
        return rows;
    }

    private ThreadAsyncInputRecord CancelInput(AgentSessionIdentity identity, JsonElement args,
        AgentExecutionContext context)
    {
        var id = Required(args, "inputId", 80);
        var current = _store.GetInput(identity, id); RequireThreadProject(current.ThreadId, context);
        return _store.CancelInput(identity, id, Long(args, "expectedRevision"));
    }

    private void RequireThreadProject(string threadId, AgentExecutionContext context) =>
        RequireProject(_store.GetThreadProject(threadId), context);

    private static string Project(JsonElement args, AgentExecutionContext context)
    {
        var requested = Optional(args, "project", 1024);
        var full = WorkspaceDirectories.Normalize(WorkspaceDirectories.ResolvePath(requested, context.Workspace));
        RequireProject(full, context); return full;
    }

    private static void RequireProject(string project, AgentExecutionContext context)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        var roots = new[] { context.Workspace }.Concat(context.AdditionalDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (roots.Length == 0) throw new AgentRequestException("WORKSPACE_REQUIRED",
            "Select a session workspace before using project-scoped thread interaction tools.");
        foreach (var root in roots)
        {
            var selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (full.Equals(selected, comparison) || full.StartsWith(selected + Path.DirectorySeparatorChar, comparison) ||
                full.StartsWith(selected + Path.AltDirectorySeparatorChar, comparison)) return;
        }
        throw new UnauthorizedAccessException("Thread project is outside selected workspaces.");
    }

    private static string Required(JsonElement args, string name, int max, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException(name + " is required.");
        var text = value.GetString() ?? string.Empty;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > max)
            throw new ArgumentException(name + " is required and bounded.");
        return text;
    }
    private static string? Optional(JsonElement args, string name, int max)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || (value.GetString()?.Length ?? 0) > max)
            throw new ArgumentException(name + " exceeds bounded size.");
        return value.GetString();
    }
    private static int Int(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
    private static long Long(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) ? value.GetInt64() : throw new ArgumentException(name + " is required.");
    private static DateTimeOffset Date(JsonElement args, string name)
    {
        var text = Required(args, name, 64);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : throw new ArgumentException(name + " must be an ISO-8601 timestamp with offset.");
    }

    private sealed class Tool(ThreadInteractionRuntimeToolSet owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = DescriptorFor(operation);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            owner.ExecuteAsync(operation, arguments, context, cancellationToken);
    }

    private sealed class WaitTool(ThreadInteractionRuntimeToolSet owner, string operation) : ICompositeAgentTool
    {
        public ToolDescriptor Descriptor { get; } = DescriptorFor(operation);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            owner.ExecuteAsync(operation, arguments, context, cancellationToken);
    }

    private static ToolDescriptor DescriptorFor(string operation)
    {
        var (id, name, category, description, readOnly, sensitive) = operation switch
        {
            "thread_list" => ("thread.list", "thread__list", "thread", "List bounded durable thread headers in a selected workspace project.", true, false),
            "thread_wait" => ("thread.wait", "thread__wait", "thread", "Wait without occupying a normal execution slot for durable thread events after a cursor.", true, false),
            "automation_create" => ("automation.create", "automation_create", "automation", "Create a durable one-time or interval automation that enqueues a thread item when due; it never calls a model or executes tools automatically.", false, true),
            "automation_update" => ("automation.update", "automation_update", "automation", "Update or pause/resume a durable thread automation using an expected revision.", false, true),
            "automation_get" => ("automation.get", "automation_get", "automation", "Read one session-owned durable thread automation.", true, false),
            "automation_list" => ("automation.list", "automation_list", "automation", "List bounded session-owned thread automations.", true, false),
            "automation_cancel" => ("automation.cancel", "automation_cancel", "automation", "Cancel a durable thread automation using an expected revision.", false, true),
            "automation_run_due" => ("automation.run_due", "automation_run_due", "automation", "Materialize currently due session-owned automations into thread queue items now.", false, true),
            "async_input_request" => ("async_input.request", "async_input_request", "async_input", "Create a durable session-owned structured input request attached to a thread.", false, true),
            "async_input_respond" => ("async_input.respond", "async_input_respond", "async_input", "Answer a pending async input at an expected revision; JSON Schema is enforced locally.", false, true),
            "async_input_wait" => ("async_input.wait", "async_input_wait", "async_input", "Wait without occupying a normal execution slot for an async input to be answered, cancelled, or expired.", true, false),
            "async_input_list" => ("async_input.list", "async_input_list", "async_input", "List bounded async input requests owned by this session.", true, false),
            _ => ("async_input.cancel", "async_input_cancel", "async_input", "Cancel a pending async input using an expected revision.", false, true)
        };
        return new(id, name, category, description, Schema(operation), readOnly, sensitive);
    }

    private static JsonElement Schema(string op)
    {
        var threadId = new { type = "string", pattern = "^[a-f0-9]{32}$" };
        var automationId = new { type = "string", pattern = "^automation_[a-f0-9]{32}$" };
        var inputId = new { type = "string", pattern = "^input_[a-f0-9]{32}$" };
        object schema = op switch
        {
            "thread_list" => new { type = "object", properties = new Dictionary<string, object> { ["project"] = new { type = "string", maxLength = 1024 }, ["query"] = new { type = "string", maxLength = 1000 }, ["limit"] = new { type = "integer", minimum = 1, maximum = 200, @default = 50 } }, additionalProperties = false },
            "thread_wait" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = threadId, ["cursor"] = new { type = "integer", minimum = 0, @default = 0 }, ["limit"] = new { type = "integer", minimum = 1, maximum = 200, @default = 100 }, ["yieldTimeMs"] = new { type = "integer", minimum = 0, maximum = 300000, @default = 5000 } }, required = new[] { "threadId" }, additionalProperties = false },
            "automation_create" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = threadId, ["name"] = new { type = "string", minLength = 1, maxLength = 240 }, ["payloadJson"] = new { type = "string", maxLength = 100000 }, ["nextDueUtc"] = new { type = "string", maxLength = 64 }, ["intervalSeconds"] = new { type = "integer", minimum = 60, maximum = 31536000 } }, required = new[] { "threadId", "name", "payloadJson", "nextDueUtc" }, additionalProperties = false },
            "automation_update" => new { type = "object", properties = new Dictionary<string, object> { ["automationId"] = automationId, ["expectedRevision"] = new { type = "integer", minimum = 1 }, ["name"] = new { type = "string", minLength = 1, maxLength = 240 }, ["payloadJson"] = new { type = "string", maxLength = 100000 }, ["nextDueUtc"] = new { type = "string", maxLength = 64 }, ["intervalSeconds"] = new { type = "integer", minimum = 0, maximum = 31536000, description = "Use 0 to convert to one-time." }, ["paused"] = new { type = "boolean" } }, required = new[] { "automationId", "expectedRevision" }, additionalProperties = false },
            "automation_get" => new { type = "object", properties = new Dictionary<string, object> { ["automationId"] = automationId }, required = new[] { "automationId" }, additionalProperties = false },
            "automation_list" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = threadId, ["status"] = new { type = "string", @enum = new[] { "active", "paused", "completed", "cancelled" } }, ["limit"] = new { type = "integer", minimum = 1, maximum = 200, @default = 50 } }, additionalProperties = false },
            "automation_cancel" => new { type = "object", properties = new Dictionary<string, object> { ["automationId"] = automationId, ["expectedRevision"] = new { type = "integer", minimum = 1 } }, required = new[] { "automationId", "expectedRevision" }, additionalProperties = false },
            "automation_run_due" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = threadId, ["maxRuns"] = new { type = "integer", minimum = 1, maximum = 200, @default = 100 } }, additionalProperties = false },
            "async_input_request" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = threadId, ["prompt"] = new { type = "string", minLength = 1, maxLength = 20000 }, ["schemaJson"] = new { type = "string", maxLength = 64000, @default = "{}" }, ["expiresInSeconds"] = new { type = "integer", minimum = 1, maximum = 604800 } }, required = new[] { "threadId", "prompt" }, additionalProperties = false },
            "async_input_respond" => new { type = "object", properties = new Dictionary<string, object> { ["inputId"] = inputId, ["expectedRevision"] = new { type = "integer", minimum = 1 }, ["answerJson"] = new { type = "string", maxLength = 100000 } }, required = new[] { "inputId", "expectedRevision", "answerJson" }, additionalProperties = false },
            "async_input_wait" => new { type = "object", properties = new Dictionary<string, object> { ["inputId"] = inputId, ["yieldTimeMs"] = new { type = "integer", minimum = 0, maximum = 300000, @default = 5000 } }, required = new[] { "inputId" }, additionalProperties = false },
            "async_input_list" => new { type = "object", properties = new Dictionary<string, object> { ["threadId"] = threadId, ["status"] = new { type = "string", @enum = new[] { "pending", "answered", "cancelled", "expired" } }, ["limit"] = new { type = "integer", minimum = 1, maximum = 200, @default = 50 } }, additionalProperties = false },
            _ => new { type = "object", properties = new Dictionary<string, object> { ["inputId"] = inputId, ["expectedRevision"] = new { type = "integer", minimum = 1 } }, required = new[] { "inputId", "expectedRevision" }, additionalProperties = false }
        };
        return WireJson.Element(schema);
    }
}
