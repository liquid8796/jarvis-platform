using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class DynamicToolRegistryTests
{
    private sealed class Tool(string id, JsonElement schema, string output) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), "test", "Synthetic dynamic tool", schema, true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply(output));
    }

    private static JsonElement Schema(params string[] required) => WireJson.Element(new
    {
        type = "object",
        properties = required.ToDictionary(x => x, _ => new { type = "string" }),
        required,
        additionalProperties = false
    });

    [Fact]
    public void Snapshot_is_stable_until_catalog_changes()
    {
        var tool = new Tool("test.echo", Schema("value"), "one");
        var registry = new DynamicToolRegistry([tool]);

        var first = registry.Snapshot;
        registry.Replace([tool]);
        var same = registry.Snapshot;

        Assert.Equal(1, first.Generation);
        Assert.Equal(first.Generation, same.Generation);
        Assert.Equal(first.Digest, same.Digest);
        Assert.Same(tool, same.Tools["test.echo"]);
    }

    [Fact]
    public void Replace_increments_generation_and_recompiles_schema()
    {
        var firstTool = new Tool("test.echo", Schema("value"), "one");
        var secondTool = new Tool("test.echo", Schema("message"), "two");
        var registry = new DynamicToolRegistry([firstTool]);

        registry.Replace([secondTool]);
        var snapshot = registry.Snapshot;

        Assert.Equal(2, snapshot.Generation);
        Assert.NotEqual(DynamicToolRegistry.CreateDigest([firstTool.Descriptor]), snapshot.Digest);
        Assert.Same(secondTool, snapshot.Tools["test.echo"]);
        Assert.False(SchemaGuard.Matches(snapshot.Schemas["test.echo"], WireJson.Element(new { value = "old" })));
        Assert.True(SchemaGuard.Matches(snapshot.Schemas["test.echo"], WireJson.Element(new { message = "new" })));
    }

    [Fact]
    public void Duplicate_tool_ids_are_rejected()
    {
        var one = new Tool("test.echo", Schema(), "one");
        var two = new Tool("test.echo", Schema(), "two");
        var ex = Assert.Throws<ArgumentException>(() => new DynamicToolRegistry([one, two]));
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Digest_is_canonical_across_input_order()
    {
        var a = new Tool("test.a", Schema("a"), "a");
        var b = new Tool("test.b", Schema("b"), "b");
        Assert.Equal(DynamicToolRegistry.CreateDigest([a.Descriptor, b.Descriptor]),
            DynamicToolRegistry.CreateDigest([b.Descriptor, a.Descriptor]));
    }
}
