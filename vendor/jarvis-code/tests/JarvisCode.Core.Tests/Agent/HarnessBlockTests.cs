using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Tests.Agent;

/// <summary>
/// The harness blocks ported from CLI 2.1.257: what decides whether each one is
/// sent, which is the half a text comparison cannot check.
/// </summary>
public sealed class HarnessBlockTests
{
    private sealed class Tool(string name) : ITool
    {
        public string Name => name;
        public string Description => "a tool";
        public System.Text.Json.Nodes.JsonObject InputSchema => new() { ["type"] = "object" };
        public bool IsReadOnly => true;
        public string DescribeCall(System.Text.Json.Nodes.JsonObject arguments) => name;
        public Task<ToolResult> ExecuteAsync(
            System.Text.Json.Nodes.JsonObject arguments, ToolExecutionContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("ok"));
    }

    [Theory]
    // The reference's shouldDefer built-ins, measured live on a ccd session.
    [InlineData("WebSearch", true)]
    [InlineData("WebFetch", true)]
    [InlineData("Monitor", true)]
    [InlineData("NotebookEdit", true)]
    [InlineData("TaskOutput", true)]
    [InlineData("EnterWorktree", true)]
    [InlineData("SuggestPluginInstall", true)]
    // …and the built-ins it loads.
    [InlineData("Read", false)]
    [InlineData("Bash", false)]
    [InlineData("Agent", false)]
    [InlineData("Skill", false)]
    [InlineData("SuggestSkills", false)]
    [InlineData("SendUserFile", false)]
    [InlineData("ScheduleWakeup", false)]
    [InlineData("ReportFindings", false)]
    [InlineData("ListAgents", false)]
    public void Built_ins_defer_the_way_the_reference_declares(string name, bool deferred) =>
        Assert.Equal(deferred, ToolDeferral.ShouldDefer(new Tool(name), isMcp: false, alwaysLoad: false));

    [Fact]
    public void ToolSearch_never_defers_itself_and_alwaysLoad_wins()
    {
        Assert.False(ToolDeferral.ShouldDefer(new Tool(ToolSearchTool.ToolName), isMcp: false, alwaysLoad: false));
        Assert.False(ToolDeferral.ShouldDefer(new Tool("mcp__x__y"), isMcp: true, alwaysLoad: true));
        Assert.True(ToolDeferral.ShouldDefer(new Tool("mcp__x__y"), isMcp: true, alwaysLoad: false));
    }

    [Fact]
    public void A_background_session_keeps_EnterWorktree()
    {
        Assert.True(ToolDeferral.ShouldDefer(new Tool("EnterWorktree"), isMcp: false, alwaysLoad: false));
        Assert.False(ToolDeferral.ShouldDefer(
            new Tool("EnterWorktree"), isMcp: false, alwaysLoad: false, sessionKind: "bg"));
    }

    [Fact]
    public void Deferred_names_are_ordinal_and_announced_once()
    {
        var registry = new DeferredToolRegistry(
            [new Tool("Read"), new Tool("WebSearch"), new Tool("mcp__gh__list_prs"), new Tool("Monitor")],
            t => ToolDeferral.ShouldDefer(t, t.Name.StartsWith("mcp__", StringComparison.Ordinal), false));

        // Ordinal order is what the reference's own addedNames.sort() gives.
        Assert.Equal(["Monitor", "WebSearch", "mcp__gh__list_prs"], registry.DeferredNames);
        Assert.Equal(["Monitor", "WebSearch", "mcp__gh__list_prs"], registry.TakeUnannouncedNames());
        Assert.Empty(registry.TakeUnannouncedNames());
    }

    [Fact]
    public void The_batching_reminder_is_suppressed_by_a_refused_call()
    {
        var clean = new List<ChatMessage>
        {
            new(Role.Assistant, [new TextBlock("working")]),
            ChatMessage.FromToolResults([new ToolResultBlock("1", "Read", "file body", IsError: false)]),
        };
        Assert.True(ToolResultReminders.BatchingApplies(clean));

        var refused = new List<ChatMessage>
        {
            new(Role.Assistant, [new TextBlock("working")]),
            ChatMessage.FromToolResults(
                [new ToolResultBlock("1", "Edit", "The user denied this tool call. Ask how…", IsError: true)]),
        };
        Assert.False(ToolResultReminders.BatchingApplies(refused));

        Assert.False(ToolResultReminders.BatchingApplies(
            [new ChatMessage(Role.User, [new TextBlock("hi")])]));
    }

    /// <summary>
    /// A gate refusal suppresses the reminder whatever reason the gate supplied.
    /// The reference can match on text because its eight refusal wordings are
    /// the whole of its vocabulary; one of this build's opens with the tool's
    /// name, so the flag is what carries it.
    /// </summary>
    [Fact]
    public void A_refusal_the_prefixes_cannot_match_still_suppresses_the_batching_reminder()
    {
        var refused = new List<ChatMessage>
        {
            new(Role.Assistant, [new TextBlock("working")]),
            ChatMessage.FromToolResults(
            [
                new ToolResultBlock(
                    "1", "Read",
                    "Read may not touch a path outside the working directory: this session runs with --restricted.",
                    IsError: true)
                { Refused = true },
            ]),
        };

        Assert.True(ToolResultReminders.FollowsToolResults(refused));
        Assert.True(ToolResultReminders.RunHasRefusal(refused));
        Assert.False(ToolResultReminders.BatchingApplies(refused));
    }

    /// <summary>
    /// A denial that was not the user's own answer leaves the batching reminder
    /// riding. Measured on CLI 2.1.257: a settings deny rule answered
    /// "Permission to use Bash with command echo hi has been denied." and both
    /// reminders still rode the turn, because none of the reference's eight
    /// <c>e1o()</c> wordings is a configuration denial.
    /// </summary>
    [Fact]
    public void A_configuration_denial_does_not_suppress_the_batching_reminder()
    {
        var messages = new List<ChatMessage>
        {
            new(Role.User, [new TextBlock("go")]),
            ChatMessage.FromToolResults(
            [
                new ToolResultBlock(
                    "1", "Bash",
                    "Permission to use Bash with command echo hi has been denied.",
                    IsError: true),
            ]),
        };
        var options = new ToolResultReminders.Options(SystemTurnModel: true, ModelOwnsText: true)
        {
            BatchingText = "BATCH",
            SecondaryText = "SECOND",
        };

        Assert.False(ToolResultReminders.RunHasRefusal(messages));
        Assert.Equal(["BATCH", "SECOND"], ToolResultReminders.Compose(messages, options));
    }

    /// <summary>
    /// A turn the harness submitted is not the user speaking, so it does not end
    /// the silent-turn run — the reference's own
    /// <c>type === "user" &amp;&amp; !isMeta</c>.
    /// </summary>
    [Fact]
    public void A_meta_user_turn_is_not_a_genuine_user_message()
    {
        var typed = new ChatMessage(Role.User, [new TextBlock("carry on")]);
        var harnessSubmitted = typed with { IsMeta = true };

        Assert.True(ToolResultReminders.IsGenuineUserMessage(typed));
        Assert.False(ToolResultReminders.IsGenuineUserMessage(harnessSubmitted));
    }

    /// <summary>
    /// The reference's <c>d</c> gates <c>k</c> alone: its
    /// <c>k = d ? QHo(…) : null</c> sits beside an ungated <c>T = JHo(…)</c>,
    /// so a refused call silences the batching nudge and leaves its secondary
    /// sibling standing.
    /// </summary>
    [Fact]
    public void A_refusal_silences_the_batching_reminder_but_not_the_secondary()
    {
        var refused = new List<ChatMessage>
        {
            new(Role.User, [new TextBlock("go")]),
            ChatMessage.FromToolResults(
                [new ToolResultBlock("1", "Edit", "denied, in words no prefix matches", IsError: true)
                { Refused = true }]),
        };
        var options = new ToolResultReminders.Options(SystemTurnModel: true, ModelOwnsText: true)
        {
            BatchingText = "BATCH",
            SecondaryText = "SECOND",
        };

        Assert.Equal(["SECOND"], ToolResultReminders.Compose(refused, options));
    }

    /// <summary>
    /// The reference stands the batching reminder down when a queued_command,
    /// teammate_mailbox or poll_events attachment follows the tool results. This
    /// build folds those in at the top of the next iteration, so the equivalent
    /// question is whether one is pending — and it gates the same one reminder.
    /// </summary>
    [Fact]
    public void A_pending_delivery_silences_the_batching_reminder_but_not_the_secondary()
    {
        var messages = new List<ChatMessage>
        {
            new(Role.User, [new TextBlock("go")]),
            ChatMessage.FromToolResults([new ToolResultBlock("1", "Read", "body", false)]),
        };
        var options = new ToolResultReminders.Options(SystemTurnModel: true, ModelOwnsText: true)
        {
            BatchingText = "BATCH",
            SecondaryText = "SECOND",
        };

        Assert.Equal(["BATCH", "SECOND"], ToolResultReminders.Compose(messages, options));
        Assert.Equal(["SECOND"], ToolResultReminders.Compose(messages, options, deliveryPending: true));
    }

    /// <summary>
    /// Neither reminder rides a turn that does not follow tool results — the
    /// reference's shared <c>wBn</c> gate, which sits ahead of <c>d</c>.
    /// </summary>
    [Fact]
    public void Neither_reminder_rides_a_turn_that_does_not_follow_tool_results()
    {
        var messages = new List<ChatMessage> { new(Role.User, [new TextBlock("just typing")]) };
        var options = new ToolResultReminders.Options(SystemTurnModel: true, ModelOwnsText: true)
        {
            BatchingText = "BATCH",
            SecondaryText = "SECOND",
        };

        Assert.False(ToolResultReminders.FollowsToolResults(messages));
        Assert.Empty(ToolResultReminders.Compose(messages, options));
    }

    /// <summary>
    /// The reference latches each reminder's text per conversation and model, so
    /// a variable changed mid-conversation moves nothing; another model in the
    /// same conversation resolves its own, and a new conversation starts over.
    /// </summary>
    [Fact]
    public void Reminder_text_latches_per_conversation_and_model()
    {
        var latch = new ToolResultReminders.TextLatch();
        const string Slot = ToolResultReminders.TextLatch.BatchingSlot;

        Assert.Equal("first", latch.Latch(Slot, "conv-1", "claude-opus-5", "first"));
        // Re-resolving the same pair answers what the conversation already holds.
        Assert.Equal("first", latch.Latch(Slot, "conv-1", "claude-opus-5", "changed"));
        // The reference keys its byModel map by the lower-cased id.
        Assert.Equal("first", latch.Latch(Slot, "conv-1", "CLAUDE-OPUS-5", "changed"));
        // Another model in the same conversation gets its own entry…
        Assert.Equal("other", latch.Latch(Slot, "conv-1", "claude-fable-5-1", "other"));
        // …and the two slots do not share one.
        Assert.Equal("sec", latch.Latch(
            ToolResultReminders.TextLatch.SecondarySlot, "conv-1", "claude-opus-5", "sec"));
        // A different conversation starts over.
        Assert.Equal("fresh", latch.Latch(Slot, "conv-2", "claude-opus-5", "fresh"));
    }

    /// <summary>
    /// The reference's <c>UCn</c>: the pattern's parts must appear in order and
    /// it anchors at neither end, so a bare literal matches anywhere.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5", "claude-opus-5", true)]
    [InlineData("opus", "claude-opus-5", true)]
    [InlineData("claude-*-5", "claude-opus-5", true)]
    [InlineData("*opus*", "claude-opus-5", true)]
    [InlineData("5-opus", "claude-opus-5", false)]
    [InlineData("claude-sonnet", "claude-opus-5", false)]
    [InlineData("", "claude-opus-5", true)]
    public void A_model_pattern_matches_the_way_the_reference_matches_it(
        string pattern, string model, bool matches) =>
        Assert.Equal(matches, ToolResultReminders.PatternMatches(pattern, model));

    /// <summary>
    /// The reference's <c>_ce</c> precedence: an exact id wins outright, then
    /// the glob with the most literal characters, and bare <c>*</c> is last.
    /// </summary>
    [Fact]
    public void An_exact_model_beats_every_pattern()
    {
        var map = new Dictionary<string, string?>
        {
            ["*"] = "star",
            ["claude-*"] = "short-glob",
            ["claude-opus-*"] = "long-glob",
            ["claude-opus-5"] = "exact",
        };
        Assert.Equal("exact", ToolResultReminders.MatchModelPattern(map, "claude-opus-5"));
    }

    [Fact]
    public void The_longest_literal_glob_wins_and_star_is_the_last_resort()
    {
        var globs = new Dictionary<string, string?>
        {
            ["*"] = "star",
            ["claude-*"] = "short-glob",
            ["claude-opus-*"] = "long-glob",
        };
        Assert.Equal("long-glob", ToolResultReminders.MatchModelPattern(globs, "claude-opus-5"));

        var starOnly = new Dictionary<string, string?> { ["*"] = "star", ["claude-sonnet-*"] = "other" };
        Assert.Equal("star", ToolResultReminders.MatchModelPattern(starOnly, "claude-opus-5"));

        // A pattern of nothing but stars is not the star pattern and matches nothing.
        var allStars = new Dictionary<string, string?> { ["**"] = "ignored" };
        Assert.Null(ToolResultReminders.MatchModelPattern(allStars, "claude-opus-5"));
    }

    [Fact]
    public void Model_patterns_are_matched_case_insensitively_and_trimmed()
    {
        var map = new Dictionary<string, string?> { ["  CLAUDE-Opus-5 "] = "exact" };
        Assert.Equal("exact", ToolResultReminders.MatchModelPattern(map, "claude-opus-5"));
    }

    /// <summary>A latched null stays null rather than re-resolving to a later value.</summary>
    [Fact]
    public void A_latched_absence_is_remembered_too()
    {
        var latch = new ToolResultReminders.TextLatch();
        Assert.Null(latch.Latch(ToolResultReminders.TextLatch.BatchingSlot, "c", "m", null));
        Assert.Null(latch.Latch(ToolResultReminders.TextLatch.BatchingSlot, "c", "m", "appeared later"));
    }

    [Fact]
    public void The_silent_turn_reminder_counts_assistant_turns_that_said_nothing()
    {
        var messages = new List<ChatMessage> { new(Role.User, [new TextBlock("go")]) };
        for (var i = 0; i < 5; i++)
        {
            messages.Add(new ChatMessage(Role.Assistant, [new ToolCallBlock("t" + i, "Read", "{}")]));
            messages.Add(ChatMessage.FromToolResults([new ToolResultBlock("t" + i, "Read", "body", false)]));
        }

        Assert.True(ToolResultReminders.SilentTurnDue(messages, ToolResultReminders.SilentTurnDefaultText, 5));

        // A turn that spoke to the user resets the run.
        messages.Add(new ChatMessage(Role.Assistant, [new TextBlock("here is what I found")]));
        Assert.False(ToolResultReminders.SilentTurnDue(messages, ToolResultReminders.SilentTurnDefaultText, 5));
    }

    [Fact]
    public void Neither_reminder_rides_a_model_without_the_system_turn()
    {
        var messages = new List<ChatMessage>
        {
            new(Role.User, [new TextBlock("go")]),
            ChatMessage.FromToolResults([new ToolResultBlock("1", "Read", "body", false)]),
        };
        var off = new ToolResultReminders.Options(SystemTurnModel: false, ModelOwnsText: true)
        {
            BatchingText = ToolResultReminders.BatchingText,
        };
        Assert.Empty(ToolResultReminders.Compose(messages, off));

        var on = new ToolResultReminders.Options(SystemTurnModel: true, ModelOwnsText: true)
        {
            BatchingText = ToolResultReminders.BatchingText,
        };
        Assert.Equal([ToolResultReminders.BatchingText], ToolResultReminders.Compose(messages, on));
    }

    [Fact]
    public void Only_a_fable_5_1_model_owns_the_batching_text()
    {
        Assert.Equal(ToolResultReminders.BatchingText,
            ToolResultReminders.ResolveBatchingText(null, modelOwnsText: true));
        Assert.Null(ToolResultReminders.ResolveBatchingText(null, modelOwnsText: false));
        // The environment override applies to any model, and a falsy value turns it off.
        Assert.Equal("say less", ToolResultReminders.ResolveBatchingText("say less", modelOwnsText: false));
        Assert.Null(ToolResultReminders.ResolveBatchingText("0", modelOwnsText: true));
    }

    [Fact]
    public void An_oversize_result_is_written_to_disk_and_previewed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var policy = new ToolResultPersistence(directory);
            var content = new string('x', 60_000);

            var applied = policy.Apply("Bash", "call-1", content, hasImages: false);

            Assert.StartsWith("<persisted-output>\nOutput too large (58.6KB). Full output saved to: ", applied);
            Assert.EndsWith("</persisted-output>", applied);
            Assert.Equal(content, File.ReadAllText(Path.Combine(directory, "call-1.txt")));

            // Under the threshold nothing moves, and an empty result gets the
            // reference's own stand-in.
            Assert.Equal("small", policy.Apply("Bash", "call-2", "small", hasImages: false));
            Assert.Equal("(Bash completed with no output)", policy.Apply("Bash", "call-3", "  ", hasImages: false));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void The_preview_is_cut_back_to_a_line_boundary()
    {
        var content = new string('a', 1_500) + "\n" + new string('b', 1_500);

        var (preview, hasMore) = ToolResultPersistence.Preview(content);

        Assert.True(hasMore);
        Assert.Equal(1_500, preview.Length);
        Assert.DoesNotContain('b', preview);
    }

    [Theory]
    [InlineData(500, "500 bytes")]
    [InlineData(2048, "2KB")]
    [InlineData(120_000, "117.2KB")]
    public void Sizes_read_the_reference_way(long chars, string expected) =>
        Assert.Equal(expected, ToolResultPersistence.FormatSize(chars));
}
