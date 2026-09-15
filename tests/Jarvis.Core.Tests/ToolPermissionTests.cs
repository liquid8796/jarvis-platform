using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Core.Tests;

public sealed class ToolPermissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-permissions-" + Guid.NewGuid());
    private static ToolDescriptor Tool(string id = "shell.PowerShell", bool readOnly = false, bool sensitive = false) =>
        new(id, id.Replace(".", "__"), "shell", "Test", WireJson.Element(new { type = "object" }), readOnly, sensitive);
    public ToolPermissionTests() => Directory.CreateDirectory(_root);
    [Fact] public void Default_requires_approval_for_mutating_or_sensitive_tools_only()
    {
        var policy = new ToolPermissionPolicy();
        Assert.True(policy.RequiresApproval(Tool()));
        Assert.True(policy.RequiresApproval(Tool(readOnly: true, sensitive: true)));
        Assert.False(policy.RequiresApproval(Tool(readOnly: true)));
        Assert.Empty(policy.FullPermissionTools);
    }
    [Fact] public void Exact_id_not_alias_category_prefix_or_future_tool_is_preapproved()
    {
        var policy = new ToolPermissionPolicy(["shell.PowerShell"]);
        Assert.False(policy.RequiresApproval(Tool()));
        foreach (var id in new[] { "shell", "shell__PowerShell", "shell.powershell", "shell.PowerShell2", "process.start" })
            Assert.False(policy.HasFullPermission(id));
    }
    [Theory] [InlineData("*")] [InlineData("shell.*")] [InlineData("")] [InlineData(" ")] [InlineData("shell. PowerShell")]
    public void Invalid_grant_fails_without_changing_active_policy(string id)
    {
        var policy = new ToolPermissionPolicy(["shell.PowerShell"]);
        Assert.Throws<ArgumentException>(() => policy.Replace([id]));
        Assert.True(policy.HasFullPermission("shell.PowerShell"));
    }
    [Fact] public void Snapshot_does_not_retain_mutable_source_collection()
    {
        var source = new List<string> { "a.one", "a.two", "a.one" };
        var policy = new ToolPermissionPolicy(source); source.Clear();
        Assert.Equal(new[] { "a.one", "a.two" }, policy.FullPermissionTools);
    }
    [Fact] public void Only_removal_raises_revocation_and_clear_restores_approval()
    {
        var policy = new ToolPermissionPolicy(); var revoked = 0;
        policy.PermissionsRevoked += () => revoked++;
        policy.Replace(["shell.PowerShell"]); policy.Replace(["shell.PowerShell", "a.two"]);
        Assert.Equal(0, revoked);
        policy.Replace(["shell.PowerShell"]); policy.Replace([]);
        Assert.Equal(2, revoked); Assert.True(policy.RequiresApproval(Tool()));
    }
    [Fact] public void Missing_file_does_not_create_or_enable_any_grants()
    {
        var file = Path.Combine(_root, "missing.json");
        Assert.Empty(new ToolPermissionStore(file).Load()); Assert.False(File.Exists(file));
    }
    [Fact] public void Saved_permissions_round_trip_and_clear_without_credentials()
    {
        var file = Path.Combine(_root, "tool-permissions.json"); var store = new ToolPermissionStore(file);
        store.Save(["a.two", "a.one", "a.one"]);
        Assert.Equal(new[] { "a.one", "a.two" }, new ToolPermissionStore(file).Load());
        Assert.DoesNotContain("token", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        store.Save([]); Assert.Empty(store.Load()); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
    [Theory] [InlineData("not-json")] [InlineData("null")] [InlineData("{}")]
    [InlineData("{\"version\":999,\"fullPermissionTools\":[]}")]
    [InlineData("{\"version\":1,\"fullPermissionTools\":[\"*\"]}")]
    public void Damaged_or_unsupported_file_never_becomes_allow_all(string content)
    {
        var file = Path.Combine(_root, "bad.json"); File.WriteAllText(file, content);
        Assert.ThrowsAny<Exception>(() => new ToolPermissionStore(file).Load());
    }
    [Fact] public void Failed_save_leaves_existing_file_unchanged()
    {
        var file = Path.Combine(_root, "settings.json"); var store = new ToolPermissionStore(file);
        store.Save(["a.one"]); var before = File.ReadAllText(file);
        Assert.Throws<ArgumentException>(() => store.Save(["*"]));
        Assert.Equal(before, File.ReadAllText(file));
    }
    [Fact] public void Full_permissions_do_not_arm_a_new_process()
    {
        var policy = new ToolPermissionPolicy(["shell.PowerShell"]);
        Assert.True(policy.HasFullPermission("shell.PowerShell")); Assert.False(new LocalControlGate().IsArmed);
    }
    [Fact] public async Task Async_scope_is_isolated_restored_and_not_process_global()
    {
        Assert.False(ToolConsentScope.IsFullPermission);
        var tasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            using (ToolConsentScope.Enter(i % 2 == 0))
            {
                await Task.Delay(5);
                Assert.Equal(i % 2 == 0, ToolConsentScope.IsFullPermission);
                using (ToolConsentScope.Enter(false)) Assert.False(ToolConsentScope.IsFullPermission);
                Assert.Equal(i % 2 == 0, ToolConsentScope.IsFullPermission);
            }
            Assert.False(ToolConsentScope.IsFullPermission);
        }));
        await Task.WhenAll(tasks); Assert.False(ToolConsentScope.IsFullPermission);
    }
    [Fact] public async Task Detached_child_loses_permission_when_parent_scope_exits()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> child;
        using (ToolConsentScope.Enter(true))
        {
            child = Task.Run(async () => { ready.SetResult(); await release.Task; return ToolConsentScope.IsFullPermission; });
            await ready.Task;
        }
        release.SetResult(); Assert.False(await child);
    }
    [Fact] public void Cancellation_immediately_disables_effective_consent()
    {
        using var ct = new CancellationTokenSource();
        using var scope = ToolConsentScope.Enter(true, ct.Token);
        Assert.True(ToolConsentScope.IsFullPermission); ct.Cancel(); Assert.False(ToolConsentScope.IsFullPermission);
    }
    [Fact] public void Exception_does_not_leave_ambient_permission_enabled()
    {
        Assert.Throws<InvalidOperationException>((Action)(() => { using var scope = ToolConsentScope.Enter(true); throw new InvalidOperationException(); }));
        Assert.False(ToolConsentScope.IsFullPermission);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
