using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ProjectInstructionsBlockTests
{
    [Fact]
    public void Instructions_are_the_reference_block()
    {
        var block = ProjectInstructionsBlock.Build("Acme", description: null, instructions: "Ship on Fridays.");
        Assert.Equal(
            "<project_instructions>\n" +
            "The user has configured the following instructions for this project (\"Acme\"):\n\n" +
            "Ship on Fridays.\n\n" +
            "Follow these instructions when working in this project.\n" +
            "</project_instructions>",
            block);
    }

    [Fact]
    public void No_instructions_names_the_local_project()
    {
        Assert.Equal(
            "<project_instructions>\n" +
            "The user is working in their local project \"Acme\".\n" +
            "</project_instructions>",
            ProjectInstructionsBlock.Build("Acme", null, null));

        Assert.Equal(
            "<project_instructions>\n" +
            "The user is working in their local project \"Acme\". The widget works.\n" +
            "</project_instructions>",
            ProjectInstructionsBlock.Build("Acme", "The widget works.", ""));
    }

    [Fact]
    public void Links_ride_their_own_block()
    {
        var block = ProjectInstructionsBlock.Build(
            "Acme", null, "Ship.",
            [new ProjectLink("https://example.com/a", "Spec"), new ProjectLink("https://example.com/b")]);

        Assert.Contains(
            "<project_links>\n" +
            "The user has attached the following links to this project as reference context.\n" +
            "If any of these are relevant to the task, use the connector that matches each link's source to read them.\n\n" +
            "<link url=\"https://example.com/a\" title=\"Spec\" />\n" +
            "<link url=\"https://example.com/b\" />\n" +
            "</project_links>",
            block,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Links_past_the_budget_are_counted_not_sent()
    {
        var links = Enumerable.Range(0, 200)
            .Select(i => new ProjectLink($"https://example.com/{i:D4}"))
            .ToList();
        var block = ProjectInstructionsBlock.Build("Acme", null, null, links);

        // 200 links of ~36 chars each cannot fit in 2000, so the rest are counted.
        Assert.Contains("additional links omitted — the user can reference them directly if needed.", block, StringComparison.Ordinal);
        Assert.DoesNotContain("/0199", block, StringComparison.Ordinal);
    }

    [Fact]
    public void One_omitted_link_is_singular()
    {
        // Each tag renders to 51 characters and costs 52 of the budget, so 38 fit
        // (1976 used, and the 39th would take 2027) and exactly one is left over.
        var links = Enumerable.Range(0, 39)
            .Select(i => new ProjectLink($"https://example.com/padding/pad/{i:D4}"))
            .ToList();
        var block = ProjectInstructionsBlock.Build("Acme", null, null, links);
        Assert.Contains("(1 additional link omitted", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_inside_a_tag_is_escaped()
    {
        var block = ProjectInstructionsBlock.Build("A & B", null, "Use <b> & \"quotes\".");
        Assert.Contains("(\"A &amp; B\")", block, StringComparison.Ordinal);
        Assert.Contains("Use &lt;b&gt; &amp; &quot;quotes&quot;.", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_escapes_only_its_angle_brackets()
        => Assert.Equal("https://example.com/?a=1&b=2&lt;x&gt;", ProjectInstructionsBlock.EscapedUrl("https://example.com/?a=1&b=2<x>"));

    [Fact]
    public void A_name_is_normalized_before_it_is_escaped()
    {
        // A ligature and a full-width letter fold under NFKD; the narrow no-break
        // space and the double prime the reference steps over do not.
        Assert.Equal("ffi A", ProjectInstructionsBlock.Normalized("ﬃ Ａ"));
        Assert.Equal("5″  x", ProjectInstructionsBlock.Normalized("5″  x"));
    }

    [Fact]
    public void Tag_characters_are_dropped()
        => Assert.Equal("ab", ProjectInstructionsBlock.Normalized("a\U000E0041b"));
}
