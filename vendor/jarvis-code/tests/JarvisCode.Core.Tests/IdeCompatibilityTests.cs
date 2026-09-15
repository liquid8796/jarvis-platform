using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Ide;

namespace JarvisCode.Core.Tests;

public sealed class IdeCompatibilityTests
{
    [Theory]
    [InlineData("ws")]
    [InlineData("sse")]
    public async Task Reference_lock_and_transport_run_diagnostics_diff_and_close(string transport)
    {
        await using var server = new MockEditor(transport);
        await using var bridge = new IdeBridge(server.Registry);
        var editor = Assert.Single(bridge.Discover(server.Workspace));
        Assert.Equal(transport, editor.Transport);
        Assert.DoesNotContain(MockEditor.Token, System.Text.Json.JsonSerializer.Serialize(editor));
        await bridge.ConnectAsync(server.Workspace);
        var diagnostics = await bridge.CallAsync(server.Workspace, "getDiagnostics", new JsonObject());
        Assert.Equal("controlled diagnostic", diagnostics[0]!["diagnostics"]![0]!["message"]!.ToString());
        var diff = await bridge.CallAsync(server.Workspace, "openDiff", server.Diff("review"));
        Assert.Equal("FILE_SAVED", diff[0]!["text"]!.ToString());
        Assert.Equal("new café\n", diff[1]!["text"]!.GetValue<string>());
        var closed = await bridge.CallAsync(server.Workspace, "closeDiff", new JsonObject { ["tab_name"] = "review" });
        Assert.Equal("TAB_CLOSED", closed[0]!["text"]!.ToString());
        Assert.Single(server.Messages, message => message["method"]?.ToString() == "initialize");
        Assert.Contains(server.Messages, message => message["method"]?.ToString() == "notifications/initialized");
        Assert.Contains(server.Messages, message => message["method"]?.ToString() == "ide_connected");
        Assert.Contains(server.Messages, message => message["params"]?["name"]?.ToString() == "close_tab");
        Assert.True(server.TransportContractMatched);
    }

    [Fact]
    public async Task Websocket_selection_notifications_and_pending_diff_are_kept_across_calls()
    {
        await using var server = new MockEditor("ws");
        await using var bridge = new IdeBridge(server.Registry);
        await bridge.ConnectAsync(server.Workspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonNode selection;
        do
        {
            selection = await bridge.CallAsync(server.Workspace, "getSelection", new JsonObject(), timeout.Token);
            if (selection["text"]?.ToString() != "selected café") await Task.Delay(10, timeout.Token);
        } while (selection["text"]?.ToString() != "selected café");
        await server.ClearSelectionAsync();
        do
        {
            selection = await bridge.CallAsync(server.Workspace, "getSelection", new JsonObject(), timeout.Token);
            if (selection["text"]?.ToString() != "") await Task.Delay(10, timeout.Token);
        } while (selection["text"]?.ToString() != "");
        var arguments = server.Diff("delayed"); arguments["wait_for_decision"] = false;
        var pending = await bridge.CallAsync(server.Workspace, "openDiff", arguments, timeout.Token);
        Assert.True(pending["pending"]!.GetValue<bool>());
        Assert.Null(pending["opened"]);
        await server.DiffRequested.Task.WaitAsync(timeout.Token);
        await bridge.CallAsync(server.Workspace, "closeDiff", new JsonObject { ["tab_name"] = "delayed" }, timeout.Token);
        Assert.Single(server.Messages, message => message["method"]?.ToString() == "initialize");
    }

    [Fact]
    public async Task Reference_discovery_rejects_wrong_process_closed_listener_and_other_workspaces()
    {
        await using var server = new MockEditor("ws");
        await using var bridge = new IdeBridge(server.Registry);
        Assert.Empty(bridge.Discover(server.Workspace + "-other"));
        Assert.False(IdePortOwner.Matches(server.Port, Environment.ProcessId + 1));
        var record = JsonNode.Parse(await File.ReadAllTextAsync(server.LockFile))!.AsObject();
        record["pid"] = 0;
        await File.WriteAllTextAsync(server.LockFile, record.ToJsonString());
        Assert.Empty(bridge.Discover(server.Workspace));
        record["pid"] = Environment.ProcessId;
        await File.WriteAllTextAsync(server.LockFile, record.ToJsonString());
        Assert.Single(bridge.Discover(server.Workspace));
        server.StopListening();
        Assert.Empty(bridge.Discover(server.Workspace));
    }

    [Fact]
    public async Task Reference_adapter_refuses_unadvertised_read_only_diff_without_sending_it()
    {
        await using var server = new MockEditor("ws");
        await using var bridge = new IdeBridge(server.Registry);
        var args = server.Diff("review"); args["read_only"] = true;
        var error = await Assert.ThrowsAsync<IOException>(() => bridge.CallAsync(server.Workspace, "openDiff", args));
        Assert.Contains("read-only", error.Message);
        Assert.DoesNotContain(server.Messages, message => message["params"]?["name"]?.ToString() == "openDiff");
    }

    [Fact]
    public async Task Cancellation_reaches_the_editor_and_authentication_never_appears_in_errors()
    {
        await using var server = new MockEditor("ws");
        await using var bridge = new IdeBridge(server.Registry);
        await bridge.ConnectAsync(server.Workspace);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bridge.CallAsync(server.Workspace, "openDiff", server.Diff("delayed"), cancellation.Token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.Messages.Any(message => message["method"]?.ToString() == "notifications/cancelled")) await Task.Delay(10, timeout.Token);
        Assert.Contains(server.Messages, message => message["params"]?["name"]?.ToString() == "close_tab");
        var error = await Assert.ThrowsAsync<IOException>(() => bridge.CallAsync(server.Workspace, "getDiagnostics", new JsonObject { ["uri"] = "reflect-auth" }));
        Assert.DoesNotContain(MockEditor.Token, error.Message);
        Assert.Contains("[redacted]", error.Message);
    }

    [Fact]
    public async Task Sse_cannot_redirect_the_local_session_to_another_origin()
    {
        await using var server = new MockEditor("sse", foreignEndpoint: true);
        await using var bridge = new IdeBridge(server.Registry);
        var error = await Assert.ThrowsAsync<IOException>(() => bridge.ConnectAsync(server.Workspace));
        Assert.Contains("verified local origin", error.Message);
    }

    [Fact]
    public async Task Legacy_workspace_line_lock_uses_verified_local_sse_listener()
    {
        await using var server = new MockEditor("sse");
        await File.WriteAllTextAsync(server.LockFile, server.Workspace + "\r\n");
        await using var bridge = new IdeBridge(server.Registry);
        Assert.Equal(Environment.ProcessId, Assert.Single(bridge.Discover(server.Workspace)).ProcessId);
        var diagnostics = await bridge.CallAsync(server.Workspace, "getDiagnostics", new JsonObject());
        Assert.Equal("controlled diagnostic", diagnostics[0]!["diagnostics"]![0]!["message"]!.ToString());
    }

    [Fact]
    public async Task Reference_adapter_refuses_files_outside_the_editors_workspace()
    {
        await using var server = new MockEditor("ws");
        await using var bridge = new IdeBridge(server.Registry);
        var args = server.Diff("outside"); args["new_file_path"] = Path.Combine(Path.GetTempPath(), "outside-editor.cs");
        var error = await Assert.ThrowsAsync<IOException>(() => bridge.CallAsync(server.Workspace, "openDiff", args));
        Assert.Contains("outside", error.Message);
        Assert.DoesNotContain(server.Messages, message => message["params"]?["name"]?.ToString() == "openDiff");
    }

    [Fact]
    public async Task Dual_stack_editor_listener_is_verified_against_its_process()
    {
        await using var server = new MockEditor("ws", dualStack: true);
        await using var bridge = new IdeBridge(server.Registry);
        Assert.Single(bridge.Discover(server.Workspace));
        var diagnostics = await bridge.CallAsync(server.Workspace, "getDiagnostics", new JsonObject());
        Assert.Equal("controlled diagnostic", diagnostics[0]!["diagnostics"]![0]!["message"]!.ToString());
    }

    private sealed class MockEditor : IAsyncDisposable
    {
        internal const string Token = "controlled-editor-authentication-token";
        private readonly TempDirectory _temp = new();
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _sends = new(1);
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _accept;
        private readonly string _transport;
        private readonly bool _foreignEndpoint;
        private WebSocket? _socket;
        private Stream? _sse;
        private JsonNode? _pendingDiff;
        internal string Registry { get; }
        internal string Workspace { get; }
        internal string LockFile { get; }
        internal int Port { get; }
        internal bool TransportContractMatched { get; private set; }
        internal ConcurrentQueue<JsonObject> Messages { get; } = new();
        internal TaskCompletionSource DiffRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal MockEditor(string transport, bool foreignEndpoint = false, bool dualStack = false)
        {
            _transport = transport; _foreignEndpoint = foreignEndpoint;
            _listener = new TcpListener(dualStack ? IPAddress.IPv6Any : IPAddress.Loopback, 0);
            if (dualStack) _listener.Server.DualMode = true;
            Registry = Path.Combine(_temp.Path, "ide"); Workspace = Path.Combine(_temp.Path, "workspace");
            Directory.CreateDirectory(Registry); Directory.CreateDirectory(Workspace);
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            LockFile = Path.Combine(Registry, Port + ".lock");
            File.WriteAllText(LockFile, new JsonObject
            {
                ["pid"] = Environment.ProcessId, ["workspaceFolders"] = new JsonArray(Workspace), ["ideName"] = "Controlled IDE",
                ["transport"] = transport, ["runningInWindows"] = true, ["authToken"] = Token,
            }.ToJsonString());
            _accept = AcceptAsync();
        }

        internal JsonObject Diff(string tab) => new()
        {
            ["old_file_path"] = Path.Combine(Workspace, "source.cs"), ["new_file_path"] = Path.Combine(Workspace, "source.cs"),
            ["new_file_contents"] = "new café\n", ["tab_name"] = tab,
        };

        private async Task AcceptAsync()
        {
            try { while (!_lifetime.IsCancellationRequested) _connections.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_lifetime.Token))); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        private async Task HandleAsync(TcpClient connection)
        {
            using (connection)
            try
            {
                var stream = connection.GetStream();
                var header = new StringBuilder(); var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, _lifetime.Token) == 0) return;
                    header.Append((char)one[0]); if (header.Length > 65536) throw new IOException("Header too large.");
                }
                var lines = header.ToString().Split("\r\n");
                var fields = lines.Skip(1).Where(line => line.Contains(':')).Select(line => line.Split(':', 2))
                    .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                if (_transport == "ws")
                {
                    TransportContractMatched = fields.GetValueOrDefault("Sec-WebSocket-Protocol") == "mcp" &&
                        fields.GetValueOrDefault("X-Claude-Code-Ide-Authorization") == Token;
                    var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(fields["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Protocol: mcp\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), _lifetime.Token);
                    using var socket = WebSocket.CreateFromStream(stream, true, "mcp", TimeSpan.FromSeconds(30)); _socket = socket;
                    var buffer = new byte[65536];
                    while (!_lifetime.IsCancellationRequested)
                    {
                        using var body = new MemoryStream(); WebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                            if (received.MessageType == WebSocketMessageType.Close) return;
                            body.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        await MessageAsync(JsonNode.Parse(body.ToArray())!.AsObject());
                    }
                }
                else if (lines[0].StartsWith("GET ", StringComparison.Ordinal))
                {
                    TransportContractMatched = !fields.ContainsKey("X-Claude-Code-Ide-Authorization");
                    _sse = stream;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\nevent: endpoint\ndata: " +
                        (_foreignEndpoint ? "http://127.0.0.1:1/messages" : "/messages") + "\n\n"), _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                    await Task.Delay(Timeout.Infinite, _lifetime.Token);
                }
                else
                {
                    var body = new byte[int.Parse(fields["Content-Length"])]; await stream.ReadExactlyAsync(body, _lifetime.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                    await MessageAsync(JsonNode.Parse(body)!.AsObject());
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        }

        private async Task MessageAsync(JsonObject message)
        {
            Messages.Enqueue(message.DeepClone().AsObject());
            if (message["method"]?.ToString() == "ide_connected")
                await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "selection_changed", ["params"] = new JsonObject { ["text"] = "selected café", ["filePath"] = Path.Combine(Workspace, "source.cs") } });
            if (message["id"] is not { } id) return;
            JsonNode result = new JsonObject();
            switch (message["method"]?.ToString())
            {
                case "initialize": result = JsonNode.Parse("""{"protocolVersion":"2025-03-26","capabilities":{"tools":{}},"serverInfo":{"name":"controlled-editor","version":"1"}}""")!; break;
                case "tools/list": result = JsonNode.Parse("""{"tools":[{"name":"getDiagnostics","inputSchema":{"type":"object","properties":{"uri":{"type":"string"}},"additionalProperties":false}},{"name":"openDiff","inputSchema":{"type":"object","properties":{"old_file_path":{"type":"string"},"new_file_path":{"type":"string"},"new_file_contents":{"type":"string"},"tab_name":{"type":"string"}},"additionalProperties":false}},{"name":"close_tab","inputSchema":{"type":"object","properties":{"tab_name":{"type":"string"}},"additionalProperties":false}}]}""")!; break;
                case "tools/call":
                    var arguments = message["params"]!["arguments"]!;
                    switch (message["params"]!["name"]!.ToString())
                    {
                        case "getDiagnostics":
                            result = arguments["uri"]?.ToString() == "reflect-auth"
                                ? new JsonObject { ["isError"] = true, ["content"] = Text(Token) }
                                : new JsonObject { ["content"] = Text("[{\"diagnostics\":[{\"message\":\"controlled diagnostic\"}]}]") };
                            break;
                        case "openDiff":
                            if (arguments["tab_name"]?.ToString() == "delayed") { _pendingDiff = id.DeepClone(); DiffRequested.TrySetResult(); return; }
                            result = new JsonObject { ["content"] = Text("FILE_SAVED", arguments["new_file_contents"]!.GetValue<string>()) }; break;
                        case "close_tab":
                            if (_pendingDiff is { } pending) { _pendingDiff = null; await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = pending, ["result"] = new JsonObject { ["content"] = Text("DIFF_REJECTED") } }); }
                            result = new JsonObject { ["content"] = Text("TAB_CLOSED") }; break;
                    }
                    break;
            }
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        }

        private static JsonArray Text(params string[] texts) => new(texts.Select(text => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text }).ToArray());
        private async Task SendAsync(JsonObject message)
        {
            await _sends.WaitAsync(_lifetime.Token);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
                if (_socket is not null)
                {
                    await _socket.SendAsync(new ArraySegment<byte>(bytes, 0, bytes.Length / 2), WebSocketMessageType.Text, false, _lifetime.Token);
                    await _socket.SendAsync(new ArraySegment<byte>(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2), WebSocketMessageType.Text, true, _lifetime.Token);
                }
                else { await _sse!.WriteAsync(Encoding.UTF8.GetBytes("event: message\ndata: " + message.ToJsonString() + "\n\n"), _lifetime.Token); await _sse.FlushAsync(_lifetime.Token); }
            }
            finally { _sends.Release(); }
        }

        internal void StopListening() => _listener.Stop();
        internal Task ClearSelectionAsync() => SendAsync(new JsonObject
        { ["jsonrpc"] = "2.0", ["method"] = "selection_changed", ["params"] = new JsonObject { ["selection"] = null, ["text"] = "" } });
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync(); _listener.Stop(); await _accept;
            await Task.WhenAll(_connections.ToArray()); _lifetime.Dispose(); _temp.Dispose();
        }
    }
}
