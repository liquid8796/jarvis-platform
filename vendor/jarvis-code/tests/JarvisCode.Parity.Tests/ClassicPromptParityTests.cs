using System;
using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Compares the classic system prompt this port renders against the one the
/// installed CLI sends to a model without a lean prompt bundle.
/// </summary>
/// <remarks>
/// The reference has two system prompts, not one, and picks between them per
/// model. Captured 2026-09-02 from CLI 2.1.257 with nothing changed but
/// <c>--model</c>: claude-opus-5 records the lean <c># Harness</c> prompt that
/// <see cref="RenderedPromptParityTests"/> covers, and claude-opus-4-5 records
/// this one, built from <c># System</c> onward. claude-sonnet-5 records the same
/// document minus one line — it is a model that takes the harness's
/// <c>system</c> turn and so receives no task-board tools, and the
/// <c># Doing tasks</c> bullet that names TaskCreate goes with them — which is
/// the second fixture here.
///
/// The fixture is compared up to <c># Environment</c>. Everything below it is
/// the environment block, whose values the machine decides and whose
/// per-model cutoff line the lean tests cover, followed by
/// <c># Context management</c> and the token-budget block, which the classic
/// form shares with the lean one and which are asserted separately.
///
/// Two lines are deliberately adapted, and the test proves they are the *only*
/// adaptations: the <c>/help</c> line and the feedback address name a product,
/// and sending this app's users to Anthropic's issue tracker would file this
/// build's bugs against another project.
/// </remarks>
public class ClassicPromptParityTests
{
    private const string Version = "2.1.257";

    private static readonly ModelInfo Opus45 = new("anthropic", "claude-opus-4-5", "Opus 4.5", 200_000);
    private static readonly ModelInfo Sonnet5 = new("anthropic", "claude-sonnet-5", "Sonnet 5", 200_000);

    private const string MemoryDirectory = "<memory-dir>";

    private const string TotalTokens = "<total_tokens>15000000 tokens left</total_tokens>";

    /// <summary>The two lines this port renames, and what the reference says.</summary>
    private static readonly (string Ours, string Reference)[] Rebrands =
    [
        ("/help: Get help with using Jarvis Code", "/help: Get help with using Claude Code"),
        ("https://github.com/liquid8796/jarvis-code/issues", "https://github.com/anthropics/claude-code/issues"),
    ];

    public static TheoryData<string, string, bool> Fixtures => new()
    {
        { $"rendered-system-prompt-cli-{Version}-classic.txt", "claude-opus-4-5", true },
        { $"rendered-system-prompt-cli-{Version}-classic-sonnet5.txt", "claude-sonnet-5", false },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_classic_prompt_matches_the_capture_up_to_the_environment(
        string fixtureName, string modelId, bool hasTaskBoard)
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Captures", fixtureName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var expectedHead = expected[..expected.IndexOf("# Environment", StringComparison.Ordinal)];

        var rendered = ClassicPromptBuilder.Build(
            Path.GetTempPath(),
            new ModelInfo("anthropic", modelId, modelId, 200_000),
            MemoryDirectory,
            hasTaskBoard: hasTaskBoard,
            totalTokensBlock: TotalTokens);

        var environment = rendered.IndexOf("# Environment", StringComparison.Ordinal);
        Assert.True(environment > 0, "the classic prompt carries an environment block");
        var head = rendered[..environment];

        foreach (var (ours, reference) in Rebrands)
        {
            Assert.Contains(ours, head, StringComparison.Ordinal);
            head = head.Replace(ours, reference, StringComparison.Ordinal);
        }

        Assert.Equal(expectedHead, head);
    }

    /// <summary>
    /// Below the environment block the classic form carries the same
    /// <c># Context management</c> paragraph, the "act" paragraph and the
    /// token-budget block as the lean one, in that order, and ends there.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_classic_prompt_ends_the_way_the_capture_does(
        string fixtureName, string modelId, bool hasTaskBoard)
    {
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Captures", fixtureName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var expectedTail = expected[expected.IndexOf("# Context management", StringComparison.Ordinal)..];

        var rendered = ClassicPromptBuilder.Build(
            Path.GetTempPath(),
            new ModelInfo("anthropic", modelId, modelId, 200_000),
            MemoryDirectory,
            hasTaskBoard: hasTaskBoard,
            totalTokensBlock: TotalTokens);
        var tail = rendered[rendered.IndexOf("# Context management", StringComparison.Ordinal)..];

        Assert.Equal(expectedTail.TrimEnd(), tail.TrimEnd());
    }

    /// <summary>
    /// The one line the two classic recordings disagree on.
    /// </summary>
    [Fact]
    public void The_task_board_line_follows_the_task_board_tools()
    {
        const string Line = "TaskCreate";
        Assert.Contains(Line, ClassicPromptBuilder.Build(Path.GetTempPath(), Opus45, null, hasTaskBoard: true), StringComparison.Ordinal);
        Assert.DoesNotContain(Line, ClassicPromptBuilder.Build(Path.GetTempPath(), Sonnet5, null, hasTaskBoard: false), StringComparison.Ordinal);
    }

    /// <summary>
    /// The section list, in order — a cheap guard that a future edit does not
    /// quietly drop one while leaving the byte comparison passing on a
    /// re-recorded fixture.
    /// </summary>
    [Fact]
    public void The_classic_prompt_carries_its_sections_in_order()
    {
        var rendered = ClassicPromptBuilder.Build(Path.GetTempPath(), Opus45, MemoryDirectory);
        string[] sections =
        [
            "# System", "# Doing tasks", "# Executing actions with care", "# Using your tools",
            "# Tone and style", "# Text output (does not apply to tool calls)",
            "# Session-specific guidance", "# auto memory", "# Environment", "# Context management",
        ];

        var at = 0;
        foreach (var section in sections)
        {
            var found = rendered.IndexOf(section, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"{section} is missing or out of order");
            at = found + section.Length;
        }
    }

    /// <summary>
    /// The gate itself: which model gets which document.
    /// </summary>
    /// <remarks>
    /// Every Anthropic row is a capture: CLI 2.1.257 driven at a local listener
    /// once per model with a cleaned environment. opus-5, opus-4-8 and the whole
    /// fable/mythos family were sent the lean form; sonnet-5, every haiku and
    /// opus 4.5 through 4.7 the classic one — sonnet-5 despite being a Claude 5
    /// model, which is why the gate is not "the 5 family". It is not the
    /// catalog's <c>lean_prompt</c> capability either: <c>claude-mythos-5</c>
    /// declares an empty capability array and was still sent the lean prompt
    /// (<see cref="ModelCatalogParityTests"/> pins that divergence). The
    /// non-Anthropic ids assert the reference's default for a model it does not
    /// recognise, which is what every model on another provider is.
    /// </remarks>
    [Theory]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-fable-5", true)]
    [InlineData("claude-fable-5-1", true)]
    [InlineData("claude-mythos-5", true)]
    [InlineData("claude-mythos-5-1", true)]
    [InlineData("llmapi/claude-opus-5", true)]
    [InlineData("us.anthropic.claude-opus-5-v1:0", true)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("claude-opus-4-6", false)]
    [InlineData("claude-opus-4-5", false)]
    [InlineData("claude-sonnet-4-5", false)]
    [InlineData("claude-haiku-4-5-20251001", false)]
    [InlineData("deepseek-chat", false)]
    [InlineData("qwen2.5-coder:32b", false)]
    public void The_prompt_generation_follows_the_model(string modelId, bool lean)
    {
        Assert.Equal(lean, ReferencePromptBuilder.UsesLeanPrompt(modelId));
    }

    /// <summary>
    /// Without a memory directory the section is dropped rather than rendered
    /// empty, the way the lean form drops its own.
    /// </summary>
    [Fact]
    public void No_memory_directory_drops_the_memory_section()
    {
        var rendered = ClassicPromptBuilder.Build(Path.GetTempPath(), Opus45, memoryDirectory: null);
        Assert.DoesNotContain("# auto memory", rendered, StringComparison.Ordinal);
        Assert.Contains("# Environment", rendered, StringComparison.Ordinal);
    }
}
