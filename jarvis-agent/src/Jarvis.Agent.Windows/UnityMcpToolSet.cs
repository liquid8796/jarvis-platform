using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>
/// Lazy, session-isolated access to the operator-configured Unity MCP server.
/// Every entry point is a normal sensitive IAgentTool: discovery/configuration never grants approval.
/// </summary>
public sealed class UnityMcpToolSet : IDisposable
{
    private readonly string _configPath;
    private readonly Func<McpServerConfig, CancellationToken, Task<IMcpClient>> _connect;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly object _sync = new();
    private readonly Dictionary<string, Suite> _suites = new(StringComparer.Ordinal);
    private readonly List<Task> _cleanup = [];
    private bool _disposed;
    public IReadOnlyList<IAgentTool> Tools { get; }

    public UnityMcpToolSet(string configPath,
        Func<McpServerConfig, CancellationToken, Task<IMcpClient>>? connect = null)
    {
        _configPath = Path.GetFullPath(configPath);
        _connect = connect ?? ConnectAsync;
        var page = "\"offset\":{\"type\":\"integer\",\"minimum\":0},\"pageSize\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50},\"query\":{\"type\":\"string\",\"maxLength\":200}";
        IAgentTool Tool(string name, string description, string properties, string required = "[]") =>
            new BridgeTool(this, new ToolDescriptor("unity." + name, name, "unity",
                description + " Uses the local JarvisAgent/mcp.json Unity entry; keep Unity's MCP session running. Local approval applies.",
                JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{" + properties + "},\"required\":" + required + "}"),
                ReadOnly: false, Sensitive: true));
        Tools = [
            Tool("list_tools", "Discover Unity MCP tool names and input schemas. Page or filter large results before calling a tool.", page),
            Tool("call_tool", "Invoke a discovered Unity MCP tool with its exact arguments. Can modify the Unity project. Failed/uncertain calls are never replayed.",
                "\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200},\"arguments\":{\"type\":\"object\"}", "[\"name\",\"arguments\"]"),
            Tool("list_resources", "List Unity MCP resources.", page),
            Tool("read_resource", "Read a Unity MCP resource by its exact URI.", "\"uri\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":4096}", "[\"uri\"]"),
            Tool("list_prompts", "List Unity MCP prompt templates.", page),
            Tool("get_prompt", "Read a Unity MCP prompt template, without executing its instructions.",
                "\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":200},\"arguments\":{\"type\":\"object\"}", "[\"name\",\"arguments\"]")
        ];
    }

    private async Task<IMcpClient> ConnectAsync(McpServerConfig config, CancellationToken token)
    {
        if (config.Type == "http")
            return await McpHttpClient.ConnectAsync(config, _http, null, token).ConfigureAwait(false);
        return await McpClient.StartAsync(config, token).ConfigureAwait(false);
    }

    private async Task<ToolReply> ExecuteAsync(string id, JsonElement args, AgentExecutionContext context, CancellationToken token)
    {
        context.SessionCancellation.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        McpServerConfig config;
        try { config = UnityMcpConfig.Load(_configPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            StopSession(context.IsolationScopeId);
            return ToolReply.Error(ex is InvalidDataException ? ex.Message : "Cannot read the Unity MCP configuration. Check mcp.json locally; it was not changed.");
        }

        Suite suite;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_suites.TryGetValue(context.IsolationScopeId, out suite!))
            {
                if (_suites.Count >= 32) return ToolReply.Error("Too many Unity MCP sessions. Stop unused Jarvis sessions first.");
                _suites.Add(context.IsolationScopeId, suite = new Suite());
            }
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, context.SessionCancellation, suite.Stop.Token);
        await suite.Serial.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (suite.Signature != config.Signature)
            {
                await suite.DisconnectAsync().ConfigureAwait(false);
                suite.Signature = config.Signature;
            }
            suite.Client ??= await _connect(config, lifetime.Token).ConfigureAwait(false);
            var client = suite.Client;
            switch (id)
            {
                case "unity.list_tools":
                    return Page(await client.ListToolsAsync(lifetime.Token).ConfigureAwait(false), args, t => t.Name + " " + t.Description);
                case "unity.list_resources":
                    return Page(await client.ListResourcesAsync(lifetime.Token).ConfigureAwait(false), args, r => r.Uri + " " + r.Name + " " + r.Description);
                case "unity.list_prompts":
                    return Page(await client.ListPromptsAsync(lifetime.Token).ConfigureAwait(false), args, p => p.Name + " " + p.Description);
                case "unity.read_resource":
                    return Text(await client.ReadResourceAsync(args.GetProperty("uri").GetString()!, lifetime.Token).ConfigureAwait(false));
                case "unity.get_prompt":
                    return Text(await client.GetPromptAsync(args.GetProperty("name").GetString()!, ObjectArguments(args), lifetime.Token).ConfigureAwait(false));
                case "unity.call_tool":
                    var result = await client.CallToolAsync(args.GetProperty("name").GetString()!, ObjectArguments(args), lifetime.Token).ConfigureAwait(false);
                    var text = result.StructuredContent is null ? result.Text : JsonSerializer.Serialize(new
                    { text = result.Text, structuredContent = result.StructuredContent }, WireJson.Options);
                    var images = result.Images?.Select(image => new WireImage(image.MediaType, image.Base64Data)).ToArray();
                    if (images is not null && (images.Length > 8 || images.Sum(image => (long)image.Base64.Length) > 4 * 1024 * 1024))
                        return ToolReply.Error("Unity returned too many image bytes. Request a smaller screenshot/result; the call was not retried.");
                    return Text(text, result.IsError) with { Images = images };
                default:
                    throw new InvalidOperationException("Unknown Unity MCP bridge operation.");
            }
        }
        catch (OperationCanceledException)
        {
            await suite.DisconnectAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or IOException or TimeoutException)
        {
            await suite.DisconnectAsync().ConfigureAwait(false);
            // Transport exceptions may contain HTTP bodies/headers. Do not echo secrets or retry mutations.
            return ToolReply.Error("Unity MCP request failed (" + ex.GetType().Name + "). Check the Unity session/server and local configuration. The call was not retried; verify Unity state before repeating a modifying action.");
        }
        finally { suite.Serial.Release(); }
    }

    private static JsonObject ObjectArguments(JsonElement args) =>
        JsonNode.Parse(args.GetProperty("arguments").GetRawText()) as JsonObject
        ?? throw new ArgumentException("Unity tool/prompt arguments must be a JSON object.");

    private static ToolReply Page<T>(IReadOnlyList<T> all, JsonElement args, Func<T, string> searchable)
    {
        var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
        var size = args.TryGetProperty("pageSize", out var s) ? s.GetInt32() : 20;
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (offset < 0 || size is < 1 or > 50) throw new ArgumentException("Invalid Unity MCP page bounds.");
        var filtered = string.IsNullOrWhiteSpace(query) ? all.ToArray() :
            all.Where(item => searchable(item).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var items = filtered.Skip(offset).Take(size).ToArray();
        var next = (long)offset + items.Length;
        return Text(JsonSerializer.Serialize(new { items, total = filtered.Length,
            nextOffset = next < filtered.Length ? (long?)next : null }, WireJson.Options));
    }

    private static ToolReply Text(string text, bool isError = false) => text.Length <= 512 * 1024
        ? new ToolReply(text, isError)
        : ToolReply.Error("Unity MCP result exceeds 512 KiB. Use a narrower query or smaller page/result. The call was not retried.");

    public void StopSession(string scope)
    {
        lock (_sync)
        {
            if (_suites.Remove(scope, out var suite)) TrackCleanup(suite);
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            foreach (var suite in _suites.Values) TrackCleanup(suite);
            _suites.Clear();
        }
    }

    private void TrackCleanup(Suite suite)
    {
        _cleanup.RemoveAll(task => task.IsCompleted);
        _cleanup.Add(suite.CloseAsync());
    }

    public void Dispose()
    {
        Task[] pending;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Pause();
            pending = _cleanup.ToArray();
        }
        try { Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        _http.Dispose();
    }

    private sealed class Suite
    {
        public SemaphoreSlim Serial { get; } = new(1, 1);
        public CancellationTokenSource Stop { get; } = new();
        public IMcpClient? Client { get; set; }
        public string? Signature { get; set; }

        public async Task DisconnectAsync()
        {
            var client = Client;
            Client = null;
            if (client is not null)
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* Cleanup must not hide cancellation/the original transport failure. */ }
        }

        public async Task CloseAsync()
        {
            try { Stop.Cancel(); }
            catch (AggregateException) { }
            await Serial.WaitAsync().ConfigureAwait(false);
            try { await DisconnectAsync().ConfigureAwait(false); }
            finally { Serial.Release(); }
            // A previously queued caller may still hold these primitives; let GC collect them.
        }
    }

    private sealed class BridgeTool(UnityMcpToolSet owner, ToolDescriptor descriptor) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken token) =>
            owner.ExecuteAsync(Descriptor.Id, arguments, context, token);
    }
}
