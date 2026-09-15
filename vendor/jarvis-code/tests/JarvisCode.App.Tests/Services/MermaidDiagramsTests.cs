using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class MermaidDiagramsTests
{
    [Theory]
    [InlineData("mermaid", true)]
    [InlineData("Mermaid", true)]
    [InlineData(" mermaid ", true)]
    [InlineData("mermaidjs", false)]
    [InlineData("bash", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_mermaid_fence_becomes_a_diagram(string? language, bool expected) =>
        Assert.Equal(expected, MermaidDiagrams.IsDiagramFence(language));

    [Fact]
    public void Carriage_returns_are_normalized_away()
    {
        Assert.Equal("flowchart LR\n  A --> B", MermaidDiagrams.Preprocess("flowchart LR\r\n  A --> B"));
        Assert.Equal("flowchart LR\n  A --> B", MermaidDiagrams.Preprocess("flowchart LR\r  A --> B"));
    }

    [Fact]
    public void Shape_data_is_stripped_out_of_the_body()
    {
        var result = MermaidDiagrams.Preprocess("flowchart LR\n  A@{ shape: rect }\n  A --> B");
        Assert.DoesNotContain("shape:", result, StringComparison.Ordinal);
        Assert.Contains("A --> B", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Shape_data_hidden_behind_the_semicolon_obfuscation_is_stripped_too()
    {
        var result = MermaidDiagrams.Preprocess("flowchart LR\n  A@;;{ shape: rect }\n");
        Assert.DoesNotContain("shape:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directive_survives_the_strip_passes_at_the_top()
    {
        // The reference lifts a %%{…}%% directive out before the strip passes and
        // puts it back above the body, so a diagram keeps its own configuration.
        var result = MermaidDiagrams.Preprocess(
            "flowchart LR\n%%{init: {\"theme\":\"base\"}}%%\n  A@{ shape: rect } --> B");
        Assert.StartsWith("%%{init: {\"theme\":\"base\"}}%%\n", result, StringComparison.Ordinal);
        Assert.Contains("flowchart LR", result, StringComparison.Ordinal);
        Assert.DoesNotContain("shape:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unclosed_directive_is_closed()
    {
        var result = MermaidDiagrams.Preprocess("%%{init: {\"theme\":\"base\"}\nflowchart LR\n  A --> B");
        Assert.EndsWith("}%%\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Front_matter_stays_at_the_top()
    {
        var result = MermaidDiagrams.Preprocess("---\ntitle: One\n---\nflowchart LR\n  A --> B");
        Assert.StartsWith("---\ntitle: One\n---\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_diagrams_oversized_space_is_clamped()
    {
        var result = MermaidDiagrams.Preprocess("block-beta\n  columns 3\n  space:99999\n");
        Assert.Contains("space:100", result, StringComparison.Ordinal);
        Assert.DoesNotContain("99999", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_space_inside_a_word_is_left_alone()
    {
        // The reference skips a match whose preceding character is a letter or an
        // underscore, so an identifier that happens to end in "space:" is not one.
        var result = MermaidDiagrams.Preprocess("block-beta\n  myspace:99999\n");
        Assert.Contains("myspace:99999", result, StringComparison.Ordinal);
    }

    [Fact]
    public void A_space_outside_a_block_diagram_is_left_alone()
    {
        var result = MermaidDiagrams.Preprocess("flowchart LR\n  A[\"space:99999\"] --> B\n");
        Assert.Contains("space:99999", result, StringComparison.Ordinal);
    }

    [Fact]
    public void The_light_and_dark_theme_variables_are_the_references_own()
    {
        Assert.Equal("#E5E5E5", Value(MermaidDiagrams.ThemeVariables(dark: true), "primaryTextColor"));
        Assert.Equal("transparent", Value(MermaidDiagrams.ThemeVariables(dark: true), "primaryColor"));
        Assert.Equal("#2D2D2D", Value(MermaidDiagrams.ThemeVariables(dark: true), "noteBkgColor"));

        Assert.Equal("#191919", Value(MermaidDiagrams.ThemeVariables(dark: false), "primaryTextColor"));
        Assert.Equal("#F0F0EB", Value(MermaidDiagrams.ThemeVariables(dark: false), "primaryColor"));
        Assert.Equal("#F5E6D8", Value(MermaidDiagrams.ThemeVariables(dark: false), "secondaryColor"));

        // The one colour both palettes share is the accent.
        Assert.Equal("#CC785C", Value(MermaidDiagrams.ThemeVariables(dark: true), "tertiaryColor"));
        Assert.Equal("#CC785C", Value(MermaidDiagrams.ThemeVariables(dark: false), "tertiaryColor"));
    }

    [Fact]
    public void The_config_carries_the_references_limits_and_the_resolved_font()
    {
        var json = MermaidDiagrams.ConfigJson(dark: false, "Segoe UI Variable Text, Segoe UI, Arial");
        Assert.Contains("\"startOnLoad\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"htmlLabels\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"maxTextSize\":20000", json, StringComparison.Ordinal);
        Assert.Contains("\"maxEdges\":400", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\":\"base\"", json, StringComparison.Ordinal);
        Assert.Contains("\"securityLevel\":\"sandbox\"", json, StringComparison.Ordinal);
        // The reference passes fontFamily "inherit" and resolves it against the
        // body before initializing; this port hands the resolved family straight in.
        Assert.Contains("\"fontFamily\":\"Segoe UI Variable Text, Segoe UI, Arial\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("inherit", json, StringComparison.Ordinal);
        // The secure allowlist, in the reference's order.
        Assert.Contains(
            "\"secure\":[\"secure\",\"securityLevel\",\"startOnLoad\",\"maxTextSize\",\"maxEdges\"," +
            "\"suppressErrorRendering\",\"htmlLabels\",\"themeCSS\",\"fontFamily\",\"altFontFamily\"," +
            "\"themeVariables\",\"theme\"]",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_the_strip_cannot_settle_is_refused_rather_than_rendered()
    {
        // Each pass strips the innermost @{} and leaves an @ next to a { that
        // becomes the next one, so N+1 of each needs N+1 passes; 33 outlasts the
        // reference's 32 and is refused instead of being handed to mermaid.
        var nested = "flowchart LR\n"
            + string.Concat(Enumerable.Repeat("@", 33))
            + string.Concat(Enumerable.Repeat("{}", 33));
        var refused = Assert.Throws<MermaidDiagrams.RefusedException>(
            () => MermaidDiagrams.Preprocess(nested));
        Assert.True(
            refused.Message == MermaidDiagrams.StripDidNotConverge
            || refused.Message == MermaidDiagrams.StripPostConditionViolated,
            "a refusal names one of the reference's two reasons, not something new: " + refused.Message);
    }

    private static string? Value(IReadOnlyList<KeyValuePair<string, string>> variables, string key) =>
        variables.FirstOrDefault(v => v.Key == key).Value;
}
