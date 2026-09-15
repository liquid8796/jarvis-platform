using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The prompt form a model on another provider receives, and — more importantly
/// — which models the setting is allowed to answer for.
/// </summary>
public sealed class PromptFormPreferenceTests
{
    /// <summary>
    /// A model the reference's tables cannot place: no catalog row, and no
    /// <c>claude-{family}-{version}</c> to read out of the id.
    /// </summary>
    [Theory]
    [InlineData("gpt-5")]
    [InlineData("o3")]
    [InlineData("gemini-3-pro")]
    [InlineData("grok-4")]
    [InlineData("deepseek-reasoner")]
    [InlineData("kimi-k2-instruct")]
    [InlineData("glm-4.6")]
    [InlineData("minimax-m2")]
    [InlineData("qwen2.5-coder:32b")]
    [InlineData("llama-4-scout")]
    [InlineData("openai/gpt-5")]
    public void Another_providers_model_is_the_settings_business(string modelId)
    {
        Assert.True(PromptFormPreference.IsOtherProviderModel(modelId));
        Assert.True(PromptFormPreference.LeanForOtherProvider(modelId, PromptFormPreference.Lean));
        Assert.False(PromptFormPreference.LeanForOtherProvider(modelId, PromptFormPreference.Classic));
    }

    /// <summary>
    /// An Anthropic model is never decided by the setting — it keeps the form
    /// the reference's own family rule gives it, in every id shape the providers
    /// hand over.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-mythos-5")]
    [InlineData("claude-haiku-4-5-20251001")]
    [InlineData("claude-sonnet-4-0")]
    [InlineData("us.anthropic.claude-opus-5-v1:0")]
    [InlineData("claude-opus-5@20260115")]
    [InlineData("llmapi/claude-opus-5")]
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-3-7-sonnet")]
    public void An_anthropic_model_is_never_the_settings_business(string modelId)
    {
        Assert.False(PromptFormPreference.IsOtherProviderModel(modelId));
        Assert.False(PromptFormPreference.LeanForOtherProvider(modelId, PromptFormPreference.Lean));
    }

    /// <summary>
    /// An Anthropic id the catalog does not name yet still reads as Anthropic —
    /// its family rule answers for it, so the setting stays out of the way
    /// rather than sending a future sonnet the lean document.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-9")]
    [InlineData("claude-opus-6")]
    [InlineData("claude-haiku-7-1")]
    public void An_unreleased_anthropic_id_still_reads_as_anthropic(string modelId)
    {
        Assert.False(PromptFormPreference.IsOtherProviderModel(modelId));
        Assert.False(PromptFormPreference.LeanForOtherProvider(modelId, PromptFormPreference.Lean));
    }

    /// <summary>
    /// The stored value: only the exact word means lean, and the default and
    /// anything unrecognised mean the reference's own answer.
    /// </summary>
    [Theory]
    [InlineData("lean", true)]
    [InlineData("Lean", true)]
    [InlineData("  LEAN  ", true)]
    [InlineData("classic", false)]
    [InlineData("Classic", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("something else", false)]
    public void Only_the_lean_value_moves_the_form(string? form, bool lean)
    {
        Assert.Equal(lean, PromptFormPreference.IsLean(form));
        Assert.Equal(lean, PromptFormPreference.LeanForOtherProvider("gpt-5", form));
    }

    /// <summary>
    /// An empty model id is nobody's business — it is the neutral profile the
    /// tool-doc builder asks for, not a model on a provider.
    /// </summary>
    [Fact]
    public void An_empty_model_id_is_not_another_providers_model()
    {
        Assert.False(PromptFormPreference.IsOtherProviderModel(""));
        Assert.False(PromptFormPreference.LeanForOtherProvider("", PromptFormPreference.Lean));
    }

    /// <summary>
    /// Uninstalled — a headless run, and every test that does not pass a value —
    /// reads as the reference's own default rather than as lean.
    /// </summary>
    [Fact]
    public void Without_the_setting_installed_nothing_moves()
    {
        Assert.False(PromptFormPreference.LeanForOtherProvider("gpt-5"));
    }

    /// <summary>
    /// The stored default is the classic form, which is what
    /// <see cref="JarvisCode.Core.Settings.AppSettings"/> ships.
    /// </summary>
    [Fact]
    public void The_default_setting_is_classic()
    {
        Assert.Equal(
            PromptFormPreference.Classic,
            new JarvisCode.Core.Settings.AppSettings().OtherProviderPromptForm);
    }
}
