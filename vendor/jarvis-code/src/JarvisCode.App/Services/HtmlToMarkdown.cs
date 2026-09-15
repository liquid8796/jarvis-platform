using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// Turns a fetched HTML page into markdown, the way the reference CLI's WebFetch
/// hands turndown's output to the model. This is a focused converter rather than
/// a port of that library: the elements a documentation or article page is made
/// of — headings, paragraphs, lists, links, emphasis, code, quotes, tables — and
/// nothing else. Anything unrecognised loses its tags and keeps its text.
/// </summary>
public static partial class HtmlToMarkdown
{
    /// <summary>The reference truncates the input to turndown at a megabyte.</summary>
    public const int MaxInputChars = 1_048_576;

    public const string TruncationNotice = "\n\n[Content truncated due to length...]";

    public static string Convert(string html)
    {
        var truncated = html.Length > MaxInputChars;
        var text = truncated ? html[..MaxInputChars] : html;

        text = Comments().Replace(text, "");
        text = NonContent().Replace(text, "\n");
        text = Breaks().Replace(text, "\n");
        text = HorizontalRules().Replace(text, "\n\n---\n\n");
        text = Headings().Replace(text, m =>
            "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + Inline(m.Groups[2].Value) + "\n\n");
        text = Preformatted().Replace(text, m => "\n\n```\n" + Decode(StripTags(m.Groups[1].Value)).Trim() + "\n```\n\n");
        text = ListItems().Replace(text, m => "\n- " + Inline(m.Groups[1].Value));
        text = Quotes().Replace(text, m => "\n\n> " + Inline(m.Groups[1].Value).Replace("\n", "\n> ") + "\n\n");
        text = TableCells().Replace(text, m => "| " + Inline(m.Groups[2].Value) + " ");
        text = TableRows().Replace(text, m => "\n" + m.Groups[1].Value.TrimEnd() + " |");
        text = Blocks().Replace(text, "\n\n");
        text = Inline(text);

        var lines = text.Split('\n').Select(line => Spaces().Replace(line, " ").TrimEnd());
        var body = BlankRuns().Replace(string.Join('\n', lines), "\n\n").Trim();
        return truncated ? body + TruncationNotice : body;
    }

    /// <summary>Inline markup — links, emphasis, code, images — then entities.</summary>
    private static string Inline(string html)
    {
        var text = Links().Replace(html, m =>
        {
            var label = Decode(StripTags(m.Groups[2].Value)).Trim();
            var href = Decode(m.Groups[1].Value).Trim();
            return label.Length == 0 ? "" : href.Length == 0 ? label : $"[{label}]({href})";
        });
        text = Images().Replace(text, m =>
        {
            var alt = Decode(m.Groups[1].Value).Trim();
            return alt.Length == 0 ? "" : $"![{alt}]";
        });
        text = Strong().Replace(text, m => Wrap(m.Groups[2].Value, "**"));
        text = Emphasis().Replace(text, m => Wrap(m.Groups[2].Value, "*"));
        text = InlineCode().Replace(text, m => Wrap(m.Groups[1].Value, "`"));
        return Decode(StripTags(text));
    }

    /// <summary>Empty markup carries no emphasis; a marker on nothing is noise.</summary>
    private static string Wrap(string inner, string marker)
    {
        var text = Decode(StripTags(inner)).Trim();
        return text.Length == 0 ? "" : marker + text + marker;
    }

    private static string StripTags(string html) => AnyTag().Replace(html, "");

    private static string Decode(string text) => WebUtility.HtmlDecode(text);

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<(script|style|noscript|svg|head|template)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContent();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<hr\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HorizontalRules();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Headings();

    [GeneratedRegex(@"<pre\b[^>]*>(.*?)</pre\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Preformatted();

    [GeneratedRegex(@"<li\b[^>]*>(.*?)</li\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"<blockquote\b[^>]*>(.*?)</blockquote\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Quotes();

    [GeneratedRegex(@"<(td|th)\b[^>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableCells();

    [GeneratedRegex(@"<tr\b[^>]*>(.*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRows();

    [GeneratedRegex(@"</?(p|div|section|article|ul|ol|table|tbody|thead|header|footer|nav|main|aside|dl|dd|dt)\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*[""']([^""']*)[""'][^>]*>(.*?)</a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Links();

    [GeneratedRegex(@"<img\b[^>]*?alt\s*=\s*[""']([^""']*)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Images();

    [GeneratedRegex(@"<(strong|b)\b[^>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Strong();

    [GeneratedRegex(@"<(em|i)\b[^>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"<code\b[^>]*>(.*?)</code\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineCode();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();
}
