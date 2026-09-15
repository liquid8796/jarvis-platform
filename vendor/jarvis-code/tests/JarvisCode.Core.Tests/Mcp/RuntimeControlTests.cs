using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Mcp;

public sealed class RuntimeControlTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string ConfigPath => Path.Combine(_temp.Path, "mcp.json");
    private static McpServerConfig Config(string type = "http") => new("fixture", type == "stdio" ? "controlled-fixture" : "", [], new Dictionary<string, string>())
        { Type = type, Url = type == "stdio" ? null : "https://mcp.invalid/controlled" };
    private static McpToolDescriptor Tool(string name) => new(name, name, new JsonObject { ["type"] = "object" });
    private void Save(params McpServerConfig[] configs) => McpConfig.SaveUserServers(ConfigPath, configs);

    [Theory]
    [InlineData("stdio")]
    [InlineData("http")]
    public async Task Disable_survives_refresh_and_reenable_keeps_config_files_unchanged(string type)
    {
        Save(Config(type));
        var original = await File.ReadAllBytesAsync(ConfigPath);
        var clients = new List<ControlledClient>();
        await using var manager = new McpManager(clientFactory: (config, _) =>
        {
            var client = new ControlledClient(config.Name, [Tool("tool" + clients.Count)]); clients.Add(client); return Task.FromResult<IMcpClient>(client);
        });
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        var oldTool = Assert.Single(manager.Tools);
        await manager.SetServerEnabledAsync("fixture", false, default);
        Assert.True(clients[0].Disposed);
        Assert.Empty(manager.Tools); Assert.Empty(manager.Resources); Assert.Empty(manager.Prompts); Assert.Empty(manager.ServerInstructions);
        Assert.Equal("disabled", Assert.Single(manager.GetServerStatuses()).Status);
        Assert.True((await oldTool.ExecuteAsync(new JsonObject(), new ToolExecutionContext { WorkingDirectory = _temp.Path }, default)).IsError);
        Assert.Equal(0, clients[0].CallCount);
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        Assert.Single(clients);
        await Assert.ThrowsAsync<McpException>(() => manager.ReconnectServerAsync("fixture", default));
        await manager.SetServerEnabledAsync("fixture", true, default);
        Assert.Equal(2, clients.Count);
        Assert.Equal("connected", Assert.Single(manager.GetServerStatuses()).Status);
        await manager.SetServerEnabledAsync("fixture", true, default);
        Assert.Equal(2, clients.Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(ConfigPath));
    }

    [Fact]
    public async Task Reconnect_evicts_fresh_cache_and_replaces_all_inventory()
    {
        Save(Config());
        var cache = new McpDiscoveryCache(Path.Combine(_temp.Path, "cache"));
        cache.Write(Config(), new McpDiscoveryEntry([Tool("cached")], [], []) { SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        var first = new ControlledClient("fixture", [Tool("old")]);
        var second = new ControlledClient("fixture", [Tool("fresh")]);
        var created = 0;
        await using var manager = new McpManager(discoveryCache: cache, clientFactory: (_, _) => Task.FromResult<IMcpClient>(created++ == 0 ? first : second));
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        Assert.Equal(0, first.ListCount);
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__cached", StringComparison.Ordinal));
        var changes = 0; manager.DiscoveryChanged += () => changes++;
        await manager.ReconnectServerAsync("fixture", default);
        Assert.True(first.Disposed); Assert.Equal(1, second.ListCount);
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__fresh", StringComparison.Ordinal));
        Assert.Equal("fresh", Assert.Single(cache.Read(Config()).Entry!.Tools).Name);
        Assert.Single(manager.Resources); Assert.Single(manager.Prompts);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Failed_configured_server_can_reconnect_and_partial_discovery_client_is_disposed()
    {
        Save(Config());
        var broken = new ControlledClient("fixture", []) { ListFailure = new McpException("controlled discovery failure") };
        var good = new ControlledClient("fixture", [Tool("good")]);
        var attempt = 0;
        await using var manager = new McpManager(clientFactory: (_, _) => Task.FromResult<IMcpClient>(attempt++ == 0 ? broken : good));
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        Assert.True(broken.Disposed); Assert.Empty(manager.Tools);
        Assert.Equal("failed", Assert.Single(manager.GetServerStatuses()).Status);
        await manager.ReconnectServerAsync("fixture", default);
        Assert.Empty(manager.LastFailures);
        Assert.Equal("connected", Assert.Single(manager.GetServerStatuses()).Status);
    }

    [Fact]
    public async Task Hosted_registration_survives_config_refresh_and_configured_controls_cannot_replace_it()
    {
        Save(Config());
        var hosted = new ControlledClient("fixture", [Tool("hosted")]);
        var factoryCalls = 0;
        await using var manager = new McpManager(clientFactory: (_, _) => { factoryCalls++; throw new InvalidOperationException("Must not replace SDK hosted client."); });
        await manager.RegisterHostedAsync(new McpServerConfig("fixture", "", [], new Dictionary<string, string>()) { Type = "sdk" }, hosted, default);
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        await Assert.ThrowsAsync<McpException>(() => manager.SetServerEnabledAsync("fixture", false, default));
        await Assert.ThrowsAsync<McpException>(() => manager.ReconnectServerAsync("fixture", default));
        await Assert.ThrowsAsync<McpException>(() => manager.SetServerEnabledAsync("missing", false, default));
        Assert.Equal(0, factoryCalls); Assert.False(hosted.Disposed);
        Assert.True(Assert.Single(manager.GetServerStatuses()).Hosted);
    }

    [Fact]
    public async Task Disable_cancels_stale_discovery_and_it_cannot_restore_evicted_cache()
    {
        Save(Config());
        var cache = new McpDiscoveryCache(Path.Combine(_temp.Path, "cache"));
        cache.Write(Config(), new McpDiscoveryEntry([Tool("cached")], [], []) { SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - McpDiscoveryCache.TtlMs - 1000 });
        var client = new ControlledClient("fixture", []) { SlowListing = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var manager = new McpManager(discoveryCache: cache, clientFactory: (_, _) => Task.FromResult<IMcpClient>(client));
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        await client.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.SetServerEnabledAsync("fixture", false, default).WaitAsync(TimeSpan.FromSeconds(3));
        client.SlowListing.SetResult([Tool("must-not-return")]);
        Assert.Empty(manager.Tools); Assert.True(client.Disposed);
        Assert.Equal(McpDiscoveryCacheStatus.Miss, cache.Read(Config()).Status);
    }

    [Fact]
    public async Task Disable_revokes_an_inflight_tool_even_when_inner_client_ignores_cancellation()
    {
        Save(Config());
        var client = new ControlledClient("fixture", [Tool("wait")]) { SlowCall = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var manager = new McpManager(clientFactory: (_, _) => Task.FromResult<IMcpClient>(client));
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        var call = Assert.Single(manager.Tools).ExecuteAsync(new JsonObject(), new ToolExecutionContext { WorkingDirectory = _temp.Path }, default);
        await client.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.SetServerEnabledAsync("fixture", false, default);
        var result = await call.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.IsError); Assert.Contains("disconnected", result.Content);
        client.SlowCall.TrySetResult(new McpCallResult("late", false));
    }

    [Fact]
    public async Task Reconnect_cancellation_disposes_new_client_and_records_failed_status()
    {
        Save(Config());
        var initial = new ControlledClient("fixture", [Tool("old")]);
        var stalled = new ControlledClient("fixture", []) { SlowListing = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var attempts = 0;
        await using var manager = new McpManager(clientFactory: (_, _) => Task.FromResult<IMcpClient>(attempts++ == 0 ? initial : stalled));
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        using var cancellation = new CancellationTokenSource();
        var reconnect = manager.ReconnectServerAsync("fixture", cancellation.Token);
        await stalled.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnect.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(initial.Disposed); Assert.True(stalled.Disposed); Assert.Empty(manager.Tools);
        Assert.Equal("failed", Assert.Single(manager.GetServerStatuses()).Status);
        stalled.SlowListing.TrySetResult([]);
    }

    [Fact]
    public async Task Actual_stdio_server_stops_reenables_and_reconnects_without_config_mutation()
    {
        var script = Path.Combine(_temp.Path, "controlled-mcp.cjs");
        await File.WriteAllTextAsync(script, """
            const readline = require('node:readline');
            const instance = require('node:crypto').randomUUID();
            const input = readline.createInterface({input:process.stdin});
            input.on('line', line => {
              const m = JSON.parse(line); if(m.id === undefined) return;
              let result = {};
              if(m.method === 'initialize') result={protocolVersion:'2024-11-05',capabilities:{tools:{}},serverInfo:{name:'controlled',version:'1'}};
              if(m.method === 'tools/list') result={tools:[{name:'instance',description:'Controlled fixture instance',inputSchema:{type:'object',properties:{}}}]};
              if(m.method === 'prompts/list') result={prompts:[]};
              if(m.method === 'resources/list') result={resources:[]};
              if(m.method === 'tools/call') result={content:[{type:'text',text:instance}]};
              process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:m.id,result})+'\n');
            });
            input.on('close', () => process.exit(0));
            """);
        Save(new McpServerConfig("fixture", "node", [script], new Dictionary<string, string>()));
        var before = await File.ReadAllBytesAsync(ConfigPath);
        await using var manager = new McpManager();
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        var context = new ToolExecutionContext { WorkingDirectory = _temp.Path };
        var first = await Assert.Single(manager.Tools).ExecuteAsync(new JsonObject(), context, default);
        Assert.False(first.IsError);
        await manager.SetServerEnabledAsync("fixture", false, default);
        Assert.Empty(manager.Tools);
        await manager.SetServerEnabledAsync("fixture", true, default);
        var second = await Assert.Single(manager.Tools).ExecuteAsync(new JsonObject(), context, default);
        Assert.False(second.IsError); Assert.NotEqual(first.Content, second.Content);
        await manager.ReconnectServerAsync("fixture", default);
        var third = await Assert.Single(manager.Tools).ExecuteAsync(new JsonObject(), context, default);
        Assert.False(third.IsError); Assert.NotEqual(second.Content, third.Content);
        Assert.Equal(before, await File.ReadAllBytesAsync(ConfigPath));
    }

    [Fact]
    public async Task Configured_http_client_reconnects_through_its_actual_transport()
    {
        Save(Config());
        using var handler = new ControlledHttpHandler();
        using var http = new HttpClient(handler);
        await using var manager = new McpManager(http: http);
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        Assert.Equal(1, handler.Initializations);
        var oldTool = Assert.Single(manager.Tools);
        await manager.SetServerEnabledAsync("fixture", false, default);
        var requests = handler.Requests;
        var denied = await oldTool.ExecuteAsync(new JsonObject(), new ToolExecutionContext { WorkingDirectory = _temp.Path }, default);
        Assert.True(denied.IsError); Assert.Equal(requests, handler.Requests);
        await manager.SetServerEnabledAsync("fixture", true, default);
        Assert.Equal(2, handler.Initializations);
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__http2", StringComparison.Ordinal));
        await manager.ReconnectServerAsync("fixture", default);
        Assert.Equal(3, handler.Initializations);
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__http3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Needs_auth_failure_becomes_disabled_then_connected_without_losing_configuration()
    {
        Save(Config());
        var attempts = 0;
        await using var manager = new McpManager(clientFactory: (_, _) =>
        {
            if (attempts++ == 0) throw new McpAuthRequiredException("controlled authentication requirement");
            return Task.FromResult<IMcpClient>(new ControlledClient("fixture", [Tool("ready")]));
        });
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        Assert.Equal("needs-auth", Assert.Single(manager.GetServerStatuses()).Status);
        await manager.SetServerEnabledAsync("fixture", false, default);
        Assert.Empty(manager.NeedsAuthServers);
        Assert.Equal("disabled", Assert.Single(manager.GetServerStatuses()).Status);
        await manager.SetServerEnabledAsync("fixture", true, default);
        Assert.Equal("connected", Assert.Single(manager.GetServerStatuses()).Status);
    }

    [Fact]
    public async Task Disabled_server_uses_latest_refresh_config_and_removed_servers_cannot_reconnect()
    {
        Save(Config());
        var seen = new List<string?>();
        await using var manager = new McpManager(clientFactory: (config, _) =>
        { seen.Add(config.Url); return Task.FromResult<IMcpClient>(new ControlledClient(config.Name, [Tool("ready")])); });
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        await manager.SetServerEnabledAsync("fixture", false, default);
        Save(Config() with { Url = "https://mcp.invalid/changed" });
        var configBytes = await File.ReadAllBytesAsync(ConfigPath);
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        Assert.Single(seen);
        await manager.SetServerEnabledAsync("fixture", true, default);
        Assert.Equal("https://mcp.invalid/changed", seen[1]);
        Assert.Equal(configBytes, await File.ReadAllBytesAsync(ConfigPath));
        Save();
        await manager.RefreshAsync(_temp.Path, ConfigPath, default);
        Assert.Empty(manager.GetServerStatuses());
        await Assert.ThrowsAsync<McpException>(() => manager.ReconnectServerAsync("fixture", default));
        Assert.Equal(2, seen.Count);
    }

    private sealed class ControlledHttpHandler : HttpMessageHandler
    {
        public int Initializations { get; private set; }
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var message = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            if (message["id"] is not { } id) return new HttpResponseMessage(HttpStatusCode.Accepted);
            JsonNode result = message["method"]?.ToString() switch
            {
                "initialize" => new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "controlled-http", ["version"] = (++Initializations).ToString() } },
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "http" + Initializations, ["inputSchema"] = new JsonObject { ["type"] = "object" } }) },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                _ => new JsonObject(),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ControlledClient(string name, IReadOnlyList<McpToolDescriptor> tools) : IMcpClient
    {
        public string ServerName => name;
        public string? Instructions => "Controlled fixture instructions";
        public bool Disposed { get; private set; }
        public int ListCount { get; private set; }
        public int CallCount { get; private set; }
        public Exception? ListFailure { get; init; }
        public TaskCompletionSource<IReadOnlyList<McpToolDescriptor>>? SlowListing { get; init; }
        public TaskCompletionSource<McpCallResult>? SlowCall { get; init; }
        public TaskCompletionSource ListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken token)
        { ListCount++; ListStarted.TrySetResult(); return ListFailure is { } error ? Task.FromException<IReadOnlyList<McpToolDescriptor>>(error) : SlowListing?.Task ?? Task.FromResult(tools); }
        public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<McpResourceDescriptor>>([new("fixture://data", "data", null, null)]);
        public Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<McpPromptDescriptor>>([new("prompt", null, [])]);
        public Task<string> ReadResourceAsync(string uri, CancellationToken token) => Task.FromResult("fixture resource");
        public Task<string> GetPromptAsync(string prompt, JsonObject arguments, CancellationToken token) => Task.FromResult("fixture prompt");
        public Task<McpCallResult> CallToolAsync(string tool, JsonObject arguments, CancellationToken token, McpElicitationCallback? elicit = null)
        { CallCount++; CallStarted.TrySetResult(); return SlowCall?.Task ?? Task.FromResult(new McpCallResult("controlled result", false)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public void Dispose() => _temp.Dispose();
}
