using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ToolProgramConnectionTests
{
    private sealed class DenyApproval : IApprovalService
    {
        public int Calls;
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(false); }
    }

    private sealed class MutatingTool : IAgentTool
    {
        public int Calls;
        public ToolDescriptor Descriptor { get; } = new("test.mutate", "test_mutate", "test", "synthetic mutating tool",
            WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), false, true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new ToolReply("mutated")); }
    }

    [Fact]
    public async Task Connection_registers_program_tool_and_nested_calls_reenter_guarded_policy()
    {
        var inner = new MutatingTool();
        var approval = new DenyApproval();
        var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection([inner], approval, gate);

        Assert.Contains(connection.Descriptors, descriptor => descriptor.Id == "tool_program.run");
        var program = connection.ToolRegistry.Snapshot.Tools["tool_program.run"];
        var context = new AgentExecutionContext(Path.GetTempPath(), "program", "session");
        var payload = WireJson.Element(new { instructions = new object[] { new { op = "call", tool = "test.mutate", args = new { } } } });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => program.ExecuteAsync(payload, context, CancellationToken.None));
        Assert.Equal(1, approval.Calls);
        Assert.Equal(0, inner.Calls);
    }
}
