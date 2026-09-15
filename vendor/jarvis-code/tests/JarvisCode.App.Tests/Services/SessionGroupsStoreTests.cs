using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class SessionGroupsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jarvis-groups-" + Guid.NewGuid().ToString("N")[..8]);

    private string FilePath => Path.Combine(_dir, "session-groups.json");

    private SessionGroupsStore NewStore() => new(FilePath);

    // Where a session came from, when a spawn_task chip started it. The reference
    // keeps this on the spawned session, which is what makes the answer outlive
    // the chip and the transcript.

    [Fact]
    public void AStartedChipIsRememberedAcrossAReload()
    {
        var store = NewStore();
        store.RecordSpawnedFrom("spawned-1", "suggesting-1", "task_0000aaaa");

        var reloaded = NewStore();
        Assert.True(reloaded.WasSpawned("suggesting-1", "task_0000aaaa"));
        Assert.False(reloaded.WasSpawned("suggesting-1", "task_0000bbbb"));
    }

    [Fact]
    public void AnotherSessionsChipIsNotThisOnes()
    {
        var store = NewStore();
        store.RecordSpawnedFrom("spawned-1", "suggesting-1", "task_0000aaaa");

        Assert.True(store.WasSpawned("suggesting-1", "task_0000aaaa"));
        Assert.False(store.WasSpawned("suggesting-2", "task_0000aaaa"));
    }

    [Fact]
    public void DeletingTheSpawnedSessionTakesTheRecordWithIt()
    {
        // The reference walks the live session list, so a deleted session stops
        // answering for the chip that started it.
        var store = NewStore();
        store.RecordSpawnedFrom("spawned-1", "suggesting-1", "task_0000aaaa");
        store.RecordSpawnedFrom("spawned-2", "suggesting-1", "task_0000bbbb");

        store.Prune(["suggesting-1", "spawned-2"]);

        Assert.False(store.WasSpawned("suggesting-1", "task_0000aaaa"));
        Assert.True(store.WasSpawned("suggesting-1", "task_0000bbbb"));
        Assert.True(NewStore().WasSpawned("suggesting-1", "task_0000bbbb"));
    }

    [Fact]
    public void CreateMoveAndPersistRoundTrip()
    {
        var store = NewStore();
        var group = store.CreateGroup("VM OCI");
        store.MoveSession("s1", group.Id);
        store.MoveSession("s2", group.Id);
        store.SetCollapsed(group.Id, collapsed: true);

        var reloaded = NewStore();
        var loaded = Assert.Single(reloaded.Data.Groups);
        Assert.Equal("VM OCI", loaded.Name);
        Assert.True(loaded.Collapsed);
        Assert.Equal(["s1", "s2"], loaded.SessionIds);
        Assert.Equal(loaded.Id, reloaded.GroupOf("s1")!.Id);
    }

    [Fact]
    public void MovingBetweenGroupsRemovesFromTheOldOne()
    {
        var store = NewStore();
        var a = store.CreateGroup("a");
        var b = store.CreateGroup("b");
        store.MoveSession("s1", a.Id);
        store.MoveSession("s1", b.Id);
        Assert.Empty(a.SessionIds);
        Assert.Equal(["s1"], b.SessionIds);

        // null moves it back to Ungrouped.
        store.MoveSession("s1", null);
        Assert.Null(store.GroupOf("s1"));
    }

    [Fact]
    public void DeletingAGroupOrphansItsSessionsToUngrouped()
    {
        var store = NewStore();
        var group = store.CreateGroup("temp");
        store.MoveSession("s1", group.Id);
        store.DeleteGroup(group.Id);
        Assert.Empty(store.Data.Groups);
        Assert.Null(store.GroupOf("s1"));
    }

    [Fact]
    public void ArchiveToggleAndPruneStaleIds()
    {
        var store = NewStore();
        var group = store.CreateGroup("g");
        store.MoveSession("kept", group.Id);
        store.MoveSession("stale", group.Id);
        store.SetArchived("gone", archived: true);
        store.SetArchived("kept-archived", archived: true);

        store.Prune(["kept", "kept-archived"]);

        Assert.Equal(["kept"], group.SessionIds);
        Assert.Equal(["kept-archived"], store.Data.ArchivedSessionIds);
        Assert.True(store.IsArchived("kept-archived"));

        store.SetArchived("kept-archived", archived: false);
        Assert.False(store.IsArchived("kept-archived"));
    }

    [Fact]
    public void PinAndUnreadRoundTripAndPrune()
    {
        var store = NewStore();
        store.SetPinned("s1", true);
        store.SetPinned("s2", true);
        store.SetUnread("s2", true);
        store.SetUnread("gone", true);

        var reloaded = NewStore();
        Assert.True(reloaded.IsPinned("s1"));
        Assert.True(reloaded.IsUnread("s2"));
        Assert.False(reloaded.IsUnread("s1"));
        Assert.Equal(["s1", "s2"], reloaded.Data.PinnedSessionIds);

        reloaded.SetPinned("s1", false);
        Assert.False(reloaded.IsPinned("s1"));

        reloaded.Prune(["s2"]);
        Assert.Equal(["s2"], reloaded.Data.PinnedSessionIds);
        Assert.Equal(["s2"], reloaded.Data.UnreadSessionIds);
    }

    [Fact]
    public void CoordinatorFlagRoundTripsAndPrunes()
    {
        var store = NewStore();
        store.SetCoordinator("s1", true);
        Assert.True(NewStore().IsCoordinator("s1"));
        store.SetCoordinator("s1", false);
        Assert.False(NewStore().IsCoordinator("s1"));

        store.SetCoordinator("gone", true);
        store.Prune(["s1"]);
        Assert.Empty(store.Data.CoordinatorSessionIds);
    }

    [Fact]
    public void RedundantMembershipWritesDoNotDirtyTheFile()
    {
        var store = NewStore();
        store.SetPinned("s1", true);
        var saves = 0;
        store.Changed += (_, _) => saves++;
        store.SetPinned("s1", true);
        store.SetUnread("s1", false);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{nope");
        var store = NewStore();
        Assert.Empty(store.Data.Groups);
        Assert.True(store.Data.ArchivedCollapsed);
    }

    [Fact]
    public void RenameIgnoresBlankNames()
    {
        var store = NewStore();
        var group = store.CreateGroup("old");
        store.RenameGroup(group.Id, "   ");
        Assert.Equal("old", group.Name);
        store.RenameGroup(group.Id, "new name");
        Assert.Equal("new name", group.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
