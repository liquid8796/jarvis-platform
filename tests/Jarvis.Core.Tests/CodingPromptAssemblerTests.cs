using Jarvis.Agent.Core.Prompting;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class CodingPromptAssemblerTests
{
    [Fact]
    public void Prompt_layers_are_stable_include_tool_capabilities_and_verification_policy()
    {
        var assembler = new CodingPromptAssembler();
        var tools = new[]
        {
            new ToolDescriptor("z.write", "z_write", "filesystem", "Write", WireJson.Element(new { type = "object" }), false, true),
            new ToolDescriptor("a.read", "a_read", "filesystem", "Read", WireJson.Element(new { type = "object" }), true)
        };

        var layers = assembler.Assemble(new CodingPromptRequest("Fix the dashboard", "C:/repo", tools,
            SkillMetadata: ["frontend-testing: rendered QA"],
            SelectedSkillInstructions: ["Always inspect the rendered page."],
            OutcomeSummaries: ["build: passed"],
            VerificationDebt: ["mobile screenshot missing"]));

        Assert.Equal(new[] { "coding-base", "frontend-browser-policy", "workspace", "tools", "skills", "outcomes", "verification-debt" },
            layers.Select(layer => layer.Name));
        Assert.Contains("build is not rendered proof", layers[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(layers[3].Content.IndexOf("a.read", StringComparison.Ordinal) < layers[3].Content.IndexOf("z.write", StringComparison.Ordinal));
        Assert.Contains("readOnly=true", layers[3].Content, StringComparison.Ordinal);
        Assert.Contains("sensitive=true", layers[3].Content, StringComparison.Ordinal);
        Assert.Contains("mobile screenshot missing", layers[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_assembly_is_bounded_without_reordering_required_layers()
    {
        var assembler = new CodingPromptAssembler(maxPromptChars: 4096);
        var huge = new string('x', 20_000);
        var layers = assembler.Assemble(new CodingPromptRequest(huge, "C:/repo", [],
            SelectedSkillInstructions: [huge], OutcomeSummaries: [huge], VerificationDebt: [huge]));

        Assert.True(layers.Sum(layer => layer.Content.Length) <= 4096);
        Assert.Equal("coding-base", layers[0].Name);
        Assert.Equal("frontend-browser-policy", layers[1].Name);
        Assert.Contains(layers, layer => layer.Name == "workspace");
    }
}
