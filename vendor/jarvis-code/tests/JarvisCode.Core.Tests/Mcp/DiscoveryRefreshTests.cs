using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;

namespace JarvisCode.Core.Tests.Mcp;

public sealed class DiscoveryRefreshTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jarvis-discovery-" + Guid.NewGuid().ToString("N"));
    private static McpServerConfig Config => new("fixture", "", [], new Dictionary<string, string>())
        { Type = "http", Url = "https://mcp.invalid/fixture" };
    private static McpToolDescriptor Tool(string name) => new(name, name, new JsonObject { ["type"] = "object" });

    [Fact]
    public async Task Hosted_clients_keep_full_inventory_across_config_refresh_and_can_be_removed()
    {
        Directory.CreateDirectory(_directory);
        var client = new DelayedClient();
        client.Tools.SetResult([Tool("hosted")]);
        await using var manager = new McpManager();
        await manager.RegisterHostedAsync(Config with { Type = "sdk", Url = null }, client, default);
        await manager.RefreshAsync(_directory, null, default);
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__hosted", StringComparison.Ordinal));
        Assert.Equal(1, manager.ConnectedToolCounts["fixture"]);
        Assert.Contains(manager.ServerInstructions, pair => pair.Instructions == "Hosted instructions");
        await manager.RemoveHostedAsync("fixture", default);
        Assert.Empty(manager.Tools);
        Assert.True(client.Disposed);
    }

    [Fact]
    public async Task Stale_inventory_is_usable_before_slow_refresh_and_is_replaced_afterwards()
    {
        var cache = Seed();
        var client = new DelayedClient();
        await using var manager = new McpManager(discoveryCache: cache, clientFactory: (_, _) => Task.FromResult<IMcpClient>(client));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.DiscoveryChanged += () => changed.TrySetResult();

        await manager.RefreshAsync(_directory, Path.Combine(_directory, "mcp.json"), default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__cached", StringComparison.Ordinal));
        Assert.False(client.Tools.Task.IsCompleted);
        client.Tools.SetResult([Tool("updated")]);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__updated", StringComparison.Ordinal));
        Assert.DoesNotContain(manager.Tools, tool => tool.Name.EndsWith("__cached", StringComparison.Ordinal));
        Assert.Equal("updated", Assert.Single(cache.Read(Config).Entry!.Tools).Name);
    }

    [Fact]
    public async Task Removing_a_server_cancels_refresh_and_cannot_resurrect_its_cache()
    {
        var cache = Seed();
        var client = new DelayedClient();
        await using var manager = new McpManager(discoveryCache: cache, clientFactory: (_, _) => Task.FromResult<IMcpClient>(client));
        var configFile = Path.Combine(_directory, "mcp.json");
        await manager.RefreshAsync(_directory, configFile, default);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        McpConfig.SaveUserServers(configFile, []);
        await manager.RefreshAsync(_directory, configFile, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(manager.Tools);
        Assert.True(client.Disposed);
        Assert.Equal(McpDiscoveryCacheStatus.Miss, cache.Read(Config).Status);
    }

    [Fact]
    public async Task Fresh_inventory_does_not_launch_refresh()
    {
        var cache = Seed(fresh: true);
        var client = new DelayedClient();
        await using var manager = new McpManager(discoveryCache: cache, clientFactory: (_, _) => Task.FromResult<IMcpClient>(client));
        await manager.RefreshAsync(_directory, Path.Combine(_directory, "mcp.json"), default);
        Assert.False(client.Started.Task.IsCompleted);
        Assert.Contains(manager.Tools, tool => tool.Name.EndsWith("__cached", StringComparison.Ordinal));
    }

    private McpDiscoveryCache Seed(bool fresh = false)
    {
        Directory.CreateDirectory(_directory);
        McpConfig.SaveUserServers(Path.Combine(_directory, "mcp.json"), [Config]);
        var cache = new McpDiscoveryCache(Path.Combine(_directory, "cache"));
        cache.Write(Config, new McpDiscoveryEntry([Tool("cached")], [], [])
        {
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (fresh ? 0 : McpDiscoveryCache.TtlMs + 10_000),
        });
        return cache;
    }

    private sealed class DelayedClient : IMcpClient
    {
        public string ServerName => "fixture";
        public string? Instructions => "Hosted instructions";
        public TaskCompletionSource<IReadOnlyList<McpToolDescriptor>> Tools { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return await Tools.Task.WaitAsync(cancellationToken);
        }
        public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<McpResourceDescriptor>>([]);
        public Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<McpPromptDescriptor>>([]);
        public Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> GetPromptAsync(string name, JsonObject arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<McpCallResult> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken, McpElicitationCallback? elicit = null) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
