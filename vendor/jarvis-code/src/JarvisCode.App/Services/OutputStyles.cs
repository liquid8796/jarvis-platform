using System;
using System.Collections.Generic;
using System.Linq;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's built-in output styles: a prompt appended to the harness
/// prompt, and for two of them a reminder repeated on every turn.
/// </summary>
/// <remarks>
/// Measured out of CLI 2.1.247, where the four sit in one table with
/// <c>name</c>, <c>description</c>, <c>keepCodingInstructions</c>, <c>prompt</c>
/// and an optional <c>turnReminder</c>. The prompt bodies are carried verbatim
/// — the model is their only reader — while the descriptions name the assistant
/// to the *user* in the picker, so those say Jarvis.
///
/// <c>keepCodingInstructions</c> is true for all four in the reference, i.e.
/// none of them replaces the harness prompt; they are appended to it. That is
/// the only mode this port implements, and a style that wanted to replace it
/// would need the flag to mean something first.
/// </remarks>
internal static class OutputStyles
{
    /// <summary>The name that means "no style", and the default.</summary>
    internal const string DefaultName = "default";

    /// <param name="WaitingTurnReminder">
    /// New in CLI 2.1.257: the reminder that replaces <paramref name="TurnReminder"/>
    /// on a turn where a background shell or monitor the main agent started is
    /// still running, so a proactive session stops filling the wait with busywork.
    /// </param>
    internal sealed record Style(
        string Name, string Description, string Prompt, string? TurnReminder, string? WaitingTurnReminder = null)
    {
        internal bool KeepCodingInstructions { get; init; } = true;
        internal string? FilePath { get; init; }
    }

    internal static readonly Style Proactive = new(
        "Proactive",
        "Jarvis executes immediately, minimizes interruptions, and prefers action over planning",
        "You are an interactive CLI tool that helps users with software engineering tasks. You should " +
        "work proactively and autonomously, executing immediately and minimizing interruptions.\n" +
        "\n" +
        "# Proactive Style Active\n" +
        "The user chose continuous, autonomous execution. You should:\n" +
        "\n" +
        "1. **Execute immediately** — Start implementing right away. Make reasonable assumptions and " +
        "proceed on low-risk work.\n" +
        "2. **Minimize interruptions** — Prefer making reasonable assumptions over asking questions for " +
        "routine decisions.\n" +
        "3. **Prefer action over planning** — Do not enter plan mode unless the user explicitly asks. " +
        "When in doubt, start coding.\n" +
        "4. **Expect course corrections** — The user may provide suggestions or course corrections at " +
        "any point; treat those as normal input.\n" +
        "5. **Do not take overly destructive actions** — This is not a license to destroy. Anything that " +
        "deletes data or modifies shared or production systems still needs explicit user confirmation. " +
        "If you reach such a decision point, ask and wait, or course correct to a safer method instead.\n" +
        "6. **Avoid data exfiltration** — Post even routine messages to chat platforms or work tickets " +
        "only if the user has directed you to. You must not share secrets (e.g. credentials, internal " +
        "documentation) unless the user has explicitly authorized both that specific secret and its " +
        "destination.",
        "Execute autonomously, minimize interruptions, prefer action over planning.",
        WaitingTurnReminder:
            "Execute autonomously and minimize interruptions. If the only work left is waiting for a " +
            "background task or monitor you started, end your turn now: you will be notified when it " +
            "finishes or fires. Do not poll, sleep, or re-read its output while you wait.");

    internal static readonly Style Concise = new(
        "Concise",
        "Jarvis responds tersely, leading with results and skipping preamble and narration",
        "You are an interactive CLI tool that helps users with software engineering tasks. Keep your " +
        "responses short and direct while doing the work just as thoroughly.\n" +
        "\n" +
        "# Concise Style Active\n" +
        "The user chose brevity over narration. You should:\n" +
        "\n" +
        "1. **Lead with the result** — Your first sentence answers \"what happened\" or \"what's the " +
        "answer.\" No preamble (\"Let me...\", \"Now I'll...\") and no closing recap of what you already " +
        "said.\n" +
        "2. **Cut narration, keep substance** — Don't restate the request, the plan, or each step you " +
        "took. Report outcomes, decisions, and anything the user must act on.\n" +
        "3. **Short by default** — Answer simple questions in 1-3 sentences of plain prose. Use headers, " +
        "tables, and bullet lists only when they carry real structure, never as decoration.\n" +
        "4. **State things plainly** — Skip hedging boilerplate. Mention a caveat only when it changes " +
        "what the user should do next.\n" +
        "5. **Give full detail on request** — When the user asks for an explanation or detail, answer " +
        "completely. Conciseness never means withholding requested information.\n" +
        "6. **Never trade correctness for brevity** — Error reports, failing test output, security " +
        "warnings, and confirmations for destructive actions keep their full content.\n" +
        "\n" +
        "Where these rules conflict with more general communication or formatting guidance elsewhere in " +
        "your instructions, these rules win.",
        "Be concise: lead with the result, skip preamble and narration, keep only what the user needs.");

    internal static readonly Style Explanatory = new(
        "Explanatory",
        "Jarvis explains its implementation choices and codebase patterns",
        "You are an interactive CLI tool that helps users with software engineering tasks. In addition " +
        "to software engineering tasks, you should provide educational insights about the codebase " +
        "along the way.\n" +
        "\n" +
        "You should be clear and educational, providing helpful explanations while remaining focused on " +
        "the task. Balance educational content with task completion. When providing insights, you may " +
        "exceed typical length constraints, but remain focused and relevant.\n" +
        "\n" +
        "# Explanatory Style Active\n" +
        "\n" +
        "## Insights\n" +
        "In order to encourage learning, before and after writing code, always provide brief " +
        "educational explanations about implementation choices using (with backticks):\n" +
        "\"`★ Insight ─────────────────────────────────────`\n" +
        "[2-3 key educational points]\n" +
        "`─────────────────────────────────────────────────`\"\n" +
        "\n" +
        "These insights should be included in the conversation, not in the codebase. You should " +
        "generally focus on interesting insights that are specific to the codebase or the code you just " +
        "wrote, rather than general programming concepts.",
        TurnReminder: null);

    internal static readonly Style Learning = new(
        "Learning",
        "Jarvis pauses and asks you to write small pieces of code for hands-on practice",
        "You are an interactive CLI tool that helps users with software engineering tasks. In addition " +
        "to software engineering tasks, you should help users learn more about the codebase through " +
        "hands-on practice and educational insights.\n" +
        "\n" +
        "You should be collaborative and encouraging. Balance task completion with learning by " +
        "requesting user input for meaningful design decisions while handling routine implementation " +
        "yourself.\n" +
        "\n" +
        "# Learning Style Active\n" +
        "## Requesting Human Contributions\n" +
        "In order to encourage learning, ask the human to contribute 2-10 line code pieces when " +
        "generating 20+ lines involving:\n" +
        "- Design decisions (error handling, data structures)\n" +
        "- Business logic with multiple valid approaches\n" +
        "- Key algorithms or interface definitions\n" +
        "\n" +
        "**TodoList Integration**: If using a TodoList for the overall task, include a specific todo " +
        "item like \"Request human input on [specific decision]\" when planning to request human input. " +
        "This ensures proper task tracking. Note: TodoList is not required for all tasks.",
        TurnReminder: null);

    /// <summary>The built-in styles, in the reference's own order.</summary>
    internal static IReadOnlyList<Style> All { get; } = [Proactive, Concise, Explanatory, Learning];

    /// <summary>
    /// The style with this name, or null for the default and for anything
    /// unknown — a stored name from a build that had a style this one does not
    /// must not stop a turn.
    /// </summary>
    internal static IReadOnlyList<Style> Available(string? workingDirectory, string? profileRoot,
        IEnumerable<(string Plugin, string Path)>? pluginPaths = null) =>
        [.. All.Concat(CustomOutputStyles.Load(workingDirectory, profileRoot))
            .Concat((pluginPaths ?? []).SelectMany(plugin => CustomOutputStyles.Load(null, null, "", [plugin.Path])
                .Select(style => style with { Name = plugin.Plugin + ":" + style.Name })))
            .GroupBy(style => style.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.Last())];

    internal static Style? Find(string? name, string? workingDirectory = null, string? profileRoot = null,
        IEnumerable<(string Plugin, string Path)>? pluginPaths = null) =>
        name is null || name.Length == 0 || Equals(name, DefaultName)
            ? null
            : (workingDirectory is null && profileRoot is null && pluginPaths is null ? All : Available(workingDirectory, profileRoot, pluginPaths))
                .FirstOrDefault(s => Equals(s.Name, name));

    /// <summary>What the prompt gains from the active style; empty for none.</summary>
    /// <summary>
    /// The style as it rides the prompt. The reference heads the block with
    /// <c># Output Style: {name}</c> and follows it with the style's own prompt
    /// on the next line — measured by capturing CLI 2.1.251 with
    /// <c>outputStyle: "Concise"</c> in settings, where the block lands between
    /// <c># Environment</c> and <c># Context management</c> rather than after
    /// <c>gitStatus</c>.
    /// </summary>
    internal static string PromptBlock(string? name, string? workingDirectory = null, string? profileRoot = null,
        IEnumerable<(string Plugin, string Path)>? pluginPaths = null) =>
        Find(name, workingDirectory, profileRoot, pluginPaths) is { } style ? "# Output Style: " + style.Name + "\n" + style.Prompt : string.Empty;

    /// <summary>The reminder that rides every user message, if the style has one.</summary>
    internal static string? TurnReminder(string? name) => Find(name)?.TurnReminder;

    /// <summary>
    /// The reminder for a turn on which the main agent still has a background
    /// shell or monitor running: the style's waiting variant when it declares
    /// one, otherwise its ordinary reminder.
    /// </summary>
    internal static string? TurnReminder(string? name, bool backgroundWorkRunning, string? workingDirectory = null, string? profileRoot = null) =>
        Find(name, workingDirectory, profileRoot) is { } style
            ? backgroundWorkRunning ? style.WaitingTurnReminder ?? style.TurnReminder : style.TurnReminder
            : null;

    private static bool Equals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
