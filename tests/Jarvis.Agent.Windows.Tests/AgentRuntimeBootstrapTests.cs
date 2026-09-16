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
    private sealed class StopHook : Jarvis.Agent.Core.Plugins.IPluginLifecycleHook
    {
        public string PluginId => "test.echo";
        public string HookName => "stop";
        public int Calls { get; private set; }
        public Task InvokeAsync(AgentLifecycleEvent evt, CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
    }
    private sealed class PluginTool(string id = "plugin.echo") : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), "plugin", "test plugin",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("ok"));
    }

    [Fact]
    public async Task Runtime_loads_persistent_constrained_process_approvals_from_disk()
    {
        Directory.CreateDirectory(_root);
        new ToolPermissionStore(Path.Combine(_root, "tool-permissions.json")).Save(
            new ToolPermissionSettings(["process.start"], ["process.start"]));

        await using var runtime = new AgentRuntime(new Approval(), new Questions(), new Artifacts(), settingsRoot: _root,
            pluginDirectory: Path.Combine(_root, "plugins"));
        var field = typeof(AgentRuntime).GetField("_permissions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var policy = Assert.IsType<ToolPermissionPolicy>(field.GetValue(runtime));
        Assert.True(policy.HasFullPermission("process.start"));
        Assert.True(policy.HasAlwaysApprovedConstrainedTool("process.start"));
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
            hooks = new[] { "stop" }
        }, WireJson.Options));

        var stopHook = new StopHook();
        await using var runtime = new AgentRuntime(new Approval(), new Questions(), new Artifacts(), settingsRoot: _root,
            pluginDirectory: pluginRoot, pluginTools: [new PluginTool(), new PluginTool("plugin.hot")], pluginHooks: [stopHook]);

        Assert.Contains(runtime.Connection.Descriptors, descriptor => descriptor.Id == "plugin.echo");
        Assert.True(runtime.Connection.AdaptiveCoordinatorAvailable);
        Assert.Single(runtime.PluginCatalog.Manifests);
        foreach (var id in new[] { "thread.create", "thread.get", "thread.append_turn", "thread.queue", "thread.search", "thread.fork", "thread.checkpoint" })
            Assert.Contains(runtime.Connection.Descriptors, descriptor => descriptor.Id == id);
        Assert.True(File.Exists(Path.Combine(_root, "thread-runtime.db")));
        Assert.True(runtime.PluginRuntime.Watching);

        runtime.Pause();
        await EventuallyAsync(() => stopHook.Calls > 0);

        var hotEntry = Path.Combine(pluginRoot, "hot.bin");
        await File.WriteAllTextAsync(hotEntry, "hot plugin metadata");
        var hotHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(hotEntry))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "hot.plugin.json"), JsonSerializer.Serialize(new
        {
            id = "test.hot", version = "1.0.0", minAgentVersion = "1.0.0", entryFile = "hot.bin", sha256 = hotHash,
            tools = new[] { new { id = "plugin.hot" } }, permissions = Array.Empty<string>(), skills = Array.Empty<string>(), hooks = Array.Empty<string>()
        }, WireJson.Options));
        await EventuallyAsync(() => runtime.Connection.Descriptors.Any(descriptor => descriptor.Id == "plugin.hot"));
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
