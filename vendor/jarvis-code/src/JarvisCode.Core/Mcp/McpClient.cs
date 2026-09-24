using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// Minimal MCP client over the stdio transport: newline-delimited JSON-RPC 2.0.
/// Supports the initialize handshake, tools/list and tools/call; server-initiated
/// requests are answered with method-not-found so well-behaved servers keep going.
/// </summary>
public sealed class McpClient : IMcpClient
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(120);

    private readonly TimeSpan _requestTimeout;
    private readonly Process _process;
    private readonly Dictionary<long, TaskCompletionSource<JsonNode>> _pending = [];
    private readonly object _lock = new();
    private long _nextId;
    private McpElicitationCallback? _activeElicit;
    private bool _dead;
    private string _deathReason = "the server was shut down";
    private Task? _readLoop;
    private Task? _stderrLoop;

    public string ServerName { get; }

    /// <summary>The server's <c>instructions</c> from the initialize handshake.</summary>
    public string? Instructions { get; private set; }

    private McpClient(string serverName, Process process, TimeSpan requestTimeout)
    {
        ServerName = serverName;
        _process = process;
        _requestTimeout = requestTimeout;
    }

    public static async Task<McpClient> StartAsync(
        McpServerConfig config, CancellationToken cancellationToken, TimeSpan? requestTimeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        var extension = Path.GetExtension(config.Command);
        if (OperatingSystem.IsWindows() &&
            !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
        {
            // Keep the shell fallback for batch/extensionless launchers such as npx.
            // Native executables must run directly: cmd interprets MCP arguments such
            // as mcpforunityserver>=0.0.0a0 as redirection, even with ArgumentList.
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(config.Command);
        }
        else
        {
            startInfo.FileName = config.Command;
        }
        foreach (var arg in config.Args)
            startInfo.ArgumentList.Add(arg);
        foreach (var (key, value) in config.Env)
            startInfo.Environment[key] = value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new McpException($"MCP server '{config.Name}': process failed to start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new McpException($"MCP server '{config.Name}': could not start '{config.Command}': {ex.Message}", ex);
        }

        var client = new McpClient(config.Name, process, requestTimeout ?? DefaultRequestTimeout);
        client._readLoop = Task.Run(client.ReadLoopAsync, CancellationToken.None);
        // MCP diagnostics must never block the server's protocol pipe, even without newlines.
        client._stderrLoop = Task.Run(client.DrainStandardErrorAsync, CancellationToken.None);
        process.Exited += (_, _) => client.FailAllPending($"the '{config.Name}' server process exited");

        try
        {
            var handshake = await client.SendRequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "JarvisCode", ["version"] = "1.0" },
            }, client._requestTimeout, cancellationToken);
            client.Instructions = Tools.JsonArgs.GetString(handshake as JsonObject ?? [], "instructions");
            await client.SendNotificationAsync("notifications/initialized");
        }
        catch (Exception)
        {
            await client.DisposeAsync();
            throw;
        }
        return client;
    }

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("tools/list", new JsonObject(), _requestTimeout, cancellationToken);
        var tools = new List<McpToolDescriptor>();
        foreach (var entry in (result["tools"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Tools.JsonArgs.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var schema = entry["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
            tools.Add(new McpToolDescriptor(
                name,
                Tools.JsonArgs.GetString(entry, "description") ?? $"Tool from the {ServerName} MCP server",
                (JsonObject)schema.DeepClone())
            {
                AlwaysLoad = McpToolDescriptor.ReadAlwaysLoad(entry),
                SearchHint = McpToolDescriptor.ReadSearchHint(entry),
                MaxResultSizeChars = McpToolDescriptor.ReadMaxResultSizeChars(entry),
                RequiresUserInteraction = McpToolDescriptor.ReadRequiresUserInteraction(entry),
            });
        }
        return tools;
    }

    /// <summary>Lists the server's resources; a server without the capability yields an empty list.</summary>
    public async Task<IReadOnlyList<McpResourceDescriptor>> ListResourcesAsync(CancellationToken cancellationToken)
    {
        JsonNode result;
        try
        {
            result = await SendRequestAsync("resources/list", new JsonObject(), _requestTimeout, cancellationToken);
        }
        catch (McpException)
        {
            return []; // resources are optional; method-not-found means none.
        }
        var resources = new List<McpResourceDescriptor>();
        foreach (var entry in (result["resources"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var uri = Tools.JsonArgs.GetString(entry, "uri");
            if (string.IsNullOrWhiteSpace(uri))
                continue;
            resources.Add(new McpResourceDescriptor(
                uri,
                Tools.JsonArgs.GetString(entry, "name") ?? uri,
                Tools.JsonArgs.GetString(entry, "description"),
                Tools.JsonArgs.GetString(entry, "mimeType")));
        }
        return resources;
    }

    /// <summary>Reads one resource and joins its text contents; binary parts are noted, not returned.</summary>
    public async Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("resources/read", new JsonObject { ["uri"] = uri },
            _requestTimeout, cancellationToken);
        var text = new StringBuilder();
        foreach (var entry in (result["contents"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Tools.JsonArgs.GetString(entry, "text") is { } part)
                text.AppendLine(part);
            else if (entry.ContainsKey("blob"))
                text.AppendLine($"[binary content ({Tools.JsonArgs.GetString(entry, "mimeType") ?? "unknown type"}) omitted]");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Lists the server's prompts; a server without the capability yields an empty list.</summary>
    public async Task<IReadOnlyList<McpPromptDescriptor>> ListPromptsAsync(CancellationToken cancellationToken)
    {
        JsonNode result;
        try
        {
            result = await SendRequestAsync("prompts/list", new JsonObject(), _requestTimeout, cancellationToken);
        }
        catch (McpException)
        {
            return [];
        }
        var prompts = new List<McpPromptDescriptor>();
        foreach (var entry in (result["prompts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Tools.JsonArgs.GetString(entry, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var arguments = new List<McpPromptArgument>();
            foreach (var arg in (entry["arguments"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Tools.JsonArgs.GetString(arg, "name") is { Length: > 0 } argName)
                    arguments.Add(new McpPromptArgument(
                        argName,
                        Tools.JsonArgs.GetString(arg, "description"),
                        arg["required"]?.GetValue<bool>() ?? false));
            }
            prompts.Add(new McpPromptDescriptor(name, Tools.JsonArgs.GetString(entry, "description"), arguments));
        }
        return prompts;
    }

    /// <summary>Expands one prompt into the text a slash invocation submits as the user message.</summary>
    public async Task<string> GetPromptAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("prompts/get", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.DeepClone(),
        }, _requestTimeout, cancellationToken);

        var text = new StringBuilder();
        foreach (var message in (result["messages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var content = message["content"];
            string? part = content switch
            {
                JsonObject block when Tools.JsonArgs.GetString(block, "type") == "text" =>
                    Tools.JsonArgs.GetString(block, "text"),
                JsonValue value when value.TryGetValue(out string? plain) => plain,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(part))
                continue;
            if (text.Length > 0)
                text.AppendLine().AppendLine();
            text.Append(part.Trim());
        }
        return text.ToString();
    }

    public async Task<McpCallResult> CallToolAsync(
        string toolName, JsonObject arguments, CancellationToken cancellationToken,
        McpElicitationCallback? elicit = null)
    {
        _activeElicit = elicit;
        try
        {
            return await CallToolCoreAsync(toolName, arguments, cancellationToken);
        }
        finally
        {
            _activeElicit = null;
        }
    }

    private async Task<McpCallResult> CallToolCoreAsync(string toolName, JsonObject arguments, CancellationToken cancellationToken)
    {
        var result = await SendRequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments.DeepClone(),
        }, ToolCallTimeout, cancellationToken);

        return McpResultParsing.ParseCallResult(result);
    }

    private async Task<JsonNode> SendRequestAsync(
        string method, JsonObject parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<JsonNode> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_lock)
        {
            if (_dead)
                throw new McpException($"MCP server '{ServerName}' is unavailable: {_deathReason}.");
            id = ++_nextId;
            _pending[id] = completion;
        }

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };
        await WriteMessageAsync(message);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                _pending.Remove(id);
            }
            throw new McpException($"MCP server '{ServerName}': {method} timed out after {timeout.TotalSeconds:0}s.");
        }
    }

    private Task SendNotificationAsync(string method) =>
        WriteMessageAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

    private async Task WriteMessageAsync(JsonObject message)
    {
        try
        {
            string line = message.ToJsonString();
            lock (_lock)
            {
                if (_dead)
                    throw new McpException($"MCP server '{ServerName}' is unavailable: {_deathReason}.");
            }
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            FailAllPending($"writing to the '{ServerName}' server failed: {ex.Message}");
            throw new McpException($"MCP server '{ServerName}' is unavailable: {ex.Message}", ex);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue; // Servers sometimes log noise to stdout; skip non-JSON lines.
                }
                if (node is not JsonObject message)
                    continue;

                if (message["id"] is JsonValue idValue && idValue.TryGetValue(out long id))
                {
                    if (message.ContainsKey("method"))
                    {
                        // Server-initiated request: elicitation goes to the active
                        // call's callback; everything else politely refuses.
                        if (Tools.JsonArgs.GetString(message, "method") == "elicitation/create" &&
                            _activeElicit is { } elicit)
                        {
                            JsonObject elicitResult;
                            try
                            {
                                elicitResult = await elicit(
                                    Tools.JsonArgs.GetString(message["params"] as JsonObject ?? [], "message") ?? "",
                                    (message["params"] as JsonObject)?["requestedSchema"] as JsonObject,
                                    CancellationToken.None);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                elicitResult = new JsonObject { ["action"] = "cancel" };
                            }

                            await WriteMessageAsync(new JsonObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = id,
                                ["result"] = elicitResult,
                            });
                            continue;
                        }

                        await WriteMessageAsync(new JsonObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["id"] = id,
                            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not supported" },
                        });
                        continue;
                    }
                    TaskCompletionSource<JsonNode>? completion;
                    lock (_lock)
                    {
                        _pending.Remove(id, out completion);
                    }
                    if (completion is null)
                        continue;
                    if (message["error"] is JsonObject error)
                    {
                        completion.TrySetException(new McpException(
                            $"MCP server '{ServerName}': {Tools.JsonArgs.GetString(error, "message") ?? "request failed"}"));
                    }
                    else
                    {
                        completion.TrySetResult(message["result"] ?? new JsonObject());
                    }
                }
                // Notifications from the server are ignored.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or McpException)
        {
            // Fall through to the shared failure path below.
        }
        FailAllPending($"the '{ServerName}' server closed its output");
    }

    private async Task DrainStandardErrorAsync()
    {
        // Discard diagnostics in a fixed-size buffer: they may contain secrets, and may
        // have no line endings. ReadToEnd/ReadLine would retain unbounded output.
        var buffer = new char[4096];
        try
        {
            while (await _process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Diagnostic-stream teardown must not hide a protocol failure or cancellation.
        }
    }

    private void FailAllPending(string reason)
    {
        List<TaskCompletionSource<JsonNode>> pending;
        lock (_lock)
        {
            _dead = true;
            _deathReason = reason;
            pending = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var completion in pending)
            completion.TrySetException(new McpException($"MCP server '{ServerName}' failed: {reason}."));
    }

    public async ValueTask DisposeAsync()
    {
        FailAllPending("the client was disposed");
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        try
        {
            _process.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        if (_readLoop is not null)
        {
            try
            {
                await Task.WhenAll(_readLoop, _stderrLoop ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is TimeoutException or McpException or IOException)
            {
            }
        }
        _process.Dispose();
    }
}
