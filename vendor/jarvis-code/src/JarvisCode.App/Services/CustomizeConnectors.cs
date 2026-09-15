using JarvisCode.Core.Mcp;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's Type column. Only three of its six values can arise here:
/// a stdio server configured on this machine is Desktop, a remote https one is
/// Web, and one an installed plugin ships is Plugin. Community and Beta label
/// entries of the claude.ai gallery, which this app has no account for.
/// </summary>
public enum ConnectorKind
{
    Desktop,
    Web,
    Plugin,
}

/// <summary>The reference's Status column, in the states this app can reach.</summary>
public enum ConnectorStatus
{
    Connected,
    Connecting,
    NotConnected,
    NeedsAuthentication,
    FailedToConnect,
}

/// <summary>The reference's "Filter by status" values.</summary>
public enum ConnectorStatusFilter
{
    All,
    Connected,
    NotConnected,
}

/// <summary>One row of the Connectors table.</summary>
public sealed record ConnectorRow(
    McpServerConfig Config,
    ConnectorKind Kind,
    ConnectorStatus Status,
    int ToolCount,
    string? PluginName,
    bool IsUserScope,
    string? FailureMessage)
{
    public string Name => Config.Name;

    /// <summary>Where the row points: a remote server's URL, a local one's command line.</summary>
    public string Endpoint => Config.IsRemote
        ? Config.Url ?? ""
        : Config.Args.Count > 0 ? $"{Config.Command} {string.Join(' ', Config.Args)}" : Config.Command;

    /// <summary>Only a user-level connector can be edited or removed from this page.</summary>
    public bool IsRemovable => IsUserScope && PluginName is null;

    /// <summary>The reference groups a failed or unauthenticated row under "Needs attention".</summary>
    public bool NeedsAttention =>
        Status is ConnectorStatus.NeedsAuthentication or ConnectorStatus.FailedToConnect;
}

/// <summary>
/// The rules of the reference's Connectors page (c40525e86): how a configured
/// server becomes a row, what its Type and Status cells say, and what the
/// status filter and the search keep.
/// </summary>
public static class ConnectorListPresentation
{
    /// <summary>The Type cell.</summary>
    public static string KindLabel(ConnectorKind kind) => kind switch
    {
        ConnectorKind.Web => "Web",
        ConnectorKind.Plugin => "Plugin",
        _ => "Desktop",
    };

    /// <summary>The Status cell.</summary>
    public static string StatusLabel(ConnectorStatus status) => status switch
    {
        ConnectorStatus.Connected => "Connected",
        ConnectorStatus.Connecting => "Connecting",
        ConnectorStatus.NeedsAuthentication => "Needs authentication",
        ConnectorStatus.FailedToConnect => "Failed to connect",
        _ => "Not connected",
    };

    /// <summary>The action the row's button offers for this status.</summary>
    public static string ActionLabel(ConnectorStatus status) => status switch
    {
        ConnectorStatus.Connected => "Disconnect",
        ConnectorStatus.NeedsAuthentication or ConnectorStatus.FailedToConnect => "Reconnect",
        _ => "Connect",
    };

    /// <summary>"Provided by the {pluginName} plugin" — the line under a plugin row's name.</summary>
    public static string? ProvidedBy(ConnectorRow row) =>
        row.PluginName is { Length: > 0 } plugin ? $"Provided by the {plugin} plugin" : null;

    /// <summary>What a connected row says about its tools.</summary>
    public static string ToolsLabel(int count) => count switch
    {
        0 => "This connector has no tools available",
        1 => "Connected · 1 tool",
        _ => $"Connected · {count} tools",
    };

    /// <summary>Builds one row from the configuration and what the manager last did with it.</summary>
    public static ConnectorRow ToRow(
        McpServerConfig config,
        IReadOnlyDictionary<string, int> connectedToolCounts,
        IReadOnlyDictionary<string, McpConnectFailure> failures,
        bool isUserScope,
        string? pluginName,
        bool connecting = false)
    {
        var kind = pluginName is not null ? ConnectorKind.Plugin
            : config.IsRemote ? ConnectorKind.Web
            : ConnectorKind.Desktop;
        if (connectedToolCounts.TryGetValue(config.Name, out var tools))
            return new ConnectorRow(config, kind, ConnectorStatus.Connected, tools, pluginName, isUserScope, null);
        if (connecting)
            return new ConnectorRow(config, kind, ConnectorStatus.Connecting, 0, pluginName, isUserScope, null);
        if (failures.TryGetValue(config.Name, out var failure))
        {
            return new ConnectorRow(
                config, kind,
                failure.NeedsAuthentication ? ConnectorStatus.NeedsAuthentication : ConnectorStatus.FailedToConnect,
                0, pluginName, isUserScope, failure.Message);
        }
        return new ConnectorRow(config, kind, ConnectorStatus.NotConnected, 0, pluginName, isUserScope, null);
    }

    /// <summary>The reference's status filter.</summary>
    public static IReadOnlyList<ConnectorRow> Filter(IEnumerable<ConnectorRow> rows, ConnectorStatusFilter filter) =>
        filter switch
        {
            ConnectorStatusFilter.Connected => [.. rows.Where(r => r.Status == ConnectorStatus.Connected)],
            ConnectorStatusFilter.NotConnected => [.. rows.Where(r => r.Status != ConnectorStatus.Connected)],
            _ => [.. rows],
        };

    public static string FilterLabel(ConnectorStatusFilter filter) => filter switch
    {
        ConnectorStatusFilter.Connected => "Connected",
        ConnectorStatusFilter.NotConnected => "Not connected",
        _ => "All",
    };

    /// <summary>A row matches on its name and on where it points.</summary>
    public static IReadOnlyList<ConnectorRow> Search(IEnumerable<ConnectorRow> rows, string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0)
            return [.. rows];
        return [.. rows.Where(r =>
            r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            r.Endpoint.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            (r.PluginName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))];
    }

    /// <summary>Attention rows first, then the rest, each alphabetical.</summary>
    public static (IReadOnlyList<ConnectorRow> Attention, IReadOnlyList<ConnectorRow> Main) Split(
        IEnumerable<ConnectorRow> rows)
    {
        var ordered = rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return ([.. ordered.Where(r => r.NeedsAttention)], [.. ordered.Where(r => !r.NeedsAttention)]);
    }

    /// <summary>
    /// The empty state for a list that has rows but shows none: the reference
    /// tells the three cases apart by which control emptied it.
    /// </summary>
    public static string EmptyLabel(ConnectorStatusFilter filter, bool searching) =>
        searching ? "No connectors match your search"
        : filter == ConnectorStatusFilter.Connected ? "No connected connectors"
        : filter == ConnectorStatusFilter.NotConnected ? "No connectors to connect"
        : "No connectors match your search";
}

/// <summary>
/// The validation behind "Add custom connector" (ce99eb202), which the reference
/// runs as a two-step form: name and URL first, OAuth client credentials behind
/// Advanced on the second step.
/// </summary>
public static class CustomConnectorForm
{
    /// <summary>The reference's transports; the URL picks one and Advanced can override it.</summary>
    public const string StreamableHttp = "http";
    public const string Sse = "sse";

    /// <summary>The reference's <c>ne</c>: a URL whose path ends in /sse takes the legacy transport.</summary>
    public static string TransportFor(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
            return StreamableHttp;
        return parsed.AbsolutePath.TrimEnd('/').EndsWith("/sse", StringComparison.OrdinalIgnoreCase)
            ? Sse
            : StreamableHttp;
    }

    /// <summary>The reference's URL error, or null when the address is usable.</summary>
    public static string? UrlError(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var trimmed = url.Trim();
        if (!trimmed.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            return "URL must start with ‘https’";
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) &&
               parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? null
            : "Invalid URL format";
    }

    /// <summary>The reference's name error: a name already on the list.</summary>
    public static string? NameError(string? name, IEnumerable<string> existingNames)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return null;
        return existingNames.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase))
            ? $"Connector named ‘{trimmed}’ already exists"
            : null;
    }

    /// <summary>Whether the first step may continue: a name and a URL, both accepted.</summary>
    public static bool CanContinue(string? name, string? url, IEnumerable<string> existingNames) =>
        !string.IsNullOrWhiteSpace(name) &&
        !string.IsNullOrWhiteSpace(url) &&
        NameError(name, existingNames) is null &&
        UrlError(url) is null;

    /// <summary>The server the form describes, ready to be written to mcp.json.</summary>
    public static McpServerConfig ToConfig(
        string name, string url, string? transport, string? clientId, string? clientSecret) =>
        new(name.Trim(), "", [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            Type = transport is Sse ? Sse : StreamableHttp,
            Url = url.Trim(),
            OAuthClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim(),
            OAuthClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim(),
        };
}

/// <summary>
/// What the sign-in dialog shows while it drives <see cref="McpOAuthFlow"/>:
/// the reference's own three phases (c752b32f8), from "Waiting for browser…" to
/// the paste-the-callback-URL fallback the browser's silence raises.
/// </summary>
public enum ConnectorSignInPhase
{
    /// <summary>Discovery and registration, before the browser is opened.</summary>
    Starting,

    /// <summary>The browser is open and the loopback listener is waiting.</summary>
    WaitingForBrowser,

    /// <summary>The listener timed out; the user pastes the address the browser landed on.</summary>
    PasteCallback,

    /// <summary>The code is being exchanged for a token.</summary>
    Reconnecting,

    /// <summary>A token was stored.</summary>
    Connected,

    /// <summary>The flow ended without one.</summary>
    Failed,
}

/// <summary>The sentences the sign-in dialog prints, all the reference's own.</summary>
public static class ConnectorSignIn
{
    /// <summary>How long the reference waits on the browser before offering the paste box.</summary>
    public static readonly TimeSpan BrowserWait = TimeSpan.FromMinutes(3);

    public static string WaitingHeadline(string name) =>
        $"Finish signing in to {name} in your browser, then click Done.";

    public const string Waiting = "Waiting for browser…";

    public const string PasteCallback = "Paste the callback URL below";

    public const string Reconnecting = "Reconnecting…";

    public const string CallbackPlaceholder = "http://localhost:…/callback?code=…";

    public static string CallbackLabel(string name) => $"Callback URL for {name}";

    /// <summary>The reference's line when the loopback listener gave up first.</summary>
    public static string BrowserDidNotReturn(string name) =>
        $"The browser didn’t return for {name}. If you finished signing in, paste the callback URL below.";

    public static string Connected(string name) => $"Connected to {name}.";

    public static string StartFailed(string name, string error) =>
        $"Couldn’t start sign-in for {name}: {error}";

    public static string CompleteFailed(string name, string error) =>
        $"Couldn’t complete sign-in for {name}: {error}";

    public static string ReconnectFailed(string name, string error) =>
        $"Couldn’t reconnect {name}: {error}";

    public static string RefusedNonHttps(string name) =>
        $"Refused to open sign-in URL for {name}: must be https";

    public static string OpenBrowserQuestion(string hostname, string name) =>
        $"Open {hostname} to authorize {name}?";

    /// <summary>The reference refuses to open a sign-in URL that is not https.</summary>
    public static bool IsOpenable(string authorizeUrl) =>
        Uri.TryCreate(authorizeUrl, UriKind.Absolute, out var parsed) &&
        parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
