using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class PluginProjectionTests
{
    private sealed class Approval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class Tool(string id, string category = "plugin") : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), category, "synthetic", WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(new ToolReply("ok"));
    }

    [Fact]
    public async Task Applying_plugin_catalog_adds_and_replaces_only_plugin_projection()
    {
        var core = new Tool("test.core", "test");
        var first = new Tool("plugin.first");
        var second = new Tool("plugin.second");
        await using var connection = new AgentConnection([core], new Approval(), new LocalControlGate());

        connection.ApplyPluginCatalog(new PluginCatalogSnapshot([], new Dictionary<string, IAgentTool> { [first.Descriptor.Id] = first }, new Dictionary<string, IReadOnlyList<string>>()));
        Assert.Contains(connection.Descriptors, d => d.Id == "plugin.first");
        Assert.Contains(connection.Descriptors, d => d.Id == "tool_program.run");

        connection.ApplyPluginCatalog(new PluginCatalogSnapshot([], new Dictionary<string, IAgentTool> { [second.Descriptor.Id] = second }, new Dictionary<string, IReadOnlyList<string>>()));
        Assert.DoesNotContain(connection.Descriptors, d => d.Id == "plugin.first");
        Assert.Contains(connection.Descriptors, d => d.Id == "plugin.second");
        Assert.Contains(connection.Descriptors, d => d.Id == "test.core");
        Assert.Contains(connection.Descriptors, d => d.Id == "tool_program.run");
    }
}
