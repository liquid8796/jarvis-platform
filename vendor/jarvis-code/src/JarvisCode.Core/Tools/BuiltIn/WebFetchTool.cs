using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Fetches a web page and returns its readable text (HTML stripped down).
/// Deliberately not read-only: reaching the network goes through the
/// permission gate like other side-effectful tools.
/// </summary>
public sealed partial class WebFetchTool(HttpClient http) : ITool
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    public string Name => "WebFetch";

    public string Description =>
        "Fetches a public http(s) URL and returns the page's readable text (HTML tags removed, scripts dropped). " +
        "Use it for documentation, changelogs or articles the task refers to.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("url", SchemaBuilder.String("The http:// or https:// URL to fetch")),
        ],
        "url");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments) =>
        $"Fetch({JsonArgs.GetString(arguments, "url") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var url = JsonArgs.GetString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
            return ToolResult.Error("url is required.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ToolResult.Error($"Only http/https URLs are supported, got: {url}");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(FetchTimeout);
        try
        {
            using var response = await http.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
                return ToolResult.Error($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {url}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxResponseBytes];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = await reader.ReadAsync(buffer.AsMemory(read), timeoutSource.Token);
                if (chunk == 0)
                    break;
                read += chunk;
            }
            var raw = new string(buffer, 0, read);

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            var text = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) || LooksLikeHtml(raw)
                ? ExtractReadableText(raw)
                : raw;
            if (string.IsNullOrWhiteSpace(text))
                return ToolResult.Error($"The page at {url} contained no readable text.");
            return ToolResult.Success(context.Truncate($"Content of {url}:\n\n{text.Trim()}", "page content"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"Fetching {url} timed out after {FetchTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"Fetching {url} failed: {ex.Message}");
        }
    }

    private static bool LooksLikeHtml(string content)
    {
        var head = content.Length > 512 ? content[..512] : content;
        return head.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
               head.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContentBlocks();

    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    /// <summary>Very small HTML-to-text: drops script/style, keeps block structure as newlines.</summary>
    internal static string ExtractReadableText(string html)
    {
        var text = NonContentBlocks().Replace(html, " ");
        text = LineBreakTags().Replace(text, "\n");
        text = AnyTag().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        var lines = text.Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]{2,}", " ").Trim())
            .ToList();
        return ExtraBlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }
}
