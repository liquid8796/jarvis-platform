using System.IO;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Tools.BuiltIn;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ToolInventoryStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-inventory-" + Guid.NewGuid().ToString("N"));

    private sealed class Questions : IUserQuestions
    {
        public Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken) =>
            Task.FromResult<UserQuestionAnswers?>(new(new Dictionary<string, string>()));
    }

    private sealed class Artifacts : IArtifactSink
    {
        public Task ShowAsync(WidgetArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void Inventory_replaces_all_legacy_mutating_surfaces_with_Codex_compatible_tools()
    {
        Directory.CreateDirectory(_root);
        using var inventory = new ToolInventory(new Questions(), new Artifacts(), settingsRoot: _root);
        using var processes = new ProcessToolSet();
        var byId = inventory.Tools.Concat(processes.Tools).ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);

        Assert.Equal("apply_patch", byId["source.apply_patch"].Descriptor.Name);
        Assert.Equal("view_image", byId["image.view_image"].Descriptor.Name);
        Assert.Equal("exec_command", byId["unified_exec.exec_command"].Descriptor.Name);
        Assert.Equal("write_stdin", byId["unified_exec.write_stdin"].Descriptor.Name);
        Assert.Equal("computer_use", byId["computer_use.computer_use"].Descriptor.Name);

        foreach (var oldId in new[]
        {
            "filesystem.Write", "filesystem.Edit", "filesystem.NotebookEdit",
            "shell.PowerShell", "shell.Bash",
            "process.start", "process.spawn", "process.read", "process.write_stdin", "process.resize_pty", "process.cancel",
            "computer.get_state", "computer.screenshot", "computer.computer_batch", "computer.open_application",
            "computer.request_access", "computer.request_teach_access", "computer.teach_step", "computer.teach_batch",
            "computer.list_granted_applications", "computer.switch_display", "computer.read_clipboard", "computer.write_clipboard"
        })
            Assert.DoesNotContain(oldId, byId.Keys);
    }

    [Fact]
    public async Task Computer_use_lists_native_windows_through_the_unified_tool()
    {
        Directory.CreateDirectory(_root);
        using var inventory = new ToolInventory(new Questions(), new Artifacts(), settingsRoot: _root);
        var tool = inventory.Tools.Single(item => item.Descriptor.Id == "computer_use.computer_use");
        var reply = await tool.ExecuteAsync(WireJson.Element(new { action = "list_windows" }),
            new AgentExecutionContext(_root, "call", "session"), CancellationToken.None);
        Assert.False(reply.IsError, reply.Text);
        using var json = System.Text.Json.JsonDocument.Parse(reply.Text);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, json.RootElement.GetProperty("windows").ValueKind);
    }

    [Fact]
    public void Computer_use_schema_covers_all_current_Codex_actions()
    {
        Directory.CreateDirectory(_root);
        using var inventory = new ToolInventory(new Questions(), new Artifacts(), settingsRoot: _root);
        var schema = inventory.Tools.Single(tool => tool.Descriptor.Id == "computer_use.computer_use").Descriptor.InputSchema;
        var compiled = SchemaGuard.Compile(schema);
        Assert.True(SchemaGuard.Matches(compiled, WireJson.Element(new { action = "list_windows" })));
        Assert.True(SchemaGuard.Matches(compiled, WireJson.Element(new
        {
            action = "click",
            window = new { app = "notepad", id = 123L },
            coordinate = new[] { 10, 20 }
        })));
        Assert.False(SchemaGuard.Matches(compiled, WireJson.Element(new { action = "legacy_computer_batch" })));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
