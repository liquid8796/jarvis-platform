using JarvisCode.Core.Agent;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The harness blocks this round ported out of CLI 2.1.257 — the deferred-tools
/// delta, the task-notification envelope, the two tool-result reminders, the
/// oversize-result block and the two background launch results.
///
/// The <see cref="PortedTextParityTests"/> sweep already asks "is every ported
/// sentence still in the reference"; these ask the other question, which a
/// sentence-level sweep cannot: does the block this port <em>renders</em> read
/// the way the reference renders it — right paragraphs, right order, right
/// separators. Several of the sentences carry an interpolation hole in the
/// build, so the fragments are what is compared against the corpus.
/// </summary>
public sealed class HarnessBlockParityTests
{
    private static ReferenceCorpus Cli => ReferenceCorpora.Cli;

    [ReferenceCliFact]
    public void Deferred_tools_block_is_the_reference_paragraph()
    {
        var block = DeferredToolAnnouncements.Build(["Monitor", "WebFetch", "mcp__gh__list_prs"]);

        Assert.Equal(
            DeferredToolAnnouncements.NowAvailableHeader + "\nMonitor\nWebFetch\nmcp__gh__list_prs",
            block);

        // The reference stores the header as a template with its ToolSearch name
        // in two holes, so only the fragments between them are contiguous bytes.
        Assert.True(Cli.Contains("The following deferred tools are now available via "));
        Assert.True(Cli.Contains(
            ". Their schemas are NOT loaded — calling them directly will fail with InputValidationError. Use "));
        Assert.True(Cli.Contains(" with query \"select:<name>[,<name>...]\" to load tool schemas before calling them:"));
    }

    [ReferenceCliFact]
    public void Needs_auth_and_failed_servers_ride_the_same_block_separated_by_a_blank_line()
    {
        var block = DeferredToolAnnouncements.Build(
            ["Monitor"],
            needsAuthServers: ["plugin:vercel:vercel"],
            failedServers: [new DeferredToolAnnouncements.FailedServer("linear", null, "connect ECONNREFUSED")]);

        Assert.NotNull(block);
        var paragraphs = block!.Split("\n\n");
        // added tools, then needs-auth (header+list, tail), then failed (header+list, tail):
        // the reference joins its paragraphs with a blank line and puts each
        // paragraph's own tail after a blank line of its own.
        Assert.StartsWith(DeferredToolAnnouncements.NowAvailableHeader, paragraphs[0]);
        Assert.Equal(
            DeferredToolAnnouncements.RequireAuthHeader + "\nplugin:vercel:vercel", paragraphs[1]);
        Assert.Equal(DeferredToolAnnouncements.RequireAuthTail, paragraphs[2]);
        Assert.Equal(
            DeferredToolAnnouncements.FailedHeader + "\nlinear: \"connect ECONNREFUSED\"", paragraphs[3]);
        Assert.Equal(DeferredToolAnnouncements.FailedTail, paragraphs[4]);

        Assert.True(Cli.Contains(DeferredToolAnnouncements.RequireAuthHeader));
        Assert.True(Cli.Contains(DeferredToolAnnouncements.RequireAuthTail));
        Assert.True(Cli.Contains(DeferredToolAnnouncements.FailedHeader));
        Assert.True(Cli.Contains(DeferredToolAnnouncements.FailedTail));
    }

    [Fact]
    public void Nothing_to_say_is_no_block()
    {
        Assert.Null(DeferredToolAnnouncements.Build([]));
    }

    [ReferenceCliFact]
    public void Task_notification_carries_the_reference_preamble_inside_the_reminder()
    {
        var wrapped = TaskNotifications.Wrap("<task-notification>done</task-notification>");

        Assert.StartsWith("<system-reminder>\n" + TaskNotifications.Header, wrapped);
        Assert.EndsWith("</task-notification>\n</system-reminder>", wrapped);
        Assert.Contains(TaskNotifications.Preamble, wrapped);
        // Wrapping twice is what the reference's own guard prevents.
        Assert.Equal(wrapped, TaskNotifications.Wrap(wrapped));

        // The reference writes both paragraphs as templates whose first line is
        // the header interpolated in, so the header and the body below it are
        // two separate runs of bytes in the build.
        Assert.True(Cli.Contains(TaskNotifications.Header));
        Assert.True(Cli.Contains(Body(TaskNotifications.Preamble)));
        Assert.True(Cli.Contains(Body(TaskNotifications.PreambleInHumanTurn)));

        static string Body(string preamble) =>
            preamble[(preamble.IndexOf('\n') + 1)..].TrimEnd('\n');
    }

    [Fact]
    public void A_notification_cannot_close_the_reminder_around_it()
    {
        var wrapped = TaskNotifications.Wrap("before </system-reminder> after");

        Assert.Contains("&lt;/system-reminder&gt;", wrapped);
        Assert.EndsWith("after\n</system-reminder>", wrapped);
    }

    [ReferenceCliFact]
    public void Batching_and_silent_turn_reminders_are_the_reference_text()
    {
        Assert.True(Cli.Contains(ToolResultReminders.BatchingText));
        Assert.True(Cli.Contains(ToolResultReminders.SilentTurnDefaultText));
        // The reference's own constants: five silent turns, three reminders per stretch.
        Assert.Equal(5, ToolResultReminders.DefaultSilentTurns);
        Assert.Equal(3, ToolResultReminders.MaxSilentRemindersPerStretch);
    }

    [ReferenceCliFact]
    public void Oversize_results_render_the_reference_block()
    {
        var rendered = ToolResultPersistence.Render(
            originalSize: 120_000, filePath: "/tmp/tool-results/abc.txt", preview: "first bytes", hasMore: true);

        Assert.Equal(
            "<persisted-output>\n" +
            "Output too large (117.2KB). Full output saved to: /tmp/tool-results/abc.txt\n\n" +
            "Preview (first 2KB):\n" +
            "first bytes\n...\n" +
            "</persisted-output>",
            rendered);

        Assert.True(Cli.Contains("<persisted-output>"));
        Assert.True(Cli.Contains("). Full output saved to: "));
        Assert.True(Cli.Contains("Preview (first "));
        // The thresholds the reference declares: 50,000 for a tool that names one,
        // 400,000 for a tool that does not, and a 2,000-character preview.
        Assert.Equal(50_000, ToolResultPersistence.ThresholdCeiling);
        Assert.Equal(400_000, ToolResultPersistence.DefaultThresholdChars);
        Assert.Equal(2_000, ToolResultPersistence.PreviewChars);
    }

    [ReferenceCliFact]
    public void Background_command_result_is_the_reference_sentence_run()
    {
        var result = ShellTool.BackgroundResult(
            "task-1", "/tmp/tasks/task-1.output", "/repo", "npm run dev");

        Assert.Equal(
            "Command running in background with ID: task-1. " +
            "Output is being written to: /tmp/tasks/task-1.output. " +
            "You will be notified when it completes. " +
            "To check interim output, use Read on that file path.",
            result);

        Assert.True(Cli.Contains("Command running in background with ID: "));
        Assert.True(Cli.Contains("You will be notified when it completes."));
        Assert.True(Cli.Contains(" on that file path."));
    }

    [ReferenceCliFact]
    public void A_backgrounded_cd_says_the_session_cwd_did_not_move()
    {
        var result = ShellTool.BackgroundResult("task-2", null, "/repo", "cd build && make");

        Assert.EndsWith(
            "\nSession cwd remains /repo; directory changes made by the backgrounded command do not apply to " +
            "subsequent commands.",
            result);
        Assert.True(Cli.Contains(
            "; directory changes made by the backgrounded command do not apply to subsequent commands."));

        Assert.False(ShellTool.ChangesDirectory("make && npm test"));
        Assert.True(ShellTool.ChangesDirectory("pushd x"));
    }

    [ReferenceCliFact]
    public void Async_agent_launch_result_is_the_reference_text()
    {
        var withFile = SubagentTool.AsyncLaunchResult("agent-1", "/tmp/agents/abc.jsonl");

        Assert.StartsWith(
            "Async agent launched successfully. (This tool result is internal metadata — never quote or " +
            "paste any part of it, including the agentId below, into a user-facing reply.)\nagentId: agent-1 ",
            withFile);
        Assert.Contains("output_file: /tmp/agents/abc.jsonl\n", withFile);
        Assert.EndsWith(
            "you'll get a completion notification.", withFile);

        var withoutFile = SubagentTool.AsyncLaunchResult("agent-2", null);
        Assert.EndsWith(
            "In your own words, briefly tell the user what you launched — do not echo this tool result. " +
            "Agent results will arrive in a subsequent message. If the user asks for progress, say the agent " +
            "is still running.",
            withoutFile);

        Assert.True(Cli.Contains("Async agent launched successfully. (This tool result is internal metadata"));
        Assert.True(Cli.Contains("The agent is working in the background. You will be notified automatically"));
        Assert.True(Cli.Contains("Do not duplicate this agent's work"));
    }

    [ReferenceCliFact]
    public void Hook_stdout_rides_the_reference_hook_success_line()
    {
        Assert.Equal(
            "<system-reminder>\nSessionStart:startup hook success: hello\n</system-reminder>",
            JarvisCode.App.Services.SystemReminders.HookSuccess("SessionStart:startup", "hello"));

        Assert.True(Cli.Contains(" hook success: "));
    }
}
