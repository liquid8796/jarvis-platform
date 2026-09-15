using Microsoft.Data.Sqlite;

namespace Jarvis.Agent.Core.Autonomous.Memory;

public sealed record MemoryPartition(string OwnerId, string Project, string Namespace)
{
    public MemoryPartition Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerId) || OwnerId.Length > 256 ||
            string.IsNullOrWhiteSpace(Project) || Project.Length > 1024 ||
            string.IsNullOrWhiteSpace(Namespace) || Namespace.Length > 128)
            throw new ArgumentException("Memory owner, project and namespace must be non-empty and bounded.");
        return this;
    }
}

public sealed record DurableMemoryEntry(
    string OwnerId,
    string Project,
    string Namespace,
    string Key,
    string Value,
    string? Provenance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Durable, partitioned SQLite memory. The legacy string-only IAgentMemory API maps
/// to a default partition; richer callers should use MemoryPartition overloads.
/// </summary>
public sealed class SqliteMemoryStore : IAgentMemory, IDisposable
{
    private static readonly MemoryPartition DefaultPartition = new("default", "default", "default");
    private readonly string _connectionString;
    private int _disposed;

    public SqliteMemoryStore()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "agent-memory.db")) { }

    public SqliteMemoryStore(string databasePath)
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

    public void Save(string key, string value) => Upsert(DefaultPartition, key, value, "legacy");

    public string? Get(string key) => Get(DefaultPartition, key)?.Value;

    public void Upsert(MemoryPartition partition, string key, string value, string? provenance = null, TimeSpan? ttl = null)
    {
        ThrowIfDisposed();
        ValidateEntry(partition, key, value, provenance, ttl);
        var now = DateTimeOffset.UtcNow;
        var expires = ttl is null ? (DateTimeOffset?)null : now.Add(ttl.Value);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory(owner, project, namespace, key, value, provenance, created_utc, updated_utc, expires_utc)
            VALUES($owner, $project, $namespace, $key, $value, $provenance, $created, $updated, $expires)
            ON CONFLICT(owner, project, namespace, key) DO UPDATE SET
                value = excluded.value,
                provenance = excluded.provenance,
                updated_utc = excluded.updated_utc,
                expires_utc = excluded.expires_utc;
            """;
        BindPartition(command, partition);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$provenance", (object?)provenance ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", expires is null ? DBNull.Value : expires.Value.ToString("O"));
        ExecuteWithBusyRetry(command);
    }

    public DurableMemoryEntry? Get(MemoryPartition partition, string key)
    {
        ThrowIfDisposed();
        partition.Validate();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512) throw new ArgumentException("Memory key must contain 1..512 characters.", nameof(key));
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        DeleteExpired(connection, partition, now);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner, project, namespace, key, value, provenance, created_utc, updated_utc, expires_utc
            FROM memory
            WHERE owner=$owner AND project=$project AND namespace=$namespace AND key=$key
              AND (expires_utc IS NULL OR expires_utc > $now)
            LIMIT 1;
            """;
        BindPartition(command, partition);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", now);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public IReadOnlyList<DurableMemoryEntry> Search(MemoryPartition partition, string query, int limit = 20)
    {
        ThrowIfDisposed();
        partition.Validate();
        if (query is null || query.Length > 1000) throw new ArgumentException("Memory search query must contain at most 1000 characters.", nameof(query));
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit), "Memory search limit must be 1..50.");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        DeleteExpired(connection, partition, now);
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrEmpty(query)
            ? """
              SELECT owner, project, namespace, key, value, provenance, created_utc, updated_utc, expires_utc
              FROM memory
              WHERE owner=$owner AND project=$project AND namespace=$namespace
                AND (expires_utc IS NULL OR expires_utc > $now)
              ORDER BY updated_utc DESC, key ASC
              LIMIT $limit;
              """
            : """
              SELECT owner, project, namespace, key, value, provenance, created_utc, updated_utc, expires_utc
              FROM memory
              WHERE owner=$owner AND project=$project AND namespace=$namespace
                AND (expires_utc IS NULL OR expires_utc > $now)
                AND (instr(lower(key), lower($query)) > 0 OR instr(lower(value), lower($query)) > 0)
              ORDER BY updated_utc DESC, key ASC
              LIMIT $limit;
              """;
        BindPartition(command, partition);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$limit", limit);
        if (!string.IsNullOrEmpty(query)) command.Parameters.AddWithValue("$query", query);
        using var reader = command.ExecuteReader();
        var results = new List<DurableMemoryEntry>(limit);
        while (reader.Read()) results.Add(ReadEntry(reader));
        return results;
    }

    private void Initialize()
    {
        using var connection = OpenCore();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS memory(
                owner TEXT NOT NULL,
                project TEXT NOT NULL,
                namespace TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                provenance TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                expires_utc TEXT NULL,
                PRIMARY KEY(owner, project, namespace, key)
            );
            CREATE INDEX IF NOT EXISTS ix_memory_scope_updated
                ON memory(owner, project, namespace, updated_utc DESC);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        ThrowIfDisposed();
        return OpenCore();
    }

    private SqliteConnection OpenCore()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void DeleteExpired(SqliteConnection connection, MemoryPartition partition, string now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory WHERE owner=$owner AND project=$project AND namespace=$namespace AND expires_utc IS NOT NULL AND expires_utc <= $now;";
        BindPartition(command, partition);
        command.Parameters.AddWithValue("$now", now);
        ExecuteWithBusyRetry(command);
    }

    private static void ExecuteWithBusyRetry(SqliteCommand command)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { command.ExecuteNonQuery(); return; }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < 4)
            { Thread.Sleep(25 * (attempt + 1)); }
        }
    }

    private static void BindPartition(SqliteCommand command, MemoryPartition partition)
    {
        partition.Validate();
        command.Parameters.AddWithValue("$owner", partition.OwnerId);
        command.Parameters.AddWithValue("$project", partition.Project);
        command.Parameters.AddWithValue("$namespace", partition.Namespace);
    }

    private static DurableMemoryEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture));

    private static void ValidateEntry(MemoryPartition partition, string key, string value, string? provenance, TimeSpan? ttl)
    {
        partition.Validate();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512) throw new ArgumentException("Memory key must contain 1..512 characters.", nameof(key));
        if (value is null || value.Length > 1_000_000) throw new ArgumentException("Memory value must contain at most 1,000,000 characters.", nameof(value));
        if (provenance?.Length > 2048) throw new ArgumentException("Memory provenance must contain at most 2048 characters.", nameof(provenance));
        if (ttl is { } lifetime && (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(3650)))
            throw new ArgumentOutOfRangeException(nameof(ttl), "Memory TTL must be positive and at most ten years.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
