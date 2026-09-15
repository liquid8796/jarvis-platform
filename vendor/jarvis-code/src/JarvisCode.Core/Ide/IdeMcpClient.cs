using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Security;

namespace JarvisCode.Core.Ide;

/// <summary>Normalizes editor MCP results without changing the editor extension or its configuration.</summary>
internal sealed class IdeMcpClient : IAsyncDisposable
{
    private readonly IdeWebSocketRpc? _webSocket;
    private readonly McpHttpClient? _sse;
    private readonly HttpClient? _http;
    private readonly WorkspacePathScope _scope;
    private readonly string _workspace;
    private readonly ConcurrentDictionary<string, Task<JsonNode>> _diffs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyDictionary<string, JsonObject> _tools = new Dictionary<string, JsonObject>();
    private JsonNode _selection = new JsonObject { ["text"] = "", ["filePath"] = null };
    internal bool IsConnected => _webSocket?.IsConnected ?? _sse?.IsLegacyConnected ?? false;

    private IdeMcpClient(IdeEditor editor, IdeWebSocketRpc? socket, McpHttpClient? sse, HttpClient? http)
    {
        _webSocket = socket; _sse = sse; _http = http;
        _workspace = editor.WorkspaceFolders[0];
        _scope = WorkspacePathScope.FromRoots(editor.WorkspaceFolders);
    }

    public static async Task<IdeMcpClient> ConnectAsync(IdeEditor editor, string? authentication, CancellationToken token)
    {
        var endpoint = new Uri($"{(editor.Transport == "ws" ? "ws" : "http")}://127.0.0.1:{editor.Port}" + (editor.Transport == "ws" ? "/" : "/sse"));
        IdeMcpClient client;
        if (editor.Transport == "ws")
        {
            var socket = new IdeWebSocketRpc();
            client = new(editor, socket, null, null);
            socket.NotificationReceived = client.Notification;
            try { await socket.ConnectAsync(endpoint, authentication, token); }
            catch { await socket.DisposeAsync(); throw; }
        }
        else
        {
            // Legacy SSE announces a POST path. Keep every request on this verified local origin, with no proxies/redirects.
            var http = new HttpClient(new SameOriginHandler(endpoint)) { Timeout = Timeout.InfiniteTimeSpan };
            McpHttpClient sse;
            try
            {
                sse = await McpHttpClient.ConnectAsync(new McpServerConfig("ide", "", [], new Dictionary<string, string>())
                { Type = "sse", Url = endpoint.AbsoluteUri }, http, null, token);
            }
            catch { http.Dispose(); throw; }
            client = new(editor, null, sse, http);
            sse.NotificationReceived = client.Notification;
        }
        try
        {
            client._tools = client._webSocket is { } ws
                ? ((await ws.RequestAsync("tools/list", new JsonObject(), token))["tools"] as JsonArray ?? [])
                    .OfType<JsonObject>().Where(tool => tool["name"] is JsonValue)
                    .ToDictionary(tool => tool["name"]!.GetValue<string>(), tool => tool["inputSchema"]?.DeepClone().AsObject() ?? new JsonObject(), StringComparer.Ordinal)
                : (await client._sse!.ListToolsAsync(token)).ToDictionary(tool => tool.Name, tool => tool.InputSchema, StringComparer.Ordinal);
            if (client._webSocket is not null) await client._webSocket.NotifyAsync("ide_connected", new JsonObject { ["pid"] = Environment.ProcessId }, token);
            else await client._sse!.NotifyRawAsync("ide_connected", new JsonObject { ["pid"] = Environment.ProcessId }, token);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    private void Notification(string method, JsonNode? parameters)
    {
        if (method != "selection_changed") return;
        var current = parameters is JsonObject && parameters["filePath"]?.GetValue<string>() is { } file && AllowedPath(file)
            ? parameters.DeepClone() : new JsonObject { ["text"] = "", ["filePath"] = null };
        Interlocked.Exchange(ref _selection, current);
    }

    public async Task<JsonNode> CallAsync(string method, JsonObject arguments, CancellationToken token)
    {
        foreach (var name in new[] { "uri", "file_path", "old_file_path", "new_file_path" })
            if (arguments[name]?.GetValue<string>() is { Length: > 0 } path && !AllowedPath(path))
                throw new InvalidOperationException("The requested file is outside the connected editor's workspace.");
        if (method is "getSelection" or "getCurrentSelection")
        {
            if (_tools.ContainsKey("getCurrentSelection")) method = "getCurrentSelection";
            else if (_tools.ContainsKey("getSelection")) method = "getSelection";
            else return Volatile.Read(ref _selection).DeepClone();
        }
        if (method == "closeDiff" && !_tools.ContainsKey(method)) method = "close_tab";
        if (method == "closeAllDiffTabs" && !_tools.ContainsKey(method))
        {
            var count = 0;
            foreach (var tab in _diffs.Keys)
            { await CallAsync("close_tab", new JsonObject { ["tab_name"] = tab }, token); count++; }
            return new JsonObject { ["closed"] = count, ["scope"] = "This connection's pending diff requests" };
        }
        if (!_tools.TryGetValue(method, out var schema)) throw new InvalidOperationException($"The connected editor does not provide {method}.");
        var supplied = arguments.DeepClone().AsObject();
        if (method == "openDiff")
        {
            // A historical comparison must never become an editable proposal merely because the other editor lacks this option.
            if (supplied["read_only"]?.GetValue<bool>() == true && schema["properties"]?["read_only"] is null)
                throw new InvalidOperationException("This editor does not advertise read-only diff support. Use the Jarvis IDE bridge for historical comparisons.");
            supplied.Remove("wait_for_decision");
        }
        if (schema["properties"] is JsonObject properties && schema["additionalProperties"]?.GetValue<bool>() == false)
            foreach (var field in supplied.Select(pair => pair.Key).ToArray())
                if (!properties.ContainsKey(field)) supplied.Remove(field);
        if (method == "openDiff" && arguments["wait_for_decision"]?.GetValue<bool>() == false)
        {
            var tab = arguments["tab_name"]?.GetValue<string>() ?? throw new ArgumentException("tab_name is required for a pending IDE diff.");
            if (_diffs.ContainsKey(tab)) throw new InvalidOperationException("A diff with this tab name is already pending.");
            var request = CallRawAsync(method, supplied, _lifetime.Token);
            _diffs[tab] = request;
            _ = ObserveDiffAsync(tab, request);
            // The reference openDiff response arrives only after user action. No fabricated 'opened' acknowledgment.
            return new JsonObject { ["pending"] = true, ["tab_name"] = tab, ["message"] = "Diff request queued; editor decision pending." };
        }
        try { return Normalize(await CallRawAsync(method, supplied, token)); }
        catch (OperationCanceledException) when (method == "openDiff" && token.IsCancellationRequested && arguments["tab_name"]?.GetValue<string>() is { } tab)
        {
            if (_tools.ContainsKey("close_tab"))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await CallRawAsync("close_tab", new JsonObject { ["tab_name"] = tab }, cleanup.Token); }
                catch (Exception) { /* Cancellation still returns to its caller even if the editor cannot close its tab. */ }
            }
            throw;
        }
    }

    private bool AllowedPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return false;
            value = uri.LocalPath;
        }
        return _scope.ValidatePath(value, _workspace).Allowed;
    }

    private async Task ObserveDiffAsync(string tab, Task<JsonNode> request)
    {
        try { await request; }
        catch (Exception) { /* Observed; a later explicit operation reconnects if the editor has gone. */ }
        finally { _diffs.TryRemove(new KeyValuePair<string, Task<JsonNode>>(tab, request)); }
    }

    private async Task<JsonNode> CallRawAsync(string method, JsonObject arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var budget = method == "openDiff" ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30);
        timeout.CancelAfter(budget);
        return _webSocket is { } ws
            ? await ws.RequestAsync("tools/call", new JsonObject { ["name"] = method, ["arguments"] = arguments }, timeout.Token)
            : await _sse!.CallToolRawAsync(method, arguments, timeout.Token, budget);
    }

    internal static JsonNode Normalize(JsonNode result)
    {
        if (result["isError"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("Editor operation failed: " + string.Join("\n", (result["content"] as JsonArray ?? []).Select(block => block?["text"]?.ToString())));
        if (result["structuredContent"] is { } structured) return structured.DeepClone();
        if (result["content"] is not JsonArray content) return result.DeepClone();
        if (content.Count == 1 && content[0]?["type"]?.ToString() == "text" && content[0]?["text"]?.GetValue<string>() is { } text)
        {
            try { if (JsonNode.Parse(text) is { } parsed) return parsed; }
            catch (JsonException) { }
        }
        return content.DeepClone();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_webSocket is not null) await _webSocket.DisposeAsync();
        if (_sse is not null) await _sse.DisposeAsync();
        _http?.Dispose();
        _lifetime.Dispose();
    }

    private sealed class SameOriginHandler(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { IsLoopback: true } uri || uri.Scheme != endpoint.Scheme || uri.Host != endpoint.Host || uri.Port != endpoint.Port)
                throw new HttpRequestException("The IDE SSE endpoint left its verified local origin.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
