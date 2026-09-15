using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// Answers an MCP elicitation (a server asking the user a question mid-call).
/// Receives the server's message and requested schema; returns the JSON-RPC
/// result object ({"action":"accept","content":{…}} | {"action":"decline"} |
/// {"action":"cancel"}).
/// </summary>
public delegate Task<JsonObject> McpElicitationCallback(
    string message, JsonObject? requestedSchema, CancellationToken cancellationToken);

/// <summary>
/// One connected MCP server, whatever the transport (stdio subprocess, or a
/// remote streamable-HTTP / SSE endpoint). The manager and tool adapters only
/// see this surface.
/// </summary>
public interface IMcpClient : IAsyncDisposable
{
    string ServerName { get; }

    /// <summary>
    /// The configured transport ("stdio", "http", "sse"). The reference keys a
    /// few rules off it — which calls may be moved to the background, above all.
    /// </summary>
    string ServerType => "stdio";

    /// <summary>
    /// The <c>instructions</c> the server returned from <c>initialize</c>, or
    /// null. The reference renders these under <c># MCP Server Instructions</c>
    /// beside its own in-process servers' blocks, and retracts them when the
    /// server goes away.
    /// </summary>
    string? Instructions => null;

    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken);

    Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken);

    Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken cancellationToken);

    Task<string> GetPromptAsync(string name, JsonObject arguments, CancellationToken cancellationToken);

    Task<McpCallResult> CallToolAsync(
        string toolName, JsonObject arguments, CancellationToken cancellationToken,
        McpElicitationCallback? elicit = null);
}
