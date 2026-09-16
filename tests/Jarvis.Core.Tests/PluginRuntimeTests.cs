using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class PluginRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-plugin-runtime-" + Guid.NewGuid().ToString("N"));

    private sealed class Tool(string id) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), "plugin", "runtime test",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(new ToolReply("ok"));
    }

    private sealed class Hook(string pluginId, string hook, bool fail = false) : IPluginLifecycleHook
    {
        public string PluginId => pluginId;
        public string HookName => hook;
        public int Calls { get; private set; }
        public Task InvokeAsync(AgentLifecycleEvent evt, CancellationToken cancellationToken)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("synthetic hook failure");
            return Task.CompletedTask;
        }
    }

    public PluginRuntimeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Runtime_binds_manifest_hooks_isolates_failures_and_resolves_skill_mcp_metadata()
    {
        Directory.CreateDirectory(Path.Combine(_root, "skills"));
        var bad = new Hook("plugin.bad", "stop", fail: true);
        var good = new Hook("plugin.good", "stop");
        WriteManifest("plugin.bad", "bad.bin", "bad", [], ["stop"], skills: ["skills"], provenance: "local-test",
            mcpDependencies: [new { id = "github", minVersion = "1.2.0" }]);
        WriteManifest("plugin.good", "good.bin", "good", [], ["stop"], skills: ["skills"], provenance: "local-test",
            mcpDependencies: [new { id = "drive", minVersion = "2.0.0" }]);

        using var runtime = new PluginRuntimeBootstrap(_root, [], new Version(1, 0, 58), [bad, good]);
        Assert.EndsWith(Path.Combine("skills"), runtime.Snapshot.SkillRoots["plugin.good"].Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("drive", runtime.Snapshot.McpDependencies["plugin.good"].Single().Id);
        Assert.Equal("local-test", runtime.Snapshot.Manifests.Single(x => x.Id == "plugin.good").Provenance);

        var hub = new AgentLifecycleHub();
        using var binding = runtime.BindLifecycle(hub);
        await hub.NotifyAsync(new AgentLifecycleEvent(AgentLifecycleKind.Stop, Reason: "test"));

        Assert.Equal(1, bad.Calls);
        Assert.Equal(1, good.Calls);
        Assert.Equal(1, runtime.HookFailureCount);
        Assert.Equal("plugin.bad:stop:InvalidOperationException", runtime.LastHookError);
    }

    [Fact]
    public void Package_update_bad_reload_and_uninstall_keep_runtime_atomic()
    {
        var tool = new Tool("plugin.echo");
        using var runtime = new PluginRuntimeBootstrap(_root, [tool], new Version(1, 0, 58));
        var changed = 0;
        runtime.Changed += _ => changed++;

        runtime.InstallOrUpdate(Package("sample", "1.0.0", "plugin.bin", "one", ["plugin.echo"]));
        Assert.Equal("1.0.0", runtime.Snapshot.Manifests.Single().Version);
        Assert.Same(tool, runtime.Snapshot.Tools["plugin.echo"]);
        Assert.Equal(1, changed);

        var manifestPath = Path.Combine(_root, "sample.plugin.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(runtime.Snapshot.Manifests.Single().Sha256, new string('0', 64), StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => runtime.Reload());
        Assert.Equal("1.0.0", runtime.Snapshot.Manifests.Single().Version);
        Assert.Equal("ArgumentException", runtime.LastReloadError);

        runtime.InstallOrUpdate(Package("sample", "1.1.0", "plugin.bin", "two", ["plugin.echo"]));
        Assert.Equal("1.1.0", runtime.Snapshot.Manifests.Single().Version);
        Assert.Null(runtime.LastReloadError);

        Assert.True(runtime.Uninstall("sample"));
        Assert.Empty(runtime.Snapshot.Manifests);
        Assert.Empty(runtime.Snapshot.Tools);
        Assert.False(File.Exists(manifestPath));
        Assert.True(changed >= 3);
    }

    [Fact]
    public async Task Watcher_publishes_valid_external_manifest_change()
    {
        var tool = new Tool("plugin.echo");
        using var runtime = new PluginRuntimeBootstrap(_root, [tool], new Version(1, 0, 58));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Changed += snapshot => { if (snapshot.Tools.ContainsKey("plugin.echo")) seen.TrySetResult(); };
        runtime.StartWatching();

        var package = Package("watched", "1.0.0", "watch.bin", "watch", ["plugin.echo"]);
        File.WriteAllBytes(Path.Combine(_root, package.Manifest.EntryFile), package.EntryBytes);
        File.WriteAllText(Path.Combine(_root, "watched.plugin.json"), JsonSerializer.Serialize(package.Manifest, WireJson.Options));

        await seen.Task.WaitAsync(stop.Token);
        Assert.Contains("plugin.echo", runtime.Snapshot.Tools.Keys);
    }

    private PluginPackage Package(string id, string version, string entryFile, string content, string[] tools)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new PluginPackage(new PluginManifest
        {
            Id = id,
            Version = version,
            MinAgentVersion = "1.0.0",
            MaxAgentVersion = "2.0.0",
            EntryFile = entryFile,
            Sha256 = hash,
            Tools = tools.Select(x => new PluginToolDeclaration { Id = x }).ToArray(),
            Permissions = [], Skills = [], Hooks = [], McpDependencies = [], Provenance = "test-package"
        }, bytes);
    }

    private void WriteManifest(string id, string entryFile, string content, string[] tools, string[] hooks,
        string[]? skills = null, string? provenance = null, object[]? mcpDependencies = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(Path.Combine(_root, entryFile), bytes);
        var manifest = new
        {
            id, version = "1.0.0", minAgentVersion = "1.0.0", maxAgentVersion = "2.0.0", entryFile,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            tools = tools.Select(x => new { id = x }).ToArray(), permissions = Array.Empty<string>(), skills = skills ?? [], hooks,
            mcpDependencies = mcpDependencies ?? [], provenance
        };
        File.WriteAllText(Path.Combine(_root, id + ".plugin.json"), JsonSerializer.Serialize(manifest, WireJson.Options));
    }

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
}
