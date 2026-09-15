using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.BackgroundTasks;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests;

public sealed class MonitorLifecycleTests
{
    [Fact]
    public async Task Monitor_returns_before_exit_delivers_events_and_stops_with_session()
    {
        using var manager = new BackgroundTaskManager();
        using var session = new CancellationTokenSource();
        var events = new ConcurrentQueue<string>();
        var context = new ToolExecutionContext
        {
            WorkingDirectory = Path.GetTempPath(), BackgroundTasks = manager, SessionLifetime = session.Token,
            MonitorEvent = (_, _, line) => events.Enqueue(line), SessionId = "monitor-test",
        };
        var tool = new MonitorTool();
        var result = await tool.ExecuteAsync(new JsonObject
        {
            ["command"] = "Write-Output 'watch-started'; Start-Sleep -Seconds 30",
            ["description"] = "Watch test", ["persistent"] = true,
        }, context, default);
        Assert.False(result.IsError, result.Content);
        var id = JsonNode.Parse(result.Content)!["taskId"]!.GetValue<string>();
        Assert.Equal(BackgroundTaskStatus.Running, manager.Get(id)!.Status);
        Assert.Equal("Monitor", manager.Get(id)!.Kind);
        for (var i = 0; i < 100 && events.IsEmpty; i++) await Task.Delay(50);
        Assert.Contains("watch-started", events);
        session.Cancel();
        for (var i = 0; i < 100 && manager.Get(id)!.Status == BackgroundTaskStatus.Running; i++) await Task.Delay(25);
        Assert.Equal(BackgroundTaskStatus.Killed, manager.Get(id)!.Status);
    }

    [Fact]
    public async Task Deadline_ends_the_monitor_with_one_timeout_event()
    {
        using var manager = new BackgroundTaskManager();
        var events = new ConcurrentQueue<string>();
        var result = await new MonitorTool().ExecuteAsync(new JsonObject
        {
            ["command"] = "Start-Sleep -Seconds 30", ["description"] = "Timeout test", ["timeout_ms"] = 1000,
        }, new ToolExecutionContext
        {
            WorkingDirectory = Path.GetTempPath(), BackgroundTasks = manager,
            MonitorEvent = (_, _, line) => events.Enqueue(line),
        }, default);
        Assert.False(result.IsError, result.Content);
        var id = JsonNode.Parse(result.Content)!["taskId"]!.GetValue<string>();
        for (var i = 0; i < 120 && manager.Get(id)!.Status == BackgroundTaskStatus.Running; i++) await Task.Delay(25);
        Assert.Equal(BackgroundTaskStatus.Killed, manager.Get(id)!.Status);
        Assert.Single(events, line => line.Contains("timed out"));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("8.8.8.8", false)]
    public void Websocket_policy_classifies_resolved_addresses(string address, bool blocked)
        => Assert.Equal(blocked, BackgroundMonitor.IsPrivateAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task Websocket_transport_delivers_real_text_and_binary_frames_then_settles_on_close()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("Handshake ended early.");
                request.Append((char)one[0]);
                Assert.True(request.Length < 16000);
            }
            var key = request.ToString().Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
            await socket.SendAsync(Encoding.UTF8.GetBytes("live-frame"), WebSocketMessageType.Text, true, timeout.Token);
            await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, timeout.Token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        }, timeout.Token);
        using var manager = new BackgroundTaskManager();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var task = manager.Adopt("websocket-test", "test", "Live frame", cancellation.Cancel, "Monitor");
        List<string> events = [];
        // Exercise only the transport using a local server; ArmAsync's public
        // destination policy remains enabled and is checked separately above.
        await BackgroundMonitor.RunSocketAsync(task, new Uri($"ws://127.0.0.1:{port}/"), [], [IPAddress.Loopback],
            TimeSpan.FromSeconds(5), cancellation, (_, line) => events.Add(line));
        await server;
        Assert.Contains("live-frame", events);
        Assert.Contains("[binary frame, 3 bytes]", events);
        Assert.Equal(BackgroundTaskStatus.Completed, manager.Get(task.Id)!.Status);
        Assert.Equal(0, manager.Get(task.Id)!.ExitCode);
    }
}
