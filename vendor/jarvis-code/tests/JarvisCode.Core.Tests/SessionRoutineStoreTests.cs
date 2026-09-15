using JarvisCode.Core.Routines;

namespace JarvisCode.Core.Tests;

public sealed class SessionRoutineStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-cron-store-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Session_jobs_never_enter_the_file_and_end_without_deleting_durable_jobs()
    {
        var file = Path.Combine(_root, "routines.json");
        var store = new RoutineStore(file);
        store.Save([new Routine { OwnerSessionId = "session", Instruction = "ephemeral secret" }]);
        Assert.False(File.Exists(file));
        Assert.Single(new RoutineStore(file).Load());
        store.Update(rows => rows.Add(new Routine { Instruction = "durable prompt" }));
        Assert.DoesNotContain("ephemeral secret", File.ReadAllText(file));
        Assert.Contains("durable prompt", File.ReadAllText(file));
        store.RemoveOwnedBy("session");
        Assert.Equal("durable prompt", Assert.Single(store.Load()).Instruction);
    }

    [Fact]
    public void Concurrent_tools_using_separate_store_instances_do_not_lose_jobs()
    {
        var file = Path.Combine(_root, "routines.json");
        Parallel.For(0, 32, i => new RoutineStore(file).Update(rows => rows.Add(new Routine
        { OwnerSessionId = "session", Instruction = "job-" + i })));
        var store = new RoutineStore(file);
        Assert.Equal(32, store.Load().Count);
        store.RemoveOwnedBy("session");
        Assert.Empty(store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
