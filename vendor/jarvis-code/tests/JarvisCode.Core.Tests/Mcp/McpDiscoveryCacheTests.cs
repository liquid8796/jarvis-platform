using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;

namespace JarvisCode.Core.Tests.Mcp;

/// <summary>
/// The reference's discovery-cache eligibility table and freshness ladder, and
/// the round trip through the store.
/// </summary>
public sealed class McpDiscoveryCacheTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static McpServerConfig Remote(
        string? headersHelper = null, bool? discoveryCache = null, string url = "https://mcp.example/v1") =>
        new("example", "", [], new Dictionary<string, string>())
        {
            Type = "http",
            Url = url,
            HeadersHelper = headersHelper,
            DiscoveryCache = discoveryCache,
        };

    [Fact]
    public void A_stdio_server_is_refused_by_transport()
    {
        var stdio = new McpServerConfig("local", "node", ["server.js"], new Dictionary<string, string>());

        Assert.Equal("transport", McpDiscoveryCache.IneligibleReason(stdio));
    }

    [Fact]
    public void The_key_is_an_opt_out_not_an_opt_in()
    {
        // The reference's table refuses on exactly `false`; null and true admit.
        Assert.Null(McpDiscoveryCache.IneligibleReason(Remote()));
        Assert.Null(McpDiscoveryCache.IneligibleReason(Remote(discoveryCache: true)));
        Assert.Equal("opt-out", McpDiscoveryCache.IneligibleReason(Remote(discoveryCache: false)));
    }

    [Fact]
    public void A_headers_helper_takes_the_cache_away()
    {
        Assert.Equal("headers-helper", McpDiscoveryCache.IneligibleReason(Remote(headersHelper: "mint-token")));
    }

    [Fact]
    public void An_unexpanded_placeholder_takes_it_away_too()
    {
        Assert.Equal("env-placeholder",
            McpDiscoveryCache.IneligibleReason(Remote(url: "https://${HOST}/v1")));
    }

    [Fact]
    public void Inside_the_ttl_is_fresh_and_past_the_stale_window_is_a_miss()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = new McpDiscoveryEntry([], [], []) { SavedAt = now };

        Assert.Equal(McpDiscoveryCacheStatus.Fresh, McpDiscoveryCache.Classify(entry, now).Status);
        Assert.Equal(
            McpDiscoveryCacheStatus.Stale,
            McpDiscoveryCache.Classify(entry, now + McpDiscoveryCache.TtlMs + 1).Status);
        var expired = McpDiscoveryCache.Classify(entry, now + McpDiscoveryCache.MaxStaleMs);
        Assert.Equal(McpDiscoveryCacheStatus.Miss, expired.Status);
        Assert.Equal("expired", expired.Reason);
    }

    [Fact]
    public void An_entry_stamped_far_in_the_future_is_refused()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var skewed = new McpDiscoveryEntry([], [], []) { SavedAt = now + McpDiscoveryCache.MaxStaleMs + 1 };

        Assert.Equal(McpDiscoveryCacheStatus.Miss, McpDiscoveryCache.Classify(skewed, now).Status);
    }

    [Fact]
    public void The_strike_threshold_retires_an_entry()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var struck = new McpDiscoveryEntry([], [], [])
        {
            SavedAt = now,
            ConsecutiveRefreshFailures = McpDiscoveryCache.Strikes,
        };

        var lookup = McpDiscoveryCache.Classify(struck, now);
        Assert.Equal(McpDiscoveryCacheStatus.Miss, lookup.Status);
        Assert.Equal("strike-threshold", lookup.Reason);
    }

    [Fact]
    public void A_written_listing_reads_back()
    {
        var cache = new McpDiscoveryCache(Path.Combine(_temp.Path, "discovery"));
        var config = Remote();
        var entry = new McpDiscoveryEntry(
            [
                new McpToolDescriptor("search", "Find things", new JsonObject { ["type"] = "object" })
                {
                    AlwaysLoad = true,
                    SearchHint = "lookup",
                    MaxResultSizeChars = 4096,
                },
            ],
            [new McpPromptDescriptor("brief", "A brief", [new McpPromptArgument("topic", null, true)])],
            [new McpResourceDescriptor("file://a", "a", null, "text/plain")]);

        cache.Write(config, entry);
        var lookup = cache.Read(config);

        Assert.Equal(McpDiscoveryCacheStatus.Fresh, lookup.Status);
        var tool = Assert.Single(lookup.Entry!.Tools);
        Assert.Equal("search", tool.Name);
        Assert.True(tool.AlwaysLoad);
        Assert.Equal("lookup", tool.SearchHint);
        Assert.Equal(4096, tool.MaxResultSizeChars);
        Assert.Equal("brief", Assert.Single(lookup.Entry.Prompts).Name);
        Assert.Equal("file://a", Assert.Single(lookup.Entry.Resources).Uri);
    }

    [Fact]
    public void A_changed_config_misses_rather_than_serving_the_old_listing()
    {
        var cache = new McpDiscoveryCache(Path.Combine(_temp.Path, "discovery"));
        cache.Write(Remote(), new McpDiscoveryEntry([], [], []));

        Assert.Equal(
            McpDiscoveryCacheStatus.Miss,
            cache.Read(Remote(url: "https://mcp.example/v2")).Status);
    }

    [Fact]
    public void An_ineligible_server_is_never_written_or_read()
    {
        var cache = new McpDiscoveryCache(Path.Combine(_temp.Path, "discovery"));
        var optedOut = Remote(discoveryCache: false);

        cache.Write(optedOut, new McpDiscoveryEntry([], [], []));
        var lookup = cache.Read(optedOut);

        Assert.Equal(McpDiscoveryCacheStatus.Miss, lookup.Status);
        Assert.Equal("opt-out", lookup.Reason);
    }
}
