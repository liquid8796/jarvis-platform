using System.IO;
using System.Text;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Compares the prompt this port *renders* against the one the installed CLI
/// actually *sends*, recorded under <c>Captures/</c>.
/// </summary>
/// <remarks>
/// Every other check in this suite searches the reference's bytes, and the
/// build stores prompts as template literals: it holds
/// <c>- You must ${Tn} the file …</c> where the sent prompt reads "You must Read
/// the file …". A byte search therefore cannot tell a section this port never
/// carried from one it words slightly differently — and on 2026-09-01 nine
/// sections the reference sends turned out to be missing here while 1,265 tests
/// stayed green. Rendering both sides and diffing is the check that catches that
/// class, and it is this port's own: the reference has no need of it.
///
/// <b>The lean prompt is per model.</b> CLI 2.1.257 assembles it from a named
/// section list with per-model predicates, so the fixtures are one recording per
/// model class: opus-5 (# Delivering work, # Corrections, the reduced-delegation
/// line), fable-5-1 (its own identity paragraph, # Writing for the user, the
/// autonomy append, the bash-first bypass notice), fable-5 (the identity and the
/// autonomy append without the rest) and opus-4-8 (the lean skeleton and nothing
/// model-specific). Each must be matched by the prompt this port renders for the
/// same model id — one table, one predicate set, four recordings.
///
/// The reference side is a recorded capture rather than a live run, which is
/// the same choice <see cref="RequestWireParityTests"/> makes for the wire
/// fixtures. Spawning the CLI from a test host does not work here: with no
/// console attached it produces no request and no output at all, while the same
/// command from a shell captures fine. Recording keeps the check deterministic
/// and CI-able; the reference's own drift is what the byte-corpus checks are
/// for.
///
/// To refresh a fixture, run the reference against a loopback listener with a
/// cleaned environment (a desktop session exports around twenty
/// CLAUDE_*/ANTHROPIC_* variables, and any left set describes *that* session):
///
///   CLAUDE_CONFIG_DIR=&lt;temp&gt; ANTHROPIC_BASE_URL=http://127.0.0.1:&lt;port&gt;
///   ANTHROPIC_API_KEY=dummy claude -p "say ok" --model claude-opus-5
///   --permission-mode plan &lt; /dev/null
///
/// then write the request's longest <c>system</c> block to the fixture, with
/// the memory directory, cwd and OS version replaced by placeholders.
///
/// The comparison is one-directional — every line the reference sends must
/// appear in ours — because this port legitimately adds sections the CLI has no
/// equivalent for.
/// </remarks>
public class RenderedPromptParityTests
{
    private const string Version = "2.1.257";

    /// <summary>The static token-budget block a fresh session opens with.</summary>
    private const string TotalTokens = "<total_tokens>15000000 tokens left</total_tokens>";

    public static TheoryData<string, string, string> Fixtures => new()
    {
        { $"rendered-system-prompt-cli-{Version}.txt", "claude-opus-5", "Opus 5" },
        { $"rendered-system-prompt-cli-{Version}-fable51.txt", "claude-fable-5-1", "Fable 5.1" },
        { $"rendered-system-prompt-cli-{Version}-fable5.txt", "claude-fable-5", "Fable 5" },
        { $"rendered-system-prompt-cli-{Version}-opus48.txt", "claude-opus-4-8", "Opus 4.8" },
    };

    /// <summary>
    /// Lines whose content the machine, the session or the entrypoint decides.
    /// Each is a value this port fills in from its own environment, so a
    /// mismatch says nothing about parity.
    /// </summary>
    private static bool IsEnvironmentSpecific(string line)
    {
        string[] prefixes =
        [
            // The product-identity sentence. This is not an environment value: the
            // port deliberately sends its own, because the line is an attribution
            // claim rather than protocol. It is declared in
            // Deltas/reference-surface-deltas.tsv and explained on PromptIdentity —
            // the exclusion used to be the only trace of the difference anywhere.
            "You are a Claude agent",           // the sdk-cli entrypoint's intro
            "You have a persistent file-based memory at",
            "You have been invoked in the following environment",
            " - Primary working directory:",
            " - Is a git repository:",
            " - Additional working directories:",
            "  - ",                              // an additional directory
            " - Platform:",
            " - Shell:",
            " - OS Version:",
            " - You are powered by the model named",
            "x-anthropic-billing-header:",       // attribution, not protocol
        ];
        return prefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_line_the_reference_sends_is_in_the_prompt_this_port_renders(
        string fixtureName, string modelId, string displayName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Captures", fixtureName);
        Assert.True(File.Exists(path), $"the recorded prompt is missing: {path}");
        var captured = File.ReadAllText(path);
        var model = new ModelInfo("anthropic", modelId, displayName, 200_000);

        // A skill is passed because the reference's capture had skills too: the
        // CLI ships built-in ones, and both harnesses gate
        // # Session-specific guidance on the session having any.
        var ours = ReferencePromptBuilder.Build(
            Directory.GetCurrentDirectory(),
            model,
            skills: [new JarvisCode.Core.Customization.SkillDefinition(
                "deploy", "Ship the app", "user", "body", "deploy.md", DateTimeOffset.Now, "user")],
            memoryDirectory: @"D:\memory",
            additionalDirectories: null,
            gitStatus: null,
            scratchpadDirectory: Path.Combine(Path.GetTempPath(), "jarvis", "parity", "scratchpad"),
            hostSections: HostPromptSections.Render(new HostPromptSections.Capabilities(
                ClickableFileLinks: true,
                RunButtonOnShellFences: true,
                HasInAppBrowser: true,
                HasChromeBrowserSurface: true)),
            totalTokensBlock: TotalTokens);

        var missing = captured
            .Split('\n')
            .Select(static l => l.TrimEnd('\r'))
            .Where(static l => l.Trim().Length > 0)
            .Where(static l => !IsEnvironmentSpecific(l))
            .Where(l => !ours.Contains(l, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, new StringBuilder()
            .AppendLine($"the recorded CLI prompt for {modelId} has {missing.Count} line(s) this port does not render:")
            .AppendLine(string.Join("\n", missing.Select(static m => "  " + Truncate(m))))
            .AppendLine()
            .AppendLine("Either port the section, or — if it describes something this app does not have —")
            .AppendLine("declare it in Deltas/reference-surface-deltas.tsv and add its opening words to")
            .AppendLine("IsEnvironmentSpecific with the reason.")
            .ToString());
    }

    /// <summary>
    /// The other direction, for the sections the reference gates per model: a
    /// section a model's recording does <em>not</em> carry must not be rendered
    /// for it either, or the per-model predicates are wrong in the permissive
    /// direction — which the one-directional check above cannot see.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_section_the_reference_withholds_from_a_model_is_withheld_here_too(
        string fixtureName, string modelId, string displayName)
    {
        var captured = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Captures", fixtureName));
        var ours = ReferencePromptBuilder.Build(
            Path.GetTempPath(),
            new ModelInfo("anthropic", modelId, displayName, 200_000),
            skills: null,
            memoryDirectory: null,
            additionalDirectories: null,
            gitStatus: null,
            totalTokensBlock: TotalTokens);

        string[] gated =
        [
            "# Delivering work",
            "# Corrections",
            "# Writing for the user",
            "You are operating autonomously.",
            "This iteration of Claude is Claude Fable",
            "Do not use the Agent tool, workflows, or deep-research",
            "Assistant knowledge cutoff is",
        ];
        foreach (var section in gated)
        {
            Assert.Equal(
                captured.Contains(section, StringComparison.Ordinal),
                ours.Contains(section, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_recorded_prompts_still_cover_every_section_they_were_recorded_for()
    {
        // A fixture that lost its content would make the check above pass while
        // proving nothing, so what each must contain is asserted separately.
        string Read(string suffix) => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Captures", $"rendered-system-prompt-cli-{Version}{suffix}.txt"));

        string[] skeleton =
        [
            "# Harness", "# Session-specific guidance", "# Memory", "# Environment",
            "# Context management", TotalTokens,
        ];
        foreach (var suffix in new[] { "", "-fable51", "-fable5", "-opus48" })
        {
            var captured = Read(suffix);
            foreach (var section in skeleton)
            {
                Assert.Contains(section, captured, StringComparison.Ordinal);
            }
        }

        Assert.Contains("# Delivering work", Read(""), StringComparison.Ordinal);
        Assert.Contains("# Corrections", Read(""), StringComparison.Ordinal);
        Assert.Contains("# Writing for the user", Read("-fable51"), StringComparison.Ordinal);
        Assert.Contains("You are operating autonomously.", Read("-fable5"), StringComparison.Ordinal);
        Assert.DoesNotContain("# Delivering work", Read("-opus48"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An active output style swaps the intro line and rides between
    /// <c># Environment</c> and <c># Context management</c> — not after
    /// <c>gitStatus</c>, where this port used to append it.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-01 by capturing CLI 2.1.251 in a repository whose
    /// <c>.claude/settings.local.json</c> carried <c>outputStyle: "Concise"</c>.
    /// Two things moved together, which is why one test covers both: the prompt
    /// grew a <c># Output Style: Concise</c> block directly after the
    /// environment bullets, and its opening sentence changed away from "…helps
    /// users with software engineering tasks." Appending the style after
    /// <c>gitStatus</c> put it two sections and a trailer too late.
    /// </remarks>
    [Fact]
    public void An_output_style_swaps_the_intro_and_rides_after_the_environment()
    {
        var rendered = ReferencePromptBuilder.Build(
            Path.GetTempPath(),
            new ModelInfo("anthropic", "claude-opus-5", "Opus 5", 200_000),
            skills: null,
            memoryDirectory: null,
            additionalDirectories: null,
            gitStatus: "Current branch: master",
            scratchpadDirectory: Path.Combine(Path.GetTempPath(), "scratch"),
            hostSections: null,
            outputStyleBlock: OutputStyles.PromptBlock("Concise"));

        Assert.Contains(
            "helps users according to your \"Output Style\" below", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "You are an interactive agent that helps users with software engineering tasks.",
            rendered,
            StringComparison.Ordinal);

        var environment = rendered.IndexOf("# Environment", StringComparison.Ordinal);
        var style = rendered.IndexOf("# Output Style: Concise", StringComparison.Ordinal);
        var context = rendered.IndexOf("# Context management", StringComparison.Ordinal);
        var git = rendered.IndexOf("gitStatus:", StringComparison.Ordinal);

        Assert.True(style > environment, "the style block follows # Environment");
        Assert.True(style < context, "the style block precedes # Context management");
        Assert.True(git > context, "gitStatus stays the last block");
    }

    /// <summary>
    /// <c># Context management</c> is one paragraph, and the "act when you have
    /// enough information" paragraph is a section of its own.
    /// </summary>
    /// <remarks>
    /// A bare CLI 2.1.251 capture carries the first without the second while a
    /// desktop session carries both, so the two cannot share a constant: fused,
    /// this port could not render the CLI's shape at all. The rendered bytes are
    /// unchanged when both are on, which is what keeps this a structural fix
    /// rather than a behaviour change.
    /// </remarks>
    [Fact]
    public void The_act_when_informed_paragraph_is_a_section_of_its_own()
    {
        string Render(bool actWhenInformed) => ReferencePromptBuilder.Build(
            Path.GetTempPath(),
            new ModelInfo("anthropic", "claude-opus-5", "Opus 5", 200_000),
            skills: null,
            memoryDirectory: null,
            additionalDirectories: null,
            gitStatus: null,
            scratchpadDirectory: null,
            hostSections: null,
            outputStyleBlock: null,
            actWhenInformed: actWhenInformed);

        const string Paragraph = "When you have enough information to act, act.";
        Assert.Contains(Paragraph, Render(true), StringComparison.Ordinal);
        Assert.DoesNotContain(Paragraph, Render(false), StringComparison.Ordinal);

        // Dropping it must not take the section above it with it.
        Assert.Contains("# Context management", Render(false), StringComparison.Ordinal);
    }

    private static string Truncate(string line) =>
        line.Length <= 140 ? line : line[..140] + "…";
}
