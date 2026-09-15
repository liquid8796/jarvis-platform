using JarvisCode.Core.Markdown;
using Xunit;

namespace JarvisCode.Core.Tests.Markdown;

/// <summary>
/// The HTML flavour a copied message carries beside its plain text. What matters
/// is that the structure survives - headings, lists, links, code, tables - and
/// that nothing in the source can escape into markup.
/// </summary>
public class MarkdownHtmlTests
{
    private static string Html(string markdown) => MarkdownHtml.RenderMarkdown(markdown);

    [Fact]
    public void HeadingsKeepTheirLevel()
    {
        Assert.Equal("<h1>Title</h1>", Html("# Title"));
        Assert.Equal("<h3>Deeper</h3>", Html("### Deeper"));
    }

    [Fact]
    public void AHeadingPastSixIsStillAValidTag()
    {
        // CommonMark stops at six, so this level cannot come from the parser -
        // but a caller can build one, and clamping keeps the output well-formed
        // rather than emitting an <h9> no HTML spec has.
        Assert.Equal(
            "<h6>Deep</h6>",
            MarkdownHtml.Render([new HeadingBlock(9, [new InlineRun("Deep")])]));
    }

    [Fact]
    public void InlineStylesNest()
    {
        Assert.Equal("<p><strong>bold</strong></p>", Html("**bold**"));
        Assert.Equal("<p><em>it</em></p>", Html("*it*"));
        Assert.Equal("<p><del>gone</del></p>", Html("~~gone~~"));
        Assert.Contains("<code>x</code>", Html("`x`"));
    }

    [Fact]
    public void ALinkKeepsItsTarget()
    {
        Assert.Equal("<p><a href=\"https://example.com\">text</a></p>", Html("[text](https://example.com)"));
    }

    [Fact]
    public void AFenceCarriesItsLanguage()
    {
        Assert.Equal(
            "<pre><code class=\"language-bash\">ls -la</code></pre>",
            Html("```bash\nls -la\n```"));
    }

    [Fact]
    public void ListsKeepTheirKindAndStart()
    {
        Assert.Equal("<ul><li>one</li><li>two</li></ul>", Html("- one\n- two"));
        Assert.StartsWith("<ol start=\"3\">", Html("3. three\n4. four"));
    }

    [Fact]
    public void ATaskItemKeepsItsBox()
    {
        var html = Html("- [x] done");
        Assert.Contains("<input type=\"checkbox\" disabled checked>", html);
    }

    [Fact]
    public void ATableKeepsItsHeaderAndAlignment()
    {
        var html = Html("| a | b |\n|:--|--:|\n| 1 | 2 |");
        Assert.Contains("<thead><tr><th>a</th><th align=\"right\">b</th></tr></thead>", html);
        Assert.Contains("<td>1</td>", html);
    }

    [Fact]
    public void MarkupInTheSourceIsEscapedRatherThanEmitted()
    {
        var html = Html("a <script>alert(1)</script> & b");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void AQuotedUrlCannotCloseTheAttribute()
    {
        var html = MarkdownHtml.Render(
        [
            new ParagraphBlock([new InlineRun("x", LinkUrl: "https://e.com/\" onload=\"boom")]),
        ]);
        Assert.DoesNotContain("onload=\"boom\"", html);
        Assert.Contains("&quot;", html);
    }

    [Fact]
    public void AHardBreakBecomesABreakTag()
    {
        Assert.Contains("<br>", Html("one  \ntwo"));
    }

    [Fact]
    public void AQuoteWrapsItsBlocks()
    {
        Assert.StartsWith("<blockquote>", Html("> quoted"));
        Assert.EndsWith("</blockquote>", Html("> quoted"));
    }

    [Fact]
    public void AnEmptyMessageRendersNothing()
    {
        Assert.Equal("", Html(""));
    }
}
