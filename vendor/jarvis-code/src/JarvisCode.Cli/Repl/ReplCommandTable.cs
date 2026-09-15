namespace JarvisCode.Cli.Repl;

/// <summary>What a command does when it is run.</summary>
internal enum ReplCommandKind
{
    /// <summary>Runs locally and prints; the turn loop keeps going.</summary>
    Local,

    /// <summary>Opens a dialog the REPL draws.</summary>
    Dialog,

    /// <summary>Sends a prompt to the model, like a typed message.</summary>
    Prompt,

    /// <summary>Ends the session.</summary>
    Exit,

    /// <summary>Refuses with the reason the feature is not available here.</summary>
    Unavailable,
}

/// <summary>One entry of the REPL's command surface.</summary>
internal sealed record ReplCommand(
    string Name,
    string Description,
    ReplCommandKind Kind = ReplCommandKind.Local,
    string? ArgumentHint = null,
    IReadOnlyList<string>? Aliases = null,
    string? Unavailable = null)
{
    /// <summary>This entry's own name plus its aliases — what a typed command resolves against.</summary>
    public IEnumerable<string> Names => Aliases is null ? [Name] : [Name, .. Aliases];

    public bool Matches(string typed) =>
        Names.Any(name => string.Equals(name, typed, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The REPL's built-in command surface: every locally implementable command the
/// reference CLI 2.1.257 offers, its aliases, and the ones whose feature is
/// account- or product-bound, which parse and then refuse with the reason
/// rather than reading as unknown. Skills, MCP prompts and plugin commands join
/// this list at runtime through the skill catalogue.
/// </summary>
internal static class ReplCommandTable
{
    /// <summary>The reference's own refusal shape for a feature this build does not carry.</summary>
    public static string NotAvailable(string name, string reason) =>
        $"/{name} ({reason}) is not available in Jarvis Code.";

    public static readonly IReadOnlyList<ReplCommand> Commands =
    [
        new("add-dir", "Add a new working directory", ArgumentHint: "<path>"),
        new("autocompact", "Set how full the context gets before auto-summarizing",
            ArgumentHint: "[auto|<tokens>]"),
        new("batch", JarvisCode.App.Services.BatchCommand.Description, ReplCommandKind.Prompt,
            ArgumentHint: JarvisCode.App.Services.BatchCommand.ArgumentHint),
        new("branch", "Create a branch of the current conversation at this point"),
        new("brief", "Toggle brief-only mode"),
        new("cd", "Move this session to a new working directory", ArgumentHint: "<path>"),
        new("clear", "Start a new session with empty context; previous session stays on disk (resumable with /resume)"),
        new("code-review", "Find and verify bugs on the current branch or a pull request",
            ReplCommandKind.Prompt, ArgumentHint: "[level|PR]"),
        new("color", "Set the prompt bar color for this session", ArgumentHint: "<name|#hex|off>"),
        new("compact", "Free up context by summarizing the conversation so far",
            ArgumentHint: "[instructions]"),
        new("config", "Open settings", ArgumentHint: "[key [value]]",
            Aliases: ["settings"]),
        new("context", "Show current context usage"),
        new("copy", "Copy Jarvis's last response to clipboard (or /copy N for the Nth-latest)", ArgumentHint: "[N]"),
        new("daemon", "Manage background services and routines"),
        new("doctor", "Check the health of your Jarvis Code installation"),
        new("debug", JarvisCode.App.Services.DebugCommand.Description, ReplCommandKind.Prompt,
            ArgumentHint: JarvisCode.App.Services.DebugCommand.ArgumentHint),
        new("effort", "Set effort level for model usage", ArgumentHint: "[level] [s]"),
        new("exit", "Exit the REPL", ReplCommandKind.Exit, Aliases: ["quit"]),
        new("export", "Export the current conversation to a file or clipboard", ReplCommandKind.Dialog),
        new("fork", "Spawn a background agent that inherits the full conversation"),
        new("goal", "Set a goal Jarvis checks before stopping", ArgumentHint: "[goal|clear]"),
        new("heapdump", "Dump the JS heap to ~/Desktop"),
        new("help", "Show help and available commands"),
        new("hooks", "View hook configurations for tool events"),
        new("init", "Initialize a new JARVIS.md file with codebase documentation", ReplCommandKind.Prompt),
        new("ide", "Manage editor integration", ArgumentHint: "[auto|list|use <id>|selection|diagnostics|close-diffs|install]"),
        new("chrome", "Manage browser integration", ArgumentHint: "[status|list|use <id>]"),
        new("insights", "Generate a report analyzing your Jarvis Code sessions"),
        new("keybindings", "Open your keyboard shortcuts file"),
        new("list-agents", "List subagents, teammates, and other Jarvis sessions you can message", Aliases: ["peers"]),
        new("loop", "Run a prompt or slash command on a recurring interval", ArgumentHint: "[interval] <prompt>"),
        new("loops", "List, create, and delete loops", ArgumentHint: "[stop <id|all>]"),
        new("mcp", "Manage MCP servers", ArgumentHint: "[reconnect|enable|disable [<server>|all]]"),
        new("mcp-auth", "Sign in to a remote MCP server", ArgumentHint: "<server>"),
        new("memory", "Edit JARVIS.md files and memory settings"),
        new("model", "Set the AI model for Jarvis Code", ReplCommandKind.Dialog, ArgumentHint: "[model]"),
        new("output-style", "Set the output style for this session", ArgumentHint: "[style]"),
        new("pause-memory", "Pause automemory for this session"),
        new("permissions", "Manage allow and deny tool permission rules",
            ArgumentHint: "[open|share|<description>]"),
        new("plan", "Enable plan mode or view the current session plan"),
        new("plugin", "Manage Jarvis Code plugins", ArgumentHint: "[install|uninstall|list]"),
        new("plugin-types", "Write jarvis-code-mcp.d.ts: the inputs of the connected MCP tools, for typing a plugin against this session"),
        new("powerup", "Discover Jarvis Code features through quick interactive lessons", ArgumentHint: "[reset]"),
        new("recap", "Generate a one-line session recap now", ReplCommandKind.Prompt),
        new("reload-plugins", "Activate pending plugin changes in the current session"),
        new("reload-skills", "Pick up skills added or changed on disk during this session"),
        new("release-notes", JarvisCode.App.Services.ReleaseNotes.Description, ReplCommandKind.Dialog),
        new("rename", "Rename the current conversation", ArgumentHint: "<new title>"),
        new("resume", "Resume a previous conversation", ReplCommandKind.Dialog,
            ArgumentHint: "[conversation id or search term]", Aliases: ["continue"]),
        new("rewind", "Restore the code and/or conversation to a previous point", ReplCommandKind.Dialog),
        new("diff", "View uncommitted changes and per-turn diffs", ReplCommandKind.Dialog, ArgumentHint: "[staged]"),
        new("babysit-pr", "Watch this session's pull request and address new CI failures", ArgumentHint: "[off|status]"),
        new("security-review", "Complete a security review of the pending changes on the current branch", ReplCommandKind.Prompt),
        new("setup-bedrock", "Reconfigure Amazon Bedrock authentication, region, or model pins"),
        new("setup-vertex", "Reconfigure Google Vertex AI authentication, project, region, or model pins"),
        new("skill-doctor", "Show which loaded skills are unused and costing context"),
        new("skills", "List available skills"),
        new("statusline", "Set up Jarvis Code's status line UI", ArgumentHint: "[command]"),
        new("status", "Show Jarvis Code status including version, model, account, API connectivity, and tool statuses"),
        new("subtask", "Send a subagent off with your full context; its result comes back here",
            ArgumentHint: "<prompt>"),
        new("tasks", "View and manage everything running in the background"),
        new("teammates", "List the session's named agents and their state"),
        new("terminal-setup", "Install a shim so `jarvis-code` launches from any terminal"),
        new("theme", "Change the theme", ReplCommandKind.Dialog, ArgumentHint: "[theme]"),
        new("tui", "Set the terminal UI renderer (default | fullscreen)"),
        new("usage", "Show session cost, plan usage, and activity stats", Aliases: ["cost", "stats"]),
        new("version", "Show this session's version (autoupdate may have a newer one)"),
        new("wellbeing", "Configure optional break reminders and quiet-hours nudges", ArgumentHint: "[minutes|off]"),
        new("workflows", "Browse running and completed workflows"),

        // Parsed, then refused with the reason — the same treatment the root
        // command's unsupported flags get, so a muscle-memory command says why
        // rather than reading as a typo.
        new("bug", "Report a bug or share your conversation", ReplCommandKind.Unavailable,
            Unavailable: "filing the conversation with Anthropic"),
        new("feedback", "Send feedback to Anthropic or report a bug", ReplCommandKind.Unavailable,
            Unavailable: "sending the conversation to Anthropic"),
        new("login", "Sign in to your Anthropic account", ReplCommandKind.Unavailable,
            Unavailable: "Anthropic account sign-in; this app holds provider API keys instead"),
        new("logout", "Sign out from your Anthropic account", ReplCommandKind.Unavailable,
            Unavailable: "Anthropic account sign-in; this app holds provider API keys instead"),
        new("teleport", "Send this session to the cloud, or resume one from claude.ai", ReplCommandKind.Unavailable,
            Unavailable: "cloud sessions", Aliases: ["tp"]),
        new("remote-control", "Control this session from your phone or claude.ai/code", ReplCommandKind.Unavailable,
            Unavailable: "Remote Control", Aliases: ["rc"]),
        new("ultraplan", "Claude Code on the web drafts a plan you can edit and approve", ReplCommandKind.Unavailable, Unavailable: "cloud planning"),
        new("ultrareview", "Find and verify bugs in your branch using Claude Code on the web", ReplCommandKind.Unavailable,
            Unavailable: "cloud multi-agent review — use /code-review in a session"),
        new("upgrade", "Upgrade to Max for higher rate limits and more Opus", ReplCommandKind.Unavailable, Unavailable: "plan billing"),
        new("install-github-app", "Set up Claude GitHub Actions for a repository", ReplCommandKind.Unavailable,
            Unavailable: "provisioning GitHub Actions against the account"),
        new("background", "Send this session to the background and free the terminal", Aliases: ["bg"]),
    ];

    private static readonly Dictionary<string, ReplCommand> ByName = Build();

    private static Dictionary<string, ReplCommand> Build()
    {
        var map = new Dictionary<string, ReplCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in Commands)
        {
            foreach (var name in command.Names)
            {
                map.TryAdd(name, command);
            }
        }

        return map;
    }

    /// <summary>Resolves a typed name (without its slash) to a command.</summary>
    public static ReplCommand? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The reference's edit-distance cap for a "did you mean" suggestion.</summary>
    public const int MaxSuggestionDistance = 2;

    /// <summary>
    /// The reference's message for a name that resolves to nothing, with its
    /// own caps: the typed name at 512 characters and the suggestion at 200.
    /// </summary>
    public static string Unknown(string name, IEnumerable<string>? extraNames = null)
    {
        var candidates = Commands.SelectMany(command => command.Names).Concat(extraNames ?? []);
        string? nearest = null;
        int best = int.MaxValue;
        foreach (var candidate in candidates)
        {
            int distance = Keys.KeybindingsFile.Levenshtein(
                name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < best)
            {
                best = distance;
                nearest = candidate;
            }
        }

        var typed = Truncate(name, 512);
        return nearest is not null && best <= MaxSuggestionDistance
            ? $"Unknown command: /{typed}. Did you mean /{Truncate(nearest, 200)}?"
            : $"Unknown command: /{typed}";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
