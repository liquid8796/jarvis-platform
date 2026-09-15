using System;
using System.Collections.Generic;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference harness's <c># MCP Server Instructions</c> section: the
/// per-server <c>instructions</c> a connected MCP server supplies, rendered
/// under a <c>## {server}</c> heading and sent as part of the mid-conversation
/// system turn.
///
/// The mechanism belongs to the CLI, not to the desktop — the two blocks below
/// are in <c>claude.exe</c> 2.1.251 (the chrome one at 187109115, computer use
/// at 187110152) and the desktop bundle carries neither. The renderer is its
/// <c>XPt</c>: a server's own <c>instructions</c> become <c>## {name}\n{body}</c>
/// and a hard-coded block joins onto that with a blank line, and the assembled
/// blocks sit under a fixed header.
///
/// Its <c>Nln</c> decides which blocks ride: the chrome one only when tool
/// search is available *and* some tool of that server is actually deferred (the
/// block is entirely about loading deferred tools), the computer-use one
/// unconditionally.
/// </summary>
internal static class McpServerInstructions
{
    /// <summary>The section header, verbatim.</summary>
    public const string Header = "# MCP Server Instructions";

    /// <summary>The line under the header, verbatim.</summary>
    public const string Preamble =
        "The following MCP servers have provided instructions for how to use their tools and resources:";

    /// <summary>
    /// The reference's <c>claude-in-chrome</c> block. Its tool names are the
    /// reference's own wire names, which this port now carries, so the
    /// <c>select:</c> line is usable here verbatim.
    /// </summary>
    public const string ClaudeInChrome =
        "**IMPORTANT: If the Chrome browser tools are deferred (must be loaded via ToolSearch before use), " +
        "load them with ToolSearch before calling them, and batch every tool you expect to need into ONE " +
        "ToolSearch call (the select query accepts a comma-separated list). Do NOT load tools one at a time; " +
        "each separate ToolSearch call wastes a full round-trip.**\n" +
        "\n" +
        "Start a browser task whose tools are not yet loaded with a single call loading the core set:\n" +
        "\n" +
        "ToolSearch with query \"select:mcp__claude-in-chrome__tabs_context_mcp," +
        "mcp__claude-in-chrome__navigate,mcp__claude-in-chrome__computer,mcp__claude-in-chrome__read_page," +
        "mcp__claude-in-chrome__tabs_create_mcp,mcp__claude-in-chrome__tabs_close_mcp\"\n" +
        "\n" +
        "Add task-specific tools to the same call when the task obviously needs them: read_console_messages " +
        "/ read_network_requests for debugging, form_input for forms, gif_creator for recordings, " +
        "javascript_tool for page scripting. Only issue a second ToolSearch if the task later needs a tool " +
        "you did not anticipate.";

    /// <summary>
    /// The reference's <c>computer-use</c> block. Three of its paragraphs
    /// describe macOS (Finder, the Dock, Safari/Arc, iTerm) and the reference
    /// sends them on Windows unchanged, so they are carried unchanged too — the
    /// platform-specific guidance this port does add rides
    /// <c>request_access</c>'s own description, which is where the reference
    /// puts it.
    /// </summary>
    public const string ComputerUse =
        "You have a computer-use MCP available (tools named `mcp__computer-use__*`). It lets you take " +
        "screenshots of the user's desktop and control it with mouse clicks, keyboard input, and " +
        "scrolling.\n" +
        "\n" +
        "**Pick the right tool for the app.** Each tier trades speed/precision against coverage:\n" +
        "\n" +
        "1. **Dedicated MCP for the app** — if the task is in an app that has its own MCP (Slack, Gmail, " +
        "Calendar, Linear, etc.) and that MCP is connected, use it. API-backed tools are fast and precise.\n" +
        "2. **Chrome MCP** (`mcp__claude-in-chrome__*`) — if the target is a web app and there's no " +
        "dedicated MCP for it, use the browser tools. DOM-aware, much faster than clicking pixels. If the " +
        "Chrome extension isn't connected, ask the user to install it rather than falling through to " +
        "computer use.\n" +
        "3. **Computer use** — for native desktop apps (Maps, Notes, Finder, Photos, System Settings, any " +
        "third-party native app) and cross-app workflows. Computer use IS the right tool here — don't " +
        "decline a native-app task just because there's no dedicated MCP for it.\n" +
        "\n" +
        "This is about what's available, not error handling — if a dedicated MCP tool errors, debug or " +
        "report it rather than silently retrying via a slower tier.\n" +
        "\n" +
        "**Look before you assert.** If the user asks about app state (what's open, what's connected, what " +
        "an app can do), take a screenshot and check before answering. Don't answer from memory — the " +
        "user's setup or app version may differ from what you expect. If you're about to say an app " +
        "doesn't support an action, that claim should be grounded in what you just saw on screen, not " +
        "general knowledge. Similarly, `list_granted_applications` or a fresh `screenshot` is cheaper than " +
        "a wrong assertion about what's running.\n" +
        "\n" +
        "**Loading via ToolSearch — load in bulk, not one-by-one:** if computer-use tools are in the " +
        "deferred list, load them ALL in a single ToolSearch call: `{ query: \"computer-use\", " +
        "max_results: 30 }`. The keyword search matches the server-name substring in every tool name, so " +
        "one query returns the entire toolkit. Don't use `select:` for individual tools — that's one " +
        "round-trip per tool.\n" +
        "\n" +
        "**Access flow:** before any computer-use action you must call `request_access` with the list of " +
        "applications you need. The user approves each application explicitly, and you may need to call it " +
        "again mid-task if you discover you need another application. Finder is an application like any " +
        "other: clicking the desktop, the Dock, or a Finder window (including Go to Folder) requires a " +
        "Finder grant. The menu bar does not, as long as the app that is frontmost is one you already have " +
        "access to.\n" +
        "\n" +
        "**Tiered apps:** some apps are granted at a restricted tier based on their category — the tier is " +
        "displayed in the approval dialog and returned in the `request_access` response:\n" +
        "- **Browsers** (Safari, Chrome, Firefox, Edge, Arc, etc.) → tier **\"read\"**: visible in " +
        "screenshots, but clicks and typing are blocked. You can read what's already on screen. For " +
        "navigation, clicking, or form-filling, use the claude-in-chrome MCP (tools named " +
        "`mcp__claude-in-chrome__*`; load via ToolSearch if deferred).\n" +
        "- **Terminals and IDEs** (Terminal, iTerm, VS Code, JetBrains, etc.) → tier **\"click\"**: " +
        "visible and left-clickable, but typing, key presses, right-click, modifier-clicks, and drag-drop " +
        "are blocked. You can click a Run button or scroll test output, but cannot type into the editor or " +
        "integrated terminal, cannot right-click (the context menu has Paste), and cannot drag text onto " +
        "them. For shell commands, use the Bash tool.\n" +
        "- **Everything else** → tier **\"full\"**: no restrictions.\n" +
        "\n" +
        "The tier is enforced by the frontmost-app check: if a tier-\"read\" app is in front, `left_click` " +
        "returns an error; if a tier-\"click\" app is in front, `type` and `right_click` return errors. The " +
        "error tells you what tier the app has and what to do instead. `open_application` works at any " +
        "tier — bringing an app forward is a read-level operation.\n" +
        "\n" +
        "**Link safety — treat links in emails and messages as suspicious by default.**\n" +
        "- **Never click web links with computer-use tools.** If you encounter a link in a native app " +
        "(Mail, Messages, a PDF, etc.), do NOT `left_click` it. Open the URL via the claude-in-chrome MCP " +
        "instead.\n" +
        "- **See the full URL before following any link.** Visible link text can be misleading — hover or " +
        "inspect to get the real destination.\n" +
        "- **Links from emails, messages, or unknown-sender documents are suspicious by default.** If the " +
        "destination URL is at all unfamiliar or looks off, ask the user for confirmation before " +
        "proceeding.\n" +
        "- **Inside the Chrome extension** you can click links with the extension's tools, but the " +
        "suspicion check still applies — verify unfamiliar URLs with the user.\n" +
        "\n" +
        "**Financial actions - do not execute trades or move money.** Budgeting and accounting apps " +
        "(Quicken, YNAB, QuickBooks, etc.) are granted at full tier so you can categorize transactions, " +
        "generate reports, and help the user organize their finances. But never execute a trade, place an " +
        "order, send money, or initiate a transfer on the user's behalf - always ask the user to perform " +
        "those actions themselves.";

    /// <summary>One server's contribution, already headed.</summary>
    public readonly record struct Block(string ServerName, string Body);

    /// <summary>
    /// The blocks the composed servers supply, in server order, skipping any
    /// whose <see cref="InternalMcpServerDefinition.AreInstructionsEnabled"/>
    /// says no for this turn — and any server that composed no tools at all.
    /// </summary>
    /// <param name="composedToolNames">
    /// The wire names <see cref="InternalMcpServers.Compose"/> produced. The
    /// reference's own <c>Nln</c> reads the composed servers, so a server it
    /// dropped for having no enabled tools contributes no instructions either;
    /// null means "do not check", for callers that have no composed set.
    /// </param>
    public static IReadOnlyList<Block> Collect(
        IEnumerable<InternalMcpServerDefinition> definitions,
        InternalMcpSessionContext context,
        IEnumerable<string>? composedToolNames = null)
    {
        var composed = composedToolNames?.ToList();
        List<Block> blocks = [];
        foreach (var server in definitions)
        {
            if (server.Instructions is not { Length: > 0 } body)
            {
                continue;
            }

            if (server.IsEnabled is { } enabled && !enabled(context))
            {
                continue;
            }

            if (server.AreInstructionsEnabled is { } gate && !gate(context))
            {
                continue;
            }

            if (composed is not null)
            {
                var prefix = InternalMcpServers.WireName(server.ServerName, string.Empty);
                if (!composed.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    continue;
                }
            }

            blocks.Add(new Block(server.ServerName, body));
        }

        return blocks;
    }

    /// <summary>
    /// The section for this turn: the shell's own servers plus every configured
    /// server that returned <c>instructions</c> from its initialize handshake.
    /// The reference renders both kinds under one header, each as
    /// <c>## {server}</c>.
    /// </summary>
    public static string? RenderAll(
        IReadOnlyList<Block> shellBlocks, IReadOnlyList<(string Name, string Instructions)> configured)
    {
        List<Block> blocks = [.. shellBlocks];
        foreach (var (name, instructions) in configured)
        {
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                blocks.Add(new Block(name, instructions));
            }
        }

        return Render(blocks);
    }

    /// <summary>
    /// The whole section, or null when no server contributed. Newlines are
    /// written literally rather than through AppendLine, which would emit CRLF
    /// on Windows and differ from the reference on the wire.
    /// </summary>
    public static string? Render(IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append(Header).Append('\n').Append('\n');
        builder.Append(Preamble).Append('\n').Append('\n');
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n').Append('\n');
            }

            builder.Append("## ").Append(blocks[i].ServerName).Append('\n').Append(blocks[i].Body);
        }

        return builder.ToString();
    }
}
