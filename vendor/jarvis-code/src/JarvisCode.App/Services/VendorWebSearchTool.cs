using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference CLI's WebSearch tool, joined into Code turns when the web-search
/// option is off and the session's provider serves Anthropic's server-side search.
/// Ported from the installed CLI 2.1.251 (bundle source plus one captured
/// side-query request): the tool answers by running a side query against the
/// session's own model with the <c>web_search_20250305</c> server tool forced via
/// <c>tool_choice</c>, then folds the returned blocks into the reference's
/// "Links:" result text. Deliberate deltas match the app's other captured wires:
/// no <c>metadata.user_id</c> and no billing/SDK system preamble.
/// </summary>
public sealed partial class VendorWebSearchTool(
    ILlmProvider provider, string modelId, ThinkingEffort effort, string sessionId) : ITool
{
    /// <summary>The provider this session runs on — the reference's provider kind.</summary>
    private readonly string _providerId = provider.Id;

    internal const string SideQuerySystemPrompt =
        "You are an assistant for performing a web search tool use";

    internal const string SideQueryUserPrefix = "Perform a web search for the query: ";

    /// <summary>The reference's server-tool search budget for one WebSearch call.</summary>
    internal const int MaxUses = 8;

    /// <summary>The reference's per-session search cap (its taskRegistry counter).</summary>
    internal const int DefaultSessionCap = 200;

    internal const string SessionCapVariable = "CLAUDE_CODE_MAX_WEB_SEARCHES_PER_SESSION";

    /// <summary>
    /// Searches already run per session. Static because tool instances are
    /// per-turn while the reference counts per session, subagents included —
    /// worker registries snapshot the parent's tool instances, so they share it.
    /// </summary>
    internal static readonly ConcurrentDictionary<string, int> SessionSearchCounts = new();

    private static readonly JsonSerializerOptions LinkJson = new()
    {
        // JSON.stringify escapes only quotes, backslashes and control characters;
        // the default encoder would turn non-ASCII titles into \uXXXX noise.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The reference's vertex allowlist, verbatim: Google serves Anthropic's web
    /// search only for these families, matched as substrings of the model id.
    /// </summary>
    private static readonly string[] VertexWebSearchFamilies =
    [
        "claude-fable-5", "claude-opus-4", "claude-opus-5",
        "claude-sonnet-5", "claude-sonnet-4", "claude-haiku-4",
    ];

    /// <summary>
    /// Models the reference lets a request turn thinking off for explicitly (its
    /// NOe allowlist); anything else on a first-party wire rejects
    /// <c>thinking:{"type":"disabled"}</c> and thinks by default.
    /// </summary>
    private static readonly string[] ExplicitlyDisablableThinking =
    [
        "claude-opus-4-0", "claude-opus-4-1", "claude-opus-4-5", "claude-opus-4-6",
        "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5",
        "claude-sonnet-4-0", "claude-sonnet-4-5", "claude-sonnet-4-6", "claude-sonnet-5",
        "claude-haiku-4-5",
    ];

    /// <summary>
    /// The reference's isEnabled gating mapped onto this app's providers:
    /// first-party Anthropic always, Vertex for the families above, Bedrock never
    /// (the cloud has no web_search server tool), everything else never — the
    /// reference has no OpenAI/Gemini path and disables its cloud gateway too.
    /// <paramref name="relaySpeaksAnthropicWire"/> settles llmapi, which only
    /// carries a server tool on its Anthropic protocol.
    /// </summary>
    public static bool IsEnabled(string providerId, string modelId, bool relaySpeaksAnthropicWire) =>
        providerId switch
        {
            "anthropic" => true,
            // The reference decides its provider kind from the CLAUDE_CODE_USE_*
            // environment alone, so a custom ANTHROPIC_BASE_URL — which is what
            // this relay is — stays "firstParty" and keeps web search on. Measured
            // against the installed CLI 2.1.251 pointed at a local listener.
            "llmapi" => relaySpeaksAnthropicWire,
            "vertex" => VertexWebSearchFamilies.Any(
                family => modelId.Contains(family, StringComparison.Ordinal)),
            _ => false,
        };

    /// <summary>The reference's provider kinds that behave as first-party for thinking.</summary>
    internal static bool IsFirstParty(string providerId) => providerId is "anthropic" or "llmapi";

    /// <summary>
    /// The model id the reference compares against its allowlists: without this
    /// app's relay prefix, a cloud's region/vendor prefix, or a version stamp.
    /// </summary>
    internal static string BaseModelId(string modelId)
    {
        var id = modelId;
        var slash = id.LastIndexOf('/');
        if (slash >= 0)
        {
            id = id[(slash + 1)..];
        }

        var claude = id.IndexOf("claude-", StringComparison.Ordinal);
        if (claude > 0)
        {
            id = id[claude..];
        }

        var at = id.IndexOf('@');
        if (at >= 0)
        {
            id = id[..at];
        }

        var version = id.IndexOf("-v1:", StringComparison.Ordinal);
        if (version >= 0)
        {
            id = id[..version];
        }

        // A trailing eight-digit stamp is a release date, not a version.
        return DateStamp().Replace(id, "");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"-\d{8}$")]
    private static partial System.Text.RegularExpressions.Regex DateStamp();

    /// <summary>The reference's tSn: everything but the claude-3 family takes a thinking config.</summary>
    internal static bool SupportsThinking(string modelId) =>
        !BaseModelId(modelId).Contains("claude-3-", StringComparison.Ordinal);

    /// <summary>
    /// The reference's NOe: on a first-party wire, a model outside the allowlist
    /// rejects an explicit "thinking off" and reasons anyway. Bedrock and Vertex
    /// answer false, so a request there simply carries no thinking config.
    /// </summary>
    internal static bool RejectsDisabledThinking(string providerId, string modelId)
    {
        var id = BaseModelId(modelId);
        if (id.Contains("claude-3-", StringComparison.Ordinal) ||
            ExplicitlyDisablableThinking.Contains(id, StringComparer.Ordinal))
        {
            return false;
        }

        return IsFirstParty(providerId);
    }

    /// <summary>
    /// Whether the side query carries <c>thinking:{"type":"disabled"}</c> at all.
    /// The reference sends it only on a first-party wire, for a model that takes
    /// a thinking config and does not reject having it turned off.
    /// </summary>
    internal static bool SendsDisabledThinking(string providerId, string modelId) =>
        IsFirstParty(providerId) &&
        SupportsThinking(modelId) &&
        !RejectsDisabledThinking(providerId, modelId);

    /// <summary>
    /// The reference demotes a forced tool_choice to auto whenever extended
    /// thinking is live for the request — which, for this side query, means the
    /// model refused to have thinking turned off.
    /// </summary>
    internal static bool ForcesToolChoice(string providerId, string modelId) =>
        SendsDisabledThinking(providerId, modelId) || !RejectsDisabledThinking(providerId, modelId);

    /// <summary>
    /// The reference clamps output_config.effort to "high" when it turned
    /// thinking off mechanically, which this side query always does.
    /// </summary>
    internal static ThinkingEffort ClampEffort(string providerId, string modelId, ThinkingEffort effort) =>
        SendsDisabledThinking(providerId, modelId) && effort > ThinkingEffort.High
            ? ThinkingEffort.High
            : effort;

    public string Name => "WebSearch";

    // The reference's lean WebSearch description (the doc variant this harness
    // runs everywhere else), month interpolated like the CLI's toLocaleString.
    public string Description =>
        "Search the web. Returns result blocks with titles and URLs. US-only.\n\n" +
        $"- The current month is {CurrentMonth()} — use this when searching for recent information.\n" +
        "- `allowed_domains` / `blocked_domains` filter results.\n" +
        "- After answering from results, end with a \"Sources:\" list of the URLs you used as markdown links.";

    internal static string CurrentMonth() =>
        DateTime.Now.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    // The schema the reference advertises, captured from a real main-loop request.
    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["description"] = "The search query to use",
                ["type"] = "string",
                ["minLength"] = 2,
            },
            ["allowed_domains"] = new JsonObject
            {
                ["description"] = "Only include search results from these domains",
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
            ["blocked_domains"] = new JsonObject
            {
                ["description"] = "Never include search results from these domains",
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
        },
        ["required"] = new JsonArray("query"),
        ["additionalProperties"] = false,
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"Search({JsonArgs.GetString(arguments, "query") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var query = JsonArgs.GetString(arguments, "query");
        var allowed = arguments["allowed_domains"] as JsonArray;
        var blocked = arguments["blocked_domains"] as JsonArray;
        if (string.IsNullOrEmpty(query))
        {
            return ToolResult.Error("Error: Missing query");
        }

        if (allowed is { Count: > 0 } && blocked is { Count: > 0 })
        {
            return ToolResult.Error(
                "Error: Cannot specify both allowed_domains and blocked_domains in the same request");
        }

        // This adapter's BodyOverride constrains an API's server-side search tool,
        // not the tools installed in a browser assistant. Never launch an auxiliary
        // browser turn if this API-only tool was carried into a different registry.
        if (!ProviderCapabilities.For(provider).SupportsToolFreeInference)
            return ToolResult.Error(
                "This provider cannot perform an isolated WebSearch side query. Use a Jarvis browser or search tool instead.");

        var sessionKey = context.SessionId ?? sessionId;
        int cap = SessionCap();
        int used = SessionSearchCounts.GetOrAdd(sessionKey, 0);
        if (used >= cap)
        {
            // The reference returns the budget notice as an ordinary result
            // through the same formatter, not as a tool error.
            return ToolResult.Success(FormatResults(query,
            [
                Entry.Commentary(
                    $"Web search was not performed: this session has used its web search budget ({used} of {cap} " +
                    "WebSearch calls). Continue with the information already gathered instead of issuing more " +
                    "searches. If more searches are genuinely needed, ask the user to raise " +
                    SessionCapVariable + "."),
            ]));
        }

        SessionSearchCounts.AddOrUpdate(sessionKey, 1, static (_, count) => count + 1);

        // Fold the stream the way the reference folds the final content array:
        // text runs become commentary entries split at server_tool_use boundaries,
        // each web_search_tool_result becomes a links entry.
        var entries = new List<Entry>();
        var textRun = new StringBuilder();
        try
        {
            var request = BuildSideQuery(_providerId, modelId, effort, query, allowed, blocked);
            await foreach (var providerEvent in provider.StreamChatAsync(request, cancellationToken))
            {
                switch (providerEvent)
                {
                    case TextDeltaEvent text:
                        textRun.Append(text.Delta);
                        break;
                    case RawBlockEvent raw:
                        FoldRawBlock(raw.RawJson, entries, textRun);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderException ex) when (entries.Count == 0 && textRun.ToString().Trim().Length == 0)
        {
            // The reference surfaces the side query's API error only when the
            // search produced nothing; partial results still format below.
            return ToolResult.Error(ex.Message);
        }
        catch (ProviderException)
        {
        }

        if (textRun.ToString().Trim() is { Length: > 0 } trailing)
        {
            entries.Add(Entry.Commentary(trailing));
        }

        return ToolResult.Success(context.Truncate(FormatResults(query, entries), "search results"));
    }

    /// <summary>
    /// The side query the reference sends, captured request-for-request: the
    /// one-line system prompt, the forced server tool with max_uses 8, thinking
    /// disabled and no prompt caching. The adapter builds its normal body for the
    /// session's effort (max_tokens at the family cap, output_config, the beta
    /// headers) and the merge patch pins the reference's deltas on top —
    /// replacing system/messages/tools also strips their cache_control, matching
    /// the capture's enablePromptCaching:false.
    /// </summary>
    internal static LlmRequest BuildSideQuery(
        string providerId,
        string modelId,
        ThinkingEffort effort,
        string query,
        JsonArray? allowedDomains,
        JsonArray? blockedDomains)
    {
        var serverTool = new JsonObject
        {
            ["type"] = "web_search_20250305",
            // Anthropic's server-side tool name, fixed by the API — the client
            // tool above is ours to rename, this is not.
            ["name"] = "web_search",
        };
        if (allowedDomains is not null)
        {
            serverTool["allowed_domains"] = allowedDomains.DeepClone();
        }

        if (blockedDomains is not null)
        {
            serverTool["blocked_domains"] = blockedDomains.DeepClone();
        }

        serverTool["max_uses"] = MaxUses;

        return new LlmRequest
        {
            ModelId = modelId,
            SystemPrompt = SideQuerySystemPrompt,
            Messages = [ChatMessage.FromUserText(SideQueryUserPrefix + query)],
            Tools = [],
            EnableWebSearch = false,
            ThinkingEffort = ClampEffort(providerId, modelId, effort),
            BodyOverride = new JsonObject
            {
                ["system"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = SideQuerySystemPrompt,
                }),
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = SideQueryUserPrefix + query,
                    }),
                }),
                ["tools"] = new JsonArray(serverTool),
                ["tool_choice"] = ForcesToolChoice(providerId, modelId)
                    ? new JsonObject { ["type"] = "tool", ["name"] = "web_search" }
                    : new JsonObject { ["type"] = "auto" },
                // Merge semantics: type flips to disabled, budget_tokens (older
                // models' shape) is removed rather than left behind — and a model
                // that rejects "thinking off" gets no thinking key at all, which
                // a null in a merge patch is what removes.
                ["thinking"] = SendsDisabledThinking(providerId, modelId)
                    ? new JsonObject { ["type"] = "disabled", ["budget_tokens"] = null }
                    : null,
                // Rides only while thinking is on; the captured side query has none.
                ["context_management"] = null,
            },
        };
    }

    private static void FoldRawBlock(string rawJson, List<Entry> entries, StringBuilder textRun)
    {
        JsonNode? block;
        try
        {
            block = JsonNode.Parse(rawJson);
        }
        catch (JsonException)
        {
            return;
        }

        switch (block?["type"]?.GetValue<string>())
        {
            case "server_tool_use":
                if (textRun.ToString().Trim() is { Length: > 0 } run)
                {
                    entries.Add(Entry.Commentary(run));
                }

                textRun.Clear();
                break;

            case "web_search_tool_result":
                if (block["content"] is JsonArray hits)
                {
                    var links = new JsonArray();
                    foreach (var hit in hits)
                    {
                        var link = new JsonObject();
                        if (hit?["title"] is { } title)
                        {
                            link["title"] = title.DeepClone();
                        }

                        if (hit?["url"] is { } url)
                        {
                            link["url"] = url.DeepClone();
                        }

                        links.Add(link);
                    }

                    entries.Add(Entry.Links(links));
                }
                else
                {
                    entries.Add(Entry.Commentary(
                        $"Web search error: {block["content"]?["error_code"]?.GetValue<string>() ?? "unknown"}"));
                }

                break;
        }
    }

    /// <summary>The reference's tool-result text, verbatim.</summary>
    internal static string FormatResults(string query, IReadOnlyList<Entry> entries)
    {
        var text = new StringBuilder();
        text.Append("Web search results for query: \"").Append(query).Append("\"\n\n");
        foreach (var entry in entries)
        {
            if (entry.Text is not null)
            {
                text.Append(entry.Text).Append("\n\n");
            }
            else if (entry.LinkArray is { Count: > 0 })
            {
                text.Append("Links: ").Append(entry.LinkArray.ToJsonString(LinkJson)).Append("\n\n");
            }
            else
            {
                text.Append("No links found.\n\n");
            }
        }

        text.Append("\nREMINDER: You MUST include the sources above in your response to the user using markdown hyperlinks.");
        return text.ToString().Trim();
    }

    internal static int SessionCap() =>
        int.TryParse(Environment.GetEnvironmentVariable(SessionCapVariable), out var cap) && cap > 0
            ? cap
            : DefaultSessionCap;

    /// <summary>One folded result entry: commentary text or a batch of links.</summary>
    internal readonly record struct Entry(string? Text, JsonArray? LinkArray)
    {
        public static Entry Commentary(string text) => new(text, null);

        public static Entry Links(JsonArray links) => new(null, links);
    }
}
