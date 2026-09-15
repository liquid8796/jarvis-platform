using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>Revokes adapters captured by an earlier turn when its configured connection is replaced or disabled.</summary>
internal sealed class RevocableMcpClient(IMcpClient inner) : IMcpClient
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private bool _disposed;
    public string ServerName => inner.ServerName;
    public string ServerType => inner.ServerType;
    public string? Instructions => inner.Instructions;

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_disposed) throw new McpException($"MCP server '{ServerName}' is disconnected. Refresh its tools before calling it again.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        }
        using (linked)
        {
            try { return await action(linked.Token).WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && _lifetime.IsCancellationRequested)
            { throw new McpException($"MCP server '{ServerName}' was disconnected while the request was running."); }
        }
    }

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken token) => RunAsync(inner.ListToolsAsync, token);
    public Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken token) => RunAsync(inner.ListResourcesAsync, token);
    public Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken token) => RunAsync(inner.ListPromptsAsync, token);
    public Task<string> ReadResourceAsync(string uri, CancellationToken token) => RunAsync(ct => inner.ReadResourceAsync(uri, ct), token);
    public Task<string> GetPromptAsync(string name, JsonObject arguments, CancellationToken token) => RunAsync(ct => inner.GetPromptAsync(name, arguments, ct), token);
    public Task<McpCallResult> CallToolAsync(string name, JsonObject arguments, CancellationToken token, McpElicitationCallback? elicit = null) =>
        RunAsync(ct => inner.CallToolAsync(name, arguments, ct, elicit), token);

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        await _lifetime.CancelAsync();
        try { await inner.DisposeAsync(); }
        finally { _lifetime.Dispose(); }
    }
}
