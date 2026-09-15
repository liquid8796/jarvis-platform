using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Plugins;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class PluginCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-plugins-" + Guid.NewGuid().ToString("N"));

    private sealed class Tool(string id) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new(id, id.Replace('.', '_'), "plugin", "synthetic", WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) => Task.FromResult(new ToolReply("ok"));
    }

    public PluginCatalogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Valid_manifest_binds_only_supplied_implementations_and_hooks()
    {
        var implementation = new Tool("plugin.echo");
        WriteManifest("sample", "plugin.bin", FileHash("plugin.bin", "payload"), ["plugin.echo"], ["interrupt", "stop"]);

        var catalog = PluginCatalog.Load(_root, [implementation], new Version(1, 0, 52));

        Assert.Single(catalog.Manifests);
        Assert.Same(implementation, catalog.Tools["plugin.echo"]);
        Assert.Contains("interrupt", catalog.Hooks["sample"]);
    }

    [Fact]
    public void Catalog_rejects_missing_tool_incompatible_agent_bad_hash_hook_and_traversal()
    {
        var hash = FileHash("plugin.bin", "payload");
        WriteManifest("missing", "plugin.bin", hash, ["plugin.missing"], []);
        Assert.Throws<ArgumentException>(() => PluginCatalog.Load(_root, [], new Version(1, 0, 52)));
        Directory.Delete(_root, true); Directory.CreateDirectory(_root);

        WriteManifest("future", "plugin.bin", FileHash("plugin.bin", "payload"), [], [], minAgentVersion: "9.0.0");
        Assert.Throws<ArgumentException>(() => PluginCatalog.Load(_root, [], new Version(1, 0, 52)));
        Directory.Delete(_root, true); Directory.CreateDirectory(_root);

        WriteManifest("badhash", "plugin.bin", new string('z', 64), [], []);
        Assert.Throws<ArgumentException>(() => PluginCatalog.Load(_root, [], new Version(1, 0, 52)));
        Directory.Delete(_root, true); Directory.CreateDirectory(_root);

        WriteManifest("badhook", "plugin.bin", FileHash("plugin.bin", "payload"), [], ["unknown"]);
        Assert.Throws<ArgumentException>(() => PluginCatalog.Load(_root, [], new Version(1, 0, 52)));
        Directory.Delete(_root, true); Directory.CreateDirectory(_root);

        WriteManifest("escape", "../outside.bin", new string('0', 64), [], []);
        Assert.Throws<ArgumentException>(() => PluginCatalog.Load(_root, [], new Version(1, 0, 52)));
    }

    [Fact]
    public void Catalog_rejects_duplicate_plugin_and_tool_ids()
    {
        var tool = new Tool("plugin.echo");
        var hashA = FileHash("a.bin", "a");
        var hashB = FileHash("b.bin", "b");
        WriteManifest("dup", "a.bin", hashA, ["plugin.echo"], [], fileName: "a.plugin.json");
        WriteManifest("dup", "b.bin", hashB, ["plugin.echo"], [], fileName: "b.plugin.json");
        Assert.Throws<ArgumentException>(() => PluginCatalog.Load(_root, [tool], new Version(1, 0, 52)));

        Directory.Delete(_root, true); Directory.CreateDirectory(_root);
        hashA = FileHash("a.bin", "a"); hashB = FileHash("b.bin", "b");
        WriteManifest("one", "a.bin", hashA, ["plugin.echo"], [], fileName: "a.plugin.json");
        WriteManifest("two", "b.bin", hashB, ["plugin.echo"], [], fileName: "b.plugin.json");
        Assert.Throws<ArgumentException>(() => PluginCatalog.Load(_root, [tool], new Version(1, 0, 52)));
    }

    private string FileHash(string name, string content)
    {
        var path = Path.Combine(_root, name); File.WriteAllText(path, content);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private void WriteManifest(string id, string entryFile, string sha256, string[] tools, string[] hooks,
        string minAgentVersion = "1.0.0", string? fileName = null)
    {
        var manifest = new { id, version = "1.0.0", minAgentVersion, entryFile, sha256,
            tools = tools.Select(toolId => new { id = toolId }).ToArray(), permissions = Array.Empty<string>(), skills = Array.Empty<string>(), hooks };
        File.WriteAllText(Path.Combine(_root, fileName ?? id + ".plugin.json"), JsonSerializer.Serialize(manifest, WireJson.Options));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
