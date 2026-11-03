using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Models;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BabylonEditorMcpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-babylon-test-" + Guid.NewGuid().ToString("N"));
    private static string EditorExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BabylonJS Editor", "BabylonJS Editor.exe");
    private static string EditorScript => Path.Combine(
        Path.GetDirectoryName(EditorExe)!, "resources", "app.asar", "build", "mcp", "index.mjs");
    private static JsonObject Config() => new()
    {
        ["mcpServers"] = new JsonObject
        {
            ["babylonjs-editor"] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = EditorExe,
                ["args"] = new JsonArray(EditorScript),
                ["env"] = new JsonObject
                {
                    ["ELECTRON_RUN_AS_NODE"] = "1",
                    ["BABYLONJS_EDITOR_ASSISTANT"] = "1",
                    ["BABYLONJS_EDITOR_MCP_URL"] = "http://127.0.0.1:49123",
                    ["BABYLONJS_EDITOR_MCP_TOKEN"] = new string('a', 64)
                }
            },
            ["rea"] = new JsonObject { ["command"] = "C:/node/node.exe" }
        }
    };
    private static JsonObject Entry(JsonObject cfg) => (JsonObject)cfg["mcpServers"]!["babylonjs-editor"]!;
    private static McpServerConfig Parse(JsonObject root)
    {
        using var doc = JsonDocument.Parse(root.ToJsonString());
        return BabylonEditorMcpConfig.Parse(doc.RootElement);
    }
    private void Save(string session, JsonObject? cfg = null)
    {
        var folder = Path.Combine(_root, session);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mcp.json"), (cfg ?? Config()).ToJsonString());
    }
    public BabylonEditorMcpTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Accepts_only_the_native_editor_binary_and_editor_issued_loopback_token()
    {
        var server = Parse(Config());
        Assert.Equal("babylonjs-editor", server.Name);
        Assert.Equal("stdio", server.Type);
        Assert.Equal(EditorExe, server.Command);
        Assert.Equal(EditorScript, Assert.Single(server.Args));
        Assert.Equal(new string('a', 64), server.Env["BABYLONJS_EDITOR_MCP_TOKEN"]);
    }

    [Theory]
    [InlineData("http://evil.example:49123")]
    [InlineData("http://localhost:49123")]
    [InlineData("https://127.0.0.1:49123")]
    [InlineData("http://127.0.0.1:49123/?x=1")]
    [InlineData("http://127.0.0.1:49123/other")]
    public void Rejects_non_editor_or_non_loopback_endpoints(string url)
    {
        var cfg = Config(); Entry(cfg)["env"]!["BABYLONJS_EDITOR_MCP_URL"] = url;
        Assert.Throws<InvalidDataException>(() => Parse(cfg));
    }

    [Fact]
    public void Rejects_other_executables_scripts_tokens_and_transport_overrides()
    {
        var cfg = Config(); Entry(cfg)["command"] = @"C:\Windows\System32\cmd.exe";
        Assert.Throws<InvalidDataException>(() => Parse(cfg));
        cfg = Config(); Entry(cfg)["args"] = new JsonArray(@"C:\other\index.mjs");
        Assert.Throws<InvalidDataException>(() => Parse(cfg));
        cfg = Config(); Entry(cfg)["env"]!["BABYLONJS_EDITOR_MCP_TOKEN"] = "short";
        Assert.Throws<InvalidDataException>(() => Parse(cfg));
        cfg = Config(); Entry(cfg)["env"]!["ELECTRON_RUN_AS_NODE"] = "0";
        Assert.Throws<InvalidDataException>(() => Parse(cfg));
        cfg = Config(); Entry(cfg)["url"] = "http://127.0.0.1:5000/mcp";
        Assert.Throws<InvalidDataException>(() => Parse(cfg));
        cfg = Config(); Entry(cfg)["env"]!["NODE_OPTIONS"] = "--require C:/malicious.js";
        Assert.Throws<InvalidDataException>(() => Parse(cfg));
    }

    [Fact]
    public void Discovers_single_editor_session_and_refuses_ambiguous_sessions()
    {
        Assert.Throws<InvalidDataException>(() => BabylonEditorMcpConfig.LoadFromSessions(_root));
        Save("one");
        Assert.Equal("babylonjs-editor", BabylonEditorMcpConfig.LoadFromSessions(_root).Name);
        var invalid = Config(); Entry(invalid)["command"] = "fake.exe"; Save("invalid", invalid);
        Assert.Equal("babylonjs-editor", BabylonEditorMcpConfig.LoadFromSessions(_root).Name);
        Save("two");
        Assert.Throws<InvalidDataException>(() => BabylonEditorMcpConfig.LoadFromSessions(_root));
    }

    [Fact]
    public void Bridge_registers_approval_gated_unique_tools_without_starting_editor()
    {
        using var bridge = new BabylonEditorMcpToolSet(Path.Combine(_root, "mcp.json"));
        Assert.Equal(6, bridge.Tools.Count);
        Assert.All(bridge.Tools, tool =>
        {
            Assert.StartsWith("babylonjs-editor.", tool.Descriptor.Id);
            Assert.StartsWith("babylonjs_editor_", tool.Descriptor.Name);
            Assert.Equal("babylonjs-editor", tool.Descriptor.Category);
            Assert.True(tool.Descriptor.Sensitive);
            Assert.False(tool.Descriptor.ReadOnly);
            SchemaGuard.Compile(tool.Descriptor.InputSchema);
        });
        using var rea = new ReaMcpToolSet(Path.Combine(_root, "mcp.json"));
        var all = bridge.Tools.Concat(rea.Tools);
        Assert.Equal(all.Count(), all.Select(t => t.Descriptor.Name).Distinct().Count());
        _ = new DynamicToolRegistry(all);
    }
}
