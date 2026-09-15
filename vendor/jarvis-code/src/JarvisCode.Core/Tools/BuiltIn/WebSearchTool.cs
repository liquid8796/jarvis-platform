using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// Web search through a SearXNG instance's JSON API. Read-only on purpose, the way
/// the reference app treats its own search: the call only reads public pages, so it
/// runs without a prompt while web_fetch and the other network tools still ask.
/// </summary>
public sealed class WebSearchTool(HttpClient http, string baseUrl) : ITool
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] TimeRanges = ["day", "week", "month", "year"];
    private const int DefaultResults = 10;
    private const int MaxResults = 20;
    private const int MaxSnippetChars = 300;

    public string Name => "WebSearch";

    public string Description =>
        "Searches the web and returns ranked results as title, URL and snippet — use it for anything past the " +
        "training cutoff or specific to a version. Follow a result with web_fetch to read the page itself.";

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("query", new JsonObject
            {
                ["type"] = "string",
                ["minLength"] = 2,
                ["description"] = "The search query to use",
            }),
            ("allowed_domains", new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Only include search results from these domains",
            }),
            ("blocked_domains", new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Never include search results from these domains",
            }),
        ],
        "query");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"Search({JsonArgs.GetString(arguments, "query") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = JsonArgs.GetString(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query))
            return ToolResult.Error("query is required.");

        var timeRange = JsonArgs.GetString(arguments, "time_range")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(timeRange) && !TimeRanges.Contains(timeRange))
            return ToolResult.Error($"Unknown time_range '{timeRange}'. Use {string.Join(", ", TimeRanges)}.");

        int count = Math.Clamp(JsonArgs.GetInt(arguments, "count") ?? DefaultResults, 1, MaxResults);
        if (BuildRequestUri(baseUrl, query, timeRange) is not { } uri)
        {
            return ToolResult.Error(
                "No usable web search endpoint is configured. Set a SearXNG base URL under " +
                "Settings > General > Search endpoint.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(SearchTimeout);
        try
        {
            using var response = await http.GetAsync(uri, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ToolResult.Error(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {uri.Host} — " +
                    "check the web search base URL in Settings.");
            }

            var payload = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (JsonException ex)
            {
                return ToolResult.Error(
                    $"{uri.Host} did not answer with JSON ({ex.Message}). A SearXNG instance needs the json " +
                    "format enabled in its settings.yml.");
            }

            var results = FilterDomains(
                node?["results"] as JsonArray ?? [],
                Domains(arguments["allowed_domains"]),
                Domains(arguments["blocked_domains"]));
            var failures = DescribeFailures(node?["unresponsive_engines"] as JsonArray);
            return ToolResult.Success(context.Truncate(Format(results, count, query, failures), "search results"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"Searching {uri.Host} timed out after {SearchTimeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"Searching {uri.Host} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the SearXNG query URL. The configured value may be the instance root or
    /// already point at its search path; anything that is not an absolute http(s) URL
    /// yields null so the caller can say so instead of failing at the socket.
    /// </summary>
    internal static Uri? BuildRequestUri(string baseUrl, string query, string? timeRange)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var root = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(root, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        if (!root.EndsWith("/search", StringComparison.OrdinalIgnoreCase))
            root += "/search";

        var url = $"{root}?q={Uri.EscapeDataString(query)}&format=json";
        if (!string.IsNullOrEmpty(timeRange))
            url += $"&time_range={timeRange}";
        return Uri.TryCreate(url, UriKind.Absolute, out var result) ? result : null;
    }

    private static IReadOnlyList<string> Domains(JsonNode? node) =>
        node is JsonArray array
            ? [.. array.Select(static n => n?.GetValue<string>()?.Trim().TrimStart('.').ToLowerInvariant())
                .Where(static d => !string.IsNullOrEmpty(d))!]
            : [];

    /// <summary>
    /// The reference hands allowed/blocked domains to its search backend; this one
    /// applies them to what SearXNG returned. A domain matches a host equal to it or
    /// ending in "." + it, so "example.com" covers "docs.example.com".
    /// </summary>
    internal static JsonArray FilterDomains(JsonArray results, IReadOnlyList<string> allowed, IReadOnlyList<string> blocked)
    {
        if (allowed.Count == 0 && blocked.Count == 0)
            return results;

        static bool Matches(string host, string domain) =>
            host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

        var kept = new JsonArray();
        foreach (var node in results)
        {
            var url = node?["url"]?.GetValue<string>();
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";
            if (allowed.Count > 0 && !allowed.Any(d => Matches(host, d)))
                continue;
            if (blocked.Any(d => Matches(host, d)))
                continue;
            kept.Add(node?.DeepClone());
        }

        return kept;
    }

    private static string Format(JsonArray results, int count, string query, string? failures)
    {
        var builder = new StringBuilder();
        int shown = 0;
        foreach (var node in results)
        {
            if (Text(node?["url"]) is not { Length: > 0 } url)
                continue;

            shown++;
            var title = Text(node?["title"]);
            builder.AppendLine($"{shown}. {(string.IsNullOrWhiteSpace(title) ? url : title)}");
            builder.AppendLine($"   {url}");
            if (Snippet(Text(node?["content"])) is { Length: > 0 } snippet)
                builder.AppendLine($"   {snippet}");
            builder.AppendLine();
            if (shown >= count)
                break;
        }

        var header = shown == 0
            ? $"No results for \"{query}\"."
            : $"{shown} result(s) for \"{query}\":";
        // Engines drop out one at a time (rate limits, CAPTCHAs); naming them is the
        // only way an empty or thin answer can be told apart from a broken instance.
        var footer = failures is null ? "" : $"\nEngines that did not answer: {failures}.";
        return (header + "\n\n" + builder.ToString().TrimEnd() + footer).Trim();
    }

    /// <summary>Reads a JSON string field, tolerating nulls and non-string values.</summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string? Snippet(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var collapsed = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaxSnippetChars ? collapsed[..MaxSnippetChars] + "…" : collapsed;
    }

    /// <summary>Renders SearXNG's [engine, reason] pairs, e.g. "duckduckgo (CAPTCHA)".</summary>
    private static string? DescribeFailures(JsonArray? unresponsive)
    {
        if (unresponsive is null || unresponsive.Count == 0)
            return null;

        var parts = new List<string>();
        foreach (var entry in unresponsive)
        {
            if (entry is not JsonArray pair || pair.Count == 0)
                continue;
            var engine = Text(pair[0]);
            if (string.IsNullOrEmpty(engine))
                continue;
            var reason = pair.Count > 1 ? Text(pair[1]) : null;
            parts.Add(string.IsNullOrEmpty(reason) ? engine : $"{engine} ({reason})");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
