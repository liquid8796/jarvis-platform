using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Models;

/// <summary>
/// A provider citation and its position in the text block (UTF-16 character offset).
/// RawJson includes opaque encrypted_index and source locations unchanged for replay.
/// A negative offset means the citation arrived before text and belongs at the block end.
/// </summary>
public sealed record TextCitation(string RawJson, int TextOffset)
{
    /// <summary>The provider that owns opaque replay tokens; null for older records.</summary>
    public string? ProviderId { get; init; }
}

/// <summary>Display-only source links. The stored/provider text is never rewritten.</summary>
public static class CitationMarkdown
{
    public static string Format(TextBlock block)
    {
        if (block.Citations is not { Count: > 0 }) return block.Text;
        var result = new StringBuilder(block.Text);
        // Descending insertion keeps source offsets stable. Reverse equal offsets
        // so multiple citations at one claim retain their provider order.
        foreach (var pair in block.Citations.Select((citation, i) => (citation, i))
                     .OrderByDescending(p => Offset(p.citation, block.Text.Length)).ThenByDescending(p => p.i))
            result.Insert(Offset(pair.citation, block.Text.Length), Link(pair.citation));
        return result.ToString();
    }

    private static int Offset(TextCitation citation, int length) =>
        citation.TextOffset < 0 ? length : Math.Clamp(citation.TextOffset, 0, length);

    public static string Link(TextCitation citation)
    {
        JsonObject? value;
        try { value = JsonNode.Parse(citation.RawJson) as JsonObject; }
        catch (System.Text.Json.JsonException) { return ""; }
        if (value is null) return "";
        var title = Text(value, "title") ?? Text(value, "document_title");
        if (Uri.TryCreate(Text(value, "url"), UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo))
        {
            var label = Escape(string.IsNullOrWhiteSpace(title) ? uri.Host : title);
            // Angle-bracket destinations handle parentheses and spaces without
            // letting a provider title or URL introduce extra markdown nodes.
            var destination = uri.AbsoluteUri.Replace("<", "%3C").Replace(">", "%3E")
                .Replace("(", "%28").Replace(")", "%29");
            return $" [{label}](<{destination}>)";
        }
        // Document citations have page/character locations, but no URL. Preserve
        // a readable locator; never invent a link to an unrelated local file.
        if (Text(value, "type") is "page_location" && Number(value, "start_page_number") is { } page)
        {
            var end = Number(value, "end_page_number"); // provider end is exclusive
            return $" [{Escape(title ?? "Document")}, p. {page}{(end > page + 1 ? "-" + (end - 1) : "")}]";
        }
        if (!string.IsNullOrWhiteSpace(title)) return $" [{Escape(title)}]";
        return "";
    }

    private static string? Text(JsonObject value, string key) =>
        value[key] is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;
    private static int? Number(JsonObject value, string key) =>
        value[key] is JsonValue item && item.TryGetValue<int>(out var number) ? number : null;
    private static string Escape(string value) => value.ReplaceLineEndings(" ")
        .Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]")
        .Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("~", "\\~")
        .Replace("<", "\\<").Replace(">", "\\>");
}
