using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Anthropic;

/// <summary>
/// Anthropic Messages API adapter (api.anthropic.com/v1/messages, SSE streaming).
/// Thinking-related stream blocks are tolerated and skipped: this client renders
/// only text and tool use.
/// <para>
/// The identity and the endpoint are overridable so a relay that speaks the same
/// Messages API — llmapi.pro and the like — reuses this wire instead of copying it.
/// Left unset, everything behaves exactly as before: Anthropic's own endpoint under
/// the "anthropic" id.
/// </para>
/// </summary>
public sealed class AnthropicProvider(
    HttpClient http,
    IApiKeySource keys,
    string? providerId = null,
    string? displayName = null,
    Func<string>? endpoint = null,
    bool betaNamespace = true,
    Func<bool>? requiresApiKey = null) : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "anthropic";

    /// <summary>Anthropic's own endpoint, used whenever no override is supplied.</summary>
    public const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";

    public static string EndpointFromBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ProviderException("The Anthropic base URL must be an absolute HTTP or HTTPS URL.");
        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = path.EndsWith("/v1/messages", StringComparison.Ordinal) ? path
            : path.EndsWith("/v1", StringComparison.Ordinal) ? path + "/messages" : path + "/v1/messages";
        return builder.Uri.AbsoluteUri;
    }

    private const string DefaultDisplayName = "Anthropic Claude";
    private const string ApiVersion = "2023-06-01";
    internal const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";

    public string Id => providerId ?? ProviderId;

    public string DisplayName => displayName ?? DefaultDisplayName;

    /// <inheritdoc />
    /// <remarks>
    /// Anthropic's own endpoint always needs a key; a relay this wire is pointed at may
    /// be a server on the local network that serves unauthenticated requests, and left
    /// unset this is exactly what it was before — true.
    /// </remarks>
    public bool RequiresApiKey => requiresApiKey?.Invoke() ?? true;

    /// <summary>Resolved per request, so a relay's base URL can be edited while the app runs.</summary>
    private string MessagesEndpoint => endpoint?.Invoke() ?? DefaultEndpoint;

    /// <summary>
    /// The CLI posts effort-era requests to the beta namespace URL; a caller's
    /// own query string wins (relay endpoints may carry one). The namespace is
    /// Anthropic's own: a relay reimplementing this wire documents the bare path
    /// and never mentions the flag, so it is switched off for those rather than
    /// sent on the chance that they ignore it.
    /// </summary>
    private string EndpointFor(LlmRequest request)
    {
        var url = MessagesEndpoint;
        return betaNamespace && request.ThinkingEffort != ThinkingEffort.Off && !url.Contains('?')
            ? url + "?beta=true"
            : url;
    }

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/json"),
            IsBearerCredential(keys.GetKey(Id))
                ? new("Authorization", "Bearer " + RequestBodyOverride.RedactedValue)
                : new("x-api-key", RequestBodyOverride.RedactedValue),
            new("anthropic-version", ApiVersion),
        };
        if (request.ThinkingEffort != ThinkingEffort.Off)
        {
            headers.Add(new("anthropic-beta", AnthropicEffort.Betas(request.ModelId)));
        }

        headers.AddRange(request.ExtraHeaders);

        return new ProviderRequestPreview("POST", EndpointFor(request), headers, BuildBody(request));
    }

    private JsonObject BuildBody(LlmRequest request) =>
        RequestBodyOverride.Apply(BuildRequestBody(request, Id), request.BodyOverride);

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        var url = EndpointFor(request);
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, Id, DisplayName, requiresApiKey: true,
            key =>
            {
                var httpRequest = ProviderHttp.CreateJsonPost(url, body);
                if (IsBearerCredential(key))
                    httpRequest.Headers.Add("Authorization", key);
                else httpRequest.Headers.Add("x-api-key", key);
                httpRequest.Headers.Add("anthropic-version", ApiVersion);
                if (request.ThinkingEffort != ThinkingEffort.Off)
                {
                    // The reference CLI's full beta list for this model class
                    // (one comma-joined header, in its order); servers that
                    // don't know a beta simply ignore it.
                    httpRequest.Headers.Add("anthropic-beta", AnthropicEffort.Betas(request.ModelId));
                }

                foreach (var (name, value) in request.ExtraHeaders)
                {
                    httpRequest.Headers.Add(name, value);
                }

                return httpRequest;
            },
            cancellationToken);

        await foreach (var providerEvent in AnthropicStream.ParseSseAsync(stream, Id, DisplayName, cancellationToken))
            yield return providerEvent;
    }

    private static bool IsBearerCredential(string? key) => key?.StartsWith("Bearer ", StringComparison.Ordinal) == true;

    internal static JsonObject BuildRequestBody(LlmRequest request, string providerId = ProviderId, bool effortExtras = true)
    {
        // Three prompt-cache breakpoints (max is four): the system prompt, the tool
        // block, and a moving one on the final message so each request extends the
        // cached prefix of the previous one.
        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["system"] = BuildSystem(request),
            ["messages"] = new JsonArray([.. MergeConsecutiveRoles(FoldForModel(request)).Select(message => BuildMessage(message, providerId))]),
        };
        var wire = AnthropicEffort.Classify(request.ModelId);
        if (request.ThinkingEffort != ThinkingEffort.Off && wire.Thinking != ThinkingWire.None)
        {
            // The reference CLI's wire, captured request-for-request: adaptive
            // thinking + the effort dial for capable models, the fixed 31999
            // budget for older ones, and max_tokens at the family cap either
            // way (the CLI ignores its own default output cap here). The
            // "display" field stays off — this app renders thinking in full.
            if (wire.Thinking == ThinkingWire.Adaptive)
            {
                body["thinking"] = new JsonObject { ["type"] = "adaptive" };
            }
            else
            {
                body["thinking"] = new JsonObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = AnthropicEffort.EnabledBudgetTokens,
                };
            }

            body["max_tokens"] = wire.MaxOutputTokens;
            if (effortExtras)
            {
                // Beta-gated body fields ride first-party (and relay) requests
                // only; Bedrock and Vertex don't advertise these betas.
                body["context_management"] = new JsonObject
                {
                    ["edits"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "clear_thinking_20251015",
                        ["keep"] = "all",
                    }),
                };
                // The dial is its own capability: opus-4-5 takes it with enabled
                // thinking, sonnet-4 does not, and every adaptive model does.
                if (wire.EffortDial)
                {
                    body["output_config"] = new JsonObject
                    {
                        ["effort"] = AnthropicEffort.WireName(request.ThinkingEffort),
                    };
                }
            }
        }
        else if (request.ThinkingEffort != ThinkingEffort.Off)
        {
            // A Claude 3 model: no thinking field at all, but still the
            // reference's output cap for the class (32000 / 8192).
            body["max_tokens"] = wire.MaxOutputTokens;
        }
        var toolEntries = request.Tools.Select(tool => (JsonNode)new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["input_schema"] = tool.InputSchema.DeepClone(),
        }).ToList();
        if (request.EnableWebSearch)
        {
            toolEntries.Add(new JsonObject
            {
                ["type"] = "web_search_20250305",
                // Anthropic's own server-side tool: the API defines this name,
                // so it is not ours to rename alongside the client tools.
                ["name"] = "web_search",
                ["max_uses"] = 5,
            });
        }
        if (toolEntries.Count > 0)
        {
            var tools = new JsonArray([.. toolEntries]);
            ((JsonObject)tools[^1]!)["cache_control"] = CacheControl(request.CacheTtl);
            body["tools"] = tools;
        }
        if (body["messages"] is JsonArray { Count: > 0 } messages &&
            messages[^1]?["content"] is JsonArray { Count: > 0 } lastContent &&
            lastContent[^1] is JsonObject lastBlock)
        {
            lastBlock["cache_control"] = CacheControl(request.CacheTtl);
        }
        return body;
    }

    /// <summary>
    /// The system array: any leading blocks in order — the reference's
    /// <c># Reporting outcomes</c> rides here uncached, ahead of the prompt — then
    /// the prompt itself with its cache breakpoint.
    /// </summary>
    private static JsonArray BuildSystem(LlmRequest request)
    {
        var system = new JsonArray();
        foreach (var block in request.LeadingSystemBlocks)
        {
            var entry = new JsonObject { ["type"] = "text", ["text"] = block.Text };
            if (block.Cached)
            {
                entry["cache_control"] = CacheControl(request.CacheTtl);
            }

            system.Add(entry);
        }

        system.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = request.SystemPrompt,
            ["cache_control"] = CacheControl(request.CacheTtl),
        });
        return system;
    }

    /// <summary>
    /// A model without the mid-conversation system role gets the harness turns
    /// folded back onto the user message they follow, each text wrapped in the
    /// reference's <c>&lt;system-reminder&gt;</c> tags — the shape a
    /// classic-prompt model's request carries (measured on claude-opus-4-5 and
    /// claude-haiku-4-5). A model with the role keeps them as they are.
    /// </summary>
    private static IReadOnlyList<ChatMessage> FoldForModel(LlmRequest request) =>
        AnthropicEffort.SupportsHarnessSystemTurn(request.ModelId)
            ? request.Messages
            : HarnessSystemTurns.Fold(request.Messages, HarnessSystemTurns.WrapAsReminder);

    /// <summary>
    /// A cache breakpoint, carrying the requested TTL when the caller set one —
    /// the reference's <c>experimental.cacheTtl</c> agent frontmatter lands here.
    /// </summary>
    private static JsonObject CacheControl(string? ttl = null)
    {
        var control = new JsonObject { ["type"] = "ephemeral" };
        if (ttl is { Length: > 0 })
        {
            control["ttl"] = ttl;
        }

        return control;
    }

    /// <summary>
    /// The Messages API expects user/assistant turns to alternate; an interrupted
    /// turn can leave two user messages in a row, so fold same-role neighbors.
    /// A harness system turn is its own role on the wire and never folds — into
    /// its neighbours or them into it — or the listing it carries would be
    /// re-attributed to the user.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> MergeConsecutiveRoles(IReadOnlyList<ChatMessage> messages)
    {
        var merged = new List<ChatMessage>();
        foreach (var message in messages)
        {
            if (merged.Count > 0 &&
                merged[^1].Role == message.Role &&
                !merged[^1].HarnessSystemTurn &&
                !message.HarnessSystemTurn)
                merged[^1] = new ChatMessage(message.Role, [.. merged[^1].Content, .. message.Content]);
            else
                merged.Add(message);
        }
        return merged;
    }

    /// <summary>
    /// The single string a harness system turn carries, or null when the message
    /// is not one or holds anything but text.
    ///
    /// The reference sends a mid-conversation system message with <c>content</c>
    /// as a bare JSON string, not the block array the two ordinary roles use —
    /// measured on CLI 2.1.257 against a local listener, where both the agent
    /// roster turn and the trailing reminder turn arrive as
    /// <c>{"role":"system","content":"…"}</c>. Several sections riding one turn
    /// are joined by a blank line: with both reminders set, that turn carries
    /// <c>"{batching}\n\n{secondary}"</c>.
    /// </summary>
    internal static string? HarnessSystemText(ChatMessage message)
    {
        if (!message.HarnessSystemTurn)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var block in message.Content)
        {
            // A harness turn is text by construction; anything else keeps the
            // array form rather than being dropped on the way out.
            if (block is not TextBlock text)
            {
                return null;
            }

            if (text.Text.Length > 0)
            {
                parts.Add(text.Text);
            }
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    private static JsonNode BuildMessage(ChatMessage message, string providerId)
    {
        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Length > 0:
                    var textNode = new JsonObject { ["type"] = "text", ["text"] = text.Text };
                    if (text.Citations is { Count: > 0 })
                    {
                        var citations = text.Citations.Where(c => c.ProviderId is null || c.ProviderId == providerId).ToArray();
                        if (citations.Length > 0)
                            textNode["citations"] = new JsonArray([.. citations.Select(c => JsonNode.Parse(c.RawJson))]);
                    }
                    content.Add(textNode);
                    break;
                case ToolCallBlock call:
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = ParseArguments(call.ArgumentsJson),
                    });
                    break;
                case ToolResultBlock result:
                    var toolResult = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.ToolCallId,
                        ["is_error"] = result.IsError,
                    };
                    if (result.Images is { Count: > 0 } images)
                    {
                        // Vision-carrying results (screenshots) use the block-array form.
                        var parts = new JsonArray();
                        if (result.Content.Length > 0)
                        {
                            parts.Add(new JsonObject { ["type"] = "text", ["text"] = result.Content });
                        }

                        foreach (var img in images)
                        {
                            parts.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = img.MediaType,
                                    ["data"] = img.Base64Data,
                                },
                            });
                        }

                        toolResult["content"] = parts;
                    }
                    else
                    {
                        toolResult["content"] = result.Content;
                    }

                    content.Add(toolResult);
                    break;
                case ImageBlock image:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MediaType,
                            ["data"] = image.Base64Data,
                        },
                    });
                    break;
                case ThinkingBlock thinking
                    when !string.IsNullOrEmpty(thinking.Signature) &&
                         !string.IsNullOrWhiteSpace(thinking.Thinking):
                    // Signed thinking must be replayed in tool-use loops; unsigned
                    // blocks are display-only and would be rejected. A signature
                    // over an empty body is neither: relays have been seen to send
                    // one (a thinking block opened, signed and closed with no
                    // delta in between), and replaying that asks the API to verify
                    // a signature against nothing.
                    content.Add(new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = thinking.Thinking,
                        ["signature"] = thinking.Signature,
                    });
                    break;
                case RawProviderBlock raw when raw.ProviderId == providerId:
                    if (TryParseRaw(raw.RawJson) is { } rawNode)
                        content.Add(rawNode);
                    break;
            }
        }
        if (content.Count == 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "(empty)" });

        return new JsonObject
        {
            // A harness system turn goes out under its own role; everything else
            // keeps the two roles this wire has always had.
            ["role"] = message.HarnessSystemTurn ? "system"
                : message.Role == Role.User ? "user"
                : "assistant",
            // …and under its own content shape: a bare string, where the two
            // ordinary roles send the block array.
            ["content"] = HarnessSystemText(message) is { } harnessText
                ? JsonValue.Create(harnessText)
                : content,
        };
    }

    internal static JsonNode ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        // ArgumentException is the transcode refusal: a stream cut between the
        // two halves of a surrogate pair leaves arguments this runtime cannot
        // re-encode, which is malformed input like any other.
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return new JsonObject();
        }
    }

    private static JsonNode? TryParseRaw(string rawJson)
    {
        try
        {
            return JsonNode.Parse(rawJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

}
