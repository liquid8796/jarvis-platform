using System.IO;
using System.Text.Json;
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
    public void Inventory_exposes_get_state_and_requires_state_on_computer_batch()
    {
        Directory.CreateDirectory(_root);
        using var inventory = new ToolInventory(new Questions(), new Artifacts(), settingsRoot: _root);
        var byId = inventory.Tools.ToDictionary(t => t.Descriptor.Id, StringComparer.Ordinal);

        Assert.Contains("computer.get_state", byId.Keys);
        Assert.Contains("computer.screenshot", byId.Keys);
        Assert.Contains("computer.computer_batch", byId.Keys);
        var batch = byId["computer.computer_batch"].Descriptor;
        var validState = new string('s', 24);
        var action = new[] { new { action = "wait", duration = 0.0 } };
        Assert.True(SchemaGuard.Matches(SchemaGuard.Compile(batch.InputSchema), WireJson.Element(new { stateId = validState, actions = action })));
        Assert.False(SchemaGuard.Matches(SchemaGuard.Compile(batch.InputSchema), WireJson.Element(new { actions = action })));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
