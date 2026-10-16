using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Models;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ReaMcpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-rea-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "mcp.json");
    private static JsonObject Configuration() => new()
    {
        ["mcpServers"] = new JsonObject
        {
            ["rea"] = new JsonObject
            {
                ["command"] = "C:/Program Files/nodejs/node.exe",
                ["args"] = new JsonArray("C:/Users/Test/AppData/Roaming/npm/node_modules/rea-agents/scripts/rea.mjs", "mcp"),
                ["env"] = new JsonObject
                {
                    ["GHIDRA_INSTALL_DIR"] = "D:/Tools/Ghidra/ghidra_12.1.4_PUBLIC",
                    ["JAVA_HOME"] = "C:/Program Files/Java/jdk-21.0.8"
                }
            },
            ["unityMCP"] = new JsonObject { ["url"] = "http://localhost:8080/mcp" },
            ["blender"] = new JsonObject { ["command"] = "C:/Python/python.exe" }
        }
    };
    private static JsonObject Entry(JsonObject obj) => (JsonObject)obj["mcpServers"]!["rea"]!;
    private static McpServerConfig Parse(JsonObject root)
    {
        using var doc = JsonDocument.Parse(root.ToJsonString());
        return ReaMcpConfig.Parse(doc.RootElement);
    }
    private void Save(JsonObject? obj = null) => File.WriteAllText(ConfigPath, (obj ?? Configuration()).ToJsonString());
    private static Task<ToolReply> Invoke(ReaMcpToolSet set, string operation = "list_tools", object? args = null,
        string scope = "one", CancellationToken token = default) =>
        set.Tools.Single(t => t.Descriptor.Id == "rea." + operation)
            .ExecuteAsync(WireJson.Element(args ?? new { }), new AgentExecutionContext("", "rea-test", scope), token);

    public ReaMcpTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Loads_only_rea_and_preserves_explicit_ghidra_environment()
    {
        var config = Parse(Configuration());
        Assert.Equal("rea", config.Name);
        Assert.Equal("stdio", config.Type);
        Assert.Equal("C:/Program Files/nodejs/node.exe", config.Command);
        Assert.Equal("mcp", config.Args[1]);
        Assert.Equal("D:/Tools/Ghidra/ghidra_12.1.4_PUBLIC", config.Env["GHIDRA_INSTALL_DIR"]);
        Assert.Equal("C:/Program Files/Java/jdk-21.0.8", config.Env["JAVA_HOME"]);
    }

    [Theory]
    [InlineData("node.exe")]
    [InlineData("C:/Windows/System32/cmd.exe")]
    [InlineData("C:/node.bat")]
    public void Rejects_unsafe_or_relative_commands(string command)
    {
        var config = Configuration();
        Entry(config)["command"] = command;
        Assert.Throws<InvalidDataException>(() => Parse(config));
    }

    [Theory]
    [InlineData("C:/other.mjs", "mcp")]
    [InlineData("C:/npm/node_modules/other/scripts/rea.mjs", "mcp")]
    [InlineData("C:/npm/node_modules/rea-agents/scripts/rea.mjs", "analyze")]
    public void Rejects_non_rea_script_or_non_mcp_mode(string script, string mode)
    {
        var config = Configuration();
        Entry(config)["args"] = new JsonArray(script, mode);
        Assert.Throws<InvalidDataException>(() => Parse(config));
    }

    [Fact]
    public void Rejects_http_disabled_or_relative_provider_environment()
    {
        var config = Configuration(); Entry(config)["url"] = "https://example.test/mcp";
        Assert.Throws<InvalidDataException>(() => Parse(config));
        config = Configuration(); Entry(config)["disabled"] = true;
        Assert.Throws<InvalidDataException>(() => Parse(config));
        config = Configuration(); Entry(config)["env"]!["GHIDRA_INSTALL_DIR"] = "./ghidra";
        Assert.Throws<InvalidDataException>(() => Parse(config));
    }

    [Fact]
    public void Bridge_is_sensitive_and_coexists_with_blender_and_unity()
    {
        using var rea = new ReaMcpToolSet(ConfigPath);
        using var blender = new BlenderMcpToolSet(ConfigPath);
        using var unity = new UnityMcpToolSet(ConfigPath);
        Assert.Equal(6, rea.Tools.Count);
        Assert.All(rea.Tools, t =>
        {
            Assert.StartsWith("rea.", t.Descriptor.Id);
            Assert.StartsWith("rea_", t.Descriptor.Name);
            Assert.Equal("rea", t.Descriptor.Category);
            Assert.True(t.Descriptor.Sensitive);
            Assert.False(t.Descriptor.ReadOnly);
            SchemaGuard.Compile(t.Descriptor.InputSchema);
        });
        var all = rea.Tools.Concat(blender.Tools).Concat(unity.Tools).ToArray();
        Assert.Equal(all.Length, all.Select(t => t.Descriptor.Id).Distinct().Count());
        Assert.Equal(all.Length, all.Select(t => t.Descriptor.Name).Distinct().Count());
        _ = new DynamicToolRegistry(all);
    }

    [Fact]
    public async Task Bridge_is_lazy_and_missing_configuration_does_not_launch()
    {
        int starts = 0;
        using var rea = new ReaMcpToolSet(ConfigPath, (_, _) =>
        {
            starts++;
            return Task.FromResult<IMcpClient>(new FakeClient());
        });
        Assert.Equal(0, starts);
        Assert.True((await Invoke(rea)).IsError);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Discovery_paginates_and_call_preserves_json_and_errors()
    {
        Save();
        var fake = new FakeClient();
        using var rea = new ReaMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        var list = JsonNode.Parse((await Invoke(rea, args: new { query = "inspect", pageSize = 1 })).Text)!;
        Assert.Equal(1, list["total"]!.GetValue<int>());
        Assert.Equal("inspect_native_function", list["items"]![0]!["name"]!.GetValue<string>());
        var call = await Invoke(rea, "call_tool", new { name = "inspect_native_function", arguments = new { address = "0x401000" } });
        Assert.True(call.IsError);
        Assert.Equal("inspect_native_function", fake.LastName);
        Assert.Equal("0x401000", fake.LastArguments!["address"]!.GetValue<string>());
        Assert.Equal(1, fake.Calls);
        Assert.Equal("AA==", Assert.Single(call.Images!).Base64);
        Assert.False(JsonNode.Parse(call.Text)!["structuredContent"]!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Per_session_connections_reconfigure_and_pause_without_retries()
    {
        Save();
        var clients = new List<FakeClient>();
        using var rea = new ReaMcpToolSet(ConfigPath, (_, _) =>
        {
            var fake = new FakeClient();
            clients.Add(fake);
            return Task.FromResult<IMcpClient>(fake);
        });
        await Invoke(rea); await Invoke(rea);
        Assert.Single(clients);
        await Invoke(rea, scope: "other");
        Assert.Equal(2, clients.Count);
        rea.StopSession("one");
        Assert.True(clients[0].Disposed);
        Assert.False(clients[1].Disposed);
        var config = Configuration();
        Entry(config)["env"]!["JAVA_HOME"] = "D:/jdk-21";
        Save(config);
        await Invoke(rea, scope: "other");
        Assert.True(clients[1].Disposed);
        Assert.Equal(3, clients.Count);
        rea.Pause();
        Assert.True(clients[2].Disposed);
    }

    private sealed class FakeClient : IMcpClient
    {
        public string ServerName => "rea";
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public string? LastName { get; private set; }
        public JsonObject? LastArguments { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<McpToolDescriptor>>([
                new("inspect_native_function", "inspect a native function", new JsonObject { ["type"] = "object" }),
                new("decompile_native_function", "decompile a native function", new JsonObject { ["type"] = "object" })
            ]);
        public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<McpResourceDescriptor>>([]);
        public Task<string> ReadResourceAsync(string uri, CancellationToken token) => Task.FromResult("");
        public Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<McpPromptDescriptor>>([]);
        public Task<string> GetPromptAsync(string name, JsonObject args, CancellationToken token) => Task.FromResult("");
        public Task<McpCallResult> CallToolAsync(string name, JsonObject args, CancellationToken token, McpElicitationCallback? elicit = null)
        {
            Calls++;
            LastName = name;
            LastArguments = (JsonObject)args.DeepClone();
            return Task.FromResult(new McpCallResult("upstream error", true)
            {
                StructuredContent = new JsonObject { ["success"] = false },
                Images = [new ImageBlock("image/png", "AA==")]
            });
        }
    }
}
