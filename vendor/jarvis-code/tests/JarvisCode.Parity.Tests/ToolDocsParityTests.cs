using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Compares the tool docs and schemas this port advertises against the ones the
/// installed CLI sends, recorded under <c>Captures/Tools/tools-cli-2.1.257.json</c>.
/// </summary>
/// <remarks>
/// The reference's tool docs are template literals with holes (tool names,
/// the commit trailer's model), so the byte-corpus check cannot read them whole;
/// like the rendered-prompt check, this one compares what is <em>sent</em>. The
/// fixture holds the tools array of three -p requests — opus-5 (lean),
/// fable-5-1 (lean, its own Bash doc) and opus-4-5 (classic) — verbatim. The
/// tools that can be built without a running app are wrapped the way
/// <c>TurnContextFactory</c> wraps them and compared field by field: description
/// and input schema, both exact.
///
/// Agent is compared on its description only, since its schema deliberately
/// carries two arguments the reference's does not (see the surface manifest).
/// </remarks>
public class ToolDocsParityTests
{
    private static readonly JsonObject Fixture = (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Captures", "Tools", "tools-cli-2.1.257.json")))!;

    private static readonly (string Form, string ModelId, string Display)[] Forms =
    [
        ("lean", "claude-opus-5", "Opus 5"),
        ("fable51", "claude-fable-5-1", "Fable 5.1"),
        ("classic", "claude-opus-4-5", "Opus 4.5"),
    ];

    /// <summary>The Core tools that need nothing but themselves to exist.</summary>
    private static ITool[] Buildable() =>
    [
        new ReadFileTool(), new WriteFileTool(), new EditFileTool(), new GlobTool(), new GrepTool(),
        new ShellTool(ShellKind.Bash), new TaskOutputTool(), new TaskKillTool(), new NotebookEditTool(),
    ];

    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (form, _, _) in Forms)
            {
                foreach (var tool in Buildable())
                {
                    data.Add(form, tool.Name);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_tool_is_advertised_exactly_as_the_reference_advertises_it(string form, string toolName)
    {
        var (_, modelId, display) = Forms.Single(f => f.Form == form);
        var expected = (JsonObject)Fixture[form]![toolName]!;
        var model = new ModelInfo("anthropic", modelId, display, 200_000);
        var wrapped = ReferenceToolDocs.Apply(
            Buildable(), profile: PromptModelProfile.For(modelId), commitTrailer: CommitTrailers.Name(model))
            .Single(t => t.Name == toolName);

        Assert.Equal(expected["description"]!.GetValue<string>(), wrapped.Description);
        Assert.True(
            JsonNode.DeepEquals(expected["input_schema"], wrapped.InputSchema),
            $"{toolName} ({form}) schema differs:\n  ref : {expected["input_schema"]!.ToJsonString()}\n  ours: {wrapped.InputSchema.ToJsonString()}");
    }

    /// <summary>
    /// The generated table is the fixture: a regeneration that drifted, or a hand
    /// edit, shows up here rather than on the wire.
    /// </summary>
    [Fact]
    public void The_captured_table_matches_the_recording()
    {
        foreach (var (form, table) in new[] { ("classic", CapturedToolDocs.Classic), ("lean", CapturedToolDocs.Lean) })
        {
            var recorded = (JsonObject)Fixture[form]!;
            foreach (var (name, description) in table)
            {
                var expected = recorded[name]!["description"]!.GetValue<string>();
                var ours = name == "Bash"
                    ? description.Replace(CommitTrailers.Token, form == "lean" ? "Claude Opus 5" : "Claude Opus 4.5", StringComparison.Ordinal)
                    : description;
                Assert.Equal(expected, ours);
                Assert.True(JsonNode.DeepEquals(recorded[name]!["input_schema"], CapturedToolDocs.Schema(name)), $"{name} schema");
            }
        }

        // Every reference tool is in the table, and the lean forms are exactly the docs that differ.
        var classicNames = ((JsonObject)Fixture["classic"]!).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(classicNames, CapturedToolDocs.Classic.Keys.ToHashSet(StringComparer.Ordinal));
        var differing = ((JsonObject)Fixture["lean"]!)
            .Where(p => Fixture["classic"]![p.Key]!["description"]!.GetValue<string>() != p.Value!["description"]!.GetValue<string>())
            .Select(static p => p.Key)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(differing, CapturedToolDocs.Lean.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The Agent doc's three measured forms, and the commit trailer's model name.
    /// </summary>
    [Theory]
    [InlineData("lean", "claude-opus-5", "Opus 5")]
    [InlineData("fable51", "claude-fable-5-1", "Fable 5.1")]
    [InlineData("classic", "claude-opus-4-5", "Opus 4.5")]
    public void The_agent_doc_and_the_commit_trailer_follow_the_model(string form, string modelId, string display)
    {
        var expected = Fixture[form]!["Agent"]!["description"]!.GetValue<string>();
        Assert.Equal(expected, ReferenceToolDocs.RunAgentDoc(PromptModelProfile.For(modelId)));

        var bash = Fixture[form]!["Bash"]!["description"]!.GetValue<string>();
        var model = new ModelInfo("anthropic", modelId, display, 200_000);
        Assert.Equal(bash, ReferenceToolDocs.BashDoc(PromptModelProfile.For(modelId), CommitTrailers.Name(model)));
    }

    [Theory]
    [InlineData("claude-opus-5", "Claude Opus 5")]
    [InlineData("claude-fable-5-1", "Claude Fable 5.1")]
    [InlineData("claude-opus-4-5", "Claude Opus 4.5")]
    [InlineData("claude-sonnet-4-5-20250929", "Claude Sonnet 4.5")]
    [InlineData("claude-haiku-4-5-20251001", "Claude Haiku 4.5")]
    [InlineData("us.anthropic.claude-opus-4-6-v1:0", "Claude Opus 4.6")]
    [InlineData("llmapi/claude-sonnet-5", "Claude Sonnet 5")]
    public void The_commit_trailer_names_the_model_the_way_the_reference_does(string modelId, string expected)
    {
        Assert.Equal(expected, CommitTrailers.Name(new ModelInfo("anthropic", modelId, "x", 200_000)));
    }

    [Fact]
    public void A_model_outside_the_claude_families_keeps_its_own_name()
    {
        Assert.Equal("gemma4 e4b", CommitTrailers.Name(new ModelInfo("ollama", "gemma4:e4b", "gemma4 e4b", 4096)));
    }
}
