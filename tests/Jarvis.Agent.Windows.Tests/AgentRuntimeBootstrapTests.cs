using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Tools.BuiltIn;

namespace Jarvis.Agent.Windows.Tests;

public sealed class AgentRuntimeBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-runtime-" + Guid.NewGuid().ToString("N"));

    private sealed class Approval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class Questions : IUserQuestions
    {
        public Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken) =>
            Task.FromResult<UserQuestionAnswers?>(new(new Dictionary<string, string>()));
    }
    private sealed class Artifacts : IArtifactSink
    {
        public Task ShowAsync(WidgetArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class PluginTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("plugin.echo", "plugin__echo", "plugin", "test plugin",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("ok"));
    }

    [Fact]
    public async Task Runtime_bootstraps_plugin_catalog_and_default_adaptive_coordinator()
    {
        var pluginRoot = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(pluginRoot);
        var entry = Path.Combine(pluginRoot, "plugin.bin");
        await File.WriteAllTextAsync(entry, "pinned plugin metadata");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(entry))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "echo.plugin.json"), JsonSerializer.Serialize(new
        {
            id = "test.echo",
            version = "1.0.0",
            minAgentVersion = "1.0.0",
            entryFile = "plugin.bin",
            sha256 = hash,
            tools = new[] { new { id = "plugin.echo" } },
            permissions = Array.Empty<string>(),
            skills = Array.Empty<string>(),
            hooks = Array.Empty<string>()
        }, WireJson.Options));

        await using var runtime = new AgentRuntime(new Approval(), new Questions(), new Artifacts(), settingsRoot: _root,
            pluginDirectory: pluginRoot, pluginTools: [new PluginTool()]);

        Assert.Contains(runtime.Connection.Descriptors, descriptor => descriptor.Id == "plugin.echo");
        Assert.True(runtime.Connection.AdaptiveCoordinatorAvailable);
        Assert.Single(runtime.PluginCatalog.Manifests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
