using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ToolCapabilityLeaseTests
{
    private static ToolDescriptor ExecCommand => new ProcessToolSet().Tools
        .Single(tool => tool.Descriptor.Id == "unified_exec.exec_command").Descriptor;

    [Fact]
    public void Exec_command_full_permission_still_requires_scoped_or_permanent_approval_at_invocation()
    {
        var policy = new ToolPermissionPolicy(["unified_exec.exec_command"]);
        var context = new AgentExecutionContext(Path.GetTempPath(), "call-1", "session-1") { TurnId = "turn-1" };
        var arguments = WireJson.Element(new { cmd = "dotnet test" });

        Assert.True(policy.HasFullPermission("unified_exec.exec_command"));
        Assert.False(policy.HasFullPermission("unified_exec.exec_command", arguments, context));
        Assert.True(policy.RequiresApproval(ExecCommand, arguments, context));
    }

    [Fact]
    public void Matching_turn_lease_authorizes_only_matching_command_workspace_and_turn()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new ToolPermissionPolicy();
            policy.GrantLease(new ToolCapabilityLease(
                "lease-1", "unified_exec.exec_command", ToolCapabilityScope.Turn, "session-1", "turn-1",
                DateTimeOffset.UtcNow.AddMinutes(1), [root], ["dotnet test"], false));
            var context = new AgentExecutionContext(root, "call-1", "session-1") { TurnId = "turn-1" };

            Assert.True(policy.HasFullPermission("unified_exec.exec_command",
                WireJson.Element(new { cmd = "dotnet test --no-restore" }), context));
            Assert.False(policy.HasFullPermission("unified_exec.exec_command",
                WireJson.Element(new { cmd = "git status" }), context));
            Assert.False(policy.HasFullPermission("unified_exec.exec_command",
                WireJson.Element(new { cmd = "dotnet test", workdir = ".." }), context));
            Assert.False(policy.HasFullPermission("unified_exec.exec_command",
                WireJson.Element(new { cmd = "dotnet test" }), context with { TurnId = "turn-2" }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Session_lease_matches_exec_command_prefix_without_authorizing_other_commands()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-exec-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = new ToolPermissionPolicy();
            policy.GrantLease(new ToolCapabilityLease(
                "exec-lease", "unified_exec.exec_command", ToolCapabilityScope.Session, "session-1", null,
                DateTimeOffset.UtcNow.AddMinutes(1), [root], ["dotnet test"], false));
            var context = new AgentExecutionContext(root, "call-1", "session-1");

            Assert.True(policy.HasFullPermission("unified_exec.exec_command",
                WireJson.Element(new { cmd = "dotnet test --no-restore" }), context));
            Assert.False(policy.HasFullPermission("unified_exec.exec_command",
                WireJson.Element(new { cmd = "dotnet build" }), context));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Lease_expiry_revokes_authority_and_raises_cancellation_signal()
    {
        var policy = new ToolPermissionPolicy();
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        policy.PermissionsRevoked += () => revoked.TrySetResult();
        policy.GrantLease(new ToolCapabilityLease(
            "lease-expiring", "unified_exec.exec_command", ToolCapabilityScope.Session, "session-1", null,
            DateTimeOffset.UtcNow.AddMilliseconds(100), [Path.GetTempPath()], ["dotnet"], false));

        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var context = new AgentExecutionContext(Path.GetTempPath(), "call-1", "session-1");
        Assert.False(policy.HasFullPermission("unified_exec.exec_command",
            WireJson.Element(new { cmd = "dotnet --info" }), context));
    }
}
