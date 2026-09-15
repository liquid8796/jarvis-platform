using JarvisCode.Core.Permissions;

namespace JarvisCode.Cli;

/// <summary>Typed view over the root command's parsed arguments.</summary>
internal sealed record CliOptions
{
    public required string Prompt { get; init; }
    public bool Print { get; init; }
    public string OutputFormat { get; init; } = "text";
    public string InputFormat { get; init; } = "text";
    public bool Verbose { get; init; }
    public bool Debug { get; init; }
    public string? DebugFile { get; init; }
    public string? Model { get; init; }
    public string? FallbackModel { get; init; }
    public bool IncludePartialMessages { get; init; }
    public bool IncludeHookEvents { get; init; }
    public bool ForwardSubagentText { get; init; }
    public bool AutoConnectIde { get; init; }
    public bool? ChromeEnabled { get; init; }
    public bool Bare { get; init; }
    public bool SafeMode { get; init; }
    public string? SettingSources { get; init; }
    public string? AgentProfile { get; init; }
    public string? InlineAgents { get; init; }
    public IReadOnlyList<string> Betas { get; init; } = [];
    public IReadOnlyList<string> PluginDirectories { get; init; } = [];
    public IReadOnlyList<string> PluginUrls { get; init; } = [];
    public bool Worktree { get; init; }
    public bool Tmux { get; init; }
    public string? WorktreeName { get; init; }
    public bool FromPr { get; init; }
    public string? FromPrValue { get; init; }
    public bool Background { get; init; }
    public bool ExcludeDynamicSections { get; init; }
    public bool? PromptSuggestions { get; init; }
    public bool ReplayUserMessages { get; init; }
    public string PermissionPrompts { get; init; } = "host";
    public string? PermissionPromptTool { get; init; }
    public string? JsonSchema { get; init; }
    public decimal? MaxBudgetUsd { get; init; }
    public string? Effort { get; init; }

    /// <summary>The turn's token target; workflow scripts read it as `budget`.</summary>
    public string? TaskBudget { get; init; }

    /// <summary>
    /// --max-turns: the reference applies it only in print mode ("This will early
    /// exit the conversation after the specified number of turns"), so an
    /// interactive run keeps the uncapped loop whatever the flag says.
    /// </summary>
    public int? MaxTurns { get; init; }

    /// <summary>--plan-mode-instructions: replaces the reference's five-phase plan workflow.</summary>
    public string? PlanModeInstructions { get; init; }
    public bool Continue { get; init; }
    public bool Resume { get; init; }
    public string? ResumeValue { get; init; }
    public bool ForkSession { get; init; }
    public string? SessionId { get; init; }
    public string? SessionName { get; init; }
    public IReadOnlyList<string> AddDirs { get; init; } = [];
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];
    public IReadOnlyList<string> Tools { get; init; } = [];
    public bool HasToolsFilter { get; init; }
    public string? PermissionModeName { get; init; }
    public bool DangerouslySkipPermissions { get; init; }
    public string? SystemPrompt { get; init; }
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// The reference's --system-prompt-snapshot: on by default for the built-in
    /// prompt, off when --system-prompt or --append-system-prompt supplies text
    /// that should apply fresh each request, and settable either way.
    /// </summary>
    public bool SystemPromptSnapshot { get; init; } = true;

    /// <summary>The reference's --restricted (or CLAUDE_CODE_RESTRICTED=1).</summary>
    public bool Restricted { get; init; }
    public string? Settings { get; init; }
    public IReadOnlyList<string> McpConfigs { get; init; } = [];
    public bool StrictMcpConfig { get; init; }
    public string? Autocompact { get; init; }
    public bool NoSessionPersistence { get; init; }
    public bool DisableSlashCommands { get; init; }
    public bool Brief { get; init; }
    public bool AxScreenReader { get; init; }

    /// <summary>Maps the CLI's mode names onto the engine's; "dontAsk" is Auto with prompts auto-denied.</summary>
    public (PermissionMode Mode, bool DontAsk) ResolvePermissionMode()
    {
        if (DangerouslySkipPermissions)
        {
            return (PermissionMode.Bypass, false);
        }

        return PermissionModeName?.ToLowerInvariant() switch
        {
            "acceptedits" => (PermissionMode.AcceptEdits, false),
            "bypasspermissions" or "bypass" => (PermissionMode.Bypass, false),
            "manual" or "default" => (PermissionMode.Manual, false),
            "plan" => (PermissionMode.Plan, false),
            "dontask" => (PermissionMode.Auto, true),
            _ => (PermissionMode.Auto, false),
        };
    }

    /// <summary>The engine's effort names for the wire's lowercase spellings.</summary>
    public static string? MapEffort(string? wire) => wire switch
    {
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra high",
        "max" => "Max",
        _ => null,
    };

    public static CliOptions From(ParsedArgs args) => new()
    {
        Prompt = string.Join(' ', args.Positionals),
        Print = args.Has("print") || args.Has("output-format") || args.Has("input-format"),
        OutputFormat = args.Value("output-format") ?? "text",
        InputFormat = args.Value("input-format") ?? "text",
        Verbose = args.Has("verbose"),
        Debug = args.Has("debug") || args.Has("debug-file"),
        DebugFile = args.Value("debug-file"),
        Model = args.Value("model"),
        FallbackModel = args.Value("fallback-model"),
        IncludePartialMessages = args.Has("include-partial-messages"),
        IncludeHookEvents = args.Has("include-hook-events"),
        ForwardSubagentText = args.Has("forward-subagent-text"),
        AutoConnectIde = args.Has("ide"),
        ChromeEnabled = args.OrderedOptions.LastOrDefault(key => key is "chrome" or "no-chrome") switch
        { "chrome" => true, "no-chrome" => false, _ => null },
        Bare = args.Has("bare"),
        SafeMode = args.Has("safe-mode"),
        SettingSources = args.Value("setting-sources"),
        AgentProfile = args.Value("agent"),
        InlineAgents = args.Value("agents"),
        Betas = CommandLine.SplitList(args.ValueList("betas")),
        PluginDirectories = args.ValueList("plugin-dir"),
        PluginUrls = args.ValueList("plugin-url"),
        Worktree = args.Has("worktree"),
        Tmux = args.Has("tmux"),
        WorktreeName = args.Value("worktree"),
        FromPr = args.Has("from-pr"),
        FromPrValue = args.Value("from-pr"),
        Background = args.Has("bg"),
        ExcludeDynamicSections = args.Has("exclude-dynamic-system-prompt-sections"),
        PromptSuggestions = args.Has("prompt-suggestions")
            ? args.Value("prompt-suggestions") is not ("false" or "0" or "no" or "off") : null,
        ReplayUserMessages = args.Has("replay-user-messages"),
        PermissionPrompts = args.Value("permission-prompts") ?? "host",
        PermissionPromptTool = args.Value("permission-prompt-tool"),
        JsonSchema = args.Value("json-schema"),
        MaxBudgetUsd = decimal.TryParse(args.Value("max-budget-usd"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var maxBudget) ? maxBudget : null,
        Effort = args.Value("effort"),
        TaskBudget = args.Value("task-budget"),
        MaxTurns = int.TryParse(args.Value("max-turns"), out int maxTurns) && maxTurns > 0 ? maxTurns : null,
        PlanModeInstructions = args.Value("plan-mode-instructions"),
        Continue = args.Has("continue"),
        Resume = args.Has("resume"),
        ResumeValue = args.Value("resume"),
        ForkSession = args.Has("fork-session"),
        SessionId = args.Value("session-id"),
        SessionName = args.Value("name"),
        AddDirs = args.ValueList("add-dir"),
        AllowedTools = CommandLine.SplitList(args.ValueList("allowedTools")),
        DisallowedTools = CommandLine.SplitList(args.ValueList("disallowedTools")),
        Tools = CommandLine.SplitList(args.ValueList("tools")),
        HasToolsFilter = args.Has("tools"),
        PermissionModeName = args.Value("permission-mode"),
        DangerouslySkipPermissions = args.Has("dangerously-skip-permissions"),
        Restricted = args.Has("restricted") ||
                     (Environment.GetEnvironmentVariable("CLAUDE_CODE_RESTRICTED")?.Trim().ToLowerInvariant()
                         is "1" or "true" or "yes" or "on"),
        SystemPrompt = PromptText(args, "system-prompt"),
        AppendSystemPrompt = PromptText(args, "append-system-prompt"),
        SystemPromptSnapshot = args.Value("system-prompt-snapshot") switch
        {
            "on" => true,
            "off" => false,
            _ => args.Value("system-prompt") is null && args.Value("append-system-prompt") is null &&
                 !args.Has("system-prompt-file") && !args.Has("append-system-prompt-file"),
        },
        Settings = args.Value("settings"),
        McpConfigs = args.ValueList("mcp-config"),
        StrictMcpConfig = args.Has("strict-mcp-config"),
        Autocompact = args.Value("autocompact"),
        NoSessionPersistence = args.Has("no-session-persistence"),
        DisableSlashCommands = args.Has("disable-slash-commands"),
        Brief = args.Has("brief"),
        AxScreenReader = args.Has("ax-screen-reader"),
    };

    private static string? PromptText(ParsedArgs arguments, string name)
    {
        if (arguments.Value(name + "-file") is not { } file) return arguments.Value(name);
        if (arguments.Has(name)) throw new CliError("Use either --" + name + " or --" + name + "-file.");
        try { return System.IO.File.ReadAllText(System.IO.Path.GetFullPath(file)); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { throw new CliError("Could not read --" + name + "-file: " + ex.Message); }
    }
}
