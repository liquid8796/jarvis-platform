using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Anthropic;

/// <summary>
/// Turns an Anthropic Messages event stream into provider events. Shared by
/// every transport that carries this event grammar: Anthropic's own SSE,
/// Vertex's streamRawPredict SSE, and Bedrock's eventstream chunks (which wrap
/// the same JSON events in binary frames).
/// </summary>
internal static class AnthropicStream
{
    /// <summary>The SSE transports (Anthropic itself, Vertex).</summary>
    public static async IAsyncEnumerable<ProviderEvent> ParseSseAsync(
        Stream stream,
        string providerId,
        string displayName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var providerEvent in ParseEventsAsync(SseEvents(stream, providerId, cancellationToken), displayName, cancellationToken, providerId))
            yield return providerEvent;
    }

    private static async IAsyncEnumerable<(string? EventName, JsonNode Node)> SseEvents(
        Stream stream, string providerId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sse in SseReader.ReadEventsAsync(stream, cancellationToken, providerId))
        {
            if (TryParse(sse.Data) is { } node)
                yield return (sse.EventName, node);
        }
    }

    /// <summary>
    /// The transport-neutral core: one Messages event at a time, provider events
    /// out, ending with the ResponseCompletedEvent the orchestrator requires.
    /// </summary>
    public static async IAsyncEnumerable<ProviderEvent> ParseEventsAsync(
        IAsyncEnumerable<(string? EventName, JsonNode Node)> events,
        string displayName,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? citationProviderId = null)
    {
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheReadTokens = 0;
        long cacheCreationTokens = 0;
        string? stopReason = null;
        // Per-index state for blocks that must be reassembled and replayed verbatim.
        var thinkingText = new Dictionary<int, System.Text.StringBuilder>();
        var thinkingSignatures = new Dictionary<int, string>();
        var rawBlocks = new Dictionary<int, JsonObject>();
        var rawBlockArgs = new Dictionary<int, System.Text.StringBuilder>();
        var textLengths = new Dictionary<int, int>();

        await foreach (var (eventName, node) in events.WithCancellation(cancellationToken))
        {
            var eventType = eventName ?? node["type"].AsText();

            switch (eventType)
            {
                case "message_start":
                    var startUsage = node["message"]?["usage"];
                    inputTokens = startUsage?["input_tokens"]?.GetValue<long>() ?? 0;
                    cacheReadTokens = startUsage?["cache_read_input_tokens"]?.GetValue<long>() ?? 0;
                    cacheCreationTokens = startUsage?["cache_creation_input_tokens"]?.GetValue<long>() ?? 0;
                    break;

                case "content_block_start":
                {
                    var block = node["content_block"];
                    int index = node["index"]?.GetValue<int>() ?? 0;
                    switch (block?["type"].AsText())
                    {
                        case "tool_use":
                            yield return new ToolCallStartedEvent(
                                index,
                                block["id"].AsText() ?? $"toolu_{index}",
                                block["name"].AsText() ?? "");
                            break;
                        case "thinking":
                            thinkingText[index] = new System.Text.StringBuilder(
                                block["thinking"].AsText() ?? "");
                            break;
                        case "text":
                            var initialText = block["text"].AsText() ?? "";
                            textLengths[index] = initialText.Length;
                            if (initialText.Length > 0) yield return new TextDeltaEvent(initialText, index);
                            if (block["citations"] is JsonArray citations)
                                foreach (var citation in citations.OfType<JsonObject>())
                                    yield return new CitationDeltaEvent(index,
                                        new TextCitation(citation.ToJsonString(), initialText.Length > 0 ? initialText.Length : -1)
                                        { ProviderId = citationProviderId });
                            break;
                        case null:
                            break;
                        default:
                            // redacted_thinking, server_tool_use, web_search_tool_result, ...
                            // Keep the whole block for verbatim replay; server_tool_use
                            // streams its input separately as input_json_delta.
                            rawBlocks[index] = (JsonObject)block.DeepClone();
                            if (block["type"].AsText() == "server_tool_use" &&
                                block["name"].AsText() is { Length: > 0 } serverToolName)
                            {
                                yield return new ServerToolNoticeEvent(serverToolName);
                            }
                            break;
                    }
                    break;
                }

                case "content_block_delta":
                {
                    var delta = node["delta"];
                    int index = node["index"]?.GetValue<int>() ?? 0;
                    switch (delta?["type"].AsText())
                    {
                        case "text_delta":
                            var text = delta["text"].AsText();
                            if (!string.IsNullOrEmpty(text))
                            {
                                textLengths[index] = textLengths.GetValueOrDefault(index) + text.Length;
                                yield return new TextDeltaEvent(text, index);
                            }
                            break;
                        case "citations_delta":
                            if (delta["citation"] is JsonObject citation)
                                yield return new CitationDeltaEvent(index,
                                    new TextCitation(citation.ToJsonString(), textLengths.GetValueOrDefault(index))
                                    { ProviderId = citationProviderId });
                            break;
                        case "input_json_delta":
                            var partial = delta["partial_json"].AsText();
                            if (string.IsNullOrEmpty(partial))
                                break;
                            if (rawBlocks.ContainsKey(index))
                            {
                                // Streaming input of a server-side tool call.
                                (rawBlockArgs.TryGetValue(index, out var args)
                                    ? args
                                    : rawBlockArgs[index] = new System.Text.StringBuilder()).Append(partial);
                            }
                            else
                            {
                                yield return new ToolCallArgumentsDeltaEvent(index, partial);
                            }
                            break;
                        case "thinking_delta":
                            var thinking = delta["thinking"].AsText();
                            if (!string.IsNullOrEmpty(thinking))
                            {
                                (thinkingText.TryGetValue(index, out var buffer)
                                    ? buffer
                                    : thinkingText[index] = new System.Text.StringBuilder()).Append(thinking);
                                yield return new ThinkingDeltaEvent(index, thinking);
                            }
                            break;
                        case "signature_delta":
                            var signature = delta["signature"].AsText();
                            if (!string.IsNullOrEmpty(signature))
                                thinkingSignatures[index] = thinkingSignatures.GetValueOrDefault(index, "") + signature;
                            break;
                    }
                    break;
                }

                case "content_block_stop":
                {
                    int index = node["index"]?.GetValue<int>() ?? 0;
                    if (thinkingText.Remove(index, out var completedThinking))
                    {
                        thinkingSignatures.Remove(index, out var completedSignature);
                        yield return new ThinkingCompletedEvent(
                            index, completedThinking.ToString(), completedSignature);
                    }
                    else if (rawBlocks.Remove(index, out var rawBlock))
                    {
                        if (rawBlockArgs.Remove(index, out var argsJson))
                            rawBlock["input"] = AnthropicProvider.ParseArguments(argsJson.ToString());
                        yield return new RawBlockEvent(index, rawBlock.ToJsonString());
                    }
                    break;
                }

                case "message_delta":
                {
                    stopReason = node["delta"]?["stop_reason"].AsText() ?? stopReason;
                    // The reference SDK overwrites every usage field this event
                    // reports and keeps message_start's for the ones it omits,
                    // so the authoritative final totals win. Reading only
                    // output_tokens here dropped the input and cache counts of
                    // any endpoint that reports them at the end — a relay in
                    // front of Anthropic does — and a cache hit then read as a
                    // context that had shrunk, which freezes every figure
                    // derived from it: the context ring, /context, the
                    // auto-compaction threshold and the task token budget.
                    var deltaUsage = node["usage"];
                    outputTokens = deltaUsage?["output_tokens"]?.GetValue<long>() ?? outputTokens;
                    inputTokens = deltaUsage?["input_tokens"]?.GetValue<long>() ?? inputTokens;
                    cacheReadTokens = deltaUsage?["cache_read_input_tokens"]?.GetValue<long>() ?? cacheReadTokens;
                    cacheCreationTokens =
                        deltaUsage?["cache_creation_input_tokens"]?.GetValue<long>() ?? cacheCreationTokens;
                    break;
                }

                case "error":
                    throw new ProviderException(
                        $"{displayName}: {node["error"]?["message"].AsText() ?? "stream error"}");
            }
        }

        yield return new ResponseCompletedEvent(
            WantsToolUse: stopReason == "tool_use",
            new Usage(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens),
            StopReason: stopReason);
    }

    internal static JsonNode? TryParse(string data)
    {
        try
        {
            return JsonNode.Parse(data);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
