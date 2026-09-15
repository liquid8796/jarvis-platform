using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// MCP over HTTP: the streamable-HTTP transport (POST JSON-RPC; the response is
/// either a JSON body or an SSE stream carrying the response), falling back to
/// the legacy HTTP+SSE transport (a standing GET stream announcing a POST
/// endpoint) when the server rejects streamable POSTs — plus Bearer tokens from
/// <see cref="McpTokenStore"/> with one silent refresh on 401.
/// Server→client requests are answered inline: elicitation/create goes to the
/// call's <see cref="McpElicitationCallback"/> (declined without one), ping is
/// ponged, and everything else is method-not-found.
/// </summary>
public sealed class McpHttpClient : IMcpClient
{
    private const string ProtocolVersion = "2025-03-26";
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(300);

    private readonly HttpClient _http;
    private readonly McpServerConfig _config;
    private readonly McpTokenStore? _tokens;
    /// <summary>
    /// Headers a <c>headersHelper</c> minted for this server, overlaying the
    /// entry's static ones — the reference's <c>Sat</c>, which spreads the
    /// helper's output over the configured headers. Empty when there is none.
    /// </summary>
    private readonly IReadOnlyDictionary<string, string> _helperHeaders;
    private readonly object _lock = new();
    private long _nextId;
    private string? _sessionId;

    // Legacy HTTP+SSE state: a standing GET stream plus the announced POST endpoint.
    private bool _legacy;
    private Uri? _legacyEndpoint;
    private Task? _legacyReadLoop;
    private CancellationTokenSource? _legacyCts;
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _legacyPending = [];

    /// <summary>The elicitation callback of the call in flight, if any.</summary>
    private McpElicitationCallback? _activeElicit;
    internal Action<string, JsonNode?>? NotificationReceived { get; set; }
    internal bool IsLegacyConnected => _legacy && _legacyReadLoop?.IsCompleted == false;

    public string ServerName => _config.Name;

    /// <summary>The server's <c>instructions</c> from the initialize handshake.</summary>
    public string? Instructions { get; private set; }

    public string ServerType => _config.Type;

    private McpHttpClient(
        McpServerConfig config,
        HttpClient http,
        McpTokenStore? tokens,
        IReadOnlyDictionary<string, string>? helperHeaders)
    {
        _config = config;
        _http = http;
        _tokens = tokens;
        _helperHeaders = helperHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<McpHttpClient> ConnectAsync(
        McpServerConfig config,
        HttpClient http,
        McpTokenStore? tokens,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? helperHeaders = null)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || !Uri.TryCreate(config.Url, UriKind.Absolute, out _))
            throw new McpException($"MCP server '{config.Name}': '{config.Url}' is not a valid URL.");

        var client = new McpHttpClient(config, http, tokens, helperHeaders);
        try
        {
            if (string.Equals(config.Type, "sse", StringComparison.OrdinalIgnoreCase))
                await client.StartLegacyAsync(cancellationToken);
            var handshake = await client.SendRequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject { ["elicitation"] = new JsonObject() },
                ["clientInfo"] = new JsonObject { ["name"] = "JarvisCode", ["version"] = "1.0" },
            }, DefaultRequestTimeout, cancellationToken);
            client.Instructions = Tools.JsonArgs.GetString(handshake as JsonObject ?? [], "instructions");
            await client.SendNotificationAsync("notifications/initialized", cancellationToken);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        return client;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("tools/list", new JsonObject(), DefaultRequestTimeout, cancellationToken);
        return McpResultParsing.ParseTools(result, ServerName);
    }

    public async Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendRequestAsync("resources/list", new JsonObject(), DefaultRequestTimeout, cancellationToken);
            return McpResultParsing.ParseResources(result);
        }
        catch (McpException ex) when (ex is not McpAuthRequiredException)
        {
            return [];
        }
    }

    public async Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("resources/read", new JsonObject { ["uri"] = uri },
            DefaultRequestTimeout, cancellationToken);
        return McpResultParsing.ParseResourceText(result);
    }

    public async Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendRequestAsync("prompts/list", new JsonObject(), DefaultRequestTimeout, cancellationToken);
            return McpResultParsing.ParsePrompts(result);
        }
        catch (McpException ex) when (ex is not McpAuthRequiredException)
        {
            return [];
        }
    }

    public async Task<string> GetPromptAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("prompts/get", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.DeepClone(),
        }, DefaultRequestTimeout, cancellationToken);
        return McpResultParsing.ParsePromptText(result);
    }

    public async Task<McpCallResult> CallToolAsync(
        string toolName, JsonObject arguments, CancellationToken cancellationToken,
        McpElicitationCallback? elicit = null)
    {
        _activeElicit = elicit;
        try
        {
            var result = await SendRequestAsync("tools/call", new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments.DeepClone(),
            }, ToolCallTimeout, cancellationToken);
            return McpResultParsing.ParseCallResult(result);
        }
        finally
        {
            _activeElicit = null;
        }
    }

    // The local IDE adapter must preserve FILE_SAVED plus exact edited text, including trailing newlines.
    internal Task<JsonNode> CallToolRawAsync(string toolName, JsonObject arguments, CancellationToken token, TimeSpan timeout) =>
        SendRequestAsync("tools/call", new JsonObject { ["name"] = toolName, ["arguments"] = arguments.DeepClone() }, timeout, token);

    internal Task NotifyRawAsync(string method, JsonObject parameters, CancellationToken token) =>
        PostFireAndForgetAsync(_legacyEndpoint ?? new Uri(_config.Url!),
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters.DeepClone() }, token);

    // ---- streamable HTTP ----

    private async Task<JsonNode> SendRequestAsync(
        string method, JsonObject parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long id;
        lock (_lock)
        {
            id = ++_nextId;
        }

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            if (_legacy)
                return await SendLegacyRequestAsync(id, message, timeoutSource.Token);
            return await SendStreamableRequestAsync(id, message, isInitialize: method == "initialize", timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"MCP server '{ServerName}': {method} timed out after {timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"MCP server '{ServerName}': {ex.Message}", ex);
        }
    }

    private async Task<JsonNode> SendStreamableRequestAsync(
        long id, JsonObject message, bool isInitialize, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(message, cancellationToken);
        if (isInitialize &&
            (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound ||
             (int)response.StatusCode == 400 && !_legacy))
        {
            // The endpoint doesn't speak streamable HTTP — fall back to legacy SSE.
            response.Dispose();
            await StartLegacyAsync(cancellationToken);
            return await SendLegacyRequestAsync(id, message, cancellationToken);
        }

        await ThrowOnAuthOrFailureAsync(response);

        if (isInitialize && response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            _sessionId = values.FirstOrDefault();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await foreach (var data in SseEvents(reader, cancellationToken))
            {
                if (JsonNode.Parse(data) is not JsonObject parsed)
                    continue;
                if (await DispatchAsync(parsed, id, cancellationToken) is { } result)
                    return result;
            }

            throw new McpException($"MCP server '{ServerName}': the stream ended before the response arrived.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (JsonNode.Parse(body) is JsonObject single)
            return UnwrapResponse(single);
        if (JsonNode.Parse(body) is JsonArray batch &&
            batch.OfType<JsonObject>().FirstOrDefault(m => MatchesId(m, id)) is { } match)
            return UnwrapResponse(match);
        throw new McpException($"MCP server '{ServerName}': unrecognized response body.");
    }

    private async Task<HttpResponseMessage> PostAsync(JsonObject message, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _config.Url)
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ApplyHeaders(request);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 &&
                _tokens is not null && await _tokens.TryRefreshAsync(_config, _http, cancellationToken))
            {
                response.Dispose();
                continue; // one silent refresh, then the fresh token rides the retry
            }

            return response;
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        foreach (var (key, value) in _config.Headers)
        {
            if (!_helperHeaders.ContainsKey(key))
                request.Headers.TryAddWithoutValidation(key, value);
        }
        foreach (var (key, value) in _helperHeaders)
            request.Headers.TryAddWithoutValidation(key, value);
        if (_sessionId is { Length: > 0 })
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (_tokens?.GetAccessToken(_config) is { Length: > 0 } token &&
            !_config.Headers.ContainsKey("Authorization") && !_helperHeaders.ContainsKey("Authorization"))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task ThrowOnAuthOrFailureAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new McpAuthRequiredException(
                $"MCP server '{ServerName}' requires authentication — run /mcp-auth {ServerName} to sign in.",
                response.Headers.WwwAuthenticate.ToString());
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new McpException(
                $"MCP server '{ServerName}': HTTP {(int)response.StatusCode}" +
                (body.Length > 0 ? $" — {(body.Length > 300 ? body[..300] : body)}" : "."));
        }
    }

    /// <summary>Handles one SSE-delivered message; returns the result once the awaited response shows up.</summary>
    private async Task<JsonNode?> DispatchAsync(JsonObject parsed, long awaitedId, CancellationToken cancellationToken)
    {
        if (parsed.ContainsKey("method"))
        {
            if (parsed["id"] is not null)
                await RespondToServerRequestAsync(parsed, cancellationToken);
            else if (NotificationReceived is { } receive && parsed["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out var method))
                receive(method, parsed["params"]?.DeepClone());
            return null; // notifications are ignored
        }

        return MatchesId(parsed, awaitedId) ? UnwrapResponse(parsed) : null;
    }

    private JsonNode UnwrapResponse(JsonObject response)
    {
        if (response["error"] is JsonObject error)
            throw new McpException(
                $"MCP server '{ServerName}': {Tools.JsonArgs.GetString(error, "message") ?? "request failed"}");
        return response["result"] ?? new JsonObject();
    }

    private static bool MatchesId(JsonObject message, long id) =>
        message["id"] is JsonValue value && value.TryGetValue(out long parsed) && parsed == id &&
        !message.ContainsKey("method");

    /// <summary>elicitation/create runs the active call's callback; ping pongs; the rest refuse.</summary>
    private async Task RespondToServerRequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var id = request["id"]!.DeepClone();
        var method = Tools.JsonArgs.GetString(request, "method");
        JsonObject reply;
        if (method == "ping")
        {
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject() };
        }
        else if (method == "elicitation/create" && _activeElicit is { } elicit)
        {
            JsonObject result;
            try
            {
                result = await elicit(
                    Tools.JsonArgs.GetString(request["params"] as JsonObject ?? [], "message") ?? "",
                    (request["params"] as JsonObject)?["requestedSchema"] as JsonObject,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new JsonObject { ["action"] = "cancel" };
            }

            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        else if (method == "elicitation/create")
        {
            reply = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject { ["action"] = "decline" },
            };
        }
        else
        {
            reply = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported" },
            };
        }

        var target = _legacy ? _legacyEndpoint : new Uri(_config.Url!);
        if (target is null)
            return;
        var post = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(reply.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(post);
        try
        {
            (await _http.SendAsync(post, cancellationToken)).Dispose();
        }
        catch (HttpRequestException)
        {
            // The server may have moved on; the reply is best-effort.
        }
    }

    // ---- legacy HTTP+SSE ----

    private async Task StartLegacyAsync(CancellationToken cancellationToken)
    {
        if (_legacy)
            return;

        var request = new HttpRequestMessage(HttpMethod.Get, _config.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(request);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowOnAuthOrFailureAsync(response);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new StreamReader(stream, Encoding.UTF8);

        // The first event must announce the POST endpoint.
        var endpointSource = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        _legacyCts = new CancellationTokenSource();
        var token = _legacyCts.Token;
        _legacyReadLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var (eventName, data) in SseEventsWithNames(reader, token))
                {
                    if (eventName == "endpoint")
                    {
                        endpointSource.TrySetResult(new Uri(new Uri(_config.Url!), data.Trim()));
                        continue;
                    }

                    if (JsonNode.Parse(data) is not JsonObject parsed)
                        continue;
                    if (parsed.ContainsKey("method"))
                    {
                        if (parsed["id"] is not null)
                            await RespondToServerRequestAsync(parsed, token);
                        else if (NotificationReceived is { } receive && parsed["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out var method))
                            receive(method, parsed["params"]?.DeepClone());
                        continue;
                    }

                    if (parsed["id"] is JsonValue value && value.TryGetValue(out long id))
                    {
                        TaskCompletionSource<JsonObject>? completion;
                        lock (_lock)
                        {
                            _legacyPending.Remove(id, out completion);
                        }

                        completion?.TrySetResult(parsed);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                or System.Text.Json.JsonException or HttpRequestException)
            {
            }

            endpointSource.TrySetException(new McpException(
                $"MCP server '{ServerName}': the SSE stream closed before the endpoint arrived."));
            List<TaskCompletionSource<JsonObject>> pending;
            lock (_lock)
            {
                pending = [.. _legacyPending.Values];
                _legacyPending.Clear();
            }

            foreach (var completion in pending)
                completion.TrySetException(new McpException($"MCP server '{ServerName}': the SSE stream closed."));
            response.Dispose();
        }, CancellationToken.None);

        _legacyEndpoint = await endpointSource.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        _legacy = true;
    }

    private async Task<JsonNode> SendLegacyRequestAsync(long id, JsonObject message, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _legacyPending[id] = completion;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _legacyEndpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyHeaders(request);
        var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new McpAuthRequiredException(
                $"MCP server '{ServerName}' requires authentication — run /mcp-auth {ServerName} to sign in.",
                response.Headers.WwwAuthenticate.ToString());
        response.Dispose();

        try
        {
            var parsed = await completion.Task.WaitAsync(cancellationToken);
            return UnwrapResponse(parsed);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _legacyPending.Remove(id);
            }

            throw;
        }
    }

    private Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        return _legacy
            ? PostFireAndForgetAsync(_legacyEndpoint!, message, cancellationToken)
            : PostFireAndForgetAsync(new Uri(_config.Url!), message, cancellationToken);
    }

    private async Task PostFireAndForgetAsync(Uri target, JsonObject message, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(request);
        try
        {
            (await _http.SendAsync(request, cancellationToken)).Dispose();
        }
        catch (HttpRequestException)
        {
        }
    }

    // ---- SSE parsing ----

    private static async IAsyncEnumerable<string> SseEvents(
        StreamReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var (_, data) in SseEventsWithNames(reader, cancellationToken))
            yield return data;
    }

    private static async IAsyncEnumerable<(string Event, string Data)> SseEventsWithNames(
        StreamReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string eventName = "message";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                    yield return (eventName, data.ToString());
                eventName = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }

        if (data.Length > 0)
            yield return (eventName, data.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        _legacyCts?.Cancel();
        if (_sessionId is { Length: > 0 })
        {
            // Streamable HTTP: DELETE ends the session; best-effort.
            var request = new HttpRequestMessage(HttpMethod.Delete, _config.Url);
            ApplyHeaders(request);
            try
            {
                (await _http.SendAsync(request, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token)).Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
            }
        }

        if (_legacyReadLoop is not null)
        {
            try
            {
                await _legacyReadLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
        }

        _legacyCts?.Dispose();
    }
}

/// <summary>A remote MCP server answered 401 — interactive sign-in is needed.</summary>
public sealed class McpAuthRequiredException(string message, string? wwwAuthenticate = null)
    : McpException(message)
{
    /// <summary>The WWW-Authenticate header, which may carry resource_metadata for OAuth discovery.</summary>
    public string? WwwAuthenticate { get; } = wwwAuthenticate;
}
