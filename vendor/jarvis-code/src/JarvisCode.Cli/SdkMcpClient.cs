using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;

namespace JarvisCode.Cli;

/// <summary>
/// JSON-RPC to an SDK-owned server, carried inside mcp_message control requests.
/// The host owns the server process/session; disposing this client releases only
/// this query's access and never terminates another application's server.
/// </summary>
internal sealed class SdkMcpClient(string name, StreamJsonInput transport) : IMcpClient
{
    private long _requestId;
    private bool _disposed;
    private JsonObject _capabilities = [];
    public string ServerName => name;
    public string ServerType => "sdk";
    public string? Instructions { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "jarvis-code", ["version"] = StreamJson.Version },
        }, cancellationToken);
        Instructions = result["instructions"]?.GetValue<string>();
        _capabilities = result["capabilities"] as JsonObject ?? [];
        await transport.RequestAsync(new JsonObject
        {
            ["subtype"] = "mcp_message", ["server_name"] = name,
            ["message"] = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
        }, cancellationToken);
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (_disposed) throw new McpException($"SDK MCP server '{name}' is disconnected.");
        var id = Interlocked.Increment(ref _requestId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(method == "tools/call" ? 300 : 60));
        JsonObject? reply;
        try
        {
            reply = await transport.RequestAsync(new JsonObject
            {
                ["subtype"] = "mcp_message", ["server_name"] = name,
                ["message"] = new JsonObject
                { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = arguments },
            }, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new McpException($"SDK MCP server '{name}' timed out on {method}."); }
        if (reply?["mcp_response"] is not JsonObject rpc || rpc["id"]?.ToString() != id.ToString())
            throw new McpException($"SDK MCP server '{name}' did not return a matching JSON-RPC response for {method}.");
        if (rpc["error"] is JsonObject error)
            throw new McpException($"SDK MCP server '{name}': {error["message"]} ({error["code"]}).");
        return rpc["result"] as JsonObject ?? throw new McpException($"SDK MCP server '{name}' returned an invalid {method} result.");
    }

    private async Task<JsonObject> ListAsync(string capability, string field, CancellationToken cancellationToken)
    {
        var values = new JsonArray();
        if (!_capabilities.ContainsKey(capability)) return new JsonObject { [field] = values };
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var args = new JsonObject();
            if (cursor is not null) args["cursor"] = cursor;
            var result = await RequestAsync(capability + "/list", args, cancellationToken);
            foreach (var item in result[field] as JsonArray ?? []) values.Add(item?.DeepClone());
            if (values.Count > 10000) throw new McpException($"SDK MCP server '{name}' listing exceeds 10000 entries.");
            cursor = result["nextCursor"]?.GetValue<string>();
            if (cursor is not null && !cursors.Add(cursor))
                throw new McpException($"SDK MCP server '{name}' repeated its pagination cursor.");
        } while (cursor is not null);
        return new JsonObject { [field] = values };
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken) =>
        McpResultParsing.ParseTools(await ListAsync("tools", "tools", cancellationToken), name);
    public async Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken) =>
        McpResultParsing.ParseResources(await ListAsync("resources", "resources", cancellationToken));
    public async Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken cancellationToken) =>
        McpResultParsing.ParsePrompts(await ListAsync("prompts", "prompts", cancellationToken));
    public async Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken) =>
        McpResultParsing.ParseResourceText(await RequestAsync("resources/read", new JsonObject { ["uri"] = uri }, cancellationToken));
    public async Task<string> GetPromptAsync(string prompt, JsonObject arguments, CancellationToken cancellationToken) =>
        McpResultParsing.ParsePromptText(await RequestAsync("prompts/get", new JsonObject
        { ["name"] = prompt, ["arguments"] = arguments.DeepClone() }, cancellationToken));
    public async Task<McpCallResult> CallToolAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken,
        McpElicitationCallback? elicit = null) => McpResultParsing.ParseCallResult(await RequestAsync("tools/call", new JsonObject
        { ["name"] = toolName, ["arguments"] = arguments.DeepClone() }, cancellationToken));
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
}
