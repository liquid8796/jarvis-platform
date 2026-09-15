using System.IO;
using System.Text;

namespace JarvisCode.Parity.Tests;

/// <summary>Which reference build a source file's text was taken from.</summary>
internal enum ReferenceSide
{
    Cli,
    Desktop,
}

/// <summary>A ported file, and the build its text must still be found in.</summary>
/// <param name="MeasuredAgainst">
/// The reference build this file's text was read from, when that is recorded.
/// A delta row says <em>why</em> a line differs but not <em>when</em> it was
/// checked, so a reference update leaves no way to tell which files were
/// measured against which build. Null means unrecorded — the state every source
/// was in before this field existed — and is not invented for a file nobody
/// measured, because a provenance nobody established is worse than none.
/// </param>
internal readonly record struct PortedSource(
    string Path, ReferenceSide Side, string What, string? MeasuredAgainst = null);

/// <summary>
/// Every sentence this port copied out of the reference, checked against the
/// reference itself — not against the seventy literals somebody remembered to
/// paste into a test.
///
/// <see cref="CliLiteralParityTests"/> pins named strings one at a time, which
/// is the right shape for a handful of load-bearing constants. This is the
/// other half: it reads the ported files, pulls out every fixed run of prose
/// the program can actually emit, and requires the reference build to still
/// contain it. What the reference does not contain must be declared in
/// <c>Deltas/ported-text-deltas.tsv</c> with the reason it differs, so a
/// deliberate adaptation reads as a decision and an accident reads as a failure.
/// </summary>
public sealed class PortedTextParityTests
{
    /// <summary>
    /// Shorter than this is not evidence: a fragment like "the user" occurs in
    /// any large build by chance, so matching it would prove nothing.
    /// </summary>
    private const int MinimumRunLength = 30;

    internal static readonly PortedSource[] Sources =
    [
        new("JarvisCode.App/Services/ReferencePromptBuilder.cs", ReferenceSide.Cli,
            "the default interactive system prompt"),
        new("JarvisCode.Core/Mcp/McpToolSchemas.cs", ReferenceSide.Cli,
            "the MCP tool input-schema normalization and validation passes, and the sentences they report",
            MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.App/Services/AdvisorPrompt.cs", ReferenceSide.Cli,
            "the # Advisor Tool prompt block, sent last in the system prompt when an advisor model is set",
            MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.App/Services/GatedPromptSections.cs", ReferenceSide.Cli,
            "the prompt sections behind a language, background-job, focus-mode or delegation-steer gate",
            MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.App/Services/ClassicPromptBuilder.cs", ReferenceSide.Cli,
            "the system prompt for a model without the Claude 5 prompt bundle",
            MeasuredAgainst: "CLI 2.1.251"),
        new("JarvisCode.App/Services/ReferenceSubagentPrompt.cs", ReferenceSide.Cli,
            "the subagent system prompt", MeasuredAgainst: "CLI 2.1.251"),
        new("JarvisCode.App/Services/SessionModeNotices.cs", ReferenceSide.Cli,
            "the permission-mode notices", MeasuredAgainst: "CLI 2.1.251"),
        new("JarvisCode.App/Services/ReferenceToolDocs.cs", ReferenceSide.Cli, "the built-in tool docs"),
        new("JarvisCode.App/Services/ReferencePrompts.cs", ReferenceSide.Cli, "/init and /security-review"),
        new("JarvisCode.App/Services/BatchCommand.cs", ReferenceSide.Cli,
            "/batch's orchestration prompt and worker template", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.App/Services/DebugCommand.cs", ReferenceSide.Cli,
            "/debug's Debug Skill prompt", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.App/Services/ReleaseNotes.cs", ReferenceSide.Cli,
            "/release-notes' picker text and formatters", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/PluginValidation.cs", ReferenceSide.Cli,
            "the plugin validate report", MeasuredAgainst: "CLI 2.1.260"),
        new("JarvisCode.App/Services/SystemReminders.cs", ReferenceSide.Cli, "the system-reminder attachments"),
        new("JarvisCode.App/Services/OutputStyles.cs", ReferenceSide.Cli, "the built-in output styles",
            MeasuredAgainst: "CLI 2.1.247"),
        new("JarvisCode.App/Services/LoopPrompts.cs", ReferenceSide.Cli, "/loop and ScheduleWakeup"),
        new("JarvisCode.App/Services/AutoCompactCommand.cs", ReferenceSide.Cli, "/autocompact"),
        new("JarvisCode.App/Services/ContextReport.cs", ReferenceSide.Cli, "the /context report"),
        new("JarvisCode.App/Services/VendorWebSearchTool.cs", ReferenceSide.Cli, "WebSearch"),
        new("JarvisCode.App/Services/VendorWebFetchTool.cs", ReferenceSide.Cli, "WebFetch"),
        new("JarvisCode.App/Services/CoordinatorMode.cs", ReferenceSide.Cli, "the coordinator prompt"),
        new("JarvisCode.App/Services/MemoryRecall.cs", ReferenceSide.Cli, "recalled-memories reminders"),
        new("JarvisCode.App/Services/InstructionPrompts.cs", ReferenceSide.Cli,
            "the external-instruction-imports dialog", MeasuredAgainst: "CLI 2.1.251"),
        new("JarvisCode.App/Services/SuggestionTools.cs", ReferenceSide.Cli,
            "SuggestSkills and SuggestPluginInstall"),
        new("JarvisCode.Core/Agent/Teams.cs", ReferenceSide.Cli, "teammates and the team file"),
        new("JarvisCode.Core/Agent/Workflows.cs", ReferenceSide.Cli, "dynamic workflows"),
        new("JarvisCode.Core/Tools/BuiltIn/TaskBoardTools.cs", ReferenceSide.Cli, "the task board"),
        new("JarvisCode.Core/Customization/SkillInvocation.cs", ReferenceSide.Cli, "skill invocation"),
        new("JarvisCode.Core/Agent/ConversationCompactor.cs", ReferenceSide.Cli, "compaction"),
        new("JarvisCode.Core/Agent/TurnRecovery.cs", ReferenceSide.Cli, "the turn recovery ladder"),
        new("JarvisCode.Core/Hooks/PromptHooks.cs", ReferenceSide.Cli, "LLM prompt hooks"),
        new("JarvisCode.Core/Hooks/RemoteHooks.cs", ReferenceSide.Cli, "HTTP and MCP hooks"),
        new("JarvisCode.Core/Agent/GoalCheckins.cs", ReferenceSide.Cli, "goal check-ins"),
        new("JarvisCode.Core/Tools/BuiltIn/ExitPlanModeTool.cs", ReferenceSide.Cli, "ExitPlanMode"),
        new("JarvisCode.App/Services/PlanModePrompts.cs", ReferenceSide.Cli, "the plan-mode workflow"),
        new("JarvisCode.App/Services/PlanModeTools.cs", ReferenceSide.Cli, "EnterPlanMode"),
        new("JarvisCode.Core/Mcp/McpAutoBackground.cs", ReferenceSide.Cli,
            "the automatic move of a slow MCP call to the background"),
        new("JarvisCode.Core/Mcp/McpHeadersHelper.cs", ReferenceSide.Cli,
            "the headers helper's four failure sentences", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Core/Agent/DeferredToolAnnouncements.cs", ReferenceSide.Cli,
            "the deferred-tools delta and the MCP server notices", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Core/Agent/TaskNotifications.cs", ReferenceSide.Cli,
            "the task-notification envelope", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Core/Agent/ToolResultReminders.cs", ReferenceSide.Cli,
            "the batching and silent-turn reminders", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Core/Tools/DeferredTools.cs", ReferenceSide.Cli,
            "ToolSearch", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Core/Tools/BuiltIn/ShellTool.cs", ReferenceSide.Cli,
            "the background-command result", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Core/Agent/SubagentTool.cs", ReferenceSide.Cli,
            "the Agent tool's launch results", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Core/Agent/ForkAgent.cs", ReferenceSide.Cli,
            "the fork agent's preamble, refusals and worktree notice", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.App/Services/ForkAgentDoc.cs", ReferenceSide.Cli,
            "the Agent tool doc while the fork gate is on", MeasuredAgainst: "CLI 2.1.257"),

        new("JarvisCode.App/Services/ChatWelcome.cs", ReferenceSide.Desktop,
            "the empty chat screen and the mascot's poke replies",
            MeasuredAgainst: "desktop 1.44121.2.0"),

        new("JarvisCode.App/Services/HostPromptSections.cs", ReferenceSide.Desktop,
            "the desktop host's own system-prompt sections", MeasuredAgainst: "desktop 1.40609.0.0"),
        new("JarvisCode.App/Services/BrowserPaneHandlers.cs", ReferenceSide.Desktop, "the Browser pane handlers"),
        new("JarvisCode.App/Services/BrowserPanePopupGuard.cs", ReferenceSide.Desktop,
            "what a page-opened popup refuses"),
        new("JarvisCode.App/Services/PreviewTools.cs", ReferenceSide.Desktop,
            "the dev-server diagnostics"),
        new("JarvisCode.App/Services/PreviewPorts.cs", ReferenceSide.Desktop,
            "launch.json autoPort and the port-in-use ladder", MeasuredAgainst: "desktop 1.44121.2.0"),
        new("JarvisCode.App/Services/BrowserPaneDomainTransitions.cs", ReferenceSide.Desktop,
            "the pane's domain-transition consent", MeasuredAgainst: "desktop 1.44121.2.0"),
        new("JarvisCode.App/Services/BrowserPaneTools.cs", ReferenceSide.Desktop, "the Browser pane tool docs"),
        new("JarvisCode.App/Services/BrowserPaneScripts.cs", ReferenceSide.Desktop, "the pane's content scripts"),
        new("JarvisCode.App/Services/ComputerUse.cs", ReferenceSide.Desktop, "computer use"),
        new("JarvisCode.App/Services/ComputerUseGrants.cs", ReferenceSide.Desktop, "the app-grant tiers"),
        new("JarvisCode.App/Services/ComputerUseWindowHints.cs", ReferenceSide.Desktop,
            "the block a window the user pointed at puts on the next message"),
        new("JarvisCode.App/Services/ComputerUseHide.cs", ReferenceSide.Desktop,
            "the note a hide-before-action leaves above the next screenshot"),
        new("JarvisCode.App/Services/ComputerUseGrantResults.cs", ReferenceSide.Desktop,
            "the computer-use grant answers", MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Services/BackgroundTaskPresentation.cs", ReferenceSide.Desktop, "the tasks pane"),
        new("JarvisCode.App/Services/ComputerUseExtras.cs", ReferenceSide.Desktop, "the computer-use extras"),
        new("JarvisCode.App/Services/TeachMode.cs", ReferenceSide.Desktop, "teach mode"),
        new("JarvisCode.App/Services/DesktopLock.cs", ReferenceSide.Desktop, "the one-driver desktop lock"),
        new("JarvisCode.App/Services/JarvisBrowserTools.cs", ReferenceSide.Desktop, "claude-in-chrome"),
        new("JarvisCode.App/Services/JarvisBrowserInspectTools.cs", ReferenceSide.Desktop,
            "claude-in-chrome's inspection tools"),
        new("JarvisCode.App/Services/JarvisBrowserComputerTool.cs", ReferenceSide.Desktop,
            "claude-in-chrome's computer and browser_batch"),
        new("JarvisCode.App/Services/JarvisBrowserUploadTools.cs", ReferenceSide.Desktop,
            "claude-in-chrome's file_upload and gif_creator"),
        new("JarvisCode.App/Services/CcdSessionTools.cs", ReferenceSide.Desktop, "ccd_session"),
        new("JarvisCode.App/Services/CcdSessionMgmtTools.cs", ReferenceSide.Desktop, "ccd_session_mgmt"),
        new("JarvisCode.App/Services/CcdDirectoryTools.cs", ReferenceSide.Desktop, "ccd_directory"),
        new("JarvisCode.App/Services/TerminalMcpTools.cs", ReferenceSide.Desktop, "the terminal server"),
        new("JarvisCode.App/Services/VisualizeTools.cs", ReferenceSide.Desktop,
            "the visualize server's two tool docs and its show_widget result",
            MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Services/ConnectorMcpTools.cs", ReferenceSide.Desktop, "mcp-registry"),
        new("JarvisCode.App/Services/McpRegistry.cs", ReferenceSide.Desktop,
            "mcp-registry's search_mcp_registry", MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Services/ScheduledTaskTools.cs", ReferenceSide.Desktop, "scheduled-tasks"),
        new("JarvisCode.App/Services/AndroidEmulatorTools.cs", ReferenceSide.Desktop,
            "the Android emulator server's control tool", MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Services/AndroidEmulatorMessages.cs", ReferenceSide.Desktop,
            "every sentence the Android emulator's control tool answers with",
            MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Views/Panels/EmulatorPanel.cs", ReferenceSide.Desktop,
            "the live emulator pane's labels and empty state", MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Services/GitHubIssues.cs", ReferenceSide.Desktop,
            "the GitHub issue picker and the context block a chosen issue puts on the composer",
            MeasuredAgainst: "desktop 1.40609.1.0"),

        // The MCP server instruction blocks are the CLI's, not the desktop's:
        // the desktop bundle carries neither, and both live in claude.exe.
        new("JarvisCode.App/Services/McpServerInstructions.cs", ReferenceSide.Cli,
            "the MCP server instruction blocks"),
        new("JarvisCode.App/Services/PreviewVerification.cs", ReferenceSide.Desktop,
            "the preview verification workflow and its nudges"),
        new("JarvisCode.App/Services/MermaidDiagrams.cs", ReferenceSide.Desktop,
            "the mermaid fence's preprocessing and its refusals",
            MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Services/ComparisonStrings.cs", ReferenceSide.Desktop,
            "the side-by-side model comparison view",
            MeasuredAgainst: "desktop 1.40609.1.0"),

        // The interactive REPL: every sentence it draws comes from the
        // reference CLI's own Ink app, so the CLI binary is what confirms them.
        new("JarvisCode.Cli/Repl/Keys/KeybindingsFile.cs", ReferenceSide.Cli,
            "the keybindings.json loader's validation", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Render/StatusRow.cs", ReferenceSide.Cli,
            "the spinner and retry rows", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Render/Footer.cs", ReferenceSide.Cli,
            "the footer and the context indicator", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Render/ShortcutsOverlay.cs", ReferenceSide.Cli,
            "the ? overlay", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Render/Welcome.cs", ReferenceSide.Cli,
            "the welcome line and the startup tips", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Dialogs/PermissionPrompt.cs", ReferenceSide.Cli,
            "the permission prompt's rows", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Dialogs/PlanApproval.cs", ReferenceSide.Cli,
            "the plan approval dialog", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Dialogs/AskUserQuestionDialog.cs", ReferenceSide.Cli,
            "the AskUserQuestion card", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Dialogs/TrustDialog.cs", ReferenceSide.Cli,
            "the workspace trust dialog", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/Dialogs/ResumePicker.cs", ReferenceSide.Cli,
            "the resume picker", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/ReplView.cs", ReferenceSide.Cli,
            "the prompt box's notices", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/Repl/ReplCommandTable.cs", ReferenceSide.Cli,
            "the command surface's descriptions and refusals", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.Cli/InteractiveRepl.Sessions.cs", ReferenceSide.Cli,
            "the session flows' notices", MeasuredAgainst: "CLI 2.1.257"),
        new("JarvisCode.App/Services/ProjectInstructionsBlock.cs", ReferenceSide.Desktop,
            "the <project_instructions> block a chat project sends",
            MeasuredAgainst: "desktop 1.40609.1.0"),

        // The PR bar's Commit button sends its prompt to the model, so the whole
        // file's prose is compared even though the labels beside it are also pinned
        // by message id in Manifest/ui-strings.tsv.
        new("JarvisCode.App/Services/GitBarPresentation.cs", ReferenceSide.Desktop,
            "the PR bar's labels and its commit prompt", MeasuredAgainst: "desktop 1.40609.1.0"),
        new("JarvisCode.App/Services/WorkingDirectoryMenu.cs", ReferenceSide.Desktop,
            "the working-directory menu and the GitHub CLI prompt",
            MeasuredAgainst: "desktop 1.40609.1.0"),
    ];

    public static TheoryData<string> PortedFiles => [.. Sources.Select(static s => s.Path)];

    [ReferenceCliTheory]
    [MemberData(nameof(PortedFiles))]
    public void Every_ported_sentence_is_still_in_the_reference(string path)
    {
        var source = Sources.Single(s => s.Path == path);
        if (source.Side == ReferenceSide.Desktop && ReferenceInstall.AppDirectory is null)
        {
            // The desktop half needs the packaged app; the CLI attribute cannot
            // express that, so it is skipped here instead of failing.
            return;
        }

        var corpus = source.Side == ReferenceSide.Cli ? ReferenceCorpora.Cli : ReferenceCorpora.Desktop;
        Assert.True(corpus.Length > 0, $"the {corpus.Name} corpus is empty — nothing was read to compare against");

        var runs = ProseRuns(source.Path);
        Assert.True(runs.Count > 0, $"no prose was extracted from {path}; the extractor or the file changed shape");

        var declared = PortedTextDeltas.For(path);
        var undeclared = corpus.Missing(runs).Where(run => !declared.ContainsKey(run)).ToList();

        Assert.True(undeclared.Count == 0, Explain(source, corpus, undeclared));
    }

    /// <summary>
    /// The other direction: a declared delta that the reference *does* contain is
    /// stale bookkeeping — the note says we deliberately differ while the text
    /// agrees, which quietly turns a real future difference into an approved one.
    /// </summary>
    [ReferenceCliFact]
    public void Declared_deltas_are_still_deltas()
    {
        var stale = new List<string>();
        foreach (var source in Sources)
        {
            if (source.Side == ReferenceSide.Desktop && ReferenceInstall.AppDirectory is null)
            {
                continue;
            }

            var corpus = source.Side == ReferenceSide.Cli ? ReferenceCorpora.Cli : ReferenceCorpora.Desktop;
            var declared = PortedTextDeltas.For(source.Path);
            if (declared.Count == 0)
            {
                continue;
            }

            var present = declared.Keys.Except(corpus.Missing(declared.Keys)).ToList();
            stale.AddRange(present.Select(text => $"{source.Path}: {Preview(text)}"));
        }

        Assert.True(stale.Count == 0,
            "these lines are declared in Deltas/ported-text-deltas.tsv as differing from the reference, " +
            "but the reference now contains them verbatim — remove the rows:\n  " +
            string.Join("\n  ", stale.Take(20)));
    }

    /// <summary>
    /// A row the generator wrote and nobody explained. Leaving one in would turn
    /// this file from a record of decisions into a list of lines the suite
    /// agreed not to look at, which is worse than not having it.
    /// </summary>
    [Fact]
    public void No_declared_delta_is_still_unreviewed()
    {
        var unreviewed = PortedTextDeltas.Files
            .SelectMany(file => PortedTextDeltas.For(file)
                .Where(static row => row.Value.StartsWith("REVIEW", StringComparison.Ordinal))
                .Select(row => $"{file}: {Preview(row.Key)}"))
            .ToList();

        Assert.True(unreviewed.Count == 0,
            $"{unreviewed.Count} row(s) in Deltas/ported-text-deltas.tsv still say REVIEW — each needs the " +
            "reason it differs from the reference:\n  " + string.Join("\n  ", unreviewed.Take(15)));
    }

    /// <summary>
    /// Every declared delta must name a file the suite still checks, or the row
    /// is dead weight nobody will notice is wrong.
    /// </summary>
    [Fact]
    public void Declared_deltas_name_files_the_suite_reads()
    {
        var known = Sources.Select(static s => s.Path).ToHashSet(StringComparer.Ordinal);
        var orphans = PortedTextDeltas.Files.Where(file => !known.Contains(file)).ToList();
        Assert.True(orphans.Count == 0,
            "Deltas/ported-text-deltas.tsv has rows for files that are no longer ported sources: " +
            string.Join(", ", orphans));
    }

    /// <summary>
    /// The runs of prose a ported file can emit: string literals only (comments
    /// are skipped by the parser), concatenation chains joined back into whole
    /// sentences, interpolation holes acting as breaks, and one line at a time.
    /// </summary>
    internal static IReadOnlyList<string> ProseRuns(string relativePath)
    {
        var text = RepoPaths.ReadSource(relativePath);
        var runs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ported in PortedText.CSharpStrings(text))
        {
            foreach (var run in ported.FixedRuns())
            {
                foreach (var line in run.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length >= MinimumRunLength && trimmed.Contains(' '))
                    {
                        runs.Add(trimmed);
                    }
                }
            }
        }

        return [.. runs.OrderBy(static r => r, StringComparer.Ordinal)];
    }

    private static string Explain(PortedSource source, ReferenceCorpus corpus, IReadOnlyList<string> undeclared)
    {
        var version = source.Side == ReferenceSide.Cli
            ? ReferenceInstall.CliVersion ?? "unknown"
            : ReferenceInstall.AppVersion ?? "unknown";
        var builder = new StringBuilder()
            .AppendLine($"{source.Path} ({source.What}) has {undeclared.Count} line(s) the reference " +
                        $"{corpus.Name} {version} does not contain:");
        foreach (var line in undeclared.Take(15))
        {
            builder.AppendLine("  " + Preview(line));
        }

        if (undeclared.Count > 15)
        {
            builder.AppendLine($"  … and {undeclared.Count - 15} more");
        }

        // Which build the text came from is the first thing you want when it
        // stops matching: it separates "the reference moved" from "this was
        // never right".
        if (source.MeasuredAgainst is { Length: > 0 } measured)
        {
            builder.AppendLine();
            builder.AppendLine($"This file's text was measured against {measured}; the installed " +
                               $"{corpus.Name} is {version}.");
        }

        return builder
            .AppendLine("Either the reference reworded it — re-find the new text and update ours — or the")
            .AppendLine("difference is deliberate, in which case add it to Deltas/ported-text-deltas.tsv with")
            .AppendLine("its reason (JARVIS_APPROVE_PORTED_TEXT=1 rewrites that file, with a first guess at")
            .AppendLine("the reasons; review every row it writes before committing it).")
            .ToString();
    }

    private static string Preview(string line) =>
        line.Length > 120 ? line[..120] + "…" : line;
}

/// <summary>
/// The lines this port deliberately does not share with the reference, and why.
///
/// The file is the record of every place the port answers differently — our tool
/// names, this app's own shell, its brand, and the features the reference has
/// that this build does not. Keeping it as data rather than as prose in a
/// document is what lets the suite tell "we decided this" from "this drifted".
/// </summary>
internal static class PortedTextDeltas
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Entries =
        new(Load);

    public static IEnumerable<string> Files => Entries.Value.Keys;

    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Deltas", "ported-text-deltas.tsv");

    /// <summary>The declared deltas for one file: line → reason.</summary>
    public static IReadOnlyDictionary<string, string> For(string file) =>
        Entries.Value.TryGetValue(file, out var rows)
            ? rows
            : new Dictionary<string, string>(StringComparer.Ordinal);

    public static string Encode(string line) => line
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

    public static string Decode(string field)
    {
        var builder = new StringBuilder(field.Length);
        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] != '\\' || i + 1 >= field.Length)
            {
                builder.Append(field[i]);
                continue;
            }

            i++;
            builder.Append(field[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                _ => field[i],
            });
        }

        return builder.ToString();
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var byFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!File.Exists(Path))
        {
            return byFile.ToDictionary(
                static p => p.Key,
                static p => (IReadOnlyDictionary<string, string>)p.Value,
                StringComparer.Ordinal);
        }

        foreach (var raw in File.ReadAllLines(Path))
        {
            if (raw.Length == 0 || raw[0] == '#')
            {
                continue;
            }

            var fields = raw.Split('\t');
            if (fields.Length < 3)
            {
                continue;
            }

            var rows = byFile.TryGetValue(fields[0], out var existing)
                ? existing
                : byFile[fields[0]] = new Dictionary<string, string>(StringComparer.Ordinal);
            rows[Decode(fields[2])] = fields[1];
        }

        return byFile.ToDictionary(
            static p => p.Key,
            static p => (IReadOnlyDictionary<string, string>)p.Value,
            StringComparer.Ordinal);
    }
}
