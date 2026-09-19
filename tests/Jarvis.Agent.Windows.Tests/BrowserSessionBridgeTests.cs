using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class BrowserSessionBridgeTests
{
    [Fact]
    public async Task Structured_qa_requires_an_explicit_extension_capability_before_dispatch()
    {
        var pipe = "jarvis-qa-capability-" + Guid.NewGuid().ToString("N");
        using var bridge = new BrowserBridge(pipe);
        await using var browser = await FakeBrowser.Connect(pipe, "Old Browser");
        await Ready(bridge, 1);
        using var session = bridge.EnterApplicationSession(AgentSessionRules.NewSessionId());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.RequestAsync("qa", new JsonObject(), CancellationToken.None));
        Assert.Contains("structured-qa-v1", error.Message);
        Assert.Equal(0, browser.RequestCount);
    }
    [Fact]
    public async Task Browser_selection_and_native_envelopes_are_scoped_per_application_session()
    {
        var pipe = "jarvis-session-test-" + Guid.NewGuid().ToString("N");
        using var bridge = new BrowserBridge(pipe);
        await using var first = await FakeBrowser.Connect(pipe, "Browser One");
        await using var second = await FakeBrowser.Connect(pipe, "Browser Two");
        await Ready(bridge, 2);
        var a = AgentSessionRules.NewSessionId(); var b = AgentSessionRules.NewSessionId();
        async Task<string> Invoke(string session, string name)
        {
            using var scope = bridge.EnterApplicationSession(session);
            Assert.Null(bridge.SelectBrowser(name));
            await Task.Yield();
            var result = await bridge.RequestAsync("ping", new JsonObject(), CancellationToken.None);
            Assert.Equal(session, result["sessionId"]!.GetValue<string>());
            Assert.Equal(name, bridge.Connections.Single(c => c.Active).Name);
            return result["browser"]!.GetValue<string>();
        }
        var results = await Task.WhenAll(Invoke(a, "Browser One"), Invoke(b, "Browser Two"));
        Assert.Equal(new[] { "Browser One", "Browser Two" }, results);
        using (bridge.EnterApplicationSession(a))
            Assert.Equal("Browser One", bridge.Connections.Single(c => c.Active).Name);
        using (bridge.EnterApplicationSession(b))
            Assert.Equal("Browser Two", bridge.Connections.Single(c => c.Active).Name);
    }

    [Fact]
    public async Task Old_extension_is_rejected_instead_of_silently_sharing_its_tab_group()
    {
        var pipe = "jarvis-session-test-" + Guid.NewGuid().ToString("N");
        using var bridge = new BrowserBridge(pipe);
        await using var browser = await FakeBrowser.Connect(pipe, "Legacy", sessionAware: false);
        await Ready(bridge, 1);
        using var scope = bridge.EnterApplicationSession(AgentSessionRules.NewSessionId());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            bridge.RequestAsync("create_tab", new JsonObject(), CancellationToken.None));
        Assert.Contains("extension", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, browser.RequestCount);
    }

    [Fact]
    public async Task Nested_scope_restores_parent_and_does_not_leak_into_legacy_calls()
    {
        var pipe = "jarvis-session-test-" + Guid.NewGuid().ToString("N");
        using var bridge = new BrowserBridge(pipe);
        await using var browser = await FakeBrowser.Connect(pipe, "One"); await Ready(bridge, 1);
        var a = AgentSessionRules.NewSessionId(); var b = AgentSessionRules.NewSessionId();
        async Task<string?> Identity() => (await bridge.RequestAsync("ping", null, default))["sessionId"]?.GetValue<string>();
        using (bridge.EnterApplicationSession(a))
        {
            using (bridge.EnterApplicationSession(b)) Assert.Equal(b, await Identity());
            Assert.Equal(a, await Identity());
        }
        Assert.Null(await Identity());
    }

    private static async Task Ready(BrowserBridge bridge, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (bridge.Connections.Count(c => c.Ready) != count) await Task.Delay(20, timeout.Token);
    }
    private sealed class FakeBrowser : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Task _loop;
        public int RequestCount;
        private FakeBrowser(NamedPipeClientStream pipe, string name, bool sessionAware)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            _loop = Task.Run(async () =>
            {
                try
                {
                    await _writer.WriteLineAsync(new JsonObject { ["event"] = "ready", ["browser"] = name,
                        ["browserFamily"] = "extension", ["browserProtocolVersion"] = 2, ["nativeHostProtocolVersion"] = 2,
                        ["capabilities"] = new JsonArray("application-sessions-v1", "tab-ownership-v1"),
                        ["extensionInstanceId"] = Guid.NewGuid().ToString("N"), ["applicationSessions"] = sessionAware }.ToJsonString());
                    while (await _reader.ReadLineAsync() is { } line)
                    {
                        var request = JsonNode.Parse(line)!.AsObject(); Interlocked.Increment(ref RequestCount);
                        await _writer.WriteLineAsync(new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = true,
                            ["data"] = new JsonObject { ["browser"] = name, ["sessionId"] = request["sessionId"]?.DeepClone() } }.ToJsonString());
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            });
        }
        public static async Task<FakeBrowser> Connect(string pipe, string name, bool sessionAware = true)
        {
            var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await stream.ConnectAsync(5000); return new(stream, name, sessionAware); }
            catch { stream.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            _pipe.Dispose();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            _reader.Dispose();
            try { _writer.Dispose(); } catch (ObjectDisposedException) { }
        }
    }
}
