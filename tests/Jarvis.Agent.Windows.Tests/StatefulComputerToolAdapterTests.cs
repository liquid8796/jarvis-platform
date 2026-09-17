using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class StatefulComputerToolAdapterTests
{
    private sealed class Inner(string id, bool readOnly) : IAgentTool
    {
        public int Calls;
        public JsonElement? LastArguments;
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), "computer", "Synthetic computer tool",
            WireJson.Element(new { type = "object", properties = new { actions = new { type = "array" } }, additionalProperties = false }), readOnly, true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            LastArguments = arguments.Clone();
            return Task.FromResult(new ToolReply("inner"));
        }
    }

    private sealed class Observer : IComputerObservationProvider
    {
        public ComputerObservation Capture() => new("Window", 7, "test", null, []);
    }

    private static AgentExecutionContext Context(string session = "s") => new(Path.GetTempPath(), "c", session);

    [Fact]
    public async Task Computer_batch_requires_current_state_and_strips_state_before_inner_tool()
    {
        var tracker = new ComputerStateTracker();
        var inner = new Inner("computer.computer_batch", readOnly: false);
        var tool = new StatefulComputerToolAdapter(inner, tracker, new Observer());
        var state = tracker.Capture("s", new Observer().Capture());

        var reply = await tool.ExecuteAsync(WireJson.Element(new { stateId = state.StateId, actions = Array.Empty<object>() }), Context(), CancellationToken.None);

        Assert.False(reply.IsError);
        Assert.Equal(1, inner.Calls);
        Assert.False(inner.LastArguments!.Value.TryGetProperty("stateId", out _));
        Assert.Throws<InvalidOperationException>(() => tracker.Validate("s", state.StateId));
    }

    [Fact]
    public async Task Stale_state_is_rejected_before_inner_tool_runs()
    {
        var tracker = new ComputerStateTracker();
        var inner = new Inner("computer.computer_batch", readOnly: false);
        var tool = new StatefulComputerToolAdapter(inner, tracker, new Observer());
        var state = tracker.Capture("s", new Observer().Capture());
        tracker.Invalidate("s");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tool.ExecuteAsync(WireJson.Element(new { stateId = state.StateId, actions = Array.Empty<object>() }), Context(), CancellationToken.None));

        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Missing_state_is_rejected_for_batch()
    {
        var tracker = new ComputerStateTracker();
        var inner = new Inner("computer.computer_batch", readOnly: false);
        var tool = new StatefulComputerToolAdapter(inner, tracker, new Observer());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tool.ExecuteAsync(WireJson.Element(new { actions = Array.Empty<object>() }), Context(), CancellationToken.None));

        Assert.Contains("stateId", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, inner.Calls);
    }

    [Theory]
    [InlineData("computer.open_application")]
    [InlineData("computer.switch_display")]
    [InlineData("computer.teach_step")]
    public async Task Other_desktop_changes_invalidate_all_sessions(string id)
    {
        var tracker = new ComputerStateTracker();
        var state = tracker.Capture("A", new Observer().Capture());
        var adapter = new StatefulComputerToolAdapter(new Inner(id, false), tracker, new Observer());
        await adapter.ExecuteAsync(WireJson.Element(new { }), Context("B"), default);
        Assert.Throws<InvalidOperationException>(() => tracker.Validate("A", state.StateId));
    }

    [Fact]
    public async Task Screenshot_emits_fresh_state_metadata()
    {
        var tracker = new ComputerStateTracker();
        var inner = new Inner("computer.screenshot", readOnly: true);
        var tool = new StatefulComputerToolAdapter(inner, tracker, new Observer());

        var reply = await tool.ExecuteAsync(WireJson.Element(new { }), Context(), CancellationToken.None);

        Assert.False(reply.IsError);
        Assert.Contains("stateId", reply.Text, StringComparison.Ordinal);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, tracker.StoredStateCount);
    }
}
