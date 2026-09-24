using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Models;

namespace Jarvis.Agent.Windows.Tests;

public sealed class UnityMcpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-unity-tests-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "mcp.json");
    private const string HttpConfig = "{\"mcpServers\":{\"unityMCP\":{\"type\":\"http\",\"url\":\"http://localhost:8080/mcp\",\"disabled\":false}}}";
    private static AgentExecutionContext Context(string id = "one") => new("", "test-call", id);

    public UnityMcpTests() { Directory.CreateDirectory(_root); }
    public void Dispose() { Directory.Delete(_root, true); }
    private void Save(string json = HttpConfig) => File.WriteAllText(ConfigPath, json);
    private static McpServerConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return UnityMcpConfig.Parse(doc.RootElement);
    }
    private static Task<ToolReply> Invoke(UnityMcpToolSet set, string id = "unity.list_tools", object? args = null,
        string scope = "one", CancellationToken ct = default) =>
        set.Tools.Single(tool => tool.Descriptor.Id == id).ExecuteAsync(WireJson.Element(args ?? new { }), Context(scope), ct);

    [Fact]
    public void Parser_accepts_unity_stdio_format_preserving_arguments_and_environment()
    {
        var config = Parse("{\"mcpServers\":{\"other\":{\"command\":\"ignored\"},\"unityMCP\":{\"type\":\"stdio\",\"command\":\"C:/tools/uvx.exe\",\"args\":[\"--from\",\"mcpforunityserver==10.2.0\",\"mcp-for-unity\",\"--transport\",\"stdio\"],\"env\":{\"TEST\":\"value with spaces\"}}}}");
        Assert.Equal("unityMCP", config.Name);
        Assert.Equal("stdio", config.Type);
        Assert.Equal("C:/tools/uvx.exe", config.Command);
        Assert.Equal("value with spaces", config.Env["TEST"]);
        Assert.Equal("stdio", config.Args[^1]);
    }

    [Fact]
    public void Parser_accepts_remote_https_headers_and_inferred_http_type()
    {
        var config = Parse("{\"mcpServers\":{\"unityMCP\":{\"url\":\"https://unity.example/mcp\",\"headers\":{\"X-API-Key\":\"test-only\"}}}}");
        Assert.Equal("http", config.Type);
        Assert.Equal("test-only", config.Headers["X-API-Key"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mcpServers\":[]}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"disabled\":true}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"enabled\":false}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"disabled\":\"false\"}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"type\":\"sse\",\"url\":\"https://example.test/mcp\"}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"url\":\"http://example.test/mcp\"}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"url\":\"https://user:pass@example.test/mcp\"}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"type\":\"stdio\",\"command\":\"uvx\",\"url\":\"http://localhost/mcp\"}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"type\":\"http\",\"url\":\"http://localhost/mcp\",\"command\":\"uvx\"}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"command\":\"uvx\",\"args\":[1]}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"command\":\"uvx\",\"env\":[]}}}")]
    [InlineData("{\"mcpServers\":{\"unityMCP\":{\"url\":\"https://example.test/mcp\",\"headers\":{\"X-Test\":\"value\\r\\nInjected: yes\"}}}}")]
    public void Parser_rejects_invalid_or_disabled_entries(string json) => Assert.Throws<InvalidDataException>(() => Parse(json));

    [Fact]
    public void Oversized_config_is_rejected_without_changing_file()
    {
        Save(new string(' ', UnityMcpConfig.MaxConfigBytes + 1));
        Assert.Throws<InvalidDataException>(() => UnityMcpConfig.Load(ConfigPath));
        Assert.Equal(UnityMcpConfig.MaxConfigBytes + 1, new FileInfo(ConfigPath).Length);
    }

    [Fact]
    public async Task Construction_is_lazy_and_missing_config_cannot_launch_a_process()
    {
        int starts = 0;
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => { starts++; return Task.FromResult<IMcpClient>(new FakeClient()); });
        Assert.Equal(0, starts);
        Assert.True((await Invoke(set)).IsError);
        Assert.Equal(0, starts);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Bridge_descriptors_are_sensitive_valid_and_never_marked_read_only()
    {
        using var set = new UnityMcpToolSet(ConfigPath);
        Assert.Equal(6, set.Tools.Count);
        foreach (var tool in set.Tools)
        {
            Assert.True(tool.Descriptor.Sensitive);
            Assert.False(tool.Descriptor.ReadOnly);
            Assert.Equal("unity", tool.Descriptor.Category);
            SchemaGuard.Compile(tool.Descriptor.InputSchema);
        }
        var registry = new DynamicToolRegistry(set.Tools);
        Assert.NotNull(registry);
    }

    [Fact]
    public async Task Discovery_pages_and_filters_exact_tool_schemas()
    {
        Save();
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(new FakeClient()));
        var reply = await Invoke(set, args: new { query = "tool", offset = 1, pageSize = 1 });
        Assert.False(reply.IsError);
        var json = JsonNode.Parse(reply.Text)!;
        Assert.Equal(3, json["total"]!.GetValue<int>());
        Assert.Equal(2, json["nextOffset"]!.GetValue<int>());
        Assert.Equal("tool_1", json["items"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("object", json["items"]![0]!["inputSchema"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Call_preserves_arguments_error_flag_images_and_structured_content()
    {
        Save();
        var fake = new FakeClient { Result = new("server text", true)
        { Images = [new ImageBlock("image/png", "AA==")], StructuredContent = new JsonObject { ["success"] = false } } };
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        var reply = await Invoke(set, "unity.call_tool", new { name = "manage_scene", arguments = new { action = "get_hierarchy" } });
        Assert.True(reply.IsError);
        Assert.Equal("manage_scene", fake.LastName);
        Assert.Equal("get_hierarchy", fake.LastArguments!["action"]!.GetValue<string>());
        Assert.Equal("AA==", Assert.Single(reply.Images!).Base64);
        Assert.False(JsonNode.Parse(reply.Text)!["structuredContent"]!["success"]!.GetValue<bool>());
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Same_session_reuses_connection_but_other_sessions_are_isolated()
    {
        Save();
        var clients = new List<FakeClient>();
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => { var client = new FakeClient(); clients.Add(client); return Task.FromResult<IMcpClient>(client); });
        await Invoke(set);
        await Invoke(set);
        Assert.Single(clients);
        await Invoke(set, scope: "two");
        Assert.Equal(2, clients.Count);
        set.StopSession("one");
        Assert.True(clients[0].Disposed);
        Assert.False(clients[1].Disposed);
        set.Pause();
        Assert.True(clients[1].Disposed);
    }

    [Fact]
    public async Task Config_change_reconnects_and_invalid_config_revokes_existing_client()
    {
        Save();
        var clients = new List<FakeClient>();
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => { var client = new FakeClient(); clients.Add(client); return Task.FromResult<IMcpClient>(client); });
        await Invoke(set);
        Save(HttpConfig.Replace("8080", "8081"));
        await Invoke(set);
        Assert.True(clients[0].Disposed);
        Assert.Equal(2, clients.Count);
        Save("{ broken");
        Assert.True((await Invoke(set)).IsError);
        Assert.True(clients[1].Disposed);
        Assert.Equal("{ broken", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task Transport_failure_is_never_replayed_and_does_not_echo_secrets()
    {
        Save();
        var fake = new FakeClient { Failure = new McpException("secret-auth-token") };
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        var reply = await Invoke(set, "unity.call_tool", new { name = "modify", arguments = new { } });
        Assert.True(reply.IsError);
        Assert.DoesNotContain("secret-auth-token", reply.Text);
        Assert.Contains("not retried", reply.Text);
        Assert.Equal(1, fake.Calls);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public async Task Pause_cancels_in_flight_call_and_disposes_transport_without_retry()
    {
        Save();
        var fake = new FakeClient { WaitForCancellation = true };
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        var pending = Invoke(set, "unity.call_tool", new { name = "wait", arguments = new { } });
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        set.Pause();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(fake.Disposed);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Precancelled_call_does_not_connect()
    {
        Save();
        int starts = 0;
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => { starts++; return Task.FromResult<IMcpClient>(new FakeClient()); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(set, ct: cancellation.Token));
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Resource_and_prompt_routes_are_available()
    {
        Save();
        var fake = new FakeClient();
        using var set = new UnityMcpToolSet(ConfigPath, (_, _) => Task.FromResult<IMcpClient>(fake));
        Assert.Contains("unity://editor/state", (await Invoke(set, "unity.list_resources")).Text);
        Assert.Equal("resource:unity://editor/state", (await Invoke(set, "unity.read_resource", new { uri = "unity://editor/state" })).Text);
        Assert.Contains("inspect", (await Invoke(set, "unity.list_prompts")).Text);
        Assert.Equal("prompt:inspect", (await Invoke(set, "unity.get_prompt", new { name = "inspect", arguments = new { } })).Text);
    }

    private sealed class FakeClient : IMcpClient
    {
        public string ServerName => "unityMCP";
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public string? LastName { get; private set; }
        public JsonObject? LastArguments { get; private set; }
        public McpCallResult Result { get; init; } = new("ok", false);
        public Exception? Failure { get; init; }
        public bool WaitForCancellation { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolDescriptor>>(
            Enumerable.Range(0, 3).Select(i => new McpToolDescriptor("tool_" + i, "test tool", new JsonObject { ["type"] = "object" })).ToArray());
        public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpResourceDescriptor>>(
            [new("unity://editor/state", "state", null, "application/json")]);
        public Task<string> ReadResourceAsync(string uri, CancellationToken ct) => Task.FromResult("resource:" + uri);
        public Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpPromptDescriptor>>([new("inspect", null, [])]);
        public Task<string> GetPromptAsync(string name, JsonObject args, CancellationToken ct) => Task.FromResult("prompt:" + name);
        public async Task<McpCallResult> CallToolAsync(string name, JsonObject args, CancellationToken ct, McpElicitationCallback? elicit = null)
        {
            Calls++;
            LastName = name;
            LastArguments = (JsonObject)args.DeepClone();
            Started.TrySetResult();
            if (Failure is not null) throw Failure;
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, ct);
            return Result;
        }
    }
}
