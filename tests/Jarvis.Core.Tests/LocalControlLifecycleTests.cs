using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Core.Tests;

public sealed class LocalControlLifecycleTests
{
    [Fact]
    public async Task Pause_and_manual_disconnect_revoke_the_unlimited_grant()
    {
        var gate = new LocalControlGate();
        await using var connection = new AgentConnection([], new DenyApproval(), gate);
        gate.Arm(); connection.Pause(); Assert.False(gate.IsArmed);
        gate.Arm(); connection.Disconnect(); Assert.False(gate.IsArmed);
    }

    [Fact]
    public async Task Disposing_the_connection_revokes_the_unlimited_grant()
    {
        var gate = new LocalControlGate();
        var connection = new AgentConnection([], new DenyApproval(), gate);
        gate.Arm(); await connection.DisposeAsync(); Assert.False(gate.IsArmed);
    }

    private sealed class DenyApproval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
