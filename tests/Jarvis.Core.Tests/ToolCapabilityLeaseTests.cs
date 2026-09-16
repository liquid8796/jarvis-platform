using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ToolCapabilityLeaseTests
{
    private static ToolDescriptor ProcessStart => new ProcessToolSet().Tools.Single(x => x.Descriptor.Id == "process.start").Descriptor;

    [Fact]
    public void Legacy_process_full_permission_requires_scoped_lease_at_invocation()
    {
        var policy = new ToolPermissionPolicy(["process.start"]);
        var context = new AgentExecutionContext(Path.GetTempPath(), "call-1", "session-1") { TurnId = "turn-1" };
        var arguments = WireJson.Element(new { command = "dotnet test" });

        Assert.True(policy.HasFullPermission("process.start"));
        Assert.False(policy.HasFullPermission("process.start", arguments, context));
        Assert.True(policy.RequiresApproval(ProcessStart, arguments, context));
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
                "lease-1", "process.start", ToolCapabilityScope.Turn, "session-1", "turn-1",
                DateTimeOffset.UtcNow.AddMinutes(1), [root], ["dotnet test"], false));
            var context = new AgentExecutionContext(root, "call-1", "session-1") { TurnId = "turn-1" };

            Assert.True(policy.HasFullPermission("process.start", WireJson.Element(new { command = "dotnet test --no-restore" }), context));
            Assert.False(policy.HasFullPermission("process.start", WireJson.Element(new { command = "git status" }), context));
            Assert.False(policy.HasFullPermission("process.start", WireJson.Element(new { command = "dotnet test", workingDirectory = ".." }), context));
            Assert.False(policy.HasFullPermission("process.start", WireJson.Element(new { command = "dotnet test" }), context with { TurnId = "turn-2" }));
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
            "lease-expiring", "process.start", ToolCapabilityScope.Session, "session-1", null,
            DateTimeOffset.UtcNow.AddMilliseconds(100), [Path.GetTempPath()], ["dotnet"], false));

        await revoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var context = new AgentExecutionContext(Path.GetTempPath(), "call-1", "session-1");
        Assert.False(policy.HasFullPermission("process.start", WireJson.Element(new { command = "dotnet --info" }), context));
    }
}
