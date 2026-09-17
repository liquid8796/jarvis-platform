using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace Jarvis.Agent.Windows.Tests;

public sealed class SessionIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-session-adapter-" + Guid.NewGuid().ToString("N"));
    public SessionIsolationTests() => Directory.CreateDirectory(_root);
    private AgentExecutionContext Empty() => new("", "call", "session-a");
    private LegacyToolAdapter Adapter(ITool tool, string category = "filesystem") =>
        new(tool, category, new Questions(), new Artifacts(), Path.Combine(_root, "private"));

    [Fact]
    public async Task Absolute_file_read_does_not_require_a_default_workspace()
    {
        var file = Path.Combine(_root, "sample.txt");
        File.WriteAllText(file, "absolute-without-default");
        var result = await Adapter(new ReadFileTool()).ExecuteAsync(WireJson.Element(new { file_path = file }), Empty(), default);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("absolute-without-default", result.Text);
    }

    [Fact]
    public async Task Explicit_absolute_call_directory_works_with_empty_session_workspace()
    {
        File.WriteAllText(Path.Combine(_root, "sample.txt"), "explicit-call-directory");
        var result = await Adapter(new ReadFileTool()).ExecuteAsync(
            WireJson.Element(new { file_path = "sample.txt", workingDirectory = _root }), Empty(), default);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("explicit-call-directory", result.Text);
    }

    [Fact]
    public async Task Relative_file_without_workspace_returns_actionable_error()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(new ReadFileTool()).ExecuteAsync(
            WireJson.Element(new { file_path = "sample.txt" }), Empty(), default));
        Assert.Contains("WORKSPACE_REQUIRED", error.Message);
    }

    [Fact]
    public async Task Shell_without_workspace_never_inherits_agent_executable_directory()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter(new ShellTool(), "shell").ExecuteAsync(
            WireJson.Element(new { command = "Get-Location", timeout = 1000 }), Empty(), default));
        Assert.Contains("WORKSPACE_REQUIRED", error.Message);
    }

    [Fact]
    public async Task Another_sessions_input_invalidates_previous_observation()
    {
        var states = new ComputerStateTracker();
        var observer = new Observer();
        var a = states.Capture("a", observer.Capture());
        var b = states.Capture("b", observer.Capture());
        var inner = new InputTool();
        var adapter = new StatefulComputerToolAdapter(inner, states, observer);
        await adapter.ExecuteAsync(WireJson.Element(new { stateId = b.StateId, actions = Array.Empty<object>() }),
            new("", "b-input", "b"), default);
        Assert.Equal(1, inner.Calls);
        var error = Assert.Throws<InvalidOperationException>(() => states.Validate("a", a.StateId));
        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Real_file_adapters_refuse_overwriting_a_file_changed_by_another_chat()
    {
        var observations = new Jarvis.Agent.Core.Execution.SessionFileObservations();
        LegacyToolAdapter Wrap(ITool tool) => new(tool, "filesystem", new Questions(), new Artifacts(), Path.Combine(_root,"private"), observations);
        var file=Path.Combine(_root,"shared.txt"); File.WriteAllText(file,"old content");
        var a=new AgentExecutionContext(_root,"read",AgentSessionRules.NewSessionId()) { OwnerId="owner",AgentDeviceId="device" };
        var b=a with {SessionId=AgentSessionRules.NewSessionId()};
        var read=Wrap(new ReadFileTool()); var write=Wrap(new WriteFileTool());
        var readArgs=WireJson.Element(new { file_path=file });
        await read.ExecuteAsync(readArgs,a,default); await read.ExecuteAsync(readArgs,b,default);
        var written=await write.ExecuteAsync(WireJson.Element(new {file_path=file,content="B content"}),b,default);
        Assert.False(written.IsError,written.Text);
        var error=await Assert.ThrowsAsync<AgentRequestException>(()=>write.ExecuteAsync(WireJson.Element(new {file_path=file,content="A stale content"}),a,default));
        Assert.Equal("FILE_CHANGED",error.Code); Assert.Equal("B content",File.ReadAllText(file));
        await read.ExecuteAsync(readArgs,a,default);
        Assert.False((await write.ExecuteAsync(WireJson.Element(new {file_path=file,content="A reconciled"}),a,default)).IsError);
    }

    private sealed class Questions : IUserQuestions
    {
        public Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct) =>
            Task.FromResult<UserQuestionAnswers?>(null);
    }
    private sealed class Artifacts : IArtifactSink
    {
        public Task ShowAsync(WidgetArtifact artifact, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Observer : IComputerObservationProvider
    {
        public ComputerObservation Capture() => new("Synthetic", 1, "test", null, []);
    }
    private sealed class InputTool : IAgentTool
    {
        public int Calls;
        public ToolDescriptor Descriptor => new("computer.computer_batch", "computer__computer_batch", "computer", "Synthetic input",
            WireJson.Element(new { type = "object" }), false, true);
        public Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        { Calls++; return Task.FromResult(new ToolReply("done")); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
