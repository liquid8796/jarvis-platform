namespace JarvisCode.Cli;

/// <summary>
/// The root command's option surface — every flag the reference CLI 2.1.251
/// declares, with the same names, value shapes, and choice lists. Flags whose
/// feature is deliberately absent from Jarvis Code (cloud sessions, teleport,
/// Chrome integration…) still parse here and are refused with a clear error at
/// dispatch, so scripts fail loudly instead of behaving differently.
/// </summary>
internal static class RootOptions
{
    public static readonly string[] EffortChoices = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// What the reference prints for an --effort value it does not know. It is a
    /// warning, not an error: the run continues at the default effort.
    /// </summary>
    /// <summary>The reference's wording when --task-budget is not a positive integer.</summary>
    public const string TaskBudgetError = "--task-budget must be a positive integer";

    private static string? ValidateTaskBudget(string value) =>
        long.TryParse(value, out var tokens) && tokens > 0 ? null : TaskBudgetError;

    private static string? ValidateDollarBudget(string value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount > 0
            ? null : "--max-budget-usd must be a positive finite number";

    public static string UnknownEffortWarning(string value) =>
        $"Warning: Unknown --effort value '{value}' — ignoring it and using the default effort. " +
        $"Valid values: {string.Join(", ", EffortChoices)}.";

    public static readonly IReadOnlyList<OptionSpec> Specs =
    [
        new("--add-dir", ValuePlaceholder: "<directories...>", Variadic: true),
        new("--agent", ValuePlaceholder: "<agent>"),
        new("--agents", ValuePlaceholder: "<json>"),
        new("--allow-dangerously-skip-permissions"),
        new("--allowedTools", ValuePlaceholder: "<tools...>", Variadic: true, Aliases: ["--allowed-tools"]),
        new("--append-system-prompt", ValuePlaceholder: "<prompt>"),
        new("--append-system-prompt-file", ValuePlaceholder: "<file>", Hidden: true),
        new("--autocompact", ValuePlaceholder: "<auto|tokens>"),
        new("--ax-screen-reader"),
        new("--bg", Aliases: ["--background"]),
        new("--bare"),
        new("--betas", ValuePlaceholder: "<betas...>", Variadic: true),
        new("--brief"),
        new("--chrome"),
        new("--cloud", ValuePlaceholder: "[description|session_id|url]", OptionalValue: true),
        new("--continue", Short: "-c"),
        new("--dangerously-skip-permissions"),
        new("--debug", Short: "-d", ValuePlaceholder: "[filter]", OptionalValue: true),
        new("--debug-file", ValuePlaceholder: "<path>"),
        new("--disable-slash-commands"),
        new("--disallowedTools", ValuePlaceholder: "<tools...>", Variadic: true, Aliases: ["--disallowed-tools"]),
        // Deliberately not a commander choice: the reference validates --effort
        // itself, warning and falling back to the default rather than failing.
        new("--effort", ValuePlaceholder: "<level>"),
        new("--environment", ValuePlaceholder: "<environment_id>"),
        new("--exclude-dynamic-system-prompt-sections"),
        new("--fallback-model", ValuePlaceholder: "<model>"),
        new("--file", ValuePlaceholder: "<specs...>", Variadic: true),
        new("--fork-session"),
        new("--forward-subagent-text"),
        new("--from-pr", ValuePlaceholder: "[value]", OptionalValue: true),
        new("--ide"),
        new("--include-hook-events"),
        new("--include-partial-messages"),
        new("--input-format", ValuePlaceholder: "<format>", Choices: ["text", "stream-json"]),
        new("--json-schema", ValuePlaceholder: "<schema>"),
        new("--max-budget-usd", ValuePlaceholder: "<amount>", Validate: ValidateDollarBudget),
        // Hidden in the reference's help, and documented there as print-only:
        // an interactive session has no turn cap at all.
        new("--max-turns", ValuePlaceholder: "<turns>", Hidden: true),
        new("--mcp-config", ValuePlaceholder: "<configs...>", Variadic: true),
        new("--model", ValuePlaceholder: "<model>"),
        new("--name", Short: "-n", ValuePlaceholder: "<name>"),
        new("--no-chrome"),
        new("--no-session-persistence"),
        new("--output-format", ValuePlaceholder: "<format>", Choices: ["text", "json", "stream-json"]),
        new("--permission-mode", ValuePlaceholder: "<mode>",
            Choices: ["acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan"], HiddenChoices: ["default"]),
        // New in the reference's 2.1.260 help: who answers a permission prompt
        // under --print. A commander choice there, so an unknown value fails the
        // way one does. Stream-json routes host prompts over its control channel.
        new("--permission-prompts", ValuePlaceholder: "<target>", Choices: ["host", "none"]),
        new("--permission-prompt-tool", ValuePlaceholder: "<tool>", Hidden: true),
        // Hidden in the reference: planning instructions that replace its five phases.
        new("--plan-mode-instructions", ValuePlaceholder: "<instructions>", Hidden: true),
        new("--plugin-dir", ValuePlaceholder: "<path>", Variadic: false),
        new("--plugin-url", ValuePlaceholder: "<url>", Variadic: false),
        new("--print", Short: "-p"),
        new("--prompt-suggestions", ValuePlaceholder: "[value]", OptionalValue: true,
            Choices: ["true", "false", "1", "0", "yes", "no", "on", "off"]),
        new("--remote-control", ValuePlaceholder: "[name]", OptionalValue: true),
        new("--remote-control-session-name-prefix", ValuePlaceholder: "<prefix>"),
        new("--replay-user-messages"),
        new("--restricted"),
        new("--resume", Short: "-r", ValuePlaceholder: "[value]", OptionalValue: true),
        new("--safe-mode"),
        new("--session-id", ValuePlaceholder: "<uuid>"),
        new("--setting-sources", ValuePlaceholder: "<sources>"),
        new("--settings", ValuePlaceholder: "<file-or-json>"),
        new("--strict-mcp-config"),
        new("--task-budget", ValuePlaceholder: "<tokens>", Validate: ValidateTaskBudget, Hidden: true),
        new("--system-prompt", ValuePlaceholder: "<prompt>"),
        new("--system-prompt-file", ValuePlaceholder: "<file>", Hidden: true),
        // New in the reference's 2.1.257 help: whether the system prompt is
        // recorded once per conversation and replayed verbatim. A commander
        // choice there, so an unknown value fails the way one does.
        new("--system-prompt-snapshot", ValuePlaceholder: "<on|off>", Choices: ["on", "off"]),
        new("--teleport", ValuePlaceholder: "[session]", OptionalValue: true),
        new("--tmux"),
        new("--tools", ValuePlaceholder: "<tools...>", Variadic: true),
        new("--verbose"),
        new("--worktree", Short: "-w", ValuePlaceholder: "[name]", OptionalValue: true),
    ];

    /// <summary>
    /// Flags whose features are deliberately absent (account/cloud-bound, or a
    /// harness this build does not carry). Keyed by option key; the value names
    /// the reason shown in the refusal.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Unsupported = new Dictionary<string, string>
    {
        ["cloud"] = "cloud sessions",
        ["environment"] = "self-hosted cloud environments",
        ["file"] = "cloud file resources",
        ["remote-control"] = "Remote Control",
        ["remote-control-session-name-prefix"] = "Remote Control",
        ["teleport"] = "teleport sessions",
    };
}
