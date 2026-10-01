using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Sessions;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class AgentSessionStoreTests
{
    [Fact]
    public void Same_account_sessions_keep_independent_workspace_snapshots()
    {
        using var fixture = new StoreFixture();
        dynamic store = fixture.Open();
        var a = Identity(); var b = Identity();
        store.Open(a, "Chat A", new WorkspaceDirectories(""), null);
        store.Open(b, "Chat B", new WorkspaceDirectories(""), null);
        AgentSessionSnapshot accepted = store.Get(a);
        AgentSessionSnapshot changed = store.SetWorkspace(a, fixture.Root, Array.Empty<string>(), 0L);
        Assert.Equal(1, changed.WorkspaceRevision);
        Assert.Equal(fixture.Root, changed.Workspace);
        Assert.Empty(accepted.Workspace);
        Assert.Empty(((AgentSessionSnapshot)store.Get(b)).Workspace);
        var conflict = Assert.Throws<AgentRequestException>(() => store.SetWorkspace(a, null, Array.Empty<string>(), 0L));
        Assert.Equal("WORKSPACE_REVISION_CONFLICT", conflict.Code);
    }

    [Fact]
    public void Owner_and_agent_bind_every_lookup_and_mailbox_delivery()
    {
        using var fixture = new StoreFixture();
        dynamic store = fixture.Open();
        var a = Identity(); var b = Identity();
        store.Open(a, "A", new WorkspaceDirectories(""), null);
        store.Open(b, "B", new WorkspaceDirectories(""), null);
        Assert.Throws<AgentRequestException>(() => store.Get(a with { OwnerId = "other-owner" }));
        Assert.Throws<AgentRequestException>(() => store.Get(a with { DeviceId = "other-agent" }));
        AgentSessionEvent sent = store.SendMessage(a, b.SessionId, "Build finished; this is coordination data, not user approval.");
        AgentSessionEventPage page = store.ReadEvents(b, 0L, 50);
        Assert.Contains(page.Events, item => item.Cursor == sent.Cursor && item.SenderSessionId == a.SessionId && item.Source == "agent-coordination-data");
        AgentSessionEventPage next = store.ReadEvents(b, page.NextCursor, 50);
        Assert.Empty(next.Events);
        Assert.Throws<AgentRequestException>(() => store.SendMessage(a, AgentSessionRules.NewSessionId(), "Unknown destination"));
    }

    [Fact]
    public void Workspace_and_mailbox_survive_reopen_but_closed_sessions_do_not_resume()
    {
        using var fixture = new StoreFixture();
        var a = Identity(); var b = Identity();
        dynamic store = fixture.Open();
        store.Open(a, "A", new WorkspaceDirectories(fixture.Root), null);
        store.Open(b, "B", new WorkspaceDirectories(""), null);
        store.SendMessage(a, b.SessionId, "Persistent coordination");
        fixture.Close();
        store = fixture.Open();
        Assert.Equal(fixture.Root, ((AgentSessionSnapshot)store.Get(a)).Workspace);
        Assert.Contains(((AgentSessionEventPage)store.ReadEvents(b, 0L, 50)).Events, e => e.Text == "Persistent coordination");
        store.Close(a);
        var closed = Assert.Throws<AgentRequestException>(() => store.Get(a));
        Assert.Equal("SESSION_CLOSED", closed.Code);
        Assert.Throws<AgentRequestException>(() => store.Open(a, "Do not resurrect", new WorkspaceDirectories(""), null));
        Assert.Null(((AgentSessionSnapshot)store.Get(b)).ClosedAt);
    }

    [Fact]
    public void Permanent_delete_removes_metadata_and_events_and_retains_a_non_resurrectable_tombstone()
    {
        using var fixture = new StoreFixture();
        dynamic store = fixture.Open();
        var source = Identity(); var deleted = Identity();
        store.Open(source, "Source", new WorkspaceDirectories(""), null);
        store.Open(deleted, "Delete me", new WorkspaceDirectories(fixture.Root), null);
        store.SendMessage(source, deleted.SessionId, "This mailbox row must be removed with the session.");

        AgentSessionDeletion tombstone = store.Delete(deleted);
        Assert.Equal(deleted.SessionId, tombstone.SessionId);
        Assert.True(tombstone.DeletedAtUnixMilliseconds > 0);
        Assert.DoesNotContain((IReadOnlyList<AgentSessionSnapshot>)store.List(
            deleted.OwnerId, deleted.DeviceId, 0, 100, true), item => item.SessionId == deleted.SessionId);
        Assert.DoesNotContain((IReadOnlyList<LocalSessionEntry>)store.ListLocal(deleted.DeviceId),
            item => item.Identity.SessionId == deleted.SessionId);
        Assert.Equal(tombstone, (AgentSessionDeletion)store.Delete(deleted));
        Assert.Equal(tombstone, Assert.Single((IReadOnlyList<AgentSessionDeletion>)store.ListDeletions(
            deleted.OwnerId, deleted.DeviceId, 0, 200)));

        var get = Assert.Throws<AgentRequestException>(() => store.Get(deleted));
        Assert.Equal("SESSION_DELETED", get.Code);
        var reopen = Assert.Throws<AgentRequestException>(() => store.Open(
            deleted, "Do not resurrect", new WorkspaceDirectories(""), null));
        Assert.Equal("SESSION_DELETED", reopen.Code);
        var events = Assert.Throws<AgentRequestException>(() => store.ReadEvents(deleted, 0L, 50));
        Assert.Equal("SESSION_DELETED", events.Code);

        fixture.Close();
        store = fixture.Open();
        var afterRestart = Assert.Throws<AgentRequestException>(() => store.Get(deleted));
        Assert.Equal("SESSION_DELETED", afterRestart.Code);
        Assert.Equal(tombstone, Assert.Single((IReadOnlyList<AgentSessionDeletion>)store.ListDeletions(
            deleted.OwnerId, deleted.DeviceId, 0, 200)));
    }

    private static AgentSessionIdentity Identity() => new("owner", "agent", AgentSessionRules.NewSessionId());

    // Reflection makes the initial RED phase a real assertion failure, not a compilation error.
    // Every assertion above calls the production store and real SQLite, without a mock implementation.
    private sealed class StoreFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "jarvis-sessions-" + Guid.NewGuid().ToString("N"));
        private IDisposable? _store;
        public StoreFixture() => Directory.CreateDirectory(Root);
        public dynamic Open()
        {
            var type = typeof(AgentConnection).Assembly.GetType("Jarvis.Agent.Core.Sessions.AgentSessionStore");
            Assert.NotNull(type);
            _store = (IDisposable)Activator.CreateInstance(type, Path.Combine(Root, "sessions.db"))!;
            return _store;
        }
        public void Close() { _store?.Dispose(); _store = null; }
        public void Dispose() { Close(); Directory.Delete(Root, true); }
    }
}
