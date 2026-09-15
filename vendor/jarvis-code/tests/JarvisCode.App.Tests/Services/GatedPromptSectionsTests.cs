using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Settings;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The four prompt sections behind a reproducible gate: which one fires, what it
/// says, and where the two prompt builders put it.
/// </summary>
public sealed class GatedPromptSectionsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] entries)
    {
        var map = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void Language_block_is_absent_without_a_configured_language()
    {
        Assert.Null(GatedPromptSections.LanguageBlock(null));
        Assert.Null(GatedPromptSections.LanguageBlock("   "));
    }

    [Fact]
    public void Language_block_names_the_language_three_times()
    {
        var block = GatedPromptSections.LanguageBlock(" japanese ");

        Assert.NotNull(block);
        Assert.StartsWith("# Language\nAlways respond in japanese.", block);
        // The reference's own sentence interpolates the language into all three
        // slots; a template that filled only the first would read as advice.
        Assert.Equal(3, block!.Split("japanese").Length - 1);
    }

    [Fact]
    public void Background_session_needs_both_of_the_reference_variables()
    {
        Assert.Null(GatedPromptSections.BackgroundSessionBlock(Env()));
        Assert.Null(GatedPromptSections.BackgroundSessionBlock(
            Env((GatedPromptSections.SessionKindVariable, "bg"))));
        Assert.Null(GatedPromptSections.BackgroundSessionBlock(
            Env((GatedPromptSections.JobDirectoryVariable, @"D:\jobs\7"))));
    }

    [Fact]
    public void Background_session_names_the_job_tmp_directory_and_the_default_isolation()
    {
        var block = GatedPromptSections.BackgroundSessionBlock(Env(
            (GatedPromptSections.SessionKindVariable, "bg"),
            (GatedPromptSections.JobDirectoryVariable, @"D:\jobs\7")));

        Assert.NotNull(block);
        Assert.StartsWith("# Background Session", block);
        Assert.Contains(Path.Combine(@"D:\jobs\7", "tmp"), block, StringComparison.Ordinal);
        Assert.Contains("Before making any code changes, use the EnterWorktree tool", block!, StringComparison.Ordinal);
        // An isolated job is told to commit before it finishes; the tail carries
        // the reference's own never-force-push sentence.
        Assert.Contains("Never push to main/master, force-push, or merge.", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_session_working_in_place_drops_the_commit_tail()
    {
        var block = GatedPromptSections.BackgroundSessionBlock(Env(
            (GatedPromptSections.SessionKindVariable, "bg"),
            (GatedPromptSections.JobDirectoryVariable, @"D:\jobs\7"),
            (GatedPromptSections.IsolationVariable, "none")))!;

        Assert.Contains("Edit files directly in your working directory", block, StringComparison.Ordinal);
        Assert.DoesNotContain("commit before finishing", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_session_isolation_worktree_asks_for_EnterWorktree_first()
    {
        var block = GatedPromptSections.BackgroundSessionBlock(Env(
            (GatedPromptSections.SessionKindVariable, "bg"),
            (GatedPromptSections.JobDirectoryVariable, @"D:\jobs\7"),
            (GatedPromptSections.IsolationVariable, "worktree")))!;

        Assert.Contains("Call the EnterWorktree tool as your first action", block, StringComparison.Ordinal);
        // The path is this app's, not the reference's — its EnterWorktree makes
        // .jarvis-worktrees.
        Assert.Contains(".jarvis-worktrees/", block, StringComparison.Ordinal);
        Assert.DoesNotContain(".claude/worktrees/", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Focus_mode_has_a_lean_and_a_classic_wording()
    {
        var lean = GatedPromptSections.FocusModeBlock(leanPrompt: true);
        var classic = GatedPromptSections.FocusModeBlock(leanPrompt: false);

        Assert.StartsWith("# Focus mode", lean);
        Assert.StartsWith("# Focus mode", classic);
        Assert.NotEqual(lean, classic);
    }

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("nonsense", "default")]
    [InlineData("no_nudges", "no_nudges")]
    [InlineData("counter_steer", "counter_steer")]
    public void Steer_reads_only_the_reference_spellings(string? value, string expected)
    {
        var environment = value is null
            ? Env()
            : Env((GatedPromptSections.SteerVariable, value));

        Assert.Equal(expected, GatedPromptSections.Steer(environment));
    }

    /// <summary>
    /// The reference resolves the steer once per session and answers with the
    /// latched value thereafter (its <c>hH</c> over <c>um.latch</c>), so a
    /// variable set or cleared mid-conversation moves nothing. It latches every
    /// outcome, "default" included.
    /// </summary>
    [Fact]
    public void The_steer_latches_for_the_session()
    {
        var latch = new GatedPromptSections.SteerLatch();
        Assert.Equal("counter_steer",
            latch.Resolve(Env((GatedPromptSections.SteerVariable, "counter_steer"))));

        // Clearing the variable does not un-steer a conversation already running.
        Assert.Equal("counter_steer", latch.Resolve(Env()));
        Assert.Equal("counter_steer",
            latch.Resolve(Env((GatedPromptSections.SteerVariable, "no_nudges"))));

        // A conversation that starts over gets a new latch with it, which is
        // what the reference's resetLatch does for a store that outlives one.
        Assert.Equal("no_nudges", new GatedPromptSections.SteerLatch()
            .Resolve(Env((GatedPromptSections.SteerVariable, "no_nudges"))));
    }

    /// <summary>"default" latches too, so a later variable cannot switch it on.</summary>
    [Fact]
    public void A_default_steer_latches_as_firmly_as_a_steering_one()
    {
        var latch = new GatedPromptSections.SteerLatch();
        Assert.Equal("default", latch.Resolve(Env()));
        Assert.Equal("default",
            latch.Resolve(Env((GatedPromptSections.SteerVariable, "counter_steer"))));
    }

    /// <summary>Resolve threads the latch, so the block follows it rather than the environment.</summary>
    [Fact]
    public void Resolve_uses_the_latched_steer()
    {
        var settings = new AppSettings();
        var latch = new GatedPromptSections.SteerLatch();
        var steering = Env((GatedPromptSections.SteerVariable, "counter_steer"));

        Assert.Equal(
            GatedPromptSections.Delegating,
            GatedPromptSections.Resolve(
                settings, leanPrompt: true, focusMode: false, hasAgentTool: true, steering, latch)
                .DelegationSteer);

        // The variable is gone, the conversation's steer is not.
        Assert.Equal(
            GatedPromptSections.Delegating,
            GatedPromptSections.Resolve(
                settings, leanPrompt: true, focusMode: false, hasAgentTool: true, Env(), latch)
                .DelegationSteer);

        // Without a latch each call resolves fresh, which is what a caller with
        // no session of its own gets.
        Assert.Null(GatedPromptSections
            .Resolve(settings, leanPrompt: true, focusMode: false, hasAgentTool: true, Env())
            .DelegationSteer);
    }

    [Fact]
    public void Delegation_block_needs_the_steer_and_the_Agent_tool()
    {
        var settings = new AppSettings();
        var steering = Env((GatedPromptSections.SteerVariable, "counter_steer"));

        Assert.Null(GatedPromptSections
            .Resolve(settings, leanPrompt: true, focusMode: false, hasAgentTool: false, steering)
            .DelegationSteer);
        Assert.Null(GatedPromptSections
            .Resolve(settings, leanPrompt: true, focusMode: false, hasAgentTool: true, Env())
            .DelegationSteer);
        Assert.Equal(
            GatedPromptSections.Delegating,
            GatedPromptSections
                .Resolve(settings, leanPrompt: true, focusMode: false, hasAgentTool: true, steering)
                .DelegationSteer);
    }

    [Fact]
    public void Nothing_is_gated_on_by_default()
    {
        var resolved = GatedPromptSections.Resolve(
            new AppSettings(), leanPrompt: true, focusMode: false, hasAgentTool: true, Env());

        Assert.Equal(GatedPromptSections.None, resolved);
    }

    [Fact]
    public void The_lean_builder_puts_each_block_in_the_reference_slot()
    {
        var model = new JarvisCode.Core.Models.ModelInfo("anthropic", "claude-opus-5", "Opus 5", 200_000);
        var prompt = ReferencePromptBuilder.Build(
            Directory.GetCurrentDirectory(),
            model,
            gated: new GatedPromptSections(
                Language: GatedPromptSections.LanguageBlock("japanese"),
                BackgroundSession: GatedPromptSections.BackgroundSessionBlock(Env(
                    (GatedPromptSections.SessionKindVariable, "bg"),
                    (GatedPromptSections.JobDirectoryVariable, @"D:\jobs\7"))),
                FocusMode: GatedPromptSections.FocusModeBlock(leanPrompt: true),
                DelegationSteer: GatedPromptSections.Delegating));

        // env_info_simple, language, bg-session, context_management, focus_mode,
        // act_dont_rederive, ..., subagent_steer_delegation, opus5_reduced_delegation.
        var environment = prompt.IndexOf("# Environment", StringComparison.Ordinal);
        var language = prompt.IndexOf("# Language", StringComparison.Ordinal);
        var background = prompt.IndexOf("# Background Session", StringComparison.Ordinal);
        var contextManagement = prompt.IndexOf("# Context management", StringComparison.Ordinal);
        var focus = prompt.IndexOf("# Focus mode", StringComparison.Ordinal);
        var steer = prompt.IndexOf("## Delegating to subagents", StringComparison.Ordinal);
        var reduced = prompt.IndexOf("Do not use the Agent tool, workflows", StringComparison.Ordinal);

        Assert.True(environment < language, "# Language follows # Environment");
        Assert.True(language < background, "# Background Session follows # Language");
        Assert.True(background < contextManagement, "# Context management follows # Background Session");
        Assert.True(contextManagement < focus, "# Focus mode follows # Context management");
        Assert.True(focus < steer, "the delegation steer follows # Focus mode");
        Assert.True(steer < reduced, "the reduced-delegation line follows the delegation steer");
    }
}

/// <summary>
/// The Agent tool doc's fork branch: which form it takes, and that the doc the
/// captures pin is unchanged while the gate is off.
/// </summary>
public sealed class ForkAgentDocTests
{
    private static PromptModelProfile Opus5 => PromptModelProfile.For("claude-opus-5");

    private static PromptModelProfile Fable51 => PromptModelProfile.For("claude-fable-5-1");

    private static PromptModelProfile Classic => PromptModelProfile.For("claude-opus-4-5");

    [Fact]
    public void The_gate_off_leaves_the_captured_doc_alone()
    {
        Assert.DoesNotContain("fork", ReferenceToolDocs.RunAgentDoc(Opus5), StringComparison.Ordinal);
        Assert.DoesNotContain("fork", ReferenceToolDocs.RunAgentDoc(Classic), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lean_fork_doc_rewrites_the_head_and_replaces_the_background_bullet()
    {
        var doc = ReferenceToolDocs.RunAgentDoc(Opus5, forkAvailable: true);

        Assert.Contains("specify a subagent_type to select an agent: `\"fork\"` forks yourself",
            doc, StringComparison.Ordinal);
        Assert.Contains("(except subagent_type: \"fork\", which inherits your context)",
            doc, StringComparison.Ordinal);
        Assert.Contains("A fork runs in the background and keeps its tool output out of your context.",
            doc, StringComparison.Ordinal);
        // The reference's Pe is empty while a fork can be spawned: the fork note
        // carries the background sentence instead of the bullet.
        Assert.DoesNotContain("- Subagents run in the background by default;", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reach_sentence_still_follows_the_delegation_stance()
    {
        // opus-5 carries the reduced-delegation line, so its doc withholds the
        // opener; fable-5-1 does not, so its doc keeps it.
        Assert.DoesNotContain("Reach for this when the task matches",
            ReferenceToolDocs.RunAgentDoc(Opus5, forkAvailable: true), StringComparison.Ordinal);
        Assert.Contains("Reach for this when the task matches",
            ReferenceToolDocs.RunAgentDoc(Fable51, forkAvailable: true), StringComparison.Ordinal);
    }

    [Fact]
    public void The_classic_fork_doc_drops_When_not_to_use_and_the_dont_race_bullet()
    {
        var doc = ReferenceToolDocs.RunAgentDoc(Classic, forkAvailable: true);

        Assert.DoesNotContain("## When not to use", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("- **Don't race**", doc, StringComparison.Ordinal);
        Assert.Contains("## When to fork", doc, StringComparison.Ordinal);
        Assert.Contains("Any agent other than a fork starts with zero context.", doc, StringComparison.Ordinal);
        Assert.Contains("For fresh agents, terse command-style prompts", doc, StringComparison.Ordinal);
        Assert.Contains("subagent_type: \"fork\",\n  name: \"ship-audit\"", doc, StringComparison.Ordinal);
    }
}
