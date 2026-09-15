using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.BackgroundTasks;

/// <summary>A monitor owns a background row and a lifetime independent of its arming call.</summary>
internal static class BackgroundMonitor
{
    internal const int DefaultTimeoutMs = 300_000;
    internal const int MaximumTimeoutMs = 3_600_000;
    private const int MaximumFrameBytes = 1_048_576;

    public static async Task<ToolResult> ArmAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken token)
    {
        if (context.BackgroundTasks is not { } manager) return ToolResult.Error("Background tasks are not available in this context.");
        if (context.MonitorEvent is null) return ToolResult.Error("This host has no monitor event channel; the watch was not armed.");
        var command = JsonArgs.GetString(arguments, "command");
        var ws = arguments["ws"] as JsonObject;
        var description = JsonArgs.GetString(arguments, "description");
        if ((string.IsNullOrWhiteSpace(command) ? 0 : 1) + (ws is null ? 0 : 1) != 1)
            return ToolResult.Error("exactly one of command or ws is required");
        if (string.IsNullOrWhiteSpace(description)) return ToolResult.Error("description is required");
        if (command?.Any(c => (char.IsControl(c) && c is not '\r' and not '\n' and not '\t') ||
            char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) == true)
            return ToolResult.Error("command contains control characters that would be hidden in the approval dialog");
        var persistent = JsonArgs.GetBool(arguments, "persistent");
        var timeout = JsonArgs.GetDouble(arguments, "timeout_ms") ?? DefaultTimeoutMs;
        if (!double.IsFinite(timeout) || timeout < 1000 || (!persistent && timeout > MaximumTimeoutMs))
            return ToolResult.Error($"timeout_ms must be between 1000 and {MaximumTimeoutMs} unless persistent is true");
        token.ThrowIfCancellationRequested();
        context.SessionLifetime.ThrowIfCancellationRequested();
        void Notice(string id, string output)
        {
            if (context.SessionLifetime.IsCancellationRequested) return;
            try { context.MonitorEvent(id, description, output.Length <= 16000 ? output : output[..16000] + " [truncated]"); }
            catch (Exception ex) { Utilities.DiagnosticLog.Write("monitor event delivery failed: " + ex.Message); }
        }

        if (command is not null)
        {
            var id = manager.Start(command, context.WorkingDirectory, context.SessionId, description,
                kind: "Monitor", onStdout: Notice);
            _ = ObserveShellAsync(manager, id, persistent ? null : TimeSpan.FromMilliseconds(timeout), Notice,
                token, context.SessionLifetime);
            return Armed(id, persistent, timeout);
        }
        var rawUrl = JsonArgs.GetString(ws!, "url");
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || url.Scheme is not ("ws" or "wss") ||
            url.UserInfo.Length > 0 || rawUrl!.Any(c => c > 127 || char.IsWhiteSpace(c)))
            return ToolResult.Error("url must be a valid ASCII ws:// or wss:// URL with no userinfo or whitespace");
        var protocols = ws!["protocols"] is JsonArray list ? list.Select(v => v?.GetValue<string>() ?? "").ToArray() : [];
        if (protocols.Distinct(StringComparer.Ordinal).Count() != protocols.Length ||
            protocols.Any(p => !Regex.IsMatch(p, "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
            return ToolResult.Error("protocols must be unique RFC 6455 tokens");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(url.DnsSafeHost, token); }
        catch (SocketException ex) { return ToolResult.Error("Monitor could not resolve the WebSocket host: " + ex.Message); }
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
            return ToolResult.Error("Monitor cannot open a WebSocket to a private, loopback, link-local, or cloud-metadata address.");
        // Pin the resolved endpoint for the handshake (including wss) so a second
        // DNS answer cannot change the destination after the policy check.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, context.SessionLifetime);
        var task = manager.Adopt(rawUrl!, context.SessionId, description, cancellation.Cancel, "Monitor");
        _ = RunSocketAsync(task, url, protocols, addresses, persistent ? null : TimeSpan.FromMilliseconds(timeout),
            cancellation, Notice);
        return Armed(task.Id, persistent, timeout);
    }

    private static ToolResult Armed(string id, bool persistent, double timeout) => ToolResult.Success(
        new JsonObject { ["taskId"] = id, ["timeoutMs"] = persistent ? 0 : timeout, ["persistent"] = persistent }.ToJsonString());

    private static async Task ObserveShellAsync(BackgroundTaskManager manager, string id, TimeSpan? timeout,
        Action<string, string> notice, CancellationToken turnToken, CancellationToken sessionToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(turnToken, sessionToken);
        var deadline = timeout is { } duration ? DateTimeOffset.UtcNow + duration : DateTimeOffset.MaxValue;
        try
        {
            while (manager.Get(id)?.Status == BackgroundTaskStatus.Running)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    notice(id, "[Monitor timed out — re-arm if needed.]");
                    manager.Kill(id);
                    return;
                }
                await Task.Delay(100, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { manager.Kill(id); }
        catch (Exception ex)
        {
            notice(id, "[Monitor stopped: " + ex.Message + "]");
            manager.Kill(id);
        }
    }

    internal static async Task RunSocketAsync(BackgroundTaskManager.AdoptedTask task, Uri url, string[] protocols,
        IPAddress[] addresses, TimeSpan? timeout, CancellationTokenSource cancellation, Action<string, string> notice)
    {
        using (cancellation)
        using (var socket = new ClientWebSocket())
        using (var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (connection, token) =>
            {
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var tcp = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await tcp.ConnectAsync(new IPEndPoint(address, connection.DnsEndPoint.Port), token);
                        return new NetworkStream(tcp, ownsSocket: true);
                    }
                    catch (Exception ex) { last = ex; tcp.Dispose(); }
                }
                throw new IOException("WebSocket connection failed.", last);
            },
        })
        using (var http = new HttpMessageInvoker(handler, disposeHandler: false))
        using (var deadline = timeout is { } limit ? new CancellationTokenSource(limit) : new CancellationTokenSource())
        using (var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, deadline.Token))
        {
            foreach (var protocol in protocols) socket.Options.AddSubProtocol(protocol);
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(combined.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(30));
                await socket.ConnectAsync(url, http, handshake.Token);
                var buffer = new byte[16 * 1024];
                while (!combined.IsCancellationRequested)
                {
                    using var frame = new MemoryStream();
                    ValueWebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer.AsMemory(), combined.Token);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            notice(task.Id, $"[WebSocket closed: {socket.CloseStatus}]");
                            task.Complete(0);
                            return;
                        }
                        if (frame.Length + received.Count > MaximumFrameBytes)
                            throw new IOException($"WebSocket frame exceeds {MaximumFrameBytes} bytes.");
                        frame.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    var text = received.MessageType == WebSocketMessageType.Binary
                        ? $"[binary frame, {frame.Length} bytes]" : Encoding.UTF8.GetString(frame.ToArray());
                    task.Append(text + "\n");
                    notice(task.Id, text);
                }
            }
            catch (OperationCanceledException)
            {
                if (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
                    notice(task.Id, "[Monitor timed out — re-arm if needed.]");
                task.Complete(null);
            }
            catch (Exception ex)
            {
                task.Append(ex.Message);
                notice(task.Id, "[WebSocket error: " + ex.Message + "]");
                task.Complete(1);
            }
        }
    }

    internal static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 ||
                (bytes[0] == 169 && bytes[1] == 254) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc;
    }
}
