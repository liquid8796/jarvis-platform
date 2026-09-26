using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Models;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BlenderMcpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-blender-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "mcp.json");
    private static JsonObject Configuration() => new()
    {
        ["mcpServers"] = new JsonObject
        {
            ["blender"] = new JsonObject
            {
                ["command"] = "C:/Blender MCP/python.exe",
                ["args"] = new JsonArray("-m", "blender_mcp.server"),
                ["env"] = new JsonObject()
            },
            ["unityMCP"] = new JsonObject { ["url"] = "http://localhost:8080/mcp" }
        }
    };
    private static JsonObject Entry(JsonObject root) => (JsonObject)root["mcpServers"]!["blender"]!;
    private static McpServerConfig Parse(JsonObject root) => Parse(root.ToJsonString());
    private static McpServerConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return BlenderMcpConfig.Parse(doc.RootElement);
    }
    private void Save(JsonObject? config = null) => File.WriteAllText(ConfigPath, (config ?? Configuration()).ToJsonString());
    private static Task<ToolReply> Invoke(ExternalMcpToolSet set, string operation = "list_tools", object? args = null,
        string scope = "one", CancellationToken ct = default) => set.Tools.Single(t => t.Descriptor.Id.EndsWith("." + operation, StringComparison.Ordinal))
        .ExecuteAsync(WireJson.Element(args ?? new { }), new AgentExecutionContext("", "blender-test", scope), ct);

    public BlenderMcpTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Parser_uses_only_blender_entry_and_supplies_explicit_safe_child_environment()
    {
        var config = Parse(Configuration());
        Assert.Equal("blender", config.Name);
        Assert.Equal("stdio", config.Type);
        Assert.Equal("C:/Blender MCP/python.exe", config.Command);
        Assert.Equal(new[] { "-m", "blender_mcp.server" }, config.Args);
        Assert.Equal("127.0.0.1", config.Env["BLENDER_HOST"]);
        Assert.Equal("9876", config.Env["BLENDER_PORT"]);
        Assert.Equal("1", config.Env["BLENDER_MCP_SAFE_MODE"]);
        Assert.Equal("true", config.Env["BLENDER_MCP_DISABLE_TELEMETRY"]);
        Assert.Equal("true", config.Env["DISABLE_TELEMETRY"]);
        Assert.Equal("utf-8", config.Env["PYTHONIOENCODING"]);
    }

    [Fact]
    public void Parser_normalizes_localhost_and_explicit_safe_environment()
    {
        var root = Configuration();
        Entry(root)["env"] = new JsonObject { ["BLENDER_HOST"] = "localhost", ["BLENDER_PORT"] = "19876",
            ["BLENDER_MCP_SAFE_MODE"] = "True", ["BLENDER_MCP_DISABLE_TELEMETRY"] = "1", ["TEST"] = "space value" };
        var config = Parse(root);
        Assert.Equal("127.0.0.1", config.Env["BLENDER_HOST"]);
        Assert.Equal("19876", config.Env["BLENDER_PORT"]);
        Assert.Equal("true", config.Env["BLENDER_MCP_DISABLE_TELEMETRY"]);
        Assert.Equal("space value", config.Env["TEST"]);
    }

    [Theory]
    [InlineData("BLENDER_HOST", "192.168.1.2")]
    [InlineData("BLENDER_HOST", "0.0.0.0")]
    [InlineData("BLENDER_HOST", "::1")]
    [InlineData("BLENDER_PORT", "0")]
    [InlineData("BLENDER_PORT", "65536")]
    [InlineData("BLENDER_PORT", "oops")]
    [InlineData("BLENDER_PORT", " 9876")]
    [InlineData("BLENDER_MCP_SAFE_MODE", "0")]
    [InlineData("BLENDER_MCP_SAFE_MODE", "false")]
    [InlineData("BLENDER_MCP_DISABLE_TELEMETRY", "false")]
    [InlineData("DISABLE_TELEMETRY", "false")]
    public void Parser_rejects_unsafe_or_invalid_blender_environment(string name, string value)
    {
        var root = Configuration();
        Entry(root)["env"]![name] = value;
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Theory]
    [InlineData("python.exe")]
    [InlineData("C:/tools/cmd.exe")]
    [InlineData("C:/tools/pythonw.exe")]
    [InlineData("C:/tools/server.cmd")]
    public void Parser_rejects_relative_or_non_python_launchers(string command)
    {
        var root = Configuration(); Entry(root)["command"] = command;
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Fact]
    public void Parser_rejects_http_shell_arguments_and_disabled_entries()
    {
        var root = Configuration(); Entry(root)["url"] = "https://example.test/mcp";
        Assert.Throws<InvalidDataException>(() => Parse(root));
        root = Configuration(); Entry(root)["args"] = new JsonArray("-c", "print('not an MCP server')");
        Assert.Throws<InvalidDataException>(() => Parse(root));
        root = Configuration(); Entry(root)["disabled"] = true;
        Assert.Throws<InvalidDataException>(() => Parse(root));
        root = Configuration(); Entry(root)["enabled"] = false;
        Assert.Throws<InvalidDataException>(() => Parse(root));
    }

    [Theory]
    [InlineData("{\"mcpServers\":{},\"mcpServers\":{}}")]
    [InlineData("{\"mcpServers\":{\"blender\":{},\"blender\":{}}}")]
    [InlineData("{\"mcpServers\":{\"blender\":{\"command\":\"x\",\"command\":\"y\"}}}")]
    [InlineData("{\"mcpServers\":{\"blender\":{\"command\":\"C:/python.exe\",\"args\":[\"-m\",\"blender_mcp.server\"],\"env\":{\"KEY\":\"one\",\"key\":\"two\"}}}}")]
    public void Ambiguous_json_is_rejected(string json) => Assert.Throws<InvalidDataException>(() => Parse(json));

    [Fact]
    public async Task Construction_and_missing_or_oversized_configuration_never_start_a_process()
    {
        int starts = 0;
        using var set = new BlenderMcpToolSet(ConfigPath, (_, _) => { starts++; return Task.FromResult<IMcpClient>(new FakeClient()); });
        Assert.Equal(0, starts);
        var missing = await Invoke(set);
        Assert.True(missing.IsError); Assert.Contains("Blender", missing.Text); Assert.DoesNotContain("Unity", missing.Text);
        File.WriteAllText(ConfigPath, new string(' ', BlenderMcpConfig.MaxConfigBytes + 1));
        Assert.True((await Invoke(set)).IsError);
        Assert.Equal(BlenderMcpConfig.MaxConfigBytes + 1, new FileInfo(ConfigPath).Length);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void Blender_and_unity_descriptors_coexist_without_broadening_permissions()
    {
        using var blender = new BlenderMcpToolSet(ConfigPath);
        using var unity = new UnityMcpToolSet(ConfigPath);
        Assert.Equal(6, blender.Tools.Count);
        foreach (var tool in blender.Tools)
        {
            Assert.Equal("blender", tool.Descriptor.Category);
            Assert.StartsWith("blender.", tool.Descriptor.Id);
            Assert.True(tool.Descriptor.Sensitive); Assert.False(tool.Descriptor.ReadOnly);
            Assert.Contains("Safe Mode", tool.Descriptor.Description);
            SchemaGuard.Compile(tool.Descriptor.InputSchema);
        }
        Assert.Equal(12, blender.Tools.Concat(unity.Tools).Select(t => t.Descriptor.Id).Distinct().Count());
        // The gateway catalog has globally unique Name values, not (Category, Name).
        Assert.Equal(12, blender.Tools.Concat(unity.Tools).Select(t => t.Descriptor.Name).Distinct().Count());
        Assert.All(blender.Tools, t => Assert.StartsWith("blender_", t.Descriptor.Name));
        Assert.All(unity.Tools, t => Assert.StartsWith("unity_", t.Descriptor.Name));
        _ = new DynamicToolRegistry(blender.Tools.Concat(unity.Tools));
    }

    [Fact]
    public async Task Realistic_discovery_and_blender_arguments_preserve_images_structured_data_and_errors()
    {
        Save();
        var fake = new FakeClient { Result = new("upstream error", true)
        { Images = [new ImageBlock("image/png", "AA==")], StructuredContent = new JsonObject { ["success"] = false } } };
        using var set = new BlenderMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        var page = JsonNode.Parse((await Invoke(set, args: new { query = "scene", pageSize = 1 })).Text)!;
        Assert.Equal("get_scene_info", page["items"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("object", page["items"]![0]!["inputSchema"]!["type"]!.GetValue<string>());
        const string code = "import bpy\nprint(bpy.app.version_string)";
        var reply = await Invoke(set, "call_tool", new { name = "execute_blender_code", arguments = new { code, user_prompt = "inspect scene" } });
        Assert.True(reply.IsError); Assert.Equal("execute_blender_code", fake.LastName);
        Assert.Equal(code, fake.LastArguments!["code"]!.GetValue<string>());
        Assert.Equal("inspect scene", fake.LastArguments!["user_prompt"]!.GetValue<string>());
        Assert.Equal("AA==", Assert.Single(reply.Images!).Base64);
        Assert.False(JsonNode.Parse(reply.Text)!["structuredContent"]!["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Transport_failure_is_sanitized_and_not_replayed()
    {
        Save(); var fake = new FakeClient { Failure = new McpException("sensitive-token") };
        using var set = new BlenderMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        var reply = await Invoke(set, "call_tool", new { name = "execute_blender_code", arguments = new { code = "pass" } });
        Assert.True(reply.IsError); Assert.Contains("not retried", reply.Text);
        Assert.DoesNotContain("sensitive-token", reply.Text); Assert.Equal(1, fake.Calls); Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task Scope_connections_and_config_changes_are_independent_from_unity()
    {
        Save(); var clients = new List<FakeClient>();
        using var blender = new BlenderMcpToolSet(ConfigPath, (_, _) => { var f = new FakeClient(); clients.Add(f); return Task.FromResult<IMcpClient>(f); });
        var unityClient = new FakeClient();
        using var unity = new UnityMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(unityClient));
        await Invoke(unity); await Invoke(blender); await Invoke(blender); await Invoke(blender, scope: "two");
        Assert.Equal(2, clients.Count);
        blender.StopSession("two"); Assert.True(clients[1].Disposed); Assert.False(clients[0].Disposed);
        var changed = Configuration(); Entry(changed)["env"]!["BLENDER_PORT"] = "9877"; Save(changed);
        await Invoke(blender); Assert.Equal(3, clients.Count); Assert.True(clients[0].Disposed);
        Entry(changed)["disabled"] = true; Save(changed);
        Assert.True((await Invoke(blender)).IsError); Assert.True(clients[2].Disposed);
        Assert.False((await Invoke(unity)).IsError); Assert.False(unityClient.Disposed);
    }

    [Fact]
    public async Task Different_sessions_cannot_overlap_blender_calls()
    {
        Save(); var first = new FakeClient { Block = true }; var second = new FakeClient(); int starts = 0;
        using var set = new BlenderMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(++starts == 1 ? first : second));
        var a = Invoke(set, "call_tool", new { name = "execute_blender_code", arguments = new { } });
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = Invoke(set, "call_tool", new { name = "get_scene_info", arguments = new { } }, scope: "two");
        Assert.False(b.IsCompleted); Assert.Equal(1, starts);
        first.Release.TrySetResult();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, starts); Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task Cancelling_queued_session_leaves_active_session_running_and_pause_releases_bridge()
    {
        Save(); var first = new FakeClient { Block = true }; int starts = 0;
        using var set = new BlenderMcpToolSet(ConfigPath, (_, _) => { starts++; return Task.FromResult<IMcpClient>(first); });
        var a = Invoke(set, "call_tool", new { name = "wait", arguments = new { } });
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = Invoke(set, scope: "two");
        set.StopSession("two");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.Equal(1, starts); Assert.False(a.IsCompleted); Assert.False(first.Disposed);
        set.Pause();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.True(first.Disposed); Assert.Equal(1, first.Calls);
    }

    [Fact]
    public async Task Precancelled_calls_do_not_connect()
    {
        Save(); int starts = 0;
        using var set = new BlenderMcpToolSet(ConfigPath, (_, _) => { starts++; return Task.FromResult<IMcpClient>(new FakeClient()); });
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(set, ct: ct.Token));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Oversized_results_are_bounded_and_optional_routes_remain_available()
    {
        Save(); var fake = new FakeClient { Result = new(new string('x', 512 * 1024 + 1), false) };
        using var set = new BlenderMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        var result = await Invoke(set, "call_tool", new { name = "get_scene_info", arguments = new { } });
        Assert.True(result.IsError); Assert.Equal(1, fake.Calls);
        Assert.Contains("blender://scene", (await Invoke(set, "list_resources")).Text);
        Assert.Equal("resource:blender://scene", (await Invoke(set, "read_resource", new { uri = "blender://scene" })).Text);
        Assert.Contains("inspect", (await Invoke(set, "list_prompts")).Text);
        Assert.Equal("prompt:inspect", (await Invoke(set, "get_prompt", new { name = "inspect", arguments = new { } })).Text);
    }

    private sealed class FakeClient : IMcpClient
    {
        public string ServerName => "blender";
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public string? LastName { get; private set; }
        public JsonObject? LastArguments { get; private set; }
        public McpCallResult Result { get; init; } = new("ok", false);
        public Exception? Failure { get; init; }
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolDescriptor>>(
            [new("get_scene_info", "inspect scene", new JsonObject { ["type"] = "object" }), new("execute_blender_code", "code", new JsonObject { ["type"] = "object" })]);
        public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceDescriptor>>([new("blender://scene", "scene", null, "application/json")]);
        public Task<string> ReadResourceAsync(string uri, CancellationToken ct) => Task.FromResult("resource:" + uri);
        public Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptDescriptor>>([new("inspect", null, [])]);
        public Task<string> GetPromptAsync(string name, JsonObject args, CancellationToken ct) => Task.FromResult("prompt:" + name);
        public async Task<McpCallResult> CallToolAsync(string name, JsonObject args, CancellationToken ct, McpElicitationCallback? elicit = null)
        {
            Calls++; LastName = name; LastArguments = (JsonObject)args.DeepClone(); Started.TrySetResult();
            if (Failure is not null) throw Failure;
            if (Block) await Release.Task.WaitAsync(ct);
            return Result;
        }
    }
}
