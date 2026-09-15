using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Tools;

/// <summary>
/// The reference's deferred-tools mechanism (CLI 2.1.257, <c>fpe</c>/<c>oZn</c>/
/// <c>eJe</c>/<c>X2t</c>): with tool search engaged, every tool that is not
/// <c>alwaysLoad</c> — the built-ins that declare <c>shouldDefer</c> and every
/// MCP tool — is held back from the advertised set and named to the model in a
/// <c>&lt;system-reminder&gt;</c> instead, until it fetches the schema with
/// <c>ToolSearch</c>. The registry is live: enabling a tool changes
/// <see cref="DeferredToolRegistry.All"/>, which the orchestrator re-reads before
/// each model call.
/// </summary>
public static class ToolDeferral
{
    /// <summary>
    /// The built-ins the reference declares <c>shouldDefer:!0</c> on (measured
    /// live on the desktop: these were the deferred names of a ccd session, and
    /// each carries the flag in the binary). The team task board's four are
    /// declared there too, behind its own <c>isEnabled</c>.
    /// </summary>
    private static readonly HashSet<string> DeferredBuiltIns = new(StringComparer.Ordinal)
    {
        "CronCreate", "CronDelete", "CronList",
        "EnterPlanMode", "ExitPlanMode",
        "EnterWorktree", "ExitWorktree",
        "ListPlugins", "ListSkills", "SearchPlugins", "SearchSkills", "SuggestPluginInstall",
        "Monitor", "NotebookEdit",
        "LSP",
        "SendMessage",
        "TaskOutput", "TaskStop",
        "TaskCreate", "TaskGet", "TaskList", "TaskUpdate",
        "WebFetch", "WebSearch",
        "DesignSync", "PushNotification", "RemoteTrigger",
        "list_mcp_resources", "read_mcp_resource",
    };

    /// <summary>
    /// The reference's per-tool <c>searchHint</c> for the tools this build defers,
    /// read off their definitions; the keyword search scores it after the name.
    /// </summary>
    private static readonly Dictionary<string, string> SearchHints = new(StringComparer.Ordinal)
    {
        ["CronCreate"] = "schedule a recurring or one-shot prompt",
        ["CronDelete"] = "cancel a scheduled cron job",
        ["CronList"] = "list active cron jobs",
        ["EnterPlanMode"] = "switch to plan mode to design an approach before coding",
        ["ExitPlanMode"] = "present plan for approval and start coding (plan mode only)",
        ["EnterWorktree"] = "create an isolated git worktree and switch into it",
        ["ExitWorktree"] = "exit a worktree session and return to the original directory",
        ["SendMessage"] = "send messages to agent teammates",
        ["TaskOutput"] = "read output/logs from a background task",
        ["TaskStop"] = "kill a running background task",
        ["TaskCreate"] = "create a task in the task list",
        ["TaskGet"] = "retrieve a task by ID",
        ["TaskList"] = "list all tasks",
        ["TaskUpdate"] = "update a task",
        ["WebFetch"] = "fetch and extract content from a URL",
        ["WebSearch"] = "search the web for current information",
        ["NotebookEdit"] = "edit Jupyter notebook cells (.ipynb)",
        ["LSP"] = "code intelligence (definitions, references, symbols, hover)",
        ["SuggestPluginInstall"] = "render a plugin install card",
        ["search_local_skills"] = "search the skills on disk by keyword",
        ["list_local_skills"] = "list the skills available in this session",
        ["list_local_plugins"] = "list the plugins installed here",
        ["search_local_plugins"] = "search the cloned marketplaces by keyword",
        ["suggest_local_skills"] = "offer a skill that is on disk but switched off",
        ["list_mcp_resources"] = "list resources from connected MCP servers",
        ["read_mcp_resource"] = "read a specific MCP resource by URI",
        ["Glob"] = "find files by name pattern or wildcard",
        ["Grep"] = "search file contents with regex (ripgrep)",
        ["Agent"] = "delegate work to a subagent",
        ["Skill"] = "invoke a slash-command skill",
        ["Write"] = "create or overwrite files",
        ["ListAgents"] = "list agents you can SendMessage to",
        ["AskUserQuestion"] = "prompt the user with a multiple-choice question",
        ["ReportFindings"] = "report code-review findings as a structured list",
    };

    /// <summary>
    /// The reference's own environment variable for the session kind; a
    /// background session never defers <c>EnterWorktree</c>, since a worktree is
    /// the first thing such a session does.
    /// </summary>
    public const string SessionKindVariable = "CLAUDE_CODE_SESSION_KIND";

    /// <summary>The reference's <c>shouldDefer</c> flag for a built-in of this name.</summary>
    /// <summary>
    /// This build's own deferred tools. They are separate from the set above
    /// because that one is a measurement of the reference and these are not the
    /// reference's tools: they read local skill directories and installed
    /// plugins where its similarly-shaped tools query a claude.ai catalog. They
    /// defer for the same reason its own do - niche discovery a turn rarely
    /// needs - but that is this build's decision, not a recorded one.
    /// </summary>
    private static readonly HashSet<string> DeferredAdditions = new(StringComparer.Ordinal)
    {
        "list_local_skills", "search_local_skills",
        "list_local_plugins", "search_local_plugins",
        "suggest_local_skills",
    };

    public static bool IsDeferredBuiltIn(string toolName) =>
        DeferredBuiltIns.Contains(toolName) || DeferredAdditions.Contains(toolName);

    /// <summary>The reference's <c>searchHint</c> for this tool, when it declares one.</summary>
    public static string? SearchHint(ITool tool) =>
        tool is ISearchHintTool hinted && !string.IsNullOrEmpty(hinted.SearchHint)
            ? hinted.SearchHint
            : SearchHints.GetValueOrDefault(tool.Name);

    /// <summary>
    /// The reference's <c>fpe</c>: <c>alwaysLoad</c> wins, then the names that are
    /// never deferred (<c>oZn</c>: ToolSearch itself, and EnterWorktree while
    /// <c>CLAUDE_CODE_SESSION_KIND</c> is <c>bg</c>), then every MCP tool defers,
    /// and a built-in defers when it declares <c>shouldDefer</c>.
    /// </summary>
    /// <param name="isMcp">True for a tool an MCP server (in-process or configured) composed.</param>
    /// <param name="alwaysLoad">The tool's own <c>alwaysLoad</c> flag.</param>
    public static bool ShouldDefer(ITool tool, bool isMcp, bool alwaysLoad, string? sessionKind = null)
    {
        if (alwaysLoad)
        {
            return false;
        }

        if (tool.Name == ToolSearchTool.ToolName)
        {
            return false;
        }

        if (tool.Name == "EnterWorktree" && sessionKind == "bg")
        {
            return false;
        }

        if (isMcp)
        {
            return true;
        }

        return IsDeferredBuiltIn(tool.Name);
    }

    /// <summary>
    /// The models the reference refuses tool search for (<c>tengu_tool_search_unsupported_models</c>'s
    /// default): matched as a substring of the lowercased id, as its <c>sW</c> does.
    /// </summary>
    public static bool IsModelSupported(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        return !lower.Contains("claude-3-5-haiku", StringComparison.Ordinal)
            && !lower.Contains("claude-3-haiku", StringComparison.Ordinal);
    }

    /// <summary>The reference's default share of the context window an auto mode compares against (<c>ldt</c>).</summary>
    public const int DefaultAutoPercent = 10;

    /// <summary>The reference's chars-per-token multiplier for the character fallback (<c>cGo</c>).</summary>
    public const double AutoCharsPerToken = 2.5;

    /// <summary>Tool search's mode, the reference's <c>eJe()</c> spellings.</summary>
    public enum Mode
    {
        /// <summary>Off: every tool is advertised (<c>standard</c>).</summary>
        Standard,

        /// <summary>On (<c>tst</c>).</summary>
        Enabled,

        /// <summary>On only when the deferrable tools are large enough (<c>tst-auto</c>).</summary>
        Auto,
    }

    /// <summary>
    /// The reference's <c>eJe()</c>: <c>CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS</c> forces
    /// standard; <c>ENABLE_TOOL_SEARCH</c> reads <c>auto:0</c> as on, <c>auto:100</c> as
    /// off, <c>auto</c>/<c>auto:N</c> as auto, a truthy value as on and a falsy one as
    /// off; unset is on.
    /// </summary>
    public static Mode ResolveMode(string? enableToolSearch, string? disableExperimentalBetas = null)
    {
        if (IsTruthy(disableExperimentalBetas))
        {
            return Mode.Standard;
        }

        var percent = ParseAutoPercent(enableToolSearch);
        if (percent == 0)
        {
            return Mode.Enabled;
        }

        if (percent == 100)
        {
            return Mode.Standard;
        }

        if (IsAuto(enableToolSearch))
        {
            return Mode.Auto;
        }

        if (IsTruthy(enableToolSearch))
        {
            return Mode.Enabled;
        }

        if (IsFalsy(enableToolSearch))
        {
            return Mode.Standard;
        }

        return Mode.Enabled;
    }

    /// <summary>
    /// The reference's <c>y_()</c> host rule: with <c>ENABLE_TOOL_SEARCH</c> unset, a
    /// first-party request that is not going to a first-party Anthropic host has tool
    /// search off, because a proxy may not forward <c>tool_reference</c> blocks.
    /// </summary>
    public static bool IsEnabledForHost(string? enableToolSearch, bool firstPartyKind, bool firstPartyHost) =>
        !(string.IsNullOrEmpty(enableToolSearch) && firstPartyKind && !firstPartyHost);

    /// <summary>The percent of the window an auto mode compares against (<c>cdt()</c>).</summary>
    public static int AutoPercent(string? enableToolSearch) =>
        ParseAutoPercent(enableToolSearch) ?? DefaultAutoPercent;

    /// <summary>
    /// The reference's auto decision on its character fallback: the deferrable
    /// tools' name + description + schema chars against <c>floor(window × pct) × 2.5</c>.
    /// </summary>
    public static bool AutoEngages(long deferrableChars, int contextWindowTokens, int percent)
    {
        var tokenThreshold = (long)Math.Floor(contextWindowTokens * (percent / 100.0));
        var charThreshold = (long)Math.Floor(tokenThreshold * AutoCharsPerToken);
        return deferrableChars >= charThreshold;
    }

    /// <summary>The characters a tool costs the request: name, description and schema, as the reference sums them.</summary>
    public static long AdvertisedChars(ITool tool) =>
        tool.Name.Length + (long)tool.Description.Length + tool.InputSchema.ToJsonString().Length;

    private static int? ParseAutoPercent(string? value)
    {
        if (value is null || !value.StartsWith("auto:", StringComparison.Ordinal))
        {
            return null;
        }

        if (!double.TryParse(value[5..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || double.IsNaN(parsed))
        {
            return null;
        }

        return (int)Math.Max(0, Math.Min(100, parsed));
    }

    private static bool IsAuto(string? value) =>
        !string.IsNullOrEmpty(value) && (value == "auto" || value.StartsWith("auto:", StringComparison.Ordinal));

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static bool IsFalsy(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Trim().ToLowerInvariant() is "0" or "false" or "no" or "off";
}

/// <summary>A tool that carries the reference's <c>searchHint</c> (an MCP tool's <c>anthropic/searchHint</c>).</summary>
public interface ISearchHintTool
{
    string? SearchHint { get; }
}

/// <summary>
/// A tool that declares how large a result of its own may be before the harness
/// persists it to disk — the reference's <c>anthropic/maxResultSizeChars</c>,
/// which its <c>j5e</c> caps at 50,000 like any other declared size.
/// </summary>
public interface IResultSizeTool
{
    int? MaxResultSizeChars { get; }
}

/// <summary>
/// The live registry tool search works over: the advertised set, the deferred
/// set, and which deferred names the harness has already announced to the model.
/// </summary>
public sealed class DeferredToolRegistry : IToolRegistry
{
    private readonly List<ITool> _active;
    private readonly Dictionary<string, ITool> _deferred;
    private readonly List<ITool> _enabled = [];
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly ISet<string>? _loaded;
    private readonly object _lock = new();

    /// <param name="loaded">
    /// The names this session has already fetched, shared across its turns. The
    /// reference gets that lifetime for free: its ToolSearch answers with
    /// <c>tool_reference</c> blocks the API expands server-side, and the
    /// tool_result carrying them is replayed on every later request, so a tool
    /// fetched once stays advertised for the whole conversation with nothing
    /// tracking it. This port writes the schemas into the result itself, so the
    /// session carries the names instead: a registry built for a later turn is
    /// seeded from this set and writes each new fetch back into it. Null keeps a
    /// fetch to this registry, which is what a preview or a one-shot wants.
    /// </param>
    public DeferredToolRegistry(
        IEnumerable<ITool> tools,
        Func<ITool, bool> defer,
        ISet<string>? loaded = null)
    {
        _loaded = loaded;
        _active = [];
        _deferred = new Dictionary<string, ITool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            // A name fetched earlier in the session is advertised again, in the
            // ordinal slot it would have held had it never been deferred.
            if (defer(tool) && loaded?.Contains(tool.Name) != true)
                _deferred[tool.Name] = tool;
            else
                _active.Add(tool);
        }
        _active.Add(new ToolSearchTool(this));
    }

    public IReadOnlyList<ITool> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _active, .. _enabled];
            }
        }
    }

    public ITool? Find(string name)
    {
        lock (_lock)
        {
            return _active.FirstOrDefault(t => t.Name == name)
                ?? _enabled.FirstOrDefault(t => t.Name == name)
                // Deferring hides a tool from the advertised set; it does not
                // take it away. The harness tells the model that calling one
                // directly "will fail with InputValidationError" — an answer
                // about the arguments, not about the name — so the name still
                // resolves and the call runs on whatever the model sent.
                ?? (_deferred.TryGetValue(name, out var hidden) ? hidden : null)
                // A tool's alias resolves here as it does in the plain registry.
                ?? _active.FirstOrDefault(Answers)
                ?? _enabled.FirstOrDefault(Answers)
                ?? _deferred.Values.FirstOrDefault(Answers);

            bool Answers(ITool tool) =>
                tool is IAliasedTool aliased && aliased.Aliases.Contains(name, StringComparer.Ordinal);
        }
    }

    /// <summary>Names still hidden, in ordinal order.</summary>
    public IReadOnlyList<string> DeferredNames
    {
        get
        {
            lock (_lock)
            {
                return [.. _deferred.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>
    /// The deferred names the harness has not yet announced, sorted the way the
    /// reference sorts its <c>addedLines</c>, and marked announced on the way
    /// out — so the first request lists every deferred tool and a later one
    /// only what appeared since.
    /// </summary>
    public IReadOnlyList<string> TakeUnannouncedNames()
    {
        lock (_lock)
        {
            var fresh = _deferred.Keys.Where(name => !_announced.Contains(name))
                .Order(StringComparer.Ordinal).ToList();
            foreach (var name in fresh)
            {
                _announced.Add(name);
            }

            return fresh;
        }
    }

    /// <summary>Moves one deferred tool into the advertised set; false when unknown.</summary>
    internal bool Enable(string name)
    {
        lock (_lock)
        {
            if (!_deferred.Remove(name, out var tool))
                return false;
            _enabled.Add(tool);
            // The session remembers, so the turn after this one advertises the
            // tool without a second fetch.
            _loaded?.Add(name);
            return true;
        }
    }

    internal IReadOnlyList<ITool> DeferredTools
    {
        get
        {
            lock (_lock)
            {
                return [.. _deferred.Values];
            }
        }
    }
}

/// <summary>
/// The reference's <c>ToolSearch</c> tool (its <c>M2t</c>): doc, schema, the
/// <c>select:</c> and keyword query forms with its ranking (<c>crn</c>), and the
/// result the doc promises — one <c>&lt;function&gt;</c> line per fetched tool
/// inside a <c>&lt;functions&gt;</c> block. The reference answers with
/// <c>tool_reference</c> blocks the API expands server-side; this port has no
/// such wire, so it writes the schemas itself and advertises the fetched tools
/// from the next model call on, which is what the doc says happens. That wire
/// also carries the reference's <i>lifetime</i> — the tool_result holding those
/// blocks is replayed on every later request, so a fetched tool stays advertised
/// for the rest of the conversation — which is why the registry takes the
/// session's fetched names and seeds itself from them.
/// </summary>
public sealed class ToolSearchTool(DeferredToolRegistry registry) : ITool, IAliasedTool
{
    public const string ToolName = "ToolSearch";

    /// <summary>The wire name this port used before it took the reference's; a stored session still replays.</summary>
    public const string LegacyName = "tool_search";

    private const int DefaultMaxResults = 5;

    /// <summary>The reference's doc, its <c>te + re + ne</c> form (a live fable-5-1 desktop session sends this one).</summary>
    public const string ReferenceDescription =
        "Fetches full schema definitions for deferred tools so they can be called.\n" +
        "\n" +
        "Deferred tools appear by name in <system-reminder> messages. Until fetched, only the name is known — " +
        "there is no parameter schema, so the tool cannot be invoked. This tool takes a query, matches it " +
        "against the deferred tool list, and returns the matched tools' complete JSONSchema definitions inside " +
        "a <functions> block. Once a tool's schema appears in that result, it is callable exactly like any tool " +
        "defined at the top of the prompt.\n" +
        "\n" +
        "Result format: each matched tool appears as one <function>{\"description\": \"...\", \"name\": \"...\", " +
        "\"parameters\": {...}}</function> line inside the <functions> block — the same encoding as the tool list " +
        "at the top of this prompt.\n" +
        "\n" +
        "Query forms:\n" +
        "- \"select:Read,Edit,Grep\" — fetch these exact tools by name\n" +
        "- \"notebook jupyter\" — keyword search, up to max_results best matches\n" +
        "- \"+slack send\" — require \"slack\" in the name, rank by remaining terms";

    /// <summary>The reference's answer when nothing matched.</summary>
    public const string NoMatch = "No matching deferred tools found";

    public string Name => ToolName;

    public IReadOnlyList<string> Aliases => [LegacyName];

    public string Description => ReferenceDescription;

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Query to find deferred tools. Use \"select:<tool_name>\" for direct selection, or keywords to search.",
            },
            ["max_results"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Maximum number of results to return (default: 5)",
                ["default"] = DefaultMaxResults,
            },
        },
        ["required"] = new JsonArray("query"),
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"ToolSearch({JsonArgs.GetString(arguments, "query") ?? "?"})";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = JsonArgs.GetString(arguments, "query")?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(ToolResult.Error("query is required."));

        var maxResults = Math.Max(1, JsonArgs.GetInt(arguments, "max_results") ?? DefaultMaxResults);
        var deferred = registry.DeferredTools;
        List<string> matches;
        if (query.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
        {
            matches = [];
            foreach (var name in query["select:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var match = deferred.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? registry.All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name;
                if (match is not null && !matches.Contains(match, StringComparer.Ordinal))
                    matches.Add(match);
            }
        }
        else
        {
            matches = KeywordSearch(query, deferred, registry.All, maxResults);
        }

        if (matches.Count == 0)
        {
            return Task.FromResult(ToolResult.Success(NoMatch));
        }

        var builder = new StringBuilder("<functions>\n");
        foreach (var name in matches)
        {
            var tool = deferred.FirstOrDefault(t => t.Name == name) ?? registry.Find(name);
            if (tool is null)
                continue;
            registry.Enable(name);
            builder.Append("<function>")
                .Append(FunctionLine(tool))
                .Append("</function>\n");
        }

        builder.Append("</functions>");
        return Task.FromResult(ToolResult.Success(builder.ToString()));
    }

    /// <summary>The doc's encoding of one fetched tool: description, name, parameters.</summary>
    public static string FunctionLine(ITool tool)
    {
        var line = new JsonObject
        {
            ["description"] = tool.Description,
            ["name"] = tool.Name,
            ["parameters"] = tool.InputSchema.DeepClone(),
        };
        return line.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// The reference's keyword search (<c>crn</c>): an exact name wins outright, an
    /// <c>mcp__</c> prefix lists the tools under it, otherwise every term is scored
    /// against the name's parts (12/10 for a whole part, 6/5 for a partial one, MCP
    /// first), the coarse name (12/10, 4/3), the joined name (3, only when nothing
    /// else scored), the search hint (4) and the description (2); a <c>+term</c> is
    /// required of the name, hint or description before scoring.
    /// </summary>
    public static List<string> KeywordSearch(string query, IReadOnlyList<ITool> deferred, IReadOnlyList<ITool> loaded, int maxResults)
    {
        var lower = query.ToLowerInvariant().Trim();
        var exact = deferred.FirstOrDefault(t => t.Name.ToLowerInvariant() == lower)
            ?? loaded.FirstOrDefault(t => t.Name.ToLowerInvariant() == lower);
        if (exact is not null)
        {
            return [exact.Name];
        }

        if (lower.StartsWith("mcp__", StringComparison.Ordinal) && lower.Length > 5)
        {
            var prefixed = deferred.Where(t => t.Name.ToLowerInvariant().StartsWith(lower, StringComparison.Ordinal))
                .Take(maxResults).Select(t => t.Name).ToList();
            if (prefixed.Count > 0)
            {
                return prefixed;
            }
        }

        var terms = lower.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var required = new List<string>();
        var others = new List<string>();
        foreach (var term in terms)
        {
            if (term.StartsWith('+') && term.Length > 1)
                required.Add(term[1..]);
            else
                others.Add(term);
        }

        var all = required.Count > 0 ? [.. required, .. others] : terms.ToList();
        var regexes = all.Distinct(StringComparer.Ordinal)
            .ToDictionary(t => t, t => new Regex($@"\b{Regex.Escape(t)}\b", RegexOptions.CultureInvariant), StringComparer.Ordinal);

        IEnumerable<ITool> candidates = deferred;
        if (required.Count > 0)
        {
            candidates = deferred.Where(tool =>
            {
                var parts = NameParts(tool);
                var description = tool.Description.ToLowerInvariant();
                var hint = ToolDeferral.SearchHint(tool)?.ToLowerInvariant() ?? "";
                return required.All(term =>
                    parts.Parts.Contains(term)
                    || parts.Parts.Any(p => p.Contains(term, StringComparison.Ordinal))
                    || parts.Coarse.Contains(term)
                    || parts.Coarse.Any(p => p.Contains(term, StringComparison.Ordinal))
                    || regexes[term].IsMatch(description)
                    || (hint.Length > 0 && regexes[term].IsMatch(hint)));
            });
        }

        var scored = new List<(string Name, int Score)>();
        foreach (var tool in candidates)
        {
            var parts = NameParts(tool);
            var description = tool.Description.ToLowerInvariant();
            var hint = ToolDeferral.SearchHint(tool)?.ToLowerInvariant() ?? "";
            var score = 0;
            foreach (var term in all)
            {
                var regex = regexes[term];
                if (parts.Parts.Contains(term))
                    score += parts.IsMcp ? 12 : 10;
                else if (parts.Parts.Any(p => p.Contains(term, StringComparison.Ordinal)))
                    score += parts.IsMcp ? 6 : 5;
                if (parts.Coarse.Contains(term))
                    score += parts.IsMcp ? 12 : 10;
                else if (parts.Coarse.Any(p => p.Contains(term, StringComparison.Ordinal)))
                    score += parts.IsMcp ? 4 : 3;
                if (parts.Full.Contains(term, StringComparison.Ordinal) && score == 0)
                    score += 3;
                if (hint.Length > 0 && regex.IsMatch(hint))
                    score += 4;
                if (regex.IsMatch(description))
                    score += 2;
            }

            if (score > 0)
                scored.Add((tool.Name, score));
        }

        return scored.OrderByDescending(s => s.Score).Take(maxResults).Select(s => s.Name).ToList();
    }

    /// <summary>The reference's <c>lrn</c>: how a name splits into searchable parts.</summary>
    internal static (List<string> Parts, List<string> Coarse, string Full, bool IsMcp) NameParts(ITool tool)
    {
        var name = tool.Name;
        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var rest = name[5..];
            var split = rest.IndexOf("__", StringComparison.Ordinal);
            var coarse = (split >= 0 ? new[] { rest[..split], rest[(split + 2)..] } : [rest])
                .Where(s => s.Length > 0).Select(s => s.ToLowerInvariant()).ToList();
            var parts = coarse.SelectMany(c => Regex.Split(c, @"[\s_.]+")).Where(p => p.Length > 0).ToList();
            return (parts, coarse, string.Join(' ', parts), true);
        }

        var spaced = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2").Replace('_', ' ').ToLowerInvariant();
        var words = spaced.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
        return (words, [name.ToLowerInvariant()], string.Join(' ', words), false);
    }
}
