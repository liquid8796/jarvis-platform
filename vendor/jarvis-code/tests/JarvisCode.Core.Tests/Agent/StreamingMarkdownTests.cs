using JarvisCode.Core.Markdown;

namespace JarvisCode.Core.Tests.Agent;

public sealed class StreamingMarkdownTests
{
    [Theory]
    [InlineData("**still typing", "still typing", InlineStyle.Bold)]
    [InlineData("*still typing", "still typing", InlineStyle.Italic)]
    [InlineData("`still typing", "still typing", InlineStyle.Code)]
    [InlineData("~~still typing", "still typing", InlineStyle.Strikethrough)]
    public void StreamingDisplaysOpenConstructsWhileNormalParsingRemainsLiteral(string text, string displayed, InlineStyle style)
    {
        var streaming = MarkdownParser.ParseInlines(text, MarkdownOptions.Default with { Streaming = true });
        Assert.Equal(displayed, string.Concat(streaming.Select(run => run.Text)));
        Assert.All(streaming, run => { Assert.True(run.Incomplete); Assert.True(run.Style.HasFlag(style)); });
        Assert.Equal(text, string.Concat(MarkdownParser.ParseInlines(text).Select(run => run.Text)));
    }

    [Fact]
    public void AnIncompleteLinkShowsItsLabelWithoutActivatingAnUnfinishedDestination()
    {
        var runs = MarkdownParser.ParseInlines("Read [the guide](https://exa", MarkdownOptions.Default with { Streaming = true });
        Assert.Equal("Read the guide", string.Concat(runs.Select(run => run.Text)));
        Assert.Null(runs.Last().LinkUrl);
        Assert.True(runs.Last().Incomplete);
        Assert.Equal("snake_case", string.Concat(MarkdownParser.ParseInlines("snake_case", MarkdownOptions.Default with { Streaming = true }).Select(run => run.Text)));
    }
}
