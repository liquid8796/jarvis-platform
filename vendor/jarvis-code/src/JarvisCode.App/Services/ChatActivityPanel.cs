using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>One result row under a search.</summary>
/// <param name="Title">What the row reads, falling back to the url.</param>
/// <param name="Url">Where it goes.</param>
/// <param name="Domain">The site, shown under the title.</param>
public sealed record ChatSearchResult(string Title, string Url, string? Domain);

/// <summary>One search the conversation ran.</summary>
/// <param name="Query">What was searched for.</param>
/// <param name="Results">What came back.</param>
/// <param name="IsError">Whether the search itself failed.</param>
public sealed record ChatSearch(string Query, IReadOnlyList<ChatSearchResult> Results, bool IsError = false);

/// <summary>
/// The Chat surface's activity drawer, ported from the reference desktop's own
/// detail panels (<c>ca2ef848d-D8BWZk64.js</c>: its <c>Kg</c> web-search panel,
/// with a count line over the searches and a centred empty state, and each search
/// (<c>Gg</c>) titled by its query with a "{n} results" secondary line).
/// </summary>
public static class ChatActivityPanel
{
    public const string WebSearchTitle = "Web search";          // jjIbNfuWTd
    public const string NoWebSearchesYet = "No web searches yet"; // xGOUVSbQU8
    public const string ClosePanel = "Close panel";              // 9Up6TiAtz+

    /// <summary>A search with no query of its own is titled by the panel.</summary>
    public const string UntitledSearch = WebSearchTitle;

    /// <summary>The count line over the searches.</summary>
    public static string SearchCount(int count) =>
        // U61wp+86Xp
        count == 1 ? "1 search" : $"{count} searches";

    /// <summary>The secondary line on one search.</summary>
    public static string ResultCount(int count) =>
        // J2v/1b5Cbz
        count == 1 ? "1 result" : $"{count} results";

    /// <summary>The chip that opens the tool-call panel.</summary>
    public static string ToolCallCount(int count) =>
        // 6b4EFZCPvR
        count == 1 ? "1 tool call" : $"{count} tool calls";

    /// <summary>
    /// The searches a conversation ran, read off the calls this engine can see: our
    /// own web_search tool and the reference's WebSearch, both of which pass the
    /// query in their arguments and links in their result. A search Anthropic runs
    /// server-side reports only its name to this engine, so it cannot be listed.
    /// </summary>
    public static ChatSearch Describe(string argumentsJson, string result, bool isError)
    {
        var query = "";
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("query", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                query = value.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // A call whose arguments never finished streaming still lists, untitled.
        }

        return new ChatSearch(
            query.Length > 0 ? query : UntitledSearch,
            isError ? [] : ParseResults(result),
            isError);
    }

    /// <summary>
    /// Pulls result rows out of a search tool's text. Both tools write one link per
    /// line; the vendor one wraps them in a JSON array on a "Links:" line.
    /// </summary>
    public static IReadOnlyList<ChatSearchResult> ParseResults(string result)
    {
        var rows = new List<ChatSearchResult>();
        foreach (var line in result.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("Links:", StringComparison.Ordinal))
            {
                rows.AddRange(ParseLinksJson(text["Links:".Length..].Trim()));
                continue;
            }

            var start = text.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                continue;
            }

            var url = text[start..].Split([' ', ')', ']', '"', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                continue;
            }

            var title = text[..start].Trim(' ', '-', '*', '.', ':', '[', ']', '(');
            rows.Add(new ChatSearchResult(title.Length > 0 ? title : url, url, parsed.Host));
        }

        return rows;
    }

    private static IEnumerable<ChatSearchResult> ParseLinksJson(string json)
    {
        List<ChatSearchResult> rows = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return rows;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("url", out var url)
                    || url.GetString() is not { Length: > 0 } address)
                {
                    continue;
                }

                var title = element.TryGetProperty("title", out var t) ? t.GetString() : null;
                rows.Add(new ChatSearchResult(
                    string.IsNullOrEmpty(title) ? address : title,
                    address,
                    Uri.TryCreate(address, UriKind.Absolute, out var parsed) ? parsed.Host : null));
            }
        }
        catch (JsonException)
        {
            // A line that only looks like the vendor's shape lists nothing.
        }

        return rows;
    }
}
