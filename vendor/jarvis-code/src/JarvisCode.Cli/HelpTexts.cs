// GENERATED from the reference CLI's own --help output (2.1.260 (Claude Code),
// captured live, brand-swapped claude->jarvis). Regenerate with
// gen-help-texts.py rather than editing by hand.
namespace JarvisCode.Cli;

internal static class HelpTexts
{
    public const string Root =
@"Usage: jarvis [options] [command] [prompt]

Jarvis Code - starts an interactive session by default, use -p/--print for
non-interactive output

Arguments:
  prompt                                Your prompt

Options:
  --add-dir <directories...>            Additional directories to allow tool
                                        access to
  --agent <agent>                       Agent for the current session. Overrides
                                        the 'agent' setting.
  --agents <json>                       JSON object defining custom agents (e.g.
                                        '{""reviewer"": {""description"": ""Reviews
                                        code"", ""prompt"": ""You are a code
                                        reviewer""}}')
  --allow-dangerously-skip-permissions  Enable bypassing all permission checks
                                        as an option, without it being enabled
                                        by default. Recommended only for
                                        sandboxes with no internet access.
  --allowedTools, --allowed-tools <tools...>
      Comma or space-separated list of tool names to allow (e.g. ""Bash(git *)
      Edit"")
  --append-system-prompt <prompt>       Append a system prompt to the default
                                        system prompt
  --autocompact <auto|tokens>           Auto-compact window size (auto, or
                                        100k–1M tokens)
  --ax-screen-reader                    Render screen-reader friendly output
                                        (flat text, no decorative borders or
                                        animations).
  --bg, --background                    Start the session in the background and
                                        return immediately. Prints the id that
                                        `jarvis attach`, `logs`, `stop` and `rm`
                                        take; `jarvis agents` lists them. With
                                        --resume <session-id>, continues that
                                        session in the background under the same
                                        ID, or starts a copy and says so when
                                        the session is already running
  --bare                                Minimal mode: skip hooks, LSP, plugin
                                        sync, attribution, auto-memory,
                                        background prefetches, keychain reads,
                                        and CLAUDE.md auto-discovery. Sets
                                        CLAUDE_CODE_SIMPLE=1. Anthropic auth is
                                        strictly ANTHROPIC_API_KEY or
                                        apiKeyHelper via --settings (OAuth and
                                        keychain are never read). 3P providers
                                        (Bedrock/Vertex/Foundry) use their own
                                        credentials. Skills still resolve via
                                        /skill-name. Explicitly provide context
                                        via: --system-prompt[-file],
                                        --append-system-prompt[-file], --add-dir
                                        (CLAUDE.md dirs), --mcp-config,
                                        --settings, --agents, --plugin-dir.
  --betas <betas...>                    Beta headers to include in API requests
                                        (API key users only)
  --brief                               Enable SendUserMessage tool for
                                        agent-to-user communication
  --chrome                              Enable Claude in Chrome integration
  --cloud [description|session_id|url]  Create a cloud session with the given
                                        description, or attach to an existing
                                        one by session ID or claude.ai/code URL
  -c, --continue                        Continue the most recent conversation in
                                        the current directory
  --dangerously-skip-permissions        Bypass all permission checks.
                                        Recommended only for sandboxes with no
                                        internet access.
  -d, --debug [filter]                  Enable debug mode with optional category
                                        filtering (e.g., ""api,hooks"" or
                                        ""!1p,!file"")
  --debug-file <path>                   Write debug logs to a specific file path
                                        (implicitly enables debug mode)
  --disable-slash-commands              Disable all skills
  --disallowedTools, --disallowed-tools <tools...>
      Comma or space-separated list of tool names to deny (e.g. ""Bash(git *)
      Edit"")
  --effort <level>                      Effort level for the current session
                                        (low, medium, high, xhigh, max)
  --environment <environment_id>        Create a new cloud session that runs on
                                        the given self-hosted environment
                                        (ccpool_...).
  --exclude-dynamic-system-prompt-sections
      Move per-machine sections (cwd, env info, memory paths, git status) from
      the system prompt into the first user message. Improves cross-user
      prompt-cache reuse. Only applies with the default system prompt (ignored
      with --system-prompt). (default: false)
  --fallback-model <model>              Enable automatic fallback to specified
                                        model(s) when the default model is
                                        overloaded or not available. Accepts a
                                        comma-separated list to try each in
                                        order. Re-tries the primary at the start
                                        of each user turn. (only works with
                                        --print)
  --file <specs...>                     File resources to download at startup.
                                        Format: file_id:relative_path (e.g.,
                                        --file file_abc:doc.txt
                                        file_def:img.png)
  --fork-session                        When resuming, create a new session ID
                                        instead of reusing the original (use
                                        with --resume or --continue)
  --forward-subagent-text               Forward subagent text and thinking
                                        blocks as assistant/user messages with
                                        parent_tool_use_id set (only works with
                                        --print and --output-format=stream-json)
  --from-pr [value]                     Resume a session linked to a PR by PR
                                        number/URL, or open interactive picker
                                        with optional search term
  -h, --help                            Display help for command
  --ide                                 Automatically connect to IDE on startup
                                        if exactly one valid IDE is available
  --include-hook-events                 Include all hook lifecycle events in the
                                        output stream (only works with
                                        --output-format=stream-json)
  --include-partial-messages            Include partial message chunks as they
                                        arrive (only works with --print and
                                        --output-format=stream-json)
  --input-format <format>               Input format (only works with --print):
                                        ""text"" (default), or ""stream-json""
                                        (realtime streaming input) (choices:
                                        ""text"", ""stream-json"")
  --json-schema <schema>                JSON Schema for structured output
                                        validation. Example:
                                        {""type"":""object"",""properties"":{""name"":{""type"":""string""}},""required"":[""name""]}
  --max-budget-usd <amount>             Maximum dollar amount to spend on API
                                        calls (only works with --print)
  --mcp-config <configs...>             Load MCP servers from JSON files or
                                        strings (space-separated)
  --model <model>                       Model for the current session. Provide
                                        an alias for the latest model (e.g.
                                        'fable', 'opus', or 'sonnet') or a
                                        model's full name (e.g.
                                        'claude-fable-5').
  -n, --name <name>                     Set a display name for this session
                                        (shown in the prompt box, /resume
                                        picker, and terminal title)
  --no-chrome                           Disable Claude in Chrome integration
  --no-session-persistence              Disable session persistence - sessions
                                        will not be saved to disk and cannot be
                                        resumed (only works with --print)
  --output-format <format>              Output format (only works with --print):
                                        ""text"" (default), ""json"" (single
                                        result), or ""stream-json"" (realtime
                                        streaming) (choices: ""text"", ""json"",
                                        ""stream-json"")
  --permission-mode <mode>              Permission mode to use for the session
                                        (choices: ""acceptEdits"", ""auto"",
                                        ""bypassPermissions"", ""manual"",
                                        ""dontAsk"", ""plan"")
  --permission-prompts <target>         Who answers permission prompts with
                                        --print: ""host"" (the SDK host or
                                        --permission-prompt-tool) or ""none""
                                        (nobody: anything that would prompt is
                                        denied automatically; the permission
                                        mode still decides everything else)
                                        (choices: ""host"", ""none"", default:
                                        ""host"")
  --plugin-dir <path>                   Load a plugin from a directory or .zip
                                        for this session only (repeatable:
                                        --plugin-dir A --plugin-dir B.zip)
                                        (default: [])
  --plugin-url <url>                    Fetch a plugin .zip from a URL for this
                                        session only (repeatable: --plugin-url A
                                        --plugin-url B) (default: [])
  -p, --print                           Print response and exit (useful for
                                        pipes). Note: The workspace trust dialog
                                        is skipped when Claude is run in
                                        non-interactive mode (via -p, or when
                                        stdout is not a TTY, e.g. piped or
                                        redirected output). Only use this in
                                        directories you trust. Settings files
                                        that fail validation are silently
                                        ignored in this mode (no error dialog is
                                        shown).
  --prompt-suggestions [value]          Enable prompt suggestions. In print/SDK
                                        mode, emits a prompt_suggestion message
                                        after each turn with a predicted next
                                        user prompt (choices: ""true"", ""false"",
                                        ""1"", ""0"", ""yes"", ""no"", ""on"", ""off"",
                                        preset: ""true"")
  --remote-control [name]               Start an interactive session with Remote
                                        Control enabled (optionally named)
  --remote-control-session-name-prefix <prefix>
      Prefix for auto-generated Remote Control session names (default: hostname)
  --replay-user-messages                Re-emit user messages from stdin back on
                                        stdout for acknowledgment (only works
                                        with --input-format=stream-json and
                                        --output-format=stream-json)
  --restricted                          Restricted mode: removes the built-in
                                        tools that run commands or code (Bash,
                                        PowerShell, REPL and the other
                                        code-running tools) and WebFetch unless
                                        --tools names them, and ignores user,
                                        project and local settings files
                                        (managed settings and --settings still
                                        apply; add --strict-mcp-config to skip
                                        MCP servers too). Also confines the file
                                        tools to the working directories
                                        (--add-dir included), refuses
                                        bypassPermissions, and lets only a
                                        person or the configured permission
                                        handler approve writes to settings, git
                                        and tool-configuration files.
  -r, --resume [value]                  Resume a conversation by session ID, or
                                        open interactive picker with optional
                                        search term
  --safe-mode                           Start with all customizations
                                        (CLAUDE.md, skills, plugins, hooks, MCP
                                        servers, custom commands and agents,
                                        output styles, workflows, custom themes,
                                        keybindings, and more) disabled — useful
                                        for troubleshooting a broken
                                        configuration. Admin-managed (policy)
                                        settings still apply. Auth, model
                                        selection, built-in tools, and
                                        permissions work normally. Sets
                                        CLAUDE_CODE_SAFE_MODE=1.
  --session-id <uuid>                   Use a specific session ID for the
                                        conversation (must be a valid UUID)
  --setting-sources <sources>           Comma-separated list of setting sources
                                        to load (user, project, local).
  --settings <file-or-json>             Path to a settings JSON file or a JSON
                                        string to load additional settings from
  --strict-mcp-config                   Only use MCP servers from --mcp-config,
                                        ignoring all other MCP configurations
  --system-prompt <prompt>              System prompt to use for the session
  --system-prompt-snapshot <on|off>     Record the system prompt once per
                                        conversation and reuse it verbatim on
                                        every request and resume (recommended:
                                        on). By default it is on for the
                                        built-in prompt, and passing
                                        --system-prompt or
                                        --append-system-prompt turns it off so
                                        the given text applies fresh each
                                        launch. on: an existing record in the
                                        conversation is sent as-is (a later
                                        launch's different
                                        --system-prompt/--append-system-prompt
                                        is ignored until compaction); otherwise
                                        the prompt is rendered with the append
                                        included, sent, and recorded. off: never
                                        record. No effect where system-prompt
                                        recording is not yet enabled. (choices:
                                        ""on"", ""off"")
  --teleport [session]                  Resume a teleport session, optionally
                                        specify session ID
  --tmux                                Create a tmux session for the worktree
                                        (requires --worktree). Uses iTerm2
                                        native panes when available; use
                                        --tmux=classic for traditional tmux.
  --tools <tools...>                    Specify the list of available tools from
                                        the built-in set. Use """" to disable all
                                        tools, ""default"" to use all tools, or
                                        specify tool names (e.g.
                                        ""Bash,Edit,Read"").
  --verbose                             Override verbose mode setting from
                                        config
  -v, --version                         Output the version number
  -w, --worktree [name]                 Create a new git worktree for this
                                        session (optionally specify a name)

Commands:
  agents [options]                      Manage background agents
  attach <id>                           Open a background session in this
                                        terminal. <id> is the short id that
                                        `jarvis --bg` prints and `jarvis agents`
                                        lists
  auth                                  Manage authentication
  auto-mode                             Inspect or reset auto mode classifier
                                        configuration
  doctor                                Check the health of your Jarvis Code
                                        installation. Reads settings files in
                                        the current directory without a trust
                                        prompt. For a full checkup that can also
                                        fix issues, run /doctor in a session.
  gateway [options]                     Run the enterprise auth/telemetry
                                        gateway
  import [options] [source]             Import config from another AI coding
                                        agent into Jarvis Code
  install [options] [target]            Install Jarvis Code native build. Use
                                        [target] to specify version (stable,
                                        latest, or specific version)
  logs <id>                             Print a background session's recent
                                        terminal output
  mcp                                   Configure and manage MCP servers
  plugin|plugins                        Manage Jarvis Code plugins
  project                               Manage Jarvis Code project state
  respawn [options] [id]                Restart a background session, or all of
                                        them with --all, so it runs the current
                                        Jarvis Code version
  rm <id>                               Delete a background session, and its
                                        worktree when that is safe. Works on
                                        sessions that have already exited
  setup-token                           Set up a long-lived authentication token
                                        (requires Claude subscription)
  stop|kill <id>                        Stop a background session. Its
                                        conversation is kept: `jarvis attach
                                        <id>` opens it again, `jarvis --resume`
                                        works once it is stopped
  ultrareview [options] [target]        Run a cloud-hosted multi-agent code
                                        review of the current branch (or a PR
                                        number / base branch) and print the
                                        findings
  update|upgrade                        Check for updates and install if
                                        available
";

    public const string Agents =
@"Usage: jarvis agents [options]

Manage background agents

Options:
  --add-dir <directory>                 Additional directory to allow tool
                                        access to in dispatched sessions
                                        (repeatable)
  --agent <agent>                       Default agent for sessions dispatched
                                        from agent view. Overrides the 'agent'
                                        setting.
  --all                                 With --json: also include completed
                                        background sessions
  --allow-dangerously-skip-permissions  Make bypass-permissions mode available
                                        to dispatched sessions without
                                        defaulting to it
  --cwd <path>                          Show only background sessions started
                                        under <path>
  --dangerously-skip-permissions        Alias for --permission-mode
                                        bypassPermissions
  --effort <level>                      Default effort level for sessions
                                        dispatched from agent view
  -h, --help                            Display help for command
  --json                                Print active sessions (interactive and
                                        background) as a JSON array and exit
                                        (for scripting; does not require a TTY)
  --mcp-config <config>                 MCP server configuration to apply to
                                        dispatched sessions (repeatable)
  --model <model>                       Default model for sessions dispatched
                                        from agent view
  --permission-mode <mode>              Default permission mode for sessions
                                        dispatched from agent view
  --plugin-dir <path>                   Load plugins from specified directory
                                        for the agent view and dispatched
                                        sessions (repeatable)
  --restricted                          Start dispatched sessions in restricted
                                        mode
  --setting-sources <sources>           Comma-separated list of setting sources
                                        to load (user, project, local).
  --settings <file-or-json>             Settings file or JSON string to apply to
                                        the agent view and dispatched sessions
  --strict-mcp-config                   Only use MCP servers from --mcp-config
                                        in dispatched sessions
";

    public const string Attach =
@"Usage: jarvis attach <id>

  Open the background session in this terminal. ← returns to agent view, Ctrl+Z drops back to your shell. The session keeps running either way.
";

    public const string Auth =
@"Usage: jarvis auth [options] [command]

Manage authentication

Options:
  -h, --help        Display help for command

Commands:
  help [command]    display help for command
  login [options]   Sign in to your Anthropic account
  logout            Log out from your Anthropic account
  status [options]  Show authentication status
";

    public const string AutoMode =
@"Usage: jarvis auto-mode [options] [command]

Inspect or reset auto mode classifier configuration

Options:
  -h, --help          Display help for command

Commands:
  config              Print the effective auto mode config as JSON: your
                      settings where set, defaults otherwise
  critique [options]  Get AI feedback on your custom auto mode rules
  defaults [options]  Print the default auto mode environment, allow, soft_deny,
                      and hard_deny rules as JSON
  help [command]      display help for command
  reset [options]     Reset auto mode configuration to the shipped defaults by
                      removing the autoMode section from your user settings file
";

    public const string Doctor =
@"Usage: jarvis doctor [options]

Check the health of your Jarvis Code installation. Reads settings files in the
current directory without a trust prompt. For a full checkup that can also fix
issues, run /doctor in a session.

Options:
  -h, --help  Display help for command
";

    public const string Gateway =
@"Usage: jarvis gateway [options]

Run the enterprise auth/telemetry gateway

Options:
  --config <path>  Path to gateway YAML config
  -h, --help       Display help for command
";

    public const string Import =
@"Usage: jarvis import [options] [source]

Import config from another AI coding agent into Jarvis Code

Arguments:
  source      Which agent to import from (codex, gemini)

Options:
  --dry-run   Show what would be imported without writing anything
  -h, --help  Display help for command
  --yes       Skip the interactive picker. On headless surfaces, pass
              --yes=<digest> from the `/import` preview.
";

    public const string Install =
@"Usage: jarvis install [options] [target]

Install Jarvis Code native build. Use [target] to specify version (stable,
latest, or specific version)

Options:
  --force     Force installation even if already installed
  -h, --help  Display help for command
";

    public const string Logs =
@"Usage: jarvis logs <id>

  Print the background session's recent terminal output.
";

    public const string Mcp =
@"Usage: jarvis mcp [options] [command]

Configure and manage MCP servers

Options:
  -h, --help                            Display help for command

Commands:
  add [options] <name> <commandOrUrl> [args...]  Add an MCP server to Jarvis Code.
  
  Examples:
    # Add HTTP server:
    jarvis mcp add --transport http sentry https://mcp.sentry.dev/mcp
  
    # Add HTTP server with headers:
    jarvis mcp add --transport http corridor https://app.corridor.dev/api/mcp --header ""Authorization: Bearer ...""
  
    # Add stdio server with environment variables:
    jarvis mcp add my-server -e API_KEY=xxx -- npx my-mcp-server
  
    # Add stdio server with subprocess flags:
    jarvis mcp add my-server -- my-command --some-flag arg1
  add-from-claude-desktop [options]     Import MCP servers from Claude Desktop
                                        (Mac and WSL only)
  add-json [options] <name> <json>      Add an MCP server (stdio, SSE, HTTP, or
                                        WebSocket) with a JSON string
  get <name>                            Get details about an MCP server.
                                        Unapproved .mcp.json servers are shown
                                        as ⏸ Pending approval and not connected
                                        to; approved servers are health-checked
                                        unless disabled for this project.
  help [command]                        display help for command
  list                                  List configured MCP servers. Unapproved
                                        .mcp.json servers are shown as ⏸ Pending
                                        approval and not connected to; approved
                                        servers are health-checked unless
                                        disabled for this project.
  login [options] <name>                Authenticate with an MCP server (HTTP,
                                        SSE, or claude.ai connector)
  logout <name>                         Clear stored OAuth credentials for an
                                        MCP server
  remove [options] <name>               Remove an MCP server
  reset-project-choices                 Reset all approved and rejected
                                        project-scoped (.mcp.json) servers
                                        within this project
  serve [options]                       Start the Jarvis Code MCP server
";

    public const string Plugin =
@"Usage: jarvis plugin|plugins [options] [command]

Manage Jarvis Code plugins

Options:
  -h, --help                           Display help for command

Commands:
  details [options] <name>             Show a plugin's component inventory and
                                       projected token cost
  disable [options] [plugin]           Disable an enabled plugin
  enable [options] <plugin>            Enable a disabled plugin
  eval [options] [target]              Run eval cases (<eval dir>/**/case.yaml
                                       or prompt.md + graders/*.md; the eval dir
                                       is evals/ unless --eval-dir or the
                                       manifest says otherwise) against a plugin
                                       and report scored results. Target is a
                                       path, a plugin name, or a
                                       `plugin@marketplace` id — installed and
                                       skills-dir plugins both resolve (and add
                                       a no-plugin baseline arm)
  help [command]                       display help for command
  init|new [options] <name>            Scaffold a new plugin at
                                       ~/.claude/skills/<name>/ (auto-loads next
                                       session as <name>@skills-dir)
  install|i [options] <plugin>         Install a plugin from available
                                       marketplaces (use plugin@marketplace for
                                       specific marketplace)
  list [options]                       List installed plugins
  marketplace                          Manage Jarvis Code marketplaces
  prune|autoremove [options]           Remove auto-installed dependencies that
                                       are no longer needed
  tag [options] [path]                 Create a {name}--v{version} git tag for a
                                       plugin release, validating that
                                       plugin.json and any enclosing marketplace
                                       entry agree
  uninstall|remove [options] <plugin>  Uninstall an installed plugin
  update [options] <plugin>            Update a plugin to the latest version
                                       (restart required to apply)
  validate [options] <path>            Validate a plugin or marketplace
                                       manifest, or the skills, agents, and
                                       commands in a directory
";

    public const string Project =
@"Usage: jarvis project [options] [command]

Manage Jarvis Code project state

Options:
  -h, --help              Display help for command

Commands:
  help [command]          display help for command
  purge [options] [path]  Delete all Jarvis Code state for a project
                          (transcripts, tasks, file history, config entry)
";

    public const string Respawn =
@"Usage: jarvis respawn <id>|--all

  Restart a background session (or all of them) so it picks up the current Claude binary.
";

    public const string Rm =
@"Usage: jarvis rm <id> [--discard-unpushed <commit>@<worktree-id>]

  Delete a background session and its worktree. Unlike `stop`, works on already-exited sessions.
  --discard-unpushed <commit>@<worktree-id>  also discard the worktree's unpushed commits (and any uncommitted changes) while it is still the same worktree at that commit — pass the value a previous 'claude rm <id>' reported
";

    public const string SetupToken =
@"Usage: jarvis setup-token [options]

Set up a long-lived authentication token (requires Claude subscription)

Options:
  -h, --help  Display help for command
";

    public const string Stop =
@"Usage: jarvis stop <id>

  Stop a background session. Its conversation is kept; resume it later with `jarvis attach <id>`.
";

    public const string Ultrareview =
@"Usage: jarvis ultrareview [options] [target]

Run a cloud-hosted multi-agent code review of the current branch (or a PR number
/ base branch) and print the findings

Options:
  -h, --help           Display help for command
  --json               Print the raw bugs.json payload instead of formatted
                       findings
  --no-post            Do not post the findings to the PR (the default; accepted
                       for parity with the /ultrareview and /code-review ultra
                       flags)
  --post               Post the finished review's findings to the PR as you (PR
                       targets only; one plain comment, not a review)
  --timeout <minutes>  Maximum minutes to wait for the review to finish
                       (default: 45)
";

    public const string Update =
@"Usage: jarvis update|upgrade [options]

Check for updates and install if available

Options:
  -h, --help  Display help for command
";

    public const string AuthLogin =
@"Usage: jarvis auth login [options]

Sign in to your Anthropic account

Options:
  --claudeai       Use Claude subscription (default)
  --console        Use Anthropic Console (API usage billing) instead of Claude
                   subscription
  --email <email>  Pre-populate email address on the login page
  -h, --help       Display help for command
  --sso            Force SSO login flow
";

    public const string AuthLogout =
@"Usage: jarvis auth logout [options]

Log out from your Anthropic account

Options:
  -h, --help  Display help for command
";

    public const string AuthStatus =
@"Usage: jarvis auth status [options]

Show authentication status

Options:
  -h, --help  Display help for command
  --json      Output as JSON (default)
  --text      Output as human-readable text
";

    public const string AutoModeConfig =
@"Usage: jarvis auto-mode config [options]

Print the effective auto mode config as JSON: your settings where set, defaults
otherwise

Options:
  -h, --help  Display help for command
";

    public const string AutoModeCritique =
@"Usage: jarvis auto-mode critique [options]

Get AI feedback on your custom auto mode rules

Options:
  -h, --help       Display help for command
  --model <model>  Override which model is used
";

    public const string AutoModeDefaults =
@"Usage: jarvis auto-mode defaults [options]

Print the default auto mode environment, allow, soft_deny, and hard_deny rules
as JSON

Options:
  -h, --help        Display help for command
  --label <prefix>  Show only rules whose label starts with this prefix
                    (case-insensitive)
";

    public const string AutoModeReset =
@"Usage: jarvis auto-mode reset [options]

Reset auto mode configuration to the shipped defaults by removing the autoMode
section from your user settings file

Options:
  -h, --help  Display help for command
  -y, --yes   Skip the confirmation prompt
";

    public const string McpAdd =
@"Usage: jarvis mcp add [options] <name> <commandOrUrl> [args...]

Add an MCP server to Jarvis Code.

Examples:
  # Add HTTP server:
  jarvis mcp add --transport http sentry https://mcp.sentry.dev/mcp

  # Add HTTP server with headers:
  jarvis mcp add --transport http corridor https://app.corridor.dev/api/mcp
--header ""Authorization: Bearer ...""

  # Add stdio server with environment variables:
  jarvis mcp add my-server -e API_KEY=xxx -- npx my-mcp-server

  # Add stdio server with subprocess flags:
  jarvis mcp add my-server -- my-command --some-flag arg1

Options:
  --callback-port <port>       Fixed port for OAuth callback (for servers
                               requiring pre-registered redirect URIs)
  --client-id <clientId>       OAuth client ID for HTTP/SSE servers
  --client-secret              Prompt for OAuth client secret (or set
                               MCP_CLIENT_SECRET env var)
  -e, --env <env...>           Set environment variables (e.g. -e KEY=value)
  -H, --header <header...>     Set headers for HTTP/SSE servers (e.g. -H
                               ""X-Api-Key: abc123"" -H ""X-Custom: value"")
  -h, --help                   Display help for command
  -s, --scope <scope>          Configuration scope (local, user, or project)
                               (default: ""local"")
  -t, --transport <transport>  Transport type (stdio, sse, http). Defaults to
                               stdio if not specified.
";

    public const string McpAddFromClaudeDesktop =
@"Usage: jarvis mcp add-from-claude-desktop [options]

Import MCP servers from Claude Desktop (Mac and WSL only)

Options:
  -h, --help           Display help for command
  -s, --scope <scope>  Configuration scope (local, user, or project) (default:
                       ""local"")
";

    public const string McpAddJson =
@"Usage: jarvis mcp add-json [options] <name> <json>

Add an MCP server (stdio, SSE, HTTP, or WebSocket) with a JSON string

Options:
  --client-secret      Prompt for OAuth client secret (or set MCP_CLIENT_SECRET
                       env var)
  -h, --help           Display help for command
  -s, --scope <scope>  Configuration scope (local, user, or project) (default:
                       ""local"")
";

    public const string McpGet =
@"Usage: jarvis mcp get [options] <name>

Get details about an MCP server. Unapproved .mcp.json servers are shown as ⏸
Pending approval and not connected to; approved servers are health-checked
unless disabled for this project.

Options:
  -h, --help  Display help for command
";

    public const string McpList =
@"Usage: jarvis mcp list [options]

List configured MCP servers. Unapproved .mcp.json servers are shown as ⏸ Pending
approval and not connected to; approved servers are health-checked unless
disabled for this project.

Options:
  -h, --help  Display help for command
";

    public const string McpLogin =
@"Usage: jarvis mcp login [options] <name>

Authenticate with an MCP server (HTTP, SSE, or claude.ai connector)

Options:
  -h, --help    Display help for command
  --no-browser  Print the authorization URL instead of opening a browser (for
                SSH/headless sessions — paste the redirect URL back when
                prompted)
";

    public const string McpLogout =
@"Usage: jarvis mcp logout [options] <name>

Clear stored OAuth credentials for an MCP server

Options:
  -h, --help  Display help for command
";

    public const string McpRemove =
@"Usage: jarvis mcp remove [options] <name>

Remove an MCP server

Options:
  -h, --help           Display help for command
  -s, --scope <scope>  Configuration scope (local, user, or project) - if not
                       specified, removes from whichever scope it exists in
";

    public const string McpResetProjectChoices =
@"Usage: jarvis mcp reset-project-choices [options]

Reset all approved and rejected project-scoped (.mcp.json) servers within this
project

Options:
  -h, --help  Display help for command
";

    public const string McpServe =
@"Usage: jarvis mcp serve [options]

Start the Jarvis Code MCP server

Options:
  -d, --debug  Enable debug mode
  -h, --help   Display help for command
  --verbose    Override verbose mode setting from config
";

    public const string PluginDetails =
@"Usage: jarvis plugin details [options] <name>

Show a plugin's component inventory and projected token cost

Options:
  -h, --help  Display help for command
";

    public const string PluginDisable =
@"Usage: jarvis plugin disable [options] [plugin]

Disable an enabled plugin

Options:
  -a, --all            Disable all enabled plugins
  -h, --help           Display help for command
  -s, --scope <scope>  Installation scope: user, project, local (default:
                       auto-detect)
";

    public const string PluginEnable =
@"Usage: jarvis plugin enable [options] <plugin>

Enable a disabled plugin

Options:
  -h, --help           Display help for command
  -s, --scope <scope>  Installation scope: user, project, local (default:
                       auto-detect)
";

    public const string PluginEval =
@"Usage: jarvis plugin eval [options] [command] [target]

Run eval cases (<eval dir>/**/case.yaml or prompt.md + graders/*.md; the eval
dir is evals/ unless --eval-dir or the manifest says otherwise) against a plugin
and report scored results. Target is a path, a plugin name, or a
`plugin@marketplace` id — installed and skills-dir plugins both resolve (and add
a no-plugin baseline arm)

Options:
  --ablation <mode>         Run a no-plugin baseline arm and report the score
                            delta (none | with-without; default: with-without
                            whenever a plugin resolves — by name, or from the
                            target path — and none when nothing does; under
                            with-without, graders marked with-only, incl.
                            `tool_used: Skill`, are a plugin-fired indicator
                            rather than part of the score)
  --allow-tools <tools...>  Operator grant for gated tools (Bash, Write, Edit,
                            WebFetch, mcp__*). Supports Tool(pattern:*) syntax
  --case <glob>             Filter cases by name glob
  --eval-dir <dir>          Directory name (below the plugin) that holds the
                            eval cases; results go to <plugin>/<dir>/results/ —
                            for an installed-plugin target, ./<dir>/results/
                            with this flag, else ./evals/results/ (default dir:
                            the manifest's experimental.evals value, else
                            evals/)
  -h, --help                Display help for command
  --json [path]             Print the full run result (prompts, graders, per-run
                            scores) as JSON to stdout, or write it to this .json
                            file
  --judge-model <model>     Override LLM-grader model (default: haiku)
  --keep-temp               Preserve scaffold dirs for debugging
  --max-cost-usd <usd>      Optional hard cost ceiling; abort and report partial
                            results if hit (exit 2). Overrun is bounded to one
                            agent run — when that run breaches, paid graders
                            (llm/baseline) are skipped while free graders still
                            score it. Runs are already bounded by max_turns and
                            timeout_seconds — only set this when you need a
                            strict budget
  --mocks <mode>            Mock stand-ins for MCP servers, from <eval
                            dir>/mocks/ (record | off; default: record — off
                            spawns the real servers, gated by --allow-tools as
                            usual)
  --model <model>           Override model for all cases
  --no-publish              Keep the HTML report local only; skip publishing it
                            to claude.ai
  --no-scaffold             Explicitly skip scaffold_script
  --output-dir <dir>        Directory for aggregate-result.json (default:
                            ./<eval dir>/results/<timestamp>/)
  --publish-report          Also require publishing the report to claude.ai
                            (already the default when your account supports it);
                            explains why if unavailable
  --report <path>           Write the self-contained HTML report (scores,
                            prompts, grader verdicts) to <path> instead of the
                            results dir
  --runs <n>                Override per-case runs (default: case.runs ?? 3)
  --scaffold                Run each case's scaffold_script (runs
                            author-supplied bash as you; off by default — only
                            use on case files you authored)
  --tag <tag...>            Filter cases by tag (repeatable)
  --threshold <0..1>        Exit 1 if any case score is below this threshold
                            (default: 1.0)
  --verbose                 Log per-message trace events to the debug log (use
                            --debug-file to read them)

Commands:
  init [options] [name]     Author an eval suite under the eval dir (evals/
                            unless --eval-dir or the manifest says otherwise)
                            via an interview that sources inputs and designs
                            graders. Use --bare <name> for a blank single-case
                            template.
";

    public const string PluginInit =
@"Usage: jarvis plugin init|new [options] <name>

Scaffold a new plugin at ~/.claude/skills/<name>/ (auto-loads next session as
<name>@skills-dir)

Options:
  --author <name>         Author name (default: git config user.name)
  --author-email <email>  Author email (default: git config user.email)
  --description <text>    Manifest description
  -f, --force             Overwrite an existing .claude-plugin/ at the target
  -h, --help              Display help for command
  --with <components...>  Also scaffold: skills, agents, hooks, mcp, lsp,
                          output-style, channel
";

    public const string PluginInstall =
@"Usage: jarvis plugin install|i [options] <plugin>

Install a plugin from available marketplaces (use plugin@marketplace for
specific marketplace)

Options:
  --config <key=value>  Set a userConfig option declared in the plugin's
                        manifest (repeatable). Values are validated against the
                        schema and stored via the same path as the interactive
                        /plugin configure flow.
  -h, --help            Display help for command
  -s, --scope <scope>   Installation scope: user, project, or local (default:
                        ""user"")
  -y, --yes             Accept the displayed marketplace-declared command
                        without the confirmation prompt — a plugin installed by
                        running a command, or one whose archive is fetched
                        through a headersHelper command (required when stdin or
                        stdout is not a TTY)
";

    public const string PluginList =
@"Usage: jarvis plugin list [options]

List installed plugins

Options:
  --available  Include available plugins from marketplaces (requires --json)
  -h, --help   Display help for command
  --json       Output as JSON
";

    public const string PluginMarketplace =
@"Usage: jarvis plugin marketplace [options] [command]

Manage Jarvis Code marketplaces

Options:
  -h, --help                  Display help for command

Commands:
  add [options] <source>      Add a marketplace from a URL, path, or GitHub repo
  help [command]              display help for command
  list [options]              List all configured marketplaces
  remove|rm [options] <name>  Remove a configured marketplace
  update [options] [name]     Update marketplace(s) from their source - updates
                              all if no name specified
";

    public const string PluginPrune =
@"Usage: jarvis plugin prune|autoremove [options]

Remove auto-installed dependencies that are no longer needed

Options:
  --dry-run            List what would be removed without removing
  -h, --help           Display help for command
  -s, --scope <scope>  Prune at scope: user, project, or local (default: ""user"")
  -y, --yes            Skip the confirmation prompt (required when stdin or
                       stdout is not a TTY)
";

    public const string PluginTag =
@"Usage: jarvis plugin tag [options] [path]

Create a {name}--v{version} git tag for a plugin release, validating that
plugin.json and any enclosing marketplace entry agree

Options:
  --dry-run            Print what would be tagged without creating it
  -f, --force          Skip the dirty-working-tree and tag-already-exists checks
  -h, --help           Display help for command
  -m, --message <msg>  Tag annotation message (use %s for the version)
  --push               Push the tag to --remote after creating it
  --remote <name>      Remote to push to with --push (default: ""origin"")
";

    public const string PluginUninstall =
@"Usage: jarvis plugin uninstall|remove [options] <plugin>

Uninstall an installed plugin

Options:
  -h, --help           Display help for command
  --keep-data          Preserve the plugin's persistent data directory
                       (~/.claude/plugins/data/{id}/)
  --prune              Also remove auto-installed dependencies that are no
                       longer needed (requires -y in non-interactive contexts)
  -s, --scope <scope>  Uninstall from scope: user, project, or local (default:
                       ""user"")
  -y, --yes            Skip the --prune confirmation prompt (required when stdin
                       or stdout is not a TTY)
";

    public const string PluginUpdate =
@"Usage: jarvis plugin update [options] <plugin>

Update a plugin to the latest version (restart required to apply)

Options:
  -h, --help           Display help for command
  -s, --scope <scope>  Installation scope: user, project, local, managed
                       (default: user)
  -y, --yes            Accept the displayed marketplace-declared command without
                       the confirmation prompt — a changed install command, or
                       the headersHelper command that fetches its archive
                       (required when stdin or stdout is not a TTY)
";

    public const string PluginValidate =
@"Usage: jarvis plugin validate [options] <path>

Validate a plugin or marketplace manifest, or the skills, agents, and commands
in a directory

Options:
  -h, --help  Display help for command
  --json      Output the validation report as JSON (same exit codes)
  --strict    Treat warnings as errors (exit 1). Use in CI to fail on
              unrecognized fields, missing metadata, and other issues that the
              runtime tolerates.
";

    public const string ProjectPurge =
@"Usage: jarvis project purge [options] [path]

Delete all Jarvis Code state for a project (transcripts, tasks, file history,
config entry)

Options:
  --all              Purge state for every project (mutually exclusive with
                     [path])
  --dry-run          List what would be deleted without deleting anything
  -h, --help         Display help for command
  -i, --interactive  Prompt for each item before deleting
  -y, --yes          Skip confirmation prompt
";

    public const string PluginEvalInit =
@"Usage: jarvis plugin eval init [options] [name]

Author an eval suite under the eval dir (evals/ unless --eval-dir or the
manifest says otherwise) via an interview that sources inputs and designs
graders. Use --bare <name> for a blank single-case template.

Options:
  --bare             Write a blank template (prompt.md + graders/criteria.md)
                     instead of running the interview
  --eval-dir <dir>   Directory (below the current directory) to write cases into
                     (default: experimental.evals from the plugin.json in the
                     current directory, else evals/)
  -h, --help         Display help for command
  -i, --interactive  Run the authoring interview (already the default in a
                     terminal); requires an interactive terminal
";

    public const string PluginMarketplaceAdd =
@"Usage: jarvis plugin marketplace add [options] <source>

Add a marketplace from a URL, path, or GitHub repo

Options:
  -h, --help           Display help for command
  --scope <scope>      Where to declare the marketplace: user (default),
                       project, or local
  --sparse <paths...>  Limit checkout to specific directories via git
                       sparse-checkout (for monorepos). Example: --sparse
                       .claude-plugin plugins
";

    public const string PluginMarketplaceList =
@"Usage: jarvis plugin marketplace list [options]

List all configured marketplaces

Options:
  -h, --help  Display help for command
  --json      Output as JSON
";

    public const string PluginMarketplaceRemove =
@"Usage: jarvis plugin marketplace remove|rm [options] <name>

Remove a configured marketplace

Options:
  -h, --help       Display help for command
  --scope <scope>  Remove the marketplace declaration from a specific settings
                   scope: user, project, or local. Omit to remove it from every
                   scope.
";

    public const string PluginMarketplaceUpdate =
@"Usage: jarvis plugin marketplace update [options] [name]

Update marketplace(s) from their source - updates all if no name specified

Options:
  -h, --help  Display help for command
";

    /// <summary>Command path ("mcp add") -> its help text.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByPath =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agents"] = Agents,
            ["attach"] = Attach,
            ["auth"] = Auth,
            ["auto-mode"] = AutoMode,
            ["doctor"] = Doctor,
            ["gateway"] = Gateway,
            ["import"] = Import,
            ["install"] = Install,
            ["logs"] = Logs,
            ["mcp"] = Mcp,
            ["plugin"] = Plugin,
            ["project"] = Project,
            ["respawn"] = Respawn,
            ["rm"] = Rm,
            ["setup-token"] = SetupToken,
            ["stop"] = Stop,
            ["ultrareview"] = Ultrareview,
            ["update"] = Update,
            ["auth login"] = AuthLogin,
            ["auth logout"] = AuthLogout,
            ["auth status"] = AuthStatus,
            ["auto-mode config"] = AutoModeConfig,
            ["auto-mode critique"] = AutoModeCritique,
            ["auto-mode defaults"] = AutoModeDefaults,
            ["auto-mode reset"] = AutoModeReset,
            ["mcp add"] = McpAdd,
            ["mcp add-from-claude-desktop"] = McpAddFromClaudeDesktop,
            ["mcp add-json"] = McpAddJson,
            ["mcp get"] = McpGet,
            ["mcp list"] = McpList,
            ["mcp login"] = McpLogin,
            ["mcp logout"] = McpLogout,
            ["mcp remove"] = McpRemove,
            ["mcp reset-project-choices"] = McpResetProjectChoices,
            ["mcp serve"] = McpServe,
            ["plugin details"] = PluginDetails,
            ["plugin disable"] = PluginDisable,
            ["plugin enable"] = PluginEnable,
            ["plugin eval"] = PluginEval,
            ["plugin init"] = PluginInit,
            ["plugin install"] = PluginInstall,
            ["plugin list"] = PluginList,
            ["plugin marketplace"] = PluginMarketplace,
            ["plugin prune"] = PluginPrune,
            ["plugin tag"] = PluginTag,
            ["plugin uninstall"] = PluginUninstall,
            ["plugin update"] = PluginUpdate,
            ["plugin validate"] = PluginValidate,
            ["project purge"] = ProjectPurge,
            ["plugin eval init"] = PluginEvalInit,
            ["plugin marketplace add"] = PluginMarketplaceAdd,
            ["plugin marketplace list"] = PluginMarketplaceList,
            ["plugin marketplace remove"] = PluginMarketplaceRemove,
            ["plugin marketplace update"] = PluginMarketplaceUpdate,
        };
}
