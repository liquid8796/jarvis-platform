using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Mcp;

/// <summary>
/// Keeps the configured MCP servers running and their tools available. Refresh
/// diffs the loaded config against the live clients: unchanged servers keep
/// running, removed ones stop, new or changed ones (re)start.
/// </summary>
/// <summary>
/// Asks the host whether a server's <c>headersHelper</c> command may run. The
/// answer is remembered for the session, keyed by server and command text, so a
/// changed command asks again. Null refuses: a run with nobody to ask does not
/// execute a configured command to mint a credential.
/// </summary>
public delegate Task<bool> McpCommandTrustCallback(
    McpServerConfig server, string command, CancellationToken cancellationToken);

public sealed class McpManager(
    System.Net.Http.HttpClient? http = null,
    McpTokenStore? tokens = null,
    McpCommandTrustCallback? headersHelperTrust = null,
    McpDiscoveryCache? discoveryCache = null,
    Func<McpServerConfig, CancellationToken, Task<IMcpClient>>? clientFactory = null,
    McpSchemaPolicy? schemaPolicy = null) : IAsyncDisposable
{
    private readonly McpSchemaPolicy _schemaPolicy = schemaPolicy ?? new();
    /// <summary>Server name + command text of every headers helper approved this session.</summary>
    private readonly HashSet<string> _approvedHelpers = new(StringComparer.Ordinal);

    /// <summary>
    /// Runs a server's headers helper, once the host has approved the command.
    /// The reference gates this on workspace trust instead (its
    /// <c>isRepoResidentConfig</c>); this build asks, because a configured
    /// command that mints a credential is the same kind of decision a hook
    /// command is. A failure is reported with the reference's own sentence and
    /// leaves the server on its static headers.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> ResolveHelperHeadersAsync(
        McpServerConfig config, string workingDirectory, List<string> messages, CancellationToken cancellationToken)
    {
        if (config.HeadersHelper is not { Length: > 0 } command)
        {
            return null;
        }

        var key = config.Name + "\u001f" + command;
        bool approved;
        lock (_approvedHelpers)
        {
            approved = _approvedHelpers.Contains(key);
        }

        if (!approved)
        {
            if (headersHelperTrust is null ||
                !await headersHelperTrust(config, command, cancellationToken))
            {
                messages.Add(McpHeadersHelper.Message(config.Name, McpHeadersHelperFailure.NotTrusted));
                return null;
            }

            lock (_approvedHelpers)
            {
                _approvedHelpers.Add(key);
            }
        }

        var result = await McpHeadersHelper.RunAsync(config, workingDirectory, cancellationToken);
        if (result.Failure is { } failure)
        {
            messages.Add(McpHeadersHelper.Message(config.Name, failure));
            return null;
        }

        return result.Headers;
    }

    private sealed class LiveServer(
        McpServerConfig Config,
        IMcpClient Client,
        IReadOnlyList<McpToolAdapter> Tools,
        IReadOnlyList<McpResourceDescriptor> Resources,
        IReadOnlyList<McpPromptDescriptor> Prompts)
    {
        public McpServerConfig Config { get; } = Config;
        public IMcpClient Client { get; } = Client;
        public IReadOnlyList<McpToolAdapter> Tools { get; set; } = Tools;
        public IReadOnlyList<McpResourceDescriptor> Resources { get; set; } = Resources;
        public IReadOnlyList<McpPromptDescriptor> Prompts { get; set; } = Prompts;
        /// <summary>Tools this server reported that the API would have rejected.</summary>
        public IReadOnlyList<(string Tool, string Reason)> DroppedTools { get; set; } = [];
        public CancellationTokenSource RefreshCancellation { get; } = new();
        public Task? RefreshTask { get; set; }
        public bool Hosted { get; init; }
    }

    /// <summary>
    /// The reference's per-tool screen: normalize, then validate, then drop or
    /// keep by the server's scope. A normalized schema also gets its note in
    /// front of the description, which is where the reference puts it.
    /// </summary>
    private McpToolDescriptor? Screen(
        McpServerConfig config,
        McpToolDescriptor descriptor,
        List<(string Tool, string Reason)> dropped)
    {
        var applied = McpToolSchemas.Apply(descriptor.InputSchema,
            _schemaPolicy.DropInvalid(config) ? McpServerScope.Repo : McpServerScope.Operator,
            out var reason, normalizeCombinators: _schemaPolicy.Normalize(config));
        if (applied is null)
        {
            dropped.Add((descriptor.Name, reason ?? "schema is invalid"));
            return null;
        }

        var (schema, note) = applied.Value;
        if (note is null && ReferenceEquals(schema, descriptor.InputSchema))
        {
            return descriptor;
        }

        var description = note is null
            ? descriptor.Description
            : string.IsNullOrEmpty(descriptor.Description) ? note : note + "\n\n" + descriptor.Description;
        return descriptor with { InputSchema = schema, Description = description };
    }

    private readonly Dictionary<string, LiveServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, McpConnectFailure> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (McpServerConfig Config, string WorkingDirectory)> _configured = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Registers an SDK-hosted MCP client with the same discovery, screening,
    /// instructions and status surface as a configured server. Ownership of the
    /// client transfers only after successful registration.
    /// </summary>
    public async Task RegisterHostedAsync(McpServerConfig config, IMcpClient client, CancellationToken cancellationToken)
    {
        if (!string.Equals(config.Name, client.ServerName, StringComparison.Ordinal))
            throw new McpException("The hosted MCP client's name must match its registration.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LiveServer? previous;
            lock (_servers)
            {
                _servers.TryGetValue(config.Name, out previous);
                if (previous is { Hosted: false })
                    throw new McpException($"MCP server '{config.Name}' is already configured; a hosted registration cannot replace it.");
            }
            var listing = await ListDiscoveryAsync(client, cancellationToken);
            var dropped = new List<(string Tool, string Reason)>();
            var tools = listing.Tools.Select(descriptor => Screen(config, descriptor, dropped))
                .OfType<McpToolDescriptor>().Select(descriptor => new McpToolAdapter(client, descriptor))
                .Where(adapter => McpBuiltInServers.IsModelVisible(adapter.Name))
                .DistinctBy(adapter => adapter.Name, StringComparer.Ordinal).ToList();
            var live = new LiveServer(config, client, tools, listing.Resources, listing.Prompts)
                { Hosted = true, DroppedTools = dropped };
            lock (_servers)
            {
                _servers[config.Name] = live;
                _failures.Remove(config.Name);
            }
            if (previous is not null) await StopServerAsync(previous);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveHostedAsync(string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LiveServer? removed = null;
            lock (_servers)
            {
                if (_servers.TryGetValue(name, out var server) && server.Hosted)
                { removed = server; _servers.Remove(name); }
            }
            if (removed is not null) await StopServerAsync(removed);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Why each configured server that is not connected failed at the last
    /// refresh, keyed by server name. A server that connected, or that was never
    /// tried, has no entry; a 401 is told apart from every other failure because
    /// the answer to it is a sign-in rather than a retry.
    /// </summary>
    public IReadOnlyDictionary<string, McpConnectFailure> LastFailures
    {
        get
        {
            lock (_servers)
            {
                return new Dictionary<string, McpConnectFailure>(_failures, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Signs in to a remote server interactively (OAuth), then the next refresh connects it.</summary>
    public async Task AuthorizeAsync(string serverName, string workingDirectory, string? userConfigPath, CancellationToken cancellationToken)
    {
        if (http is null || tokens is null)
            throw new McpException("MCP OAuth is not available in this host.");
        var config = McpConfig.Load(workingDirectory, userConfigPath)
            .FirstOrDefault(c => c.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        if (config is null)
            throw new McpException($"No configured MCP server named '{serverName}'.");
        if (!config.IsRemote)
            throw new McpException($"MCP server '{serverName}' is a local (stdio) server — nothing to sign in to.");
        await McpOAuth.AuthorizeInteractivelyAsync(config, http, tokens, cancellationToken);
    }

    private async Task<IMcpClient> ConnectAsync(
        McpServerConfig config,
        string workingDirectory,
        List<string> messages,
        CancellationToken cancellationToken) =>
        clientFactory is not null ? await clientFactory(config, cancellationToken) : config.IsRemote
            ? await McpHttpClient.ConnectAsync(
                config,
                http ?? throw new McpException($"MCP server '{config.Name}': remote servers need an HTTP-capable host."),
                tokens,
                cancellationToken,
                await ResolveHelperHeadersAsync(config, workingDirectory, messages, cancellationToken))
            : await McpClient.StartAsync(
                config,
                cancellationToken,
                config.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);

    /// <summary>
    /// This server's tools, prompts and resources — from the discovery cache
    /// immediately when its entry is fresh or still usable while stale. Stale
    /// entries are refreshed after their live server has entered the registry.
    /// </summary>
    private async Task<(McpDiscoveryEntry Entry, bool Refresh)> DiscoverAsync(
        McpServerConfig config, IMcpClient client, CancellationToken cancellationToken)
    {
        var lookup = discoveryCache?.Read(config);
        if (lookup is { Status: McpDiscoveryCacheStatus.Fresh, Entry: { } fresh })
        {
            return (fresh, false);
        }
        if (lookup is { Status: McpDiscoveryCacheStatus.Stale, Entry: { } stale })
            return (stale, true);

        var listed = await ListDiscoveryAsync(client, cancellationToken);
        discoveryCache?.Write(config, listed);
        return (listed, false);
    }

    private static async Task<McpDiscoveryEntry> ListDiscoveryAsync(IMcpClient client, CancellationToken cancellationToken) =>
        new(await client.ListToolsAsync(cancellationToken),
            await client.ListPromptsAsync(cancellationToken),
            await client.ListResourcesAsync(cancellationToken))
        {
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    /// <summary>Raised after a background refresh changes the advertised inventory.</summary>
    public event Action? DiscoveryChanged;

    private void NotifyDiscoveryChanged()
    {
        foreach (var listener in DiscoveryChanged?.GetInvocationList() ?? [])
            try { ((Action)listener)(); } catch (Exception) { }
    }

    private async Task RefreshDiscoveryAsync(LiveServer server, McpDiscoveryEntry previous)
    {
        try
        {
            var listing = await ListDiscoveryAsync(server.Client, server.RefreshCancellation.Token);
            var dropped = new List<(string Tool, string Reason)>();
            var tools = listing.Tools.Select(descriptor => Screen(server.Config, descriptor, dropped))
                .OfType<McpToolDescriptor>().Select(descriptor => new McpToolAdapter(server.Client, descriptor))
                .Where(adapter => McpBuiltInServers.IsModelVisible(adapter.Name))
                .DistinctBy(adapter => adapter.Name, StringComparer.Ordinal).ToList();
            lock (_servers)
            {
                if (!_servers.TryGetValue(server.Config.Name, out var current) || !ReferenceEquals(current, server))
                    return;
                server.Tools = tools;
                server.Resources = listing.Resources;
                server.Prompts = listing.Prompts;
                server.DroppedTools = dropped;
                discoveryCache?.Write(server.Config, listing);
            }
            // An optional host listener cannot turn a successful refresh into
            // a server failure or an unobserved task exception.
            NotifyDiscoveryChanged();
        }
        catch (OperationCanceledException) when (server.RefreshCancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is McpException or IOException or InvalidOperationException)
        {
            lock (_servers)
                if (_servers.TryGetValue(server.Config.Name, out var current) && ReferenceEquals(current, server))
                    discoveryCache?.RecordRefreshFailure(server.Config, previous);
        }
    }

    private static async ValueTask StopServerAsync(LiveServer server)
    {
        await server.RefreshCancellation.CancelAsync();
        await server.Client.DisposeAsync();
        if (server.RefreshTask is { } refresh)
            try { await refresh; } catch (OperationCanceledException) { }
        server.RefreshCancellation.Dispose();
    }

    private async Task StartConfiguredAsync(McpServerConfig config, string workingDirectory, List<string> messages,
        CancellationToken token, bool forceDiscovery = false)
    {
        IMcpClient? client = null;
        var published = false;
        try
        {
            client = new RevocableMcpClient(await ConnectAsync(config, workingDirectory, messages, token));
            var discovery = forceDiscovery
                ? (Entry: await ListDiscoveryAsync(client, token), Refresh: false)
                : await DiscoverAsync(config, client, token);
            if (forceDiscovery) discoveryCache?.Write(config, discovery.Entry);
            var listing = discovery.Entry;
            var dropped = new List<(string Tool, string Reason)>();
            var tools = listing.Tools.Select(descriptor => Screen(config, descriptor, dropped)).OfType<McpToolDescriptor>()
                .Select(descriptor => new McpToolAdapter(client, descriptor)).Where(adapter => McpBuiltInServers.IsModelVisible(adapter.Name))
                .DistinctBy(adapter => adapter.Name, StringComparer.Ordinal).ToList();
            foreach (var (tool, reason) in dropped)
                messages.Add($"Skipping tool \"{tool}\": its input schema would be rejected by the Anthropic API ({reason}). Other tools from this server remain available.");
            token.ThrowIfCancellationRequested();
            var server = new LiveServer(config, client, tools, listing.Resources, listing.Prompts) { DroppedTools = dropped };
            lock (_servers)
            {
                _servers[config.Name] = server;
                _failures.Remove(config.Name);
                if (discovery.Refresh) server.RefreshTask = Task.Run(() => RefreshDiscoveryAsync(server, listing));
                published = true;
            }
            messages.Add($"MCP server '{config.Name}' connected: {tools.Count} tool(s).");
        }
        finally { if (!published && client is not null) await client.DisposeAsync(); }
    }

    /// <summary>Reconnects one previously configured server; no configuration files are reread or rewritten.</summary>
    public Task ReconnectServerAsync(string name, CancellationToken token) => ChangeConfiguredServerAsync(name, enabled: null, token);

    /// <summary>Temporarily changes one configured server's enabled state for this manager's lifetime.</summary>
    public Task SetServerEnabledAsync(string name, bool enabled, CancellationToken token) => ChangeConfiguredServerAsync(name, enabled, token);

    private async Task ChangeConfiguredServerAsync(string name, bool? enabled, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var changed = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (McpServerConfig Config, string WorkingDirectory) configured;
            LiveServer? previous;
            lock (_servers)
            {
                if (_servers.TryGetValue(name, out previous) && previous.Hosted)
                    throw new McpException($"MCP server '{name}' is SDK-hosted; its SDK owner must change the registration.");
                if (!_configured.TryGetValue(name, out configured)) throw new McpException($"No configured MCP server named '{name}'.");
                if (enabled is null && _disabled.Contains(name)) throw new McpException($"MCP server '{name}' is disabled; enable it before reconnecting.");
                if (enabled == true && previous is not null && !_disabled.Contains(name)) return;
                if (enabled == false && _disabled.Contains(name) && previous is null) return;
                if (enabled == false) _disabled.Add(name);
                else if (enabled == true) _disabled.Remove(name);
                _servers.Remove(name);
                _failures.Remove(name);
                changed = true;
            }
            if (previous is not null) await StopServerAsync(previous);
            // Cancel and detach the old refresher before eviction so it cannot restore an obsolete inventory.
            if (previous is not null) discoveryCache?.Forget(previous.Config);
            discoveryCache?.Forget(configured.Config);
            if (enabled == false) return;
            try { await StartConfiguredAsync(configured.Config, configured.WorkingDirectory, [], token, forceDiscovery: true); }
            catch (OperationCanceledException)
            {
                discoveryCache?.Forget(configured.Config);
                lock (_servers) _failures[name] = new McpConnectFailure("Connection attempt cancelled.", false, DateTimeOffset.Now);
                throw;
            }
            catch (Exception error) when (error is McpException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                discoveryCache?.Forget(configured.Config);
                lock (_servers) _failures[name] = new McpConnectFailure(error.Message, error is McpAuthRequiredException, DateTimeOffset.Now);
                throw error is McpException ? error : new McpException($"MCP server '{name}' could not reconnect: {error.Message}", error);
            }
        }
        finally { _gate.Release(); if (changed) NotifyDiscoveryChanged(); }
    }

    /// <summary>Includes disconnected configured servers, while keeping SDK-hosted registrations distinct.</summary>
    public IReadOnlyList<McpServerStatus> GetServerStatuses()
    {
        lock (_servers)
        {
            return _configured.Keys.Concat(_servers.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(name => _servers.TryGetValue(name, out var server)
                    ? new McpServerStatus(server.Config.Name, "connected", server.Tools.Count, null, server.Hosted)
                    : _disabled.Contains(name) ? new McpServerStatus(name, "disabled", 0)
                    : _failures.TryGetValue(name, out var failure) ? new McpServerStatus(name, failure.NeedsAuthentication ? "needs-auth" : "failed", 0, failure.Message)
                    : new McpServerStatus(name, "pending", 0)).ToArray();
        }
    }

    /// <summary>Status lines describing what changed or failed; empty when nothing happened.</summary>
    public async Task<IReadOnlyList<string>> RefreshAsync(
        string workingDirectory,
        string? userConfigPath,
        CancellationToken cancellationToken,
        IEnumerable<string>? extraConfigFiles = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var messages = new List<string>();
            var desired = McpConfig.Load(workingDirectory, userConfigPath, extraConfigFiles)
                .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

            List<LiveServer> stale;
            List<McpServerConfig> removedConfigurations;
            lock (_servers)
            {
                removedConfigurations = _configured.Where(pair => !desired.ContainsKey(pair.Key)).Select(pair => pair.Value.Config).ToList();
                _configured.Clear();
                foreach (var config in desired.Values) _configured[config.Name] = (config, workingDirectory);
                stale = [.. _servers.Values.Where(live => !live.Hosted && (
                    !desired.TryGetValue(live.Config.Name, out var config) ||
                    config.Signature != live.Config.Signature || _disabled.Contains(live.Config.Name)))];
                foreach (var live in stale)
                    _servers.Remove(live.Config.Name);
            }
            foreach (var removed in removedConfigurations) discoveryCache?.Forget(removed);
            foreach (var live in stale)
            {
                await StopServerAsync(live);
                // The cache is keyed by the config that produced the listing, so
                // a changed one simply misses; a removed server's entry is
                // dropped rather than left to expire.
                if (!desired.ContainsKey(live.Config.Name))
                {
                    discoveryCache?.Forget(live.Config);
                }

                messages.Add(desired.ContainsKey(live.Config.Name)
                    ? $"MCP server '{live.Config.Name}': configuration changed, restarting."
                    : $"MCP server '{live.Config.Name}' stopped (removed from configuration).");
            }

            lock (_servers)
            {
                foreach (var name in _failures.Keys.Where(n => !desired.ContainsKey(n)).ToList())
                    _failures.Remove(name);
            }
            foreach (var config in desired.Values)
            {
                lock (_servers)
                {
                    if (_servers.ContainsKey(config.Name) || _disabled.Contains(config.Name))
                        continue;
                }
                try
                {
                    await StartConfiguredAsync(config, workingDirectory, messages, cancellationToken);
                }
                catch (McpException ex)
                {
                    lock (_servers)
                    {
                        _failures[config.Name] = new McpConnectFailure(
                            ex.Message, NeedsAuthentication: ex is McpAuthRequiredException, DateTimeOffset.Now);
                    }
                    messages.Add(ex.Message);
                }
            }
            return messages;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Configured servers whose last connection attempt was refused for want of a
    /// credential (HTTP 401). The reference names these to the model in its
    /// deferred-tools delta so it does not report the capability as missing.
    /// </summary>
    /// <summary>
    /// The reference's <c>droppedTools</c>, rendered as its
    /// <c># Unavailable MCP Tools</c> entries: one line per tool it excluded,
    /// or a single counted line for a server past its cap of 30.
    /// </summary>
    public IReadOnlyList<string> DroppedToolEntries
    {
        get
        {
            lock (_servers)
            {
                var entries = new List<string>();
                foreach (var server in _servers.Values)
                {
                    if (server.DroppedTools.Count == 0)
                    {
                        continue;
                    }

                    if (server.DroppedTools.Count > McpToolSchemas.MaxEntriesPerServer)
                    {
                        entries.Add(McpToolSchemas.Summary(server.Config.Name, server.DroppedTools.Count));
                        continue;
                    }

                    foreach (var (tool, reason) in server.DroppedTools)
                    {
                        entries.Add(McpToolSchemas.Entry(server.Config.Name, tool, reason));
                    }
                }

                return entries;
            }
        }
    }

    public IReadOnlyList<string> NeedsAuthServers
    {
        get
        {
            lock (_servers)
            {
                return [.. _failures
                    .Where(pair => pair.Value.NeedsAuthentication)
                    .Select(pair => pair.Key)
                    .Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>
    /// Configured servers whose last connection attempt failed for another
    /// reason, with the error the reference quotes beside the name.
    /// </summary>
    public IReadOnlyList<(string Name, string Error)> FailedServers
    {
        get
        {
            lock (_servers)
            {
                return [.. _failures
                    .Where(pair => !pair.Value.NeedsAuthentication)
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => (pair.Key, pair.Value.Message))];
            }
        }
    }

    /// <summary>
    /// Each connected server's <c>instructions</c> from its initialize
    /// handshake, in name order — the reference renders these under
    /// <c># MCP Server Instructions</c> beside its own servers' blocks.
    /// </summary>
    public IReadOnlyList<(string Name, string Instructions)> ServerInstructions
    {
        get
        {
            lock (_servers)
            {
                return [.. _servers.Values
                    .Where(live => !string.IsNullOrWhiteSpace(live.Client.Instructions))
                    .OrderBy(live => live.Config.Name, StringComparer.Ordinal)
                    .Select(live => (live.Config.Name, live.Client.Instructions!))];
            }
        }
    }

    public IReadOnlyList<ITool> Tools
    {
        get
        {
            lock (_servers)
            {
                return [.. _servers.Values.SelectMany(s => s.Tools)];
            }
        }
    }

    /// <summary>Resources advertised by the connected servers, tagged with the server name.</summary>
    public IReadOnlyList<(string Server, McpResourceDescriptor Resource)> Resources
    {
        get
        {
            lock (_servers)
            {
                return [.. _servers.Values.SelectMany(s =>
                    s.Resources.Select(r => (s.Config.Name, r)))];
            }
        }
    }

    /// <summary>Prompts advertised by the connected servers, tagged with the server name.</summary>
    public IReadOnlyList<(string Server, McpPromptDescriptor Prompt)> Prompts
    {
        get
        {
            lock (_servers)
            {
                return [.. _servers.Values.SelectMany(s =>
                    s.Prompts.Select(p => (s.Config.Name, p)))];
            }
        }
    }

    /// <summary>Reads one resource from a connected server; throws McpException when either is unknown.</summary>
    public Task<string> ReadResourceAsync(string serverName, string uri, CancellationToken cancellationToken) =>
        FindClient(serverName).ReadResourceAsync(uri, cancellationToken);

    /// <summary>Expands one server prompt; throws McpException when the server is unknown.</summary>
    public Task<string> GetPromptAsync(
        string serverName, string promptName, JsonObject arguments, CancellationToken cancellationToken) =>
        FindClient(serverName).GetPromptAsync(promptName, arguments, cancellationToken);

    private IMcpClient FindClient(string serverName)
    {
        lock (_servers)
        {
            if (_servers.TryGetValue(serverName, out var live))
                return live.Client;
        }
        var connected = string.Join(", ", ConnectedToolCounts.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        throw new McpException($"No connected MCP server named '{serverName}'." +
            (connected.Length > 0 ? $" Connected: {connected}." : " No servers are connected."));
    }

    /// <summary>Tool count per connected server, for status displays.</summary>
    public IReadOnlyDictionary<string, int> ConnectedToolCounts
    {
        get
        {
            lock (_servers)
            {
                return _servers.Values.ToDictionary(
                    s => s.Config.Name, s => s.Tools.Count, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The URLs of the connected servers this session reaches over HTTP, for a
    /// host-reachability report. A stdio server contributes none.
    /// </summary>
    public IReadOnlyList<string> RemoteServerUrls
    {
        get
        {
            lock (_servers)
            {
                return [.. _servers.Values
                    .Where(s => s.Config.IsRemote && s.Config.Url is { Length: > 0 })
                    .Select(s => s.Config.Url!)];
            }
        }
    }

    public IReadOnlyList<string> DescribeStatus()
    {
        lock (_servers)
        {
            if (_servers.Count == 0)
                return [];
            return [.. _servers.Values.Select(s =>
                $"{s.Config.Name}  ({(s.Config.IsRemote ? s.Config.Url : $"{s.Config.Command} {string.Join(' ', s.Config.Args)}")})  {s.Tools.Count} tool(s): " +
                string.Join(", ", s.Tools.Select(t => t.SourceToolName)))];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            List<LiveServer> servers;
            lock (_servers)
            {
                servers = [.. _servers.Values];
                _servers.Clear();
                _configured.Clear();
                _disabled.Clear();
                _failures.Clear();
            }
            foreach (var server in servers)
                await StopServerAsync(server);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Why a configured server is not connected, from the last refresh that tried it.</summary>
public sealed record McpConnectFailure(string Message, bool NeedsAuthentication, DateTimeOffset At);

/// <summary>Session runtime status, including configured servers intentionally disconnected by SDK controls.</summary>
public sealed record McpServerStatus(string Name, string Status, int ToolCount, string? Error = null, bool Hosted = false);
