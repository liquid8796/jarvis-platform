using System.IO;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ComputerStateToolTests
{
    private sealed class Observer(ComputerObservation observation) : IComputerObservationProvider
    {
        public ComputerObservation Capture() => observation;
    }

    [Fact]
    public async Task Get_state_returns_state_id_generation_and_bounded_observation()
    {
        var nodes = Enumerable.Range(0, 250)
            .Select(i => new AccessibilityNode("Button", new string('n', 200) + i, "id-" + i))
            .ToArray();
        var observation = new ComputerObservation("Editor", 123, "editor", new("Edit", "Prompt", "input"), nodes);
        var tracker = new ComputerStateTracker();
        var tool = new ComputerStateTool(tracker, new Observer(observation));
        var context = new AgentExecutionContext(Path.GetTempPath(), "call", "session-a");

        var reply = await tool.ExecuteAsync(WireJson.Element(new { }), context, CancellationToken.None);

        Assert.False(reply.IsError);
        Assert.True(reply.Text.Length <= ComputerStateTool.MaxOutputChars);
        Assert.Contains("stateId", reply.Text, StringComparison.Ordinal);
        Assert.Contains("generation", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Editor", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Prompt", reply.Text, StringComparison.Ordinal);
        Assert.True(tracker.StoredStateCount == 1);
    }

    [Fact]
    public void Descriptor_is_read_only_but_sensitive_and_accepts_no_arguments()
    {
        var tool = new ComputerStateTool(new ComputerStateTracker(), new Observer(new(null, null, null, null, [])));

        Assert.Equal("computer.get_state", tool.Descriptor.Id);
        Assert.True(tool.Descriptor.ReadOnly);
        Assert.True(tool.Descriptor.Sensitive);
        Assert.True(SchemaGuard.Matches(SchemaGuard.Compile(tool.Descriptor.InputSchema), WireJson.Element(new { })));
        Assert.False(SchemaGuard.Matches(SchemaGuard.Compile(tool.Descriptor.InputSchema), WireJson.Element(new { unexpected = true })));
    }
}
