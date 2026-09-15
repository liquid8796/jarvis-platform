using Jarvis.Agent.Core.Autonomous.Memory;

namespace Jarvis.Core.Tests;

public sealed class SqliteMemoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-memory-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_root, "memory.db");

    public SqliteMemoryStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Values_persist_across_reopen_and_scopes_are_isolated()
    {
        using (var store = new SqliteMemoryStore(Db))
        {
            store.Upsert(new MemoryPartition("owner-a", "project-a", "notes"), "key", "value-a", "test");
            store.Upsert(new MemoryPartition("owner-b", "project-a", "notes"), "key", "value-b", "test");
        }

        using var reopened = new SqliteMemoryStore(Db);
        Assert.Equal("value-a", reopened.Get(new MemoryPartition("owner-a", "project-a", "notes"), "key")?.Value);
        Assert.Equal("value-b", reopened.Get(new MemoryPartition("owner-b", "project-a", "notes"), "key")?.Value);
        Assert.Null(reopened.Get(new MemoryPartition("owner-a", "project-b", "notes"), "key"));
    }

    [Fact]
    public void Upsert_retains_provenance_searches_bounded_and_excludes_expired_rows()
    {
        using var store = new SqliteMemoryStore(Db);
        var scope = new MemoryPartition("owner", "project", "notes");
        store.Upsert(scope, "alpha", "first value", "source-1");
        store.Upsert(scope, "alpha", "updated value", "source-2");
        store.Upsert(scope, "beta", "search needle", "source-3");
        store.Upsert(scope, "expired", "search needle old", "source-4", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(15);

        var alpha = store.Get(scope, "alpha");
        Assert.NotNull(alpha);
        Assert.Equal("updated value", alpha.Value);
        Assert.Equal("source-2", alpha.Provenance);
        Assert.Null(store.Get(scope, "expired"));
        var hits = store.Search(scope, "needle", limit: 1);
        Assert.Single(hits);
        Assert.Equal("beta", hits[0].Key);
    }

    [Fact]
    public async Task Concurrent_writers_do_not_lose_distinct_keys()
    {
        using var store = new SqliteMemoryStore(Db);
        var scope = new MemoryPartition("owner", "project", "notes");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            store.Upsert(scope, "k" + i, "v" + i, "parallel"))));

        Assert.Equal(12, store.Search(scope, "", limit: 20).Count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
