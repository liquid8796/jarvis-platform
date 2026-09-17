using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class PerSessionToolSetTests
{
    [Fact]
    public async Task Stateful_tool_instances_are_private_to_each_session_and_reset_on_close()
    {
        var tools = new PerSessionToolSet(() => [new Counter()]);
        var a = new AgentExecutionContext("", "call", AgentSessionRules.NewSessionId()) { OwnerId = "owner", AgentDeviceId = "device" };
        var b = a with { SessionId = AgentSessionRules.NewSessionId() };
        var tool = Assert.Single(tools.Tools);
        async Task<string> Invoke(AgentExecutionContext c) => (await tool.ExecuteAsync(WireJson.Element(new {}), c, default)).Text;
        Assert.Equal("1", await Invoke(a)); Assert.Equal("2", await Invoke(a));
        Assert.Equal("1", await Invoke(b));
        tools.Forget(a.RequireSessionIdentity());
        Assert.Equal("1", await Invoke(a)); Assert.Equal("2", await Invoke(b));
    }
    private sealed class Counter : IAgentTool
    {
        private int _calls;
        public ToolDescriptor Descriptor { get; } = new("computer.fake", "computer__fake", "computer", "test", WireJson.Element(new {type="object"}), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken ct) => Task.FromResult(new ToolReply((++_calls).ToString()));
    }
}
