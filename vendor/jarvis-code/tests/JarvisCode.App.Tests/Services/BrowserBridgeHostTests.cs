using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class BrowserBridgeHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-browser-broker-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Client_forwards_to_existing_browser_without_becoming_a_native_connection()
    {
        var pipe = "jarvis-browser-fixture-" + Guid.NewGuid().ToString("N");
        using var desktop = new BrowserBridge(pipe);
        using var relay = new FakeBrowserRelay(pipe, (command, args) =>
            new JsonObject { ["command"] = command, ["value"] = args["value"]?.DeepClone() }, "Fixture Chrome");
        await Until(() => desktop.ExtensionReady);
        using var host = new BrowserBridgeHost(_root, desktop);
        using var client = BrowserBridge.ForDesktopProfile(_root);
        await client.RefreshDesktopConnectionAsync();
        var result = await client.RequestAsync("tabs", new JsonObject { ["value"] = "fixture" }, default);
        Assert.Equal("tabs", result["command"]!.GetValue<string>());
        Assert.Equal("fixture", result["value"]!.GetValue<string>());
        Assert.Single(desktop.Connections);
        Assert.Single(client.Connections);
    }

    [Fact]
    public async Task Each_client_selects_its_browser_without_changing_desktop_or_other_client()
    {
        var pipe = "jarvis-browser-fixture-" + Guid.NewGuid().ToString("N");
        using var desktop = new BrowserBridge(pipe);
        using var chrome = new FakeBrowserRelay(pipe, (_, _) => new JsonObject { ["browser"] = "Chrome" }, "Chrome");
        await Until(() => desktop.ExtensionReady);
        using var edge = new FakeBrowserRelay(pipe, (_, _) => new JsonObject { ["browser"] = "Edge" }, "Edge");
        await Until(() => desktop.Connections.Count(connection => connection.Ready) == 2);
        using var host = new BrowserBridgeHost(_root, desktop);
        using var first = BrowserBridge.ForDesktopProfile(_root);
        using var second = BrowserBridge.ForDesktopProfile(_root);
        await first.RefreshDesktopConnectionAsync();
        await second.RefreshDesktopConnectionAsync();
        Assert.Null(first.SelectBrowser("Edge"));
        Assert.Equal("Edge", (await first.RequestAsync("tabs", null, default))["browser"]!.GetValue<string>());
        Assert.Equal("Chrome", (await second.RequestAsync("tabs", null, default))["browser"]!.GetValue<string>());
        Assert.Equal("Chrome", Assert.Single(desktop.Connections, connection => connection.Active).Name);
    }

    [Fact]
    public async Task Other_profile_is_unavailable_and_host_exit_disconnects_existing_client()
    {
        var pipe = "jarvis-browser-fixture-" + Guid.NewGuid().ToString("N");
        using var desktop = new BrowserBridge(pipe);
        using var relay = new FakeBrowserRelay(pipe, (_, _) => new JsonObject(), "Fixture Chrome");
        await Until(() => desktop.ExtensionReady);
        var host = new BrowserBridgeHost(_root, desktop);
        using var client = BrowserBridge.ForDesktopProfile(_root);
        using var other = BrowserBridge.ForDesktopProfile(Path.Combine(_root, "other-profile"));
        try
        {
            await client.RefreshDesktopConnectionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.RefreshDesktopConnectionAsync());
            Assert.False(other.IsConnected);
        }
        finally { host.Dispose(); }
        await Until(() => !client.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync("tabs", null, default));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Profile\":null,\"Token\":null,\"Pipe\":null}")]
    public async Task Corrupt_registration_is_a_controlled_connection_error(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(BrowserBridgeHost.RegistrationPath(_root), json);
        using var client = BrowserBridge.ForDesktopProfile(_root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RefreshDesktopConnectionAsync());
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Browser_stop_targets_the_client_driving_the_page_and_preserves_other_sessions()
    {
        var pipe = "jarvis-browser-fixture-" + Guid.NewGuid().ToString("N");
        using var desktop = new BrowserBridge(pipe);
        using var relay = new FakeBrowserRelay(pipe, (_, _) => new JsonObject(), "Fixture Chrome");
        await Until(() => desktop.ExtensionReady);
        using var host = new BrowserBridgeHost(_root, desktop);
        using var owner = BrowserBridge.ForDesktopProfile(_root);
        using var observer = BrowserBridge.ForDesktopProfile(_root);
        await owner.RefreshDesktopConnectionAsync();
        await observer.RefreshDesktopConnectionAsync();
        int ownerStops = 0, observerStops = 0, desktopStops = 0;
        owner.StopRequested += () => Interlocked.Increment(ref ownerStops);
        observer.StopRequested += () => Interlocked.Increment(ref observerStops);
        desktop.StopRequested += () => Interlocked.Increment(ref desktopStops);
        await owner.RequestAsync("computer", new JsonObject { ["action"] = "screenshot" }, default);
        await observer.RequestAsync("tabs", null, default);
        await relay.SendEventAsync("stop_requested");
        await Until(() => Volatile.Read(ref ownerStops) == 1);
        await observer.RefreshDesktopConnectionAsync();
        Assert.Equal(0, observerStops);
        Assert.Equal(0, desktopStops);
        await desktop.RequestAsync("computer", new JsonObject { ["action"] = "screenshot" }, default);
        await relay.SendEventAsync("stop_requested");
        await Until(() => Volatile.Read(ref desktopStops) == 1);
        await owner.RefreshDesktopConnectionAsync();
        Assert.Equal(1, ownerStops);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
