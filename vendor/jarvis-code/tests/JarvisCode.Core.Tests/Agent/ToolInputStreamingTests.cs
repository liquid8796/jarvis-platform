using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Agent;

public sealed class ToolInputStreamingTests
{
    [Fact]
    public async Task ToolInputStreamsBeforeExecutionAndPreservesCallIdentityAndFinalArguments()
    {
        var tool = new ProbeTool();
        var provider = new ScriptedProvider(
        [new ToolCallStartedEvent(3, "call-1", tool.Name), new ToolCallArgumentsDeltaEvent(3, "{\"text\":\"par"),
            new ToolCallArgumentsDeltaEvent(3, "tial\"}"), new ResponseCompletedEvent(true, new Usage(1, 1), StopReasons.ToolUse)],
        [new TextDeltaEvent("done"), new ResponseCompletedEvent(false, new Usage(1, 1), StopReasons.EndTurn)]);
        var context = new AgentTurnContext
        {
            Provider = provider, ModelId = "scripted", SystemPrompt = "test",
            Messages = new List<ChatMessage> { ChatMessage.FromUserText("go") },
            Tools = new ToolRegistry([tool]), PermissionGate = new AutoApprovePermissionGate(),
            ToolContext = new ToolExecutionContext { WorkingDirectory = Path.GetTempPath() },
        };
        var deltas = new List<ToolInputDelta>();
        await foreach (var entry in new AgentOrchestrator().RunTurnAsync(context, default))
        {
            if (entry is ToolInputStarted or ToolInputDelta) Assert.Null(tool.ExecutedText);
            if (entry is ToolInputDelta delta) deltas.Add(delta);
        }
        Assert.Equal(2, deltas.Count);
        Assert.All(deltas, delta => { Assert.Equal("call-1", delta.CallId); Assert.Equal(tool.Name, delta.ToolName); });
        Assert.Equal("{\"text\":\"partial\"}", string.Concat(deltas.Select(delta => delta.Delta)));
        Assert.Equal("partial", tool.ExecutedText);
    }

    private sealed class ProbeTool : ITool
    {
        public string? ExecutedText { get; private set; }
        public string Name => "probe";
        public string Description => "probe";
        public JsonObject InputSchema => new() { ["type"] = "object" };
        public bool IsReadOnly => true;
        public string DescribeCall(JsonObject arguments) => Name;
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken token)
        {
            ExecutedText = arguments["text"]?.GetValue<string>();
            return Task.FromResult(ToolResult.Success("ok"));
        }
    }
}
