using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ReadAloudTests
{
    [Fact]
    public void Fenced_code_is_not_read_out()
        => Assert.Equal(
            "Here it is. That is all.",
            ReadAloud.Speakable("Here it is.\n\n```csharp\nvar x = 1;\n```\n\nThat is all."));

    [Fact]
    public void Emphasis_and_headings_are_dropped()
        => Assert.Equal(
            "Results The build is green and fast.",
            ReadAloud.Speakable("## Results\n\nThe **build** is _green_ and *fast*."));

    [Fact]
    public void A_link_keeps_its_text_and_loses_its_url()
        => Assert.Equal(
            "See the docs for more.",
            ReadAloud.Speakable("See [the docs](https://example.com/a/b) for more."));

    [Fact]
    public void An_image_is_read_as_nothing()
        => Assert.Equal("Before after.", ReadAloud.Speakable("Before ![a chart](chart.png) after."));

    [Fact]
    public void Inline_code_is_read_without_its_backticks()
        => Assert.Equal("Run dotnet build first.", ReadAloud.Speakable("Run `dotnet build` first."));

    [Fact]
    public void List_bullets_and_table_rules_go()
    {
        var spoken = ReadAloud.Speakable("- one\n- two\n\n| Name | Value |\n| --- | --- |\n| a | 1 |");
        Assert.Equal("one two | Name | Value | | a | 1 |", spoken);
        // The rule row between the header and the body is not read out.
        Assert.DoesNotContain("---", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_to_say_reads_as_empty()
        => Assert.Equal("", ReadAloud.Speakable("```\ncode only\n```"));

    [Fact]
    public void The_label_is_the_reference_pair()
    {
        Assert.Equal("Read aloud", ReadAloud.Read);
        Assert.Equal("Pause", ReadAloud.Pause);
    }
}
