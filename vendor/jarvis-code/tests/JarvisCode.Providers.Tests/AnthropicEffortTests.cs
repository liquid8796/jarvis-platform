using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.Providers.Tests;

/// <summary>
/// The effort wire, verified field-for-field against requests captured from the
/// installed Claude Code CLI 2.1.251 (ANTHROPIC_BASE_URL pointed at a local
/// listener; one capture per effort level and model class).
/// </summary>
public class AnthropicEffortTests
{
    [Theory]
    // The 5 family and Opus/Sonnet 4.6+ run adaptive thinking + the effort dial.
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-sonnet-5", true)]
    [InlineData("claude-fable-5", true)]
    [InlineData("claude-opus-4-6", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("us.anthropic.claude-opus-4-6-v1:0", true)]
    [InlineData("claude-opus-5@20260115", true)]
    // Everything older keeps the fixed budget; date stamps are not minor versions.
    [InlineData("claude-sonnet-4-20250514", false)]
    [InlineData("claude-haiku-4-5-20251001", false)]
    [InlineData("claude-opus-4-1-20250805", false)]
    [InlineData("claude-3-7-sonnet-20250219", false)]
    [InlineData("us.anthropic.claude-v1:0", false)]
    [InlineData("test-model", false)]
    public void SupportsAdaptive_MatchesTheCliModelClasses(string modelId, bool expected)
        => Assert.Equal(expected, AnthropicEffort.SupportsAdaptive(modelId));

    [Theory]
    [InlineData(ThinkingEffort.Low, "low")]
    [InlineData(ThinkingEffort.Medium, "medium")]
    [InlineData(ThinkingEffort.High, "high")]
    [InlineData(ThinkingEffort.XHigh, "xhigh")]
    [InlineData(ThinkingEffort.Max, "max")]
    public void WireName_UsesTheCliSpelling(ThinkingEffort effort, string expected)
        => Assert.Equal(expected, AnthropicEffort.WireName(effort));

    private static LlmRequest Request(string model, ThinkingEffort effort) => new()
    {
        ModelId = model,
        SystemPrompt = "be helpful",
        Messages = [new ChatMessage(Role.User, [new TextBlock("hi")])],
        ThinkingEffort = effort,
    };

    [Fact]
    public void AdaptiveModel_MirrorsTheCapturedCliBody()
    {
        var body = AnthropicProvider.BuildRequestBody(Request("claude-opus-5", ThinkingEffort.High));

        // thinking: {"type":"adaptive"} — no budget, and no "display" (the CLI
        // sends display:"omitted" only because -p mode hides thinking; this app
        // renders it, so the optional field stays off).
        var thinking = body["thinking"]!.AsObject();
        Assert.Equal("adaptive", thinking["type"]!.GetValue<string>());
        Assert.False(thinking.ContainsKey("budget_tokens"));
        Assert.False(thinking.ContainsKey("display"));

        Assert.Equal(64_000, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("high", body["output_config"]!["effort"]!.GetValue<string>());

        var edit = body["context_management"]!["edits"]!.AsArray().Single()!.AsObject();
        Assert.Equal("clear_thinking_20251015", edit["type"]!.GetValue<string>());
        Assert.Equal("all", edit["keep"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(ThinkingEffort.XHigh, "xhigh")]
    [InlineData(ThinkingEffort.Max, "max")]
    public void TheUpperRungs_TravelVerbatim(ThinkingEffort effort, string wire)
    {
        var body = AnthropicProvider.BuildRequestBody(Request("claude-opus-5", effort));
        Assert.Equal(wire, body["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void OlderModel_KeepsTheFixedBudget_AndNoEffortDial()
    {
        var body = AnthropicProvider.BuildRequestBody(Request("claude-sonnet-4-20250514", ThinkingEffort.High));

        var thinking = body["thinking"]!.AsObject();
        Assert.Equal("enabled", thinking["type"]!.GetValue<string>());
        // The CLI's budget is level-independent: 31999 = the 32000 cap − 1.
        Assert.Equal(31_999, thinking["budget_tokens"]!.GetValue<int>());
        Assert.Equal(32_000, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["output_config"]);
        // context_management still rides (the capture shows it for sonnet-4 too).
        Assert.NotNull(body["context_management"]);
    }

    [Fact]
    public void CloudBodies_SkipTheBetaGatedExtras()
    {
        var body = AnthropicProvider.BuildRequestBody(
            Request("claude-opus-5", ThinkingEffort.High), "bedrock", effortExtras: false);

        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(body["output_config"]);
        Assert.Null(body["context_management"]);
    }

    [Fact]
    public void EffortOff_LeavesTheLegacyBodyAlone()
    {
        var body = AnthropicProvider.BuildRequestBody(Request("claude-opus-5", ThinkingEffort.Off));

        Assert.Null(body["thinking"]);
        Assert.Null(body["output_config"]);
        Assert.Null(body["context_management"]);
        Assert.Equal(8192, body["max_tokens"]!.GetValue<int>());
    }

    /// <summary>
    /// The beta header per model class, each list byte-identical to a request
    /// captured from CLI 2.1.257 (and the desktop's 2.1.255) at a local listener.
    /// advisor-tool is gone from every class; the slot after effort is stable
    /// across both builds and every run, so it is pinned.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-5",
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07," +
        "effort-2025-11-24,fallback-credit-2026-06-01,afk-mode-2026-01-31")]
    [InlineData("claude-fable-5-1",
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07," +
        "effort-2025-11-24,fallback-credit-2026-06-01,afk-mode-2026-01-31")]
    [InlineData("claude-opus-4-8",
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07," +
        "effort-2025-11-24,afk-mode-2026-01-31")]
    [InlineData("claude-sonnet-5",
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07," +
        "effort-2025-11-24,afk-mode-2026-01-31")]
    [InlineData("claude-opus-4-6",
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,effort-2025-11-24,afk-mode-2026-01-31")]
    [InlineData("claude-opus-4-5",
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,effort-2025-11-24")]
    [InlineData("claude-sonnet-4-20250514",
        "claude-code-20250219,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05")]
    [InlineData("claude-haiku-4-5-20251001",
        "interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27," +
        "prompt-caching-scope-2026-01-05,claude-code-20250219")]
    [InlineData("claude-3-7-sonnet-20250219", "claude-code-20250219,prompt-caching-scope-2026-01-05")]
    [InlineData("claude-3-5-sonnet-20241022", "claude-code-20250219,prompt-caching-scope-2026-01-05")]
    public void BetaLists_AreTheCliListsVerbatim(string modelId, string expected)
        => Assert.Equal(expected, AnthropicEffort.Betas(modelId));

    /// <summary>The four axes of the wire, per measured model class.</summary>
    [Theory]
    [InlineData("claude-opus-5", ThinkingWire.Adaptive, true, true, 64_000)]
    [InlineData("claude-fable-5", ThinkingWire.Adaptive, true, true, 64_000)]
    [InlineData("claude-mythos-5-1", ThinkingWire.Adaptive, true, true, 64_000)]
    [InlineData("claude-opus-4-8", ThinkingWire.Adaptive, true, true, 64_000)]
    [InlineData("claude-sonnet-5", ThinkingWire.Adaptive, true, true, 64_000)]
    [InlineData("claude-opus-4-6", ThinkingWire.Adaptive, true, false, 64_000)]
    [InlineData("claude-opus-4-7", ThinkingWire.Adaptive, true, false, 64_000)]
    // Adaptive on the 32000 cap: the output cap is the catalog's own field and
    // not a consequence of the thinking class. Captured on CLI 2.1.257, with
    // opus-4-6 in the same run as a control reproducing its fixture's 64000.
    [InlineData("claude-sonnet-4-6", ThinkingWire.Adaptive, true, false, 32_000)]
    [InlineData("claude-opus-4-5", ThinkingWire.Enabled, true, false, 32_000)]
    [InlineData("claude-sonnet-4-5", ThinkingWire.Enabled, false, false, 32_000)]
    [InlineData("claude-haiku-4-5-20251001", ThinkingWire.Enabled, false, false, 32_000)]
    [InlineData("claude-3-7-sonnet-20250219", ThinkingWire.None, false, false, 32_000)]
    [InlineData("claude-3-5-sonnet-20241022", ThinkingWire.None, false, false, 8_192)]
    [InlineData("test-model", ThinkingWire.Enabled, false, false, 32_000)]
    public void Classify_MatchesTheCapturedWire(
        string modelId, ThinkingWire thinking, bool effortDial, bool systemTurn, int maxTokens)
    {
        var wire = AnthropicEffort.Classify(modelId);
        Assert.Equal(thinking, wire.Thinking);
        Assert.Equal(effortDial, wire.EffortDial);
        Assert.Equal(systemTurn, wire.HarnessSystemTurn);
        Assert.Equal(maxTokens, wire.MaxOutputTokens);
    }
}
