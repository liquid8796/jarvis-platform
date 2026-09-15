using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// Which prompt document and which bundled sections a model receives.
/// </summary>
/// <remarks>
/// Every row here is a measurement, not a reading: CLI 2.1.257 was driven at a
/// local listener once per model with a cleaned environment, over the thirteen
/// models its catalog names and will still route (claude-opus-4-0 and
/// claude-opus-4-1 are remapped to Opus 5 before a request is built, so they
/// have no wire of their own). The reference's catalog carries a
/// <c>lean_prompt</c> capability that looks like the gate and is not one —
/// see <see cref="Mythos_5_takes_the_lean_prompt_its_capability_array_denies"/>.
/// </remarks>
public sealed class PromptModelProfileTests
{
    /// <summary>
    /// The prompt form, as captured. opus-4-8, opus-5 and the whole
    /// fable/mythos family take the lean document; every sonnet, every haiku,
    /// the Claude 3 generation and opus 4.5 through 4.7 take the classic one.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-fable-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-mythos-5", true)]
    [InlineData("claude-mythos-5-1", true)]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("claude-opus-4-6", false)]
    [InlineData("claude-opus-4-5", false)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData("claude-sonnet-4-0", false)]
    [InlineData("claude-haiku-4-5", false)]
    public void The_prompt_form_is_the_one_the_reference_sent(string modelId, bool lean)
    {
        Assert.Equal(lean, PromptModelProfile.For(modelId).Lean);
    }

    /// <summary>
    /// The capability array is not the gate, and this is the row that proves
    /// it: <c>claude-mythos-5</c> ships <c>capabilities:[]</c> — no
    /// <c>lean_prompt</c>, no <c>fable_5_mitigations</c> — and the reference
    /// still sends it the lean document and
    /// <c># Communicating with the user</c>, exactly like claude-fable-5.
    /// </summary>
    [Fact]
    public void Mythos_5_takes_the_lean_prompt_its_capability_array_denies()
    {
        var profile = PromptModelProfile.For("claude-mythos-5");

        Assert.True(profile.Lean);
        Assert.True(profile.Fable);
        Assert.False(profile.Fable51);
        Assert.True(profile.HasFableIdentity);
        Assert.True(profile.HasAutonomyAppend);
        Assert.False(profile.HasDeliveringWork);
        Assert.Equal(CommunicationSection.CommunicatingWithUser, profile.Communication);
    }

    /// <summary>
    /// The paragraph after <c># Harness</c>, as captured: fable-5 and mythos-5
    /// get the full section, the 5.1 pair get the one-line turn-updates rule,
    /// and opus-5 and opus-4-8 get the code-style line.
    /// </summary>
    [Theory]
    [InlineData("claude-fable-5", CommunicationSection.CommunicatingWithUser)]
    [InlineData("claude-mythos-5", CommunicationSection.CommunicatingWithUser)]
    [InlineData("claude-fable-5-1", CommunicationSection.TurnUpdates)]
    [InlineData("claude-mythos-5-1", CommunicationSection.TurnUpdates)]
    [InlineData("claude-opus-5", CommunicationSection.CodeStyle)]
    [InlineData("claude-opus-4-8", CommunicationSection.CodeStyle)]
    public void The_communication_paragraph_follows_the_capture(
        string modelId, CommunicationSection expected)
    {
        Assert.Equal(expected, PromptModelProfile.For(modelId).Communication);
    }

    /// <summary>
    /// <c># Delivering work</c> rode opus-5, fable-5-1 and mythos-5-1 and no
    /// other capture; <c># Corrections</c> rode opus-5 alone.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5", true, true)]
    [InlineData("claude-fable-5-1", true, false)]
    [InlineData("claude-mythos-5-1", true, false)]
    [InlineData("claude-fable-5", false, false)]
    [InlineData("claude-mythos-5", false, false)]
    [InlineData("claude-opus-4-8", false, false)]
    public void The_opus5_and_fable51_sections_follow_the_capture(
        string modelId, bool deliveringWork, bool corrections)
    {
        var profile = PromptModelProfile.For(modelId);

        Assert.Equal(deliveringWork, profile.HasDeliveringWork);
        Assert.Equal(corrections, profile.HasCorrections);
    }

    /// <summary>
    /// The knowledge cutoff, from the reference's own catalog and agreeing with
    /// every capture. This is the one thing the catalog is read for.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5", "May 2026")]
    [InlineData("claude-opus-4-8", "January 2026")]
    [InlineData("claude-opus-4-7", "January 2026")]
    [InlineData("claude-opus-4-6", "May 2025")]
    [InlineData("claude-opus-4-5", "May 2025")]
    [InlineData("claude-fable-5", "January 2026")]
    [InlineData("claude-fable-5-1", "June 2026")]
    [InlineData("claude-mythos-5", "January 2026")]
    [InlineData("claude-mythos-5-1", "June 2026")]
    [InlineData("claude-sonnet-5", "January 2026")]
    [InlineData("claude-sonnet-4-6", "August 2025")]
    [InlineData("claude-haiku-4-5", "February 2025")]
    public void The_knowledge_cutoff_is_the_one_the_reference_printed(string modelId, string cutoff)
    {
        Assert.Equal(cutoff, PromptModelProfile.For(modelId).KnowledgeCutoff);
    }

    /// <summary>
    /// The catalog files the 4.0 models under an explicit <c>-0</c>, which a
    /// bare <c>claude-{family}-{major}</c> key misses. Captured: a
    /// claude-sonnet-4-0 run really does print "January 2025", where this port
    /// printed no cutoff line at all.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-4-0", "January 2025")]
    [InlineData("claude-sonnet-4-20250514", "January 2025")]
    [InlineData("claude-opus-4-0", "January 2025")]
    [InlineData("claude-haiku-4-5-20251001", "February 2025")]
    public void A_date_stamped_or_zero_minor_id_still_finds_its_cutoff(string modelId, string cutoff)
    {
        Assert.Equal(cutoff, PromptModelProfile.For(modelId).KnowledgeCutoff);
    }

    /// <summary>
    /// The Claude 3 generation carries no cutoff line — its catalog rows have no
    /// <c>knowledge_cutoff</c> — and takes the classic prompt.
    /// </summary>
    [Theory]
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-3-7-sonnet-20250219")]
    [InlineData("claude-3-5-haiku-20241022")]
    public void The_claude_3_generation_has_no_cutoff_line(string modelId)
    {
        var profile = PromptModelProfile.For(modelId);

        Assert.Null(profile.KnowledgeCutoff);
        Assert.False(profile.Lean);
    }

    /// <summary>
    /// The id shapes the providers hand over all name the same model.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-opus-5-20260514")]
    [InlineData("us.anthropic.claude-opus-5-v1:0")]
    [InlineData("claude-opus-5@20260115")]
    [InlineData("llmapi/claude-opus-5")]
    [InlineData("anthropic/claude-opus-5")]
    public void Every_spelling_of_a_model_resolves_to_its_row(string modelId)
    {
        var profile = PromptModelProfile.For(modelId);

        Assert.True(profile.Lean);
        Assert.True(profile.Opus5);
        Assert.Equal("May 2026", profile.KnowledgeCutoff);
    }

    /// <summary>
    /// The task board rides the mid-conversation system turn, measured as 26
    /// tools against 30: the lean models and sonnet-5 take no board, everything
    /// older does.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5", false)]
    [InlineData("claude-opus-4-8", false)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-fable-5", false)]
    [InlineData("claude-fable-5-1", false)]
    [InlineData("claude-mythos-5", false)]
    [InlineData("claude-mythos-5-1", false)]
    [InlineData("claude-opus-4-7", true)]
    [InlineData("claude-opus-4-6", true)]
    [InlineData("claude-opus-4-5", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-sonnet-4-0", true)]
    [InlineData("claude-haiku-4-5", true)]
    [InlineData("gpt-5", true)]
    public void The_task_board_follows_the_capture(string modelId, bool takesBoard)
    {
        Assert.Equal(takesBoard, PromptModelProfile.For(modelId).TakesTaskBoard);
    }

    /// <summary>
    /// A model reached through another provider matches none of the lean
    /// families, so it takes the classic prompt and no bundle — the reference's
    /// own answer for a model it does not recognise.
    /// </summary>
    [Theory]
    [InlineData("gpt-5")]
    [InlineData("o3")]
    [InlineData("gemini-3-pro")]
    [InlineData("grok-4")]
    [InlineData("deepseek-chat")]
    [InlineData("deepseek-reasoner")]
    [InlineData("kimi-k2-instruct")]
    [InlineData("glm-4.6")]
    [InlineData("minimax-m2")]
    [InlineData("qwen2.5-coder:32b")]
    [InlineData("llama-4-scout")]
    [InlineData("")]
    public void A_model_from_another_provider_takes_the_classic_prompt(string modelId)
    {
        var profile = PromptModelProfile.For(modelId);

        Assert.False(profile.Lean);
        Assert.False(profile.Opus5);
        Assert.False(profile.Fable);
        Assert.False(profile.Fable51);
        Assert.False(profile.Opus48);
        Assert.Null(profile.KnowledgeCutoff);
    }

    /// <summary>
    /// The commit-trailer name reads the canonical id, so a stamped id still
    /// trails the family name.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4")]
    [InlineData("claude-opus-5", "claude-opus-5")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5")]
    public void The_canonical_name_is_unchanged(string modelId, string canonical)
    {
        Assert.Equal(canonical, PromptModelProfile.For(modelId).Canonical);
    }
}
