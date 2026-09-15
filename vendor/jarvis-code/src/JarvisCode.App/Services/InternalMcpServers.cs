using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The desktop shell's in-process MCP layer, ported from the reference app's
/// <c>InternalMcpServerManager.createProxyServers</c> (app.asar 1.40609.0.0,
/// <c>.vite/build/index2.chunk-CEBgETf7.js</c>).
///
/// The reference does not hand its engine a flat list of app tools: it declares
/// a set of server definitions, each with a name, a static tool list, an
/// optional <see cref="InternalMcpServerDefinition.GetDynamicTools"/> that adds
/// tools once a capability is live, and an <c>isEnabled</c> predicate answered
/// against the session. It then proxies each surviving tool to the model under
/// <c>mcp__{server}__{tool}</c>, drops any tool the user switched off
/// (<c>local:{server}:{tool}</c> = false), and skips a server whose tools are
/// all gone. That partition is the whole point: the wire name says which
/// subsystem answered, and a subsystem that is off contributes nothing.
///
/// This class is that layer. Tools stay ordinary <see cref="ITool"/>s — the
/// engine is untouched — and the rename happens here, at the boundary.
/// </summary>
public static class InternalMcpServers
{
    /// <summary>The prefix every in-process server's tools wear on the wire.</summary>
    public const string Prefix = "mcp__";

    /// <summary>
    /// The reference's per-tool preference key: <c>local:{server}:{tool}</c>,
    /// read as "false means off" so an absent key leaves the tool on.
    /// </summary>
    public static string ToggleKey(string serverName, string toolName) =>
        $"local:{serverName}:{toolName}";

    /// <summary>The wire name for one tool of one server.</summary>
    public static string WireName(string serverName, string toolName) =>
        $"{Prefix}{serverName}__{toolName}";

    /// <summary>
    /// Splits a wire name back into its server and tool halves. Returns false
    /// for anything that is not <c>mcp__server__tool</c> shaped — including a
    /// bare tool name and a two-part <c>mcp__x</c>.
    /// </summary>
    public static bool TrySplit(string wireName, out string serverName, out string toolName)
    {
        serverName = "";
        toolName = "";
        if (!wireName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = wireName[Prefix.Length..];
        var cut = rest.IndexOf("__", StringComparison.Ordinal);
        if (cut <= 0 || cut + 2 >= rest.Length)
        {
            return false;
        }

        serverName = rest[..cut];
        toolName = rest[(cut + 2)..];
        return true;
    }

    /// <summary>
    /// The tool half of a wire name, or the name itself when it carries no
    /// server prefix. This is the reference's own prefix normalisation — its
    /// batch validator and its transcript renderer both key off the short name,
    /// because the behaviour belongs to the tool and not to the server it was
    /// filed under.
    /// </summary>
    public static string ShortName(string wireName) =>
        TrySplit(wireName, out _, out var tool) ? tool : wireName;

    /// <summary>
    /// Composes the definitions into the tools a turn advertises. A server whose
    /// <see cref="InternalMcpServerDefinition.IsEnabled"/> says no contributes
    /// nothing, a tool switched off in <paramref name="toggles"/> is dropped,
    /// and a server left with no tools is skipped entirely — all three are the
    /// reference's own steps, in its order.
    /// </summary>
    /// <param name="configuredServerNames">
    /// MCP servers the user configured. One of those whose name normalizes onto
    /// a shell server's takes the name: the reference's own shadow rule (the
    /// second clause of its <c>ptr</c>), and the only thing standing between two
    /// servers and one wire name, where the loser would silently vanish.
    /// </param>
    public static IReadOnlyList<ITool> Compose(
        IEnumerable<InternalMcpServerDefinition> definitions,
        InternalMcpSessionContext context,
        IReadOnlyDictionary<string, bool>? toggles = null,
        IEnumerable<string>? configuredServerNames = null)
    {
        var shadowed = configuredServerNames is null
            ? []
            : new HashSet<string>(
                configuredServerNames.Select(JarvisCode.Core.Mcp.McpBuiltInServers.InternalKey),
                StringComparer.Ordinal);

        List<ITool> composed = [];
        foreach (var server in definitions)
        {
            if (server.IsEnabled is { } enabled && !enabled(context))
            {
                continue;
            }

            if (shadowed.Contains(JarvisCode.Core.Mcp.McpBuiltInServers.InternalKey(server.ServerName)))
            {
                continue;
            }

            var tools = server.GetDynamicTools is { } dynamic
                ? [.. server.Tools, .. dynamic()]
                : server.Tools;

            List<ITool> kept = [];
            foreach (var tool in tools)
            {
                if (toggles is not null &&
                    toggles.TryGetValue(ToggleKey(server.ServerName, tool.Name), out var on) &&
                    !on)
                {
                    continue;
                }

                server.LegacyNames.TryGetValue(tool.Name, out var legacy);
                kept.Add(new McpServerTool(server.ServerName, tool, legacy, server.AlwaysLoad, context));
            }

            // "Server has no enabled tools, skipping" — the reference drops the
            // whole server rather than advertising an empty one.
            if (kept.Count == 0)
            {
                continue;
            }

            composed.AddRange(kept);
        }

        return composed;
    }

    /// <summary>
    /// The resources the enabled servers serve, as <c>resources/list</c> would
    /// report them. The reference's own <c>handleListResources</c> is per server
    /// and only <c>visualize</c> implements one; the same <c>isEnabled</c> that
    /// removes a server's tools removes its resources with them.
    /// </summary>
    public static IReadOnlyList<(string ServerName, JarvisCode.Core.Mcp.McpResourceDescriptor Resource)>
        ListResources(
            IEnumerable<InternalMcpServerDefinition> definitions,
            InternalMcpSessionContext context)
    {
        List<(string, JarvisCode.Core.Mcp.McpResourceDescriptor)> listed = [];
        foreach (var server in definitions)
        {
            if (server.IsEnabled is { } enabled && !enabled(context))
            {
                continue;
            }

            foreach (var resource in server.Resources)
            {
                listed.Add((server.ServerName, resource));
            }
        }

        return listed;
    }

    /// <summary>
    /// The reference's <c>handleReadResource</c>: the first enabled server that
    /// answers for this uri wins, and a uri nothing serves reads as null rather
    /// than as an empty document.
    /// </summary>
    public static InternalMcpResourceContents? ReadResource(
        IEnumerable<InternalMcpServerDefinition> definitions,
        InternalMcpSessionContext context,
        string uri)
    {
        foreach (var server in definitions)
        {
            if (server.IsEnabled is { } enabled && !enabled(context))
            {
                continue;
            }

            if (server.ReadResource?.Invoke(uri) is { } contents)
            {
                return contents;
            }
        }

        return null;
    }

    /// <summary>
    /// Every server name this shell can produce. The turn's deferral predicate
    /// consults it: these wear <c>mcp__</c> names but are the app's own tools,
    /// not tools fetched from a configured MCP server, so they must never be
    /// hidden behind tool_search.
    /// </summary>
    public static readonly IReadOnlyList<string> ServerNames =
    [
        InternalMcpServerNames.ClaudeBrowser,
        InternalMcpServerNames.ClaudeInChrome,
        InternalMcpServerNames.ComputerUse,
        InternalMcpServerNames.CcdSession,
        InternalMcpServerNames.CcdSessionMgmt,
        InternalMcpServerNames.CcdDirectory,
        InternalMcpServerNames.Terminal,
        InternalMcpServerNames.McpRegistry,
        InternalMcpServerNames.ScheduledTasks,
        InternalMcpServerNames.Visualize,
        InternalMcpServerNames.AndroidEmulator,
    ];

    /// <summary>True for a wire name produced by this shell rather than by a configured MCP server.</summary>
    public static bool IsInternal(string wireName) =>
        TrySplit(wireName, out var server, out _) &&
        ServerNames.Contains(server, StringComparer.Ordinal);

    /// <summary>
    /// The reference's <c>alwaysLoad</c>, which reaches the wire as
    /// <c>_meta: {"anthropic/alwaysLoad": true}</c> and is the first thing its
    /// deferral predicate reads (CLI 2.1.251, <c>AO(e)</c>: <c>if (e.alwaysLoad
    /// === true) return false</c>). A tool without it is deferrable like any
    /// other MCP tool — in a ccd session that is every internal server except
    /// Claude_Browser, ccd_session and visualize.
    /// </summary>
    public static bool IsAlwaysLoad(ITool tool) => tool is McpServerTool { AlwaysLoad: true };

    /// <summary>
    /// The reference's per-call stall timeout: <c>Wft()</c> in
    /// <c>index.chunk-C5__TEgr.js</c> — <c>Hft = 3e5</c> as the floor and
    /// <c>Uft = 6e4</c> added to a configured <c>mcp.toolTimeoutSec</c>.
    /// </summary>
    public static readonly TimeSpan StallFloor = TimeSpan.FromMilliseconds(300_000);

    /// <summary>The grace the reference adds on top of a configured tool timeout.</summary>
    public static readonly TimeSpan StallGrace = TimeSpan.FromMilliseconds(60_000);

    /// <summary>
    /// <c>Wft()</c>: the floor when nothing is configured, otherwise the larger
    /// of the floor and the configured timeout plus the grace.
    /// </summary>
    public static TimeSpan StallTimeout(TimeSpan? configuredToolTimeout) =>
        configuredToolTimeout is { } configured && configured + StallGrace > StallFloor
            ? configured + StallGrace
            : StallFloor;

    /// <summary>
    /// The reference's stall result, verbatim. It names the *timeout*, not the
    /// real wait — the timer re-arms while a permission card is up, so the two
    /// differ, and the reference prints <c>c/1e3</c> either way.
    /// </summary>
    public static string StallMessage(string toolName, TimeSpan timeout) =>
        $"{toolName} timed out after {timeout.TotalSeconds:0.###}s. The underlying operation (browser " +
        "extension, CDP, Apple Events) may be stuck or unresponsive.";

    /// <summary>
    /// One tool of one internal server: the same tool, renamed onto the wire.
    ///
    /// A tool that used to carry a different name keeps that one as an alias, so
    /// a reopened session whose stored calls predate the rename still resolves.
    /// The bare *current* name is deliberately not an alias: Claude_Browser and
    /// claude-in-chrome both have a read_page, a computer and a navigate, so a
    /// bare alias would resolve to whichever server composed last — and no stored
    /// session ever used those bare names anyway.
    /// </summary>
    private sealed class McpServerTool(
        string serverName,
        ITool inner,
        IReadOnlyList<string>? legacyNames,
        bool alwaysLoad,
        InternalMcpSessionContext session)
        : ITool, IAliasedTool
    {
        public string Name { get; } = WireName(serverName, inner.Name);

        public string Description => inner.Description;

        public JsonObject InputSchema => inner.InputSchema;

        public bool IsReadOnly => inner.IsReadOnly;

        public IReadOnlyList<string> Aliases { get; } = legacyNames ?? [];

        /// <summary>Whether this tool is exempt from tool_search deferral.</summary>
        public bool AlwaysLoad { get; } = alwaysLoad;

        /// <summary>The tool underneath, for callers that dispatch by identity rather than name.</summary>
        public ITool Inner => inner;

        public string DescribeCall(JsonObject arguments) => inner.DescribeCall(arguments);

        /// <summary>
        /// Runs the tool, racing it against the reference's stall timeout. The
        /// reference does not cancel the losing call — it only stops waiting for
        /// it — so a subsystem that later wakes up does not find its work torn
        /// down half-done. The tool's exception is still observed, so a faulted
        /// abandoned task cannot surface as an unobserved-exception crash.
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var call = inner.ExecuteAsync(arguments, context, cancellationToken);

            // Most of these tools answer from memory and are already done here.
            // Arming a five-minute timer to watch a completed task would be two
            // allocations and a timer per call for nothing.
            if (call.IsCompleted)
            {
                return await call.ConfigureAwait(false);
            }

            var timeout = session.StallTimeout;
            using var stallCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var stall = StallAsync(timeout, session.HasPendingPermission, stallCancellation.Token);

            var finished = await Task.WhenAny(call, stall).ConfigureAwait(false);
            if (finished == call)
            {
                stallCancellation.Cancel();
                return await call.ConfigureAwait(false);
            }

            _ = call.ContinueWith(
                static faulted => _ = faulted.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return ToolResult.Error(StallMessage(inner.Name, timeout));
        }

        /// <summary>
        /// The reference's re-arming timer: each expiry that lands while a
        /// permission card is up schedules another interval instead of firing.
        /// Cancelled — by the call finishing or the turn ending — it never
        /// completes, so the race is decided by the call.
        /// </summary>
        private static async Task StallAsync(
            TimeSpan interval, Func<bool>? hasPendingPermission, CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                if (hasPendingPermission?.Invoke() != true)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>The reference's server names, spelled exactly as they reach the wire.</summary>
public static class InternalMcpServerNames
{
    public const string ClaudeBrowser = "Claude_Browser";
    public const string ClaudeInChrome = "claude-in-chrome";
    public const string ComputerUse = "computer-use";
    public const string CcdSession = "ccd_session";
    public const string CcdSessionMgmt = "ccd_session_mgmt";
    public const string CcdDirectory = "ccd_directory";
    public const string Terminal = "terminal";
    public const string McpRegistry = "mcp-registry";
    public const string ScheduledTasks = "scheduled-tasks";
    public const string Visualize = "visualize";

    /// <summary>
    /// The Android emulator server. Its name carries spaces — "Claude Code
    /// Android Emulator" — which the reference's own wire normalisation turns
    /// into underscores, so this is the normalised form the prefix is built
    /// from rather than the display name.
    /// </summary>
    public const string AndroidEmulator = "Claude_Code_Android_Emulator";
}

/// <summary>
/// What a server definition is enabled against. The reference answers
/// <c>isEnabled</c> with the session's type, whether a browser pane exists and
/// whether the session is remote; this app has one session type that reaches
/// here (the Code surface — Chat runs toolless) plus the same pane question.
/// </summary>
public sealed record InternalMcpSessionContext
{
    /// <summary>The reference's <c>ccd</c> session type — a Code-surface session.</summary>
    public const string CodeSessionType = "ccd";

    public string SessionType { get; init; } = CodeSessionType;

    public bool HasBrowserPane { get; init; } = true;

    /// <summary>Subagent turns take a snapshot registry and never re-read it.</summary>
    public bool IsSubagent { get; init; }

    /// <summary>
    /// The reference's <c>isSSH</c>, which removes ccd_directory, terminal and
    /// the Browser pane on a remote host: those three drive a UI that is on the
    /// machine the user is sitting at. This app has no remote-host mode, so it
    /// is always false — carried so the predicates read as the reference's.
    /// </summary>
    public bool IsSsh { get; init; }

    /// <summary>
    /// True when this session's model comes from somewhere other than Anthropic.
    /// The reference removes the connector registry for a third-party provider
    /// (its <c>JD().type !== "3p"</c>), because the directory it searches is the
    /// account's.
    /// </summary>
    public bool IsThirdPartyProvider { get; init; }

    /// <summary>
    /// The reference's <c>launchEnabled</c> setting, read its way: only an
    /// explicit false takes the Browser pane's server away.
    /// </summary>
    public bool BrowserPaneLaunchEnabled { get; init; } = true;

    /// <summary>
    /// Whether the browser-extension family is offered at all — the reference's
    /// <c>shouldEnableChromeExtensionBridge() &amp;&amp; !isDisabled()</c>. It is
    /// deliberately not a question about the session type: the extension server
    /// exists wherever the bridge does.
    /// </summary>
    public bool ChromeExtensionEnabled { get; init; } = true;

    /// <summary>
    /// Whether computer use is switched on. The reference resolves its
    /// computer-use server's <c>isEnabled</c> per turn rather than snapshotting
    /// it when the tools were built, so a switch flipped mid-session takes
    /// effect on the next model call.
    /// </summary>
    public bool ComputerUseEnabled { get; init; } = true;

    /// <summary>
    /// True while a permission card is on screen. The stall timer re-arms rather
    /// than firing whenever this says yes, so a call the user has not answered
    /// yet never reads as a stuck subsystem. Null (the default) means "nothing
    /// can be pending", which is what a headless host wants.
    /// </summary>
    public Func<bool>? HasPendingPermission { get; init; }

    /// <summary>
    /// How long a proxied call may run before the shell stops waiting for it.
    /// Resolved by the caller with <see cref="InternalMcpServers.StallTimeout"/>
    /// from the user's configured <c>mcp.toolTimeoutSec</c>, so the policy lives
    /// where the setting is read rather than being re-derived here.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = InternalMcpServers.StallFloor;

    /// <summary>
    /// True when this turn can defer tools behind tool_search. The reference
    /// gates the claude-in-chrome instruction block on it, because the block
    /// says nothing useful to a session whose tools are all loaded already.
    /// </summary>
    public bool ToolSearchAvailable { get; init; }
}

/// <summary>
/// One in-process MCP server: a name, the tools it always carries, the tools it
/// grows once a capability is live, and whether it is enabled at all.
/// </summary>
public sealed record InternalMcpServerDefinition(string ServerName, IReadOnlyList<ITool> Tools)
{
    /// <summary>
    /// Tools that exist only under a live capability — the reference's
    /// <c>getDynamicTools</c>. Evaluated per turn, so a grant made mid-session
    /// adds its tools to the next model call.
    /// </summary>
    public Func<IReadOnlyList<ITool>>? GetDynamicTools { get; init; }

    /// <summary>Answered per turn; false removes the whole server.</summary>
    public Func<InternalMcpSessionContext, bool>? IsEnabled { get; init; }

    /// <summary>
    /// The MCP resources this server advertises — the reference's
    /// <c>handleListResources</c>. Empty for every server but <c>visualize</c>,
    /// whose widget runtime is served as <c>ui://imagine/show-widget.html</c>.
    /// </summary>
    public IReadOnlyList<JarvisCode.Core.Mcp.McpResourceDescriptor> Resources { get; init; } = [];

    /// <summary>
    /// The reference's <c>handleReadResource</c>: the document behind one of
    /// <see cref="Resources"/>, or null for a uri this server does not serve.
    /// </summary>
    public Func<string, InternalMcpResourceContents?>? ReadResource { get; init; }

    /// <summary>
    /// The reference's <c>alwaysLoad</c>. It sets the flag per tool, but every
    /// server that sets it in a ccd session sets it on all of them, so this is
    /// a server-level switch: <c>Claude_Browser</c> (at the push site),
    /// <c>ccd_session</c> (<c>Zr()</c>) and <c>visualize</c>. Everything else
    /// stays deferrable.
    /// </summary>
    public bool AlwaysLoad { get; init; }

    /// <summary>
    /// The server's MCP <c>instructions</c> — the body the harness renders under
    /// <c>## {server}</c> inside <c># MCP Server Instructions</c>. Null for a
    /// server that supplies none, which is most of them.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Answered per turn: false suppresses <see cref="Instructions"/> without
    /// removing the server. The reference gates the claude-in-chrome block on
    /// tool search being available *and* some tool of that server actually being
    /// deferred, since the whole block is about loading deferred tools.
    /// </summary>
    public Func<InternalMcpSessionContext, bool>? AreInstructionsEnabled { get; init; }

    /// <summary>
    /// The names a tool used to carry in this port before it took the
    /// reference's. Keyed by the tool's current name, each becomes a resolvable
    /// alias so a session stored under an old name still replays — several,
    /// because one reference tool can replace a whole family of this app's own
    /// (the Android emulator's control replaces four bare android_* tools).
    /// Aliases are never advertised, so the wire surface stays the reference's.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LegacyNames { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>
/// One resource read out of an in-process server, in the shape
/// <c>resources/read</c> answers with: the uri that was asked for, its mime
/// type, its text, and the <c>_meta</c> the reference rides beside it (for the
/// widget runtime, the CSP the host must apply and the sandbox permissions it
/// grants). Core's <see cref="JarvisCode.Core.Mcp.McpClient"/> flattens a read
/// to text because a configured server's resources are read by the model; this
/// one is read by the window, which needs the metadata too.
/// </summary>
public sealed record InternalMcpResourceContents(
    string Uri, string MimeType, string Text, JsonObject? Meta = null);
