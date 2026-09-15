using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Mcp;

namespace JarvisCode.Cli;

/// <summary>Per-query SDK hooks and in-process MCP server registrations.</summary>
internal sealed class CliSdkSession(McpManager manager, string sessionId, string cwd, string transcriptPath,
    IReadOnlyList<string> configuredServers, bool disableHooks = false, bool disableCustomizations = false)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SdkMcpClient> _clients = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _disabled = new(StringComparer.Ordinal);
    private IReadOnlyList<HookDefinition> _hooks = [];
    private StreamJsonInput? _transport;
    private Task _ready = Task.CompletedTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _observations = new();
    public Task Ready => _ready;
    public bool ForwardSubagentText { get; private set; }
    public IReadOnlyList<Core.Customization.CustomAgentDefinition> Agents { get; private set; } = [];
    public IReadOnlyList<string>? Skills { get; private set; }
    public bool ExcludeDynamicSections { get; private set; }

    public void Attach(StreamJsonInput transport) => _transport = transport;

    public Task InitializeAsync(JsonObject request, CancellationToken cancellationToken)
    {
        _ready = InitializeCoreAsync(request, cancellationToken);
        return _ready;
    }

    private async Task InitializeCoreAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new CliError("SDK control transport is not attached.");
        _hooks = disableHooks ? [] : ParseHooks(request["hooks"]);
        ForwardSubagentText = request["forwardSubagentText"]?.GetValue<bool>() ?? false;
        Agents = disableCustomizations ? [] : CliAgents.Parse(request["agents"]?.ToJsonString());
        Skills = disableCustomizations ? [] : request["skills"] is JsonArray skills ? [.. skills.Select(skill => skill!.GetValue<string>())] : null;
        ExcludeDynamicSections = request["excludeDynamicSections"]?.GetValue<bool>() ?? false;
        if (disableCustomizations) return;
        var names = new HashSet<string>(configuredServers, StringComparer.Ordinal);
        foreach (var item in request["sdkMcpServers"] as JsonArray ?? request["sdk_mcp_servers"] as JsonArray ?? [])
        {
            var name = item is JsonValue value && value.TryGetValue<string>(out var plain) ? plain : item?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        foreach (var name in names)
        {
            var client = new SdkMcpClient(name, transport);
            await client.InitializeAsync(cancellationToken);
            await manager.RegisterHostedAsync(new McpServerConfig(name, "", [], new Dictionary<string, string>())
                { Type = "sdk", DiscoveryCache = false }, client, cancellationToken);
            _clients[name] = client;
        }
    }

    public HookRunner ApplyHooks(HookRunner? original) =>
        (original ?? new HookRunner([], cwd)).WithSdkCallbacks(_hooks, RunHookAsync);

    public void Observe(HookRunner hooks, HookEvent hookEvent, JsonObject payload, CancellationToken cancellationToken)
    {
        if (!hooks.Has(hookEvent)) return;
        var id = Guid.NewGuid().ToString();
        var task = hooks.RunEventAsync(hookEvent, payload, cancellationToken,
            hookEvent == HookEvent.InstructionsLoaded ? payload["load_reason"]?.ToString() : null);
        _observations[id] = task;
        _ = task.ContinueWith(completed =>
        {
            _observations.TryRemove(id, out var removed);
            if (completed.Exception is { } error) Core.Utilities.DiagnosticLog.Write("SDK hook failed: " + error.GetBaseException().Message);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task DrainObservationsAsync() => Task.WhenAll(_observations.Values);

    private async Task<JsonObject?> RunHookAsync(HookDefinition hook, JsonObject payload, string? toolUseId,
        CancellationToken cancellationToken)
    {
        var input = (JsonObject)payload.DeepClone();
        input["hook_event_name"] = hook.Event.ToString();
        input["session_id"] = sessionId;
        input["cwd"] = cwd;
        input["transcript_path"] = transcriptPath;
        if (input["tool"] is { } tool) input["tool_name"] = tool.DeepClone();
        if (input["arguments"] is { } args) input["tool_input"] = args.DeepClone();
        if (input["result"] is { } result) input["tool_response"] = result.DeepClone();
        if (toolUseId is not null) input["tool_use_id"] = toolUseId;
        return await (_transport ?? throw new CliError("SDK host is disconnected.")).RequestAsync(new JsonObject
        {
            ["subtype"] = "hook_callback", ["callback_id"] = hook.CallbackId,
            ["input"] = input, ["tool_use_id"] = toolUseId,
        }, cancellationToken);
    }

    public static IReadOnlyList<HookDefinition> ParseHooks(JsonNode? source)
    {
        if (source is null) return [];
        if (source is not JsonObject events) throw new CliError("initialize.hooks must be an object.");
        var hooks = new List<HookDefinition>();
        foreach (var (eventName, matches) in events)
        {
            if (!Enum.TryParse<HookEvent>(eventName, ignoreCase: true, out var hookEvent) &&
                HookRunner.ParseEventName(eventName) is not { } _)
                throw new CliError($"Unknown SDK hook event: {eventName}.");
            if (HookRunner.ParseEventName(eventName) is { } parsed) hookEvent = parsed;
            if (matches is not JsonArray matchers) throw new CliError($"SDK hook {eventName} must contain an array of matchers.");
            foreach (var matcher in matchers.OfType<JsonObject>())
            {
                var pattern = matcher["matcher"]?.GetValue<string>();
                if (pattern is { Length: > 0 })
                {
                    try { _ = new System.Text.RegularExpressions.Regex(pattern, default, TimeSpan.FromSeconds(1)); }
                    catch (ArgumentException ex) { throw new CliError("Invalid SDK hook matcher: " + ex.Message); }
                }
                var timeout = matcher["timeout"]?.GetValue<double>() ?? 60;
                if (!double.IsFinite(timeout) || timeout <= 0 || timeout > 600)
                    throw new CliError("SDK hook timeout must be between 0 and 600 seconds.");
                if (matcher["hookCallbackIds"] is not JsonArray callbacks)
                    throw new CliError("SDK hook matcher requires hookCallbackIds.");
                foreach (var callback in callbacks)
                {
                    var id = callback?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(id)) throw new CliError("SDK hook callback id cannot be empty.");
                    hooks.Add(new HookDefinition(hookEvent, pattern, id, (int)Math.Ceiling(timeout), HookKind.Callback,
                        CallbackId: id));
                }
            }
        }
        return hooks;
    }

    public async Task ReconnectAsync(string name, CancellationToken cancellationToken)
    {
        if (!_clients.ContainsKey(name)) { await manager.ReconnectServerAsync(name, cancellationToken); return; }
        if (_disabled.ContainsKey(name)) throw new CliError($"MCP server '{name}' is disabled; enable it before reconnecting.");
        await manager.RemoveHostedAsync(name, cancellationToken);
        var client = new SdkMcpClient(name, _transport!);
        await client.InitializeAsync(cancellationToken);
        await manager.RegisterHostedAsync(new McpServerConfig(name, "", [], new Dictionary<string, string>())
            { Type = "sdk", DiscoveryCache = false }, client, cancellationToken);
        _clients[name] = client;
    }

    public async Task ToggleAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        if (!_clients.ContainsKey(name)) { await manager.SetServerEnabledAsync(name, enabled, cancellationToken); return; }
        if (enabled) { _disabled.TryRemove(name, out _); await ReconnectAsync(name, cancellationToken); }
        else { await manager.RemoveHostedAsync(name, cancellationToken); _disabled[name] = 0; }
    }

    public JsonObject ServerStatus()
    {
        var statuses = manager.GetServerStatuses().ToList();
        foreach (var name in _disabled.Keys)
        {
            statuses.RemoveAll(status => status.Name == name);
            statuses.Add(new McpServerStatus(name, "disabled", 0, null, true));
        }
        return new JsonObject { ["mcpServers"] = new JsonArray([.. statuses.Select(status =>
        {
            var row = new JsonObject { ["name"] = status.Name, ["status"] = status.Status };
            if (status.Error is not null) row["error"] = status.Error;
            if (status.Status == "connected") row["tools"] = new JsonArray([.. manager.Tools.OfType<McpToolAdapter>()
                .Where(tool => tool.ServerName == status.Name).Select(tool => (JsonNode)new JsonObject
                { ["name"] = tool.SourceToolName, ["description"] = tool.Description })]);
            return (JsonNode)row;
        })]) };
    }

    public static IReadOnlyList<string> ReadConfiguredServerNames(CliOptions options, string cwd, string userConfig)
    {
        var sources = new List<string>();
        if (options.SafeMode) return [];
        var tiers = CliSettingsStore.ParseSources(options.SettingSources);
        if (!options.StrictMcpConfig && !options.Bare && tiers.Contains("user") && File.Exists(userConfig)) sources.Add(File.ReadAllText(userConfig));
        foreach (var config in options.McpConfigs)
            sources.Add(config.TrimStart().StartsWith('{') ? config : File.ReadAllText(Path.GetFullPath(config, cwd)));
        var projectConfig = Path.Combine(cwd, McpConfig.ProjectRelativePath);
        if (!options.StrictMcpConfig && !options.Bare && (tiers.Contains("project") || tiers.Contains("local")) && File.Exists(projectConfig)) sources.Add(File.ReadAllText(projectConfig));
        var entries = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            JsonObject? root;
            try { root = JsonNode.Parse(source) as JsonObject; }
            catch (JsonException ex) { throw new CliError("Invalid MCP config JSON: " + ex.Message); }
            if (root?["mcpServers"] is JsonObject servers)
                foreach (var (name, entry) in servers) entries[name] = entry?["type"]?.GetValue<string>() == "sdk";
            if (root?["servers"] is JsonArray array)
                foreach (var entry in array.OfType<JsonObject>())
                    if (entry["name"]?.GetValue<string>() is { } name) entries[name] = entry["type"]?.GetValue<string>() == "sdk";
        }
        return [.. entries.Where(entry => entry.Value).Select(entry => entry.Key)];
    }
}
