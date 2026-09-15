namespace JarvisCode.Core.Mcp;

/// <summary>
/// The reference CLI's own view of MCP servers it did not configure: the wire
/// name it gives a server's tools, and which server names belong to the harness
/// rather than to the user.
///
/// This is the CLI half of the built-in-MCP surface, distinct from
/// <c>JarvisCode.App.Services.InternalMcpServers</c>, which is the desktop
/// half. The CLI never hosts these servers — they arrive over its <c>sdk</c>
/// transport from whatever host is running it — but it does carry the
/// authoritative list of their names, because several rules key off "is this
/// server the harness's own".
///
/// Measured from CLI 2.1.251 (<c>chunk-fp51h7wf.js</c>, <c>chunk-0v0qa21m.js</c>)
/// and confirmed identical in the 2.1.247 build the desktop runs.
/// </summary>
public static class McpBuiltInServers
{
    /// <summary>The reference's <c>ide</c> server: an editor extension, never this process.</summary>
    public const string IdeServerName = "ide";

    /// <summary>
    /// Server names the reference treats as internal whatever else is
    /// configured — its <c>_Sn</c>. Matched against the raw name, not the
    /// normalized one, which is why <c>IDE</c> does not qualify while
    /// <c>Remote-Devices</c> does (through the prefix list below).
    /// </summary>
    public static readonly IReadOnlyList<string> UnconditionallyInternalNames =
        [IdeServerName, "remote-devices"];

    private static readonly HashSet<string> AlwaysInternal =
        new(UnconditionallyInternalNames, StringComparer.Ordinal);

    /// <summary>Normalized names that are internal only when they match whole — its <c>fZ</c>.</summary>
    public static readonly IReadOnlyList<string> ExactlyMatchedNames =
    [
        "workspace", "terminal", "office", "visualize",
        "window_halo", "dev_debug", "ccd_session", "ccd_session_mgmt",
    ];

    private static readonly HashSet<string> ExactInternal =
        new(ExactlyMatchedNames, StringComparer.Ordinal);

    /// <summary>Normalized prefixes that make a name internal — its <c>pZ</c>, in its order.</summary>
    public static readonly IReadOnlyList<string> PrefixMatchedNames =
    [
        "claude_in_chrome", "claude_browser", "claude_preview",
        "claude_code_ios_simulator", "claude_code_android_emulator",
        "computer_use", "framebuffer", "plugins", "skills", "mcp_registry",
        "scheduled_tasks", "cowork", "session_info", "dispatch",
        "remote_devices", "ccd_directory",
    ];

    private static readonly string[] PrefixInternal = [.. PrefixMatchedNames];

    /// <summary>
    /// The two <c>ide</c> tools the reference lets the model see — its <c>xr</c>.
    /// The rest of that server's surface (openDiff, close_tab, closeAllDiffTabs …)
    /// is the harness's to call, not the model's.
    /// </summary>
    public static readonly IReadOnlyList<string> IdeModelVisibleTools =
        ["mcp__ide__executeCode", "mcp__ide__getDiagnostics"];

    /// <summary>
    /// The reference's <c>ln</c>: the normalization every MCP server and tool
    /// name goes through on its way to the wire. Hyphens survive; everything
    /// outside <c>[a-zA-Z0-9_-]</c> becomes an underscore. A claude.ai connector
    /// additionally has its underscore runs collapsed and trimmed, so its opaque
    /// id stays readable.
    /// </summary>
    public static string Ln(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(chars[i]) || chars[i] is '_' or '-'))
            {
                chars[i] = '_';
            }
        }

        var normalized = new string(chars);
        if (!name.StartsWith("claude.ai ", StringComparison.Ordinal))
        {
            return normalized;
        }

        var collapsed = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (c != '_' || collapsed.Length == 0 || collapsed[^1] != '_')
            {
                collapsed.Append(c);
            }
        }

        return collapsed.ToString().Trim('_');
    }

    /// <summary>The reference's <c>Ul</c>: the prefix one server's tools all wear.</summary>
    public static string WirePrefix(string serverName) => $"mcp__{Ln(serverName)}__";

    /// <summary>
    /// The reference's <c>xc</c>: the wire name of one tool of one server. Both
    /// halves go through <see cref="Ln"/> and neither is truncated — a length
    /// cap would let two long tool names collapse onto one wire name, and the
    /// loser of that collision silently disappears from the tool list.
    /// </summary>
    public static string WireName(string serverName, string toolName) =>
        WirePrefix(serverName) + Ln(toolName);

    /// <summary>
    /// The reference's <c>DP</c>: the looser normalization the internal-name
    /// test uses. Unlike <see cref="Ln"/> it folds case and hyphens, so
    /// <c>claude-in-chrome</c>, <c>Claude In Chrome</c> and
    /// <c>claude_in_chrome</c> are one name.
    /// </summary>
    public static string InternalKey(string name)
    {
        // A *run* of invalid characters collapses to one underscore, which is
        // what the `+` in the reference's character class does; an underscore
        // already in the name is valid and so never joins that run.
        var builder = new System.Text.StringBuilder(name.Length);
        bool inRun = false;
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            {
                builder.Append(c == '-' ? '_' : char.ToLowerInvariant(c));
                inRun = false;
            }
            else
            {
                if (!inRun)
                {
                    builder.Append('_');
                }

                inRun = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The reference's <c>ptr</c>: does this server name belong to the harness?
    ///
    /// <paramref name="configuredServerNames"/> is what the user configured. A
    /// configured server of the same normalized name wins — the reference lets
    /// the user's own server keep the name rather than have a built-in shadow
    /// it — except for the two names in <see cref="AlwaysInternal"/>, which are
    /// answered before that check.
    /// </summary>
    public static bool IsBuiltInName(string serverName, IEnumerable<string>? configuredServerNames = null)
    {
        if (AlwaysInternal.Contains(serverName))
        {
            return true;
        }

        var key = InternalKey(serverName);
        if (configuredServerNames is not null &&
            configuredServerNames.Any(name => InternalKey(name) == key))
        {
            return false;
        }

        return ExactInternal.Contains(key) ||
               PrefixInternal.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// The reference's <c>tn</c>: may the model see this tool? Everything that
    /// is not the <c>ide</c> server's may; of that server's, only the two the
    /// reference publishes.
    /// </summary>
    public static bool IsModelVisible(string wireName) =>
        !wireName.StartsWith("mcp__ide__", StringComparison.Ordinal) ||
        IdeModelVisibleTools.Contains(wireName, StringComparer.Ordinal);
}
