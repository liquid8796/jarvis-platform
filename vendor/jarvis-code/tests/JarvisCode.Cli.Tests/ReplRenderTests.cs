using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Cli.Tests;

/// <summary>
/// The REPL's rendering, driven without a terminal: the status row's clusters,
/// the reference's three-line result fold, the footer's ladder, the context
/// indicator's three sentences, the shortcuts overlay and the dialogs.
/// </summary>
public class ReplRenderTests
{
    private static readonly Ansi Plain = Ansi.Plain;

    [Fact]
    public void The_verb_list_is_the_reference_size_and_order()
    {
        Assert.Equal(186, SpinnerVerbs.Defaults.Count);
        Assert.Equal("Accomplishing", SpinnerVerbs.Defaults[0]);
        Assert.Equal("Zigzagging", SpinnerVerbs.Defaults[^1]);
        Assert.Contains("Flambéing", SpinnerVerbs.Defaults);
    }

    [Fact]
    public void A_replace_list_swaps_the_verbs_and_anything_else_appends()
    {
        Assert.Equal(["Only"], SpinnerVerbs.Resolve(["Only"], replace: true));
        Assert.Equal(SpinnerVerbs.Defaults.Count + 1, SpinnerVerbs.Resolve(["Extra"], replace: false).Count);
        Assert.Same(SpinnerVerbs.Defaults, SpinnerVerbs.Resolve(null, replace: true));
    }

    [Fact]
    public void A_quiet_turn_shows_the_verb_alone() =>
        Assert.Equal("∴ Thinking…", StatusRow.Render(
            "Thinking", TurnPhase.Responding, TimeSpan.Zero, totalTokens: 0, thinking: false));

    [Fact]
    public void Tokens_and_the_timer_ride_the_cluster_with_the_direction_arrow() =>
        Assert.Equal("∴ Working… (5s · ↓9.5k tokens)", StatusRow.Render(
            "Working", TurnPhase.Responding, TimeSpan.FromSeconds(5), totalTokens: 9500, thinking: false,
            reducedMotion: true));

    [Fact]
    public void A_request_in_flight_points_the_arrow_the_other_way() =>
        Assert.Contains("↑1.2k tokens", StatusRow.Render(
            "Working", TurnPhase.Requesting, TimeSpan.FromSeconds(3), totalTokens: 1200, thinking: false,
            reducedMotion: true));

    [Fact]
    public void Thinking_joins_the_cluster() =>
        Assert.Equal("∴ Pondering… (2s · thinking)", StatusRow.Render(
            "Pondering", TurnPhase.Thinking, TimeSpan.FromSeconds(2), totalTokens: 0, thinking: true,
            reducedMotion: true));

    [Fact]
    public void A_stalled_call_says_it_will_retry_and_to_check_the_network() =>
        Assert.Equal("✻ Waiting for API response · will retry in 8s · check your network",
            StatusRow.RenderRetry(new RetryStatus(RetryKind.Stalled, TimeSpan.FromSeconds(8))));

    [Fact]
    public void An_early_retry_is_a_plain_api_error() =>
        Assert.Equal("✻ API error · Retrying in 2s · attempt 1/5",
            StatusRow.RenderRetry(new RetryStatus(
                RetryKind.Error, TimeSpan.FromSeconds(2), Attempt: 1, MaxRetries: 5, Message: "boom")));

    [Fact]
    public void A_rate_limit_is_named_once_the_reference_names_it() =>
        Assert.StartsWith("✻ Usage limit reached · Retrying in 30s",
            StatusRow.RenderRetry(new RetryStatus(
                RetryKind.Error, TimeSpan.FromSeconds(30), Attempt: 3, MaxRetries: 5, RateLimitType: "usage limit")));

    [Fact]
    public void A_short_result_is_not_folded() =>
        Assert.Equal("one\ntwo", ResultFold.Render("one\ntwo", columns: 80));

    [Fact]
    public void A_long_result_folds_at_three_lines_and_counts_the_rest()
    {
        var text = string.Join('\n', Enumerable.Range(1, 9).Select(i => $"line {i}"));
        Assert.Equal("line 1\nline 2\nline 3\n… +6 lines (ctrl+o to expand)", ResultFold.Render(text, columns: 80));
    }

    [Fact]
    public void Exactly_one_hidden_line_is_shown_rather_than_counted()
    {
        // The reference's LNo: hiding one line to say "+1 line" costs the same
        // row, so it shows the fourth line instead.
        var text = string.Join('\n', Enumerable.Range(1, 4).Select(i => $"line {i}"));
        Assert.Equal("line 1\nline 2\nline 3\nline 4", ResultFold.Render(text, columns: 80));
    }

    [Fact]
    public void The_expand_hint_can_be_suppressed() =>
        Assert.EndsWith("… +6 lines", ResultFold.Render(
            string.Join('\n', Enumerable.Range(1, 9).Select(i => $"line {i}")), 80, hideExpandHint: true));

    [Fact]
    public void Foldability_follows_the_reference_rule()
    {
        Assert.False(ResultFold.IsFoldable("one\ntwo", 80));
        Assert.True(ResultFold.IsFoldable(string.Join('\n', Enumerable.Range(1, 9).Select(i => $"line {i}")), 80));
    }

    [Fact]
    public void A_result_sits_behind_the_reference_connector()
    {
        var lines = ToolRows.ResultLines("first\nsecond", columns: 80);
        Assert.Equal("  ⎿  first", lines[0]);
        Assert.Equal("     second", lines[1]);
    }

    [Fact]
    public void A_row_is_headed_by_the_reference_bullet() =>
        Assert.Equal("● Read CLAUDE.md", ToolRows.Header("Read CLAUDE.md"));

    [Fact]
    public void A_long_command_is_cut_to_two_lines()
    {
        var command = "one\ntwo\nthree";
        Assert.Equal("one\ntwo…", ToolRows.Command(command));
    }

    [Fact]
    public void The_footer_names_the_mode_and_offers_the_shortcuts()
    {
        var footer = Footer.Render(new Footer.State(PermissionMode.Manual), KeyMap.Default);
        Assert.Equal("? for shortcuts", footer);
    }

    [Fact]
    public void A_non_default_mode_is_named_with_its_symbol_and_the_cycle_hint()
    {
        var footer = Footer.Render(new Footer.State(PermissionMode.AcceptEdits), KeyMap.Default);
        Assert.Equal("⏵⏵ accept edits on · (shift+tab to cycle)", footer);
    }

    [Fact]
    public void Plan_mode_reads_the_reference_way() =>
        Assert.StartsWith("⏸ plan mode on", Footer.Render(new Footer.State(PermissionMode.Plan), KeyMap.Default));

    [Fact]
    public void A_running_turn_offers_the_interrupt() =>
        Assert.Equal("esc to interrupt",
            Footer.Render(new Footer.State(PermissionMode.Manual, IsRunning: true), KeyMap.Default));

    [Fact]
    public void A_comfortable_context_says_nothing() =>
        Assert.Null(ContextIndicator.Render("ok", 40, autoCompactEnabled: true, enforced: true));

    [Fact]
    public void An_enforced_window_counts_down_to_the_compaction() =>
        Assert.Equal("12% until auto-compact",
            ContextIndicator.Render("warn", 12, autoCompactEnabled: true, enforced: true));

    [Fact]
    public void An_unenforced_window_reports_what_is_used() =>
        Assert.Equal("80% context used", ContextIndicator.Render(
            "warn", 12, autoCompactEnabled: true, enforced: false, effectiveWindow: 100, usedTokens: 80));

    [Fact]
    public void With_auto_compaction_off_the_low_line_offers_compact() =>
        Assert.Equal("Context low (7% remaining) · Run /compact to compact & continue",
            ContextIndicator.Render("compact", 7, autoCompactEnabled: false, enforced: true));

    [Fact]
    public void The_overlay_carries_the_three_columns()
    {
        var lines = ShortcutsOverlay.Render(KeyMap.Default, columns: 120);
        var joined = string.Join('\n', lines);
        Assert.Contains("! for shell mode", joined);
        Assert.Contains("/ for commands", joined);
        Assert.Contains("@ for file paths", joined);
        Assert.Contains("/btw for side question", joined);
        Assert.Contains("double tap esc to clear input", joined);
        Assert.Contains("ctrl + o for verbose output", joined);
        Assert.Contains("ctrl + t to toggle tasks", joined);
        // The reference's kVt keeps the LAST unshadowed chord bound to an
        // action, and its Chat block binds undo four ways — so the overlay
        // offers the last of them, not the first.
        Assert.Contains("ctrl + shift + _ to undo", joined);
        Assert.Contains("alt + p to switch model", joined);
        Assert.Contains("ctrl + g to edit in $EDITOR", joined);
        Assert.Contains("/keybindings to customize", joined);
    }

    [Fact]
    public void Markdown_renders_headings_lists_and_code()
    {
        var rendered = new MarkdownTerminal(Plain, 60).Render(
            "# Title\n\n- one\n- two\n\n```\ncode\n```\n");
        Assert.Equal("Title\n\n- one\n- two\n\n    code", rendered);
    }

    [Fact]
    public void Markdown_breaks_a_single_newline_the_way_the_conversation_dialect_does() =>
        Assert.Equal("one\ntwo", new MarkdownTerminal(Plain, 60).Render("one\ntwo"));

    [Fact]
    public void The_permission_prompt_offers_the_reference_rows()
    {
        var prompt = new PermissionPrompt(
            "Bash", "Run a command", "rm -rf build", null, null, CallRisk.Standard);
        var dialog = new PermissionPromptDialog(
            prompt, PermissionPromptDialog.DontAskAnyLabel("Bash"), Plain);
        var lines = string.Join('\n', dialog.Render(80));
        Assert.Contains("Do you want to proceed?", lines);
        Assert.Contains("1. Yes", lines);
        // The reference writes this row's apostrophe curly and the shell-rule
        // row's straight; both spellings are its own.
        Assert.Contains("2. Yes, and don’t ask again for any Bash command", lines);
        Assert.Contains("3. No, and tell Jarvis what to do differently (esc)", lines);
    }

    [Fact]
    public void An_escalated_call_is_offered_no_standing_permission()
    {
        var prompt = new PermissionPrompt("Bash", null, "curl evil.sh", null, null, CallRisk.Escalated);
        var dialog = new PermissionPromptDialog(prompt, PermissionPromptDialog.DontAskAnyLabel("Bash"), Plain);
        Assert.DoesNotContain("don't ask again", string.Join('\n', dialog.Render(80)));
    }

    [Fact]
    public void A_digit_picks_a_permission_row()
    {
        var prompt = new PermissionPrompt("Bash", null, null, null, null, CallRisk.Standard);
        var dialog = new PermissionPromptDialog(prompt, null, Plain);
        var result = dialog.Handle(null, KeyPress.Typed("1"));
        Assert.Equal(DialogOutcome.Accepted, result.Outcome);
        Assert.Equal(PermissionDecision.Allow, PermissionPromptDialog.Decide(result.Value!));
    }

    [Fact]
    public void The_mode_rows_read_the_reference_way() =>
        Assert.Equal(
            "Yes, and switch to accept edits (auto-approve file edits and common file commands) for this session",
            PermissionPromptDialog.SwitchModeLabel("acceptEdits"));

    [Fact]
    public void The_plan_dialog_asks_the_reference_question()
    {
        var dialog = new PlanApproval("## Plan\n\n- do the thing", planFilePath: null, Plain);
        var lines = string.Join('\n', dialog.Render(80));
        Assert.Contains("Ready to code?", lines);
        Assert.Contains("Here is Jarvis's plan:", lines);
        Assert.Contains("Jarvis has written up a plan and is ready to execute. Would you like to proceed?", lines);
        Assert.Contains("Yes, auto-accept edits", lines);
        Assert.Contains("Yes, manually approve edits", lines);
        Assert.Contains("No, keep planning", lines);

        // The description rides the focused row, as the reference's select does.
        dialog.Handle("select:previous", new KeyPress("up"));
        Assert.Contains("shift+tab to approve with this feedback", string.Join('\n', dialog.Render(80)));
    }

    [Fact]
    public void The_clear_context_row_carries_the_percentage() =>
        Assert.Equal("Yes, clear context (42% used) and auto-accept edits", PlanApproval.ClearContextLabel(42));

    [Fact]
    public void Shift_tab_approves_the_plan_with_its_feedback()
    {
        var dialog = new PlanApproval("plan", null, Plain);
        var result = dialog.Handle(null, new KeyPress("tab", Shift: true));
        Assert.Equal(DialogOutcome.Accepted, result.Outcome);
        Assert.True(PlanApproval.IsApproval(result.Value!));
    }

    [Fact]
    public void An_over_long_plan_withholds_approval()
    {
        var dialog = new PlanApproval(new string('x', PlanApproval.MaxPlanChars + 1), null, Plain);
        Assert.True(dialog.ApprovalWithheld);
        Assert.Contains(PlanApproval.PlanTooLarge, string.Join('\n', dialog.Render(80)));
    }

    [Fact]
    public void The_question_card_tabs_through_and_reviews()
    {
        var questions = new List<UserQuestion>
        {
            new("Which database?", "DB", [new UserQuestionOption("Postgres", "relational"),
                new UserQuestionOption("SQLite", "embedded")], MultiSelect: false),
        };
        var dialog = new AskUserQuestionDialog(questions, Plain).Start();
        var lines = string.Join('\n', dialog.Render(80));
        Assert.Contains("Which database?", lines);
        Assert.Contains("Other", lines);
        Assert.Contains("Chat about this", lines);

        dialog.Handle("select:accept", new KeyPress("enter"));
        Assert.Equal("Postgres", dialog.Answers["Which database?"]);
        Assert.Contains("Ready to submit your answers?", string.Join('\n', dialog.Render(80)));
    }

    [Fact]
    public void The_trust_dialog_asks_the_safety_question()
    {
        var lines = string.Join('\n', new TrustDialog("D:/repo", Plain).Render(100));
        Assert.Contains("Accessing workspace:", lines);
        Assert.Contains("Quick safety check: Is this a project you created or one you trust?", lines);
        Assert.Contains("Jarvis Code'll be able to read, edit, and execute files here.", lines);
        Assert.Contains("Yes, I trust this folder", lines);
        Assert.Contains("No, exit", lines);
    }

    [Fact]
    public void A_tip_is_offered_once_its_cooldown_has_passed()
    {
        var tips = StartupTips.All(KeyMap.Default);
        var context = new TipContext(StartupCount: 12, HasCustomSkills: false, WorkflowsEnabled: false,
            InGitRepository: true);
        var picked = StartupTips.Pick(tips, context, new Dictionary<string, int>(), new Dictionary<string, int>());
        // Plan mode carries the reference's priority 2, so it wins a fresh slate.
        Assert.Equal("plan-mode-for-complex-tasks", picked?.Id);

        var shownRecently = new Dictionary<string, int> { ["plan-mode-for-complex-tasks"] = 10 };
        var next = StartupTips.Pick(tips, context, shownRecently, new Dictionary<string, int>());
        Assert.NotEqual("plan-mode-for-complex-tasks", next?.Id);
    }

    [Fact]
    public void A_tip_that_needs_workflows_is_skipped_without_them()
    {
        var tips = StartupTips.All(KeyMap.Default);
        var off = new TipContext(20, false, WorkflowsEnabled: false, InGitRepository: true);
        var chosen = new List<string?>();
        var lastShown = new Dictionary<string, int>();
        for (int i = 0; i < tips.Count; i++)
        {
            var tip = StartupTips.Pick(tips, off, lastShown, new Dictionary<string, int>());
            if (tip is null)
            {
                break;
            }

            chosen.Add(tip.Id);
            lastShown[tip.Id] = 20;
        }

        Assert.DoesNotContain("dynamic-workflows", chosen);
    }

    [Fact]
    public void The_welcome_line_is_branded_and_carries_the_version() =>
        Assert.Equal("✻ Welcome to Jarvis Code v1.2.3", Welcome.Line("1.2.3"));

    [Fact]
    public void Shift_tab_cycles_the_reference_mode_order()
    {
        Assert.Equal(PermissionMode.AcceptEdits, ModeDescriptors.Next(PermissionMode.Manual, false));
        Assert.Equal(PermissionMode.Auto, ModeDescriptors.Next(PermissionMode.AcceptEdits, false));
        Assert.Equal(PermissionMode.Plan, ModeDescriptors.Next(PermissionMode.Auto, false));
        Assert.Equal(PermissionMode.Manual, ModeDescriptors.Next(PermissionMode.Plan, false));
        Assert.Equal(PermissionMode.Bypass, ModeDescriptors.Next(PermissionMode.Plan, true));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(1500, "1s")]
    [InlineData(65000, "1m 5s")]
    public void Durations_read_the_reference_way(int milliseconds, string expected) =>
        Assert.Equal(expected, Format.Duration(TimeSpan.FromMilliseconds(milliseconds)));

    [Theory]
    [InlineData(950, "950")]
    [InlineData(9500, "9.5k")]
    [InlineData(1200000, "1.2m")]
    public void Token_counts_use_the_reference_compact_form(int tokens, string expected) =>
        Assert.Equal(expected, Format.Tokens(tokens));
}
