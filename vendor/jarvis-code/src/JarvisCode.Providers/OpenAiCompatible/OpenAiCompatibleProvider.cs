using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.OpenAiCompatible;

/// <summary>
/// One wire for the endpoints that speak OpenAI's chat-completions format without being
/// OpenAI: the aggregators (OpenRouter, TokenRouter) and the vendors that publish a
/// compatible surface of their own (DeepSeek, Zhipu AI, MiniMax), plus whatever endpoint a
/// user points a custom provider at. What differs between them is small and declared in an
/// <see cref="OpenAiCompatibleDialect"/>; what they share is this file, so a sixth endpoint
/// costs a dialect rather than another copy of the message builder.
/// <para>
/// <see cref="OpenAi.OpenAiProvider"/> is deliberately left alone. It is OpenAI's own
/// adapter, pinned by the request-wire parity suite, and its behavior is not something a
/// relay's quirk should be able to move.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleProvider(
    HttpClient http,
    IApiKeySource keys,
    string providerId,
    string displayName,
    Func<string?> baseUrl,
    OpenAiCompatibleDialect dialect,
    Func<bool>? requiresApiKey = null) : ILlmProvider, IRequestInspector
{
    /// <summary>Reasoning streams as one block; wire tool indices shift past it.</summary>
    private const int ThinkingBlockIndex = 0;
    private const int ToolCallIndexOffset = 1;

    public string Id => providerId;

    public string DisplayName => displayName;

    /// <inheritdoc />
    public bool RequiresApiKey => requiresApiKey?.Invoke() ?? true;

    /// <summary>Resolved per request, so an edited base URL applies without a restart.</summary>
    private string Endpoint => OpenAiCompatibleEndpoint.Resolve(baseUrl(), dialect.DefaultBaseUrl);

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request) =>
        new("POST", Endpoint, ProviderHttp.BearerPreviewHeaders(keys, providerId), BuildBody(request));

    private JsonObject BuildBody(LlmRequest request) =>
        RequestBodyOverride.Apply(BuildRequestBody(request, dialect), request.BodyOverride);

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        var url = Endpoint;
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, providerId, displayName, RequiresApiKey,
            key =>
            {
                var httpRequest = ProviderHttp.CreateJsonPost(url, body);
                if (key is not null)
                    httpRequest.Headers.Add("Authorization", $"Bearer {key}");
                return httpRequest;
            },
            cancellationToken);

        long inputTokens = 0;
        long outputTokens = 0;
        string? finishReason = null;
        bool sawToolCall = false;
        var thinking = new StringBuilder();
        bool thinkingClosed = false;
        // Tool-call starts are announced once the name is known; argument fragments
        // arriving before that are buffered against the wire index.
        var announced = new HashSet<int>();
        var pendingArgs = new Dictionary<int, string>();
        var callIds = new Dictionary<int, string>();
        var callNames = new Dictionary<int, string>();

        await foreach (var sse in SseReader.ReadEventsAsync(stream, cancellationToken, providerId))
        {
            if (sse.Data == "[DONE]")
                break;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(sse.Data);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (node?["usage"] is JsonObject usage)
            {
                inputTokens = usage["prompt_tokens"]?.GetValue<long>() ?? inputTokens;
                outputTokens = usage["completion_tokens"]?.GetValue<long>() ?? outputTokens;
            }

            if (node?["choices"] is not JsonArray { Count: > 0 } choices)
                continue;
            var choice = choices[0];
            finishReason = choice?["finish_reason"].AsText() ?? finishReason;

            var delta = choice?["delta"];
            var reasoning = dialect.StreamsReasoning ? ReadReasoning(delta) : null;
            if (!string.IsNullOrEmpty(reasoning))
            {
                thinking.Append(reasoning);
                yield return new ThinkingDeltaEvent(ThinkingBlockIndex, reasoning);
            }

            var text = delta?["content"].AsText();
            var toolCalls = delta?["tool_calls"] as JsonArray;
            // Reasoning precedes the answer, so close its block before the first
            // answer content — the accumulator orders blocks by when they appear.
            if (thinking.Length > 0 && !thinkingClosed &&
                (!string.IsNullOrEmpty(text) || toolCalls is { Count: > 0 }))
            {
                thinkingClosed = true;
                yield return new ThinkingCompletedEvent(ThinkingBlockIndex, thinking.ToString(), Signature: null);
            }

            if (!string.IsNullOrEmpty(text))
                yield return new TextDeltaEvent(text);

            if (toolCalls is null)
                continue;
            foreach (var call in toolCalls)
            {
                int index = call?["index"]?.GetValue<int>() ?? 0;
                if (call?["id"].AsText() is { Length: > 0 } id)
                    callIds[index] = id;
                if (call?["function"]?["name"].AsText() is { Length: > 0 } name)
                    callNames[index] = name;

                if (!announced.Contains(index) && callNames.TryGetValue(index, out var knownName))
                {
                    announced.Add(index);
                    sawToolCall = true;
                    yield return new ToolCallStartedEvent(
                        index + ToolCallIndexOffset,
                        callIds.GetValueOrDefault(index, $"call_{index}"),
                        knownName);
                    if (pendingArgs.Remove(index, out var buffered) && buffered.Length > 0)
                        yield return new ToolCallArgumentsDeltaEvent(index + ToolCallIndexOffset, buffered);
                }

                var argsDelta = call?["function"]?["arguments"].AsText();
                if (string.IsNullOrEmpty(argsDelta))
                    continue;
                if (announced.Contains(index))
                    yield return new ToolCallArgumentsDeltaEvent(index + ToolCallIndexOffset, argsDelta);
                else
                    pendingArgs[index] = pendingArgs.GetValueOrDefault(index, "") + argsDelta;
            }
        }

        if (thinking.Length > 0 && !thinkingClosed)
            yield return new ThinkingCompletedEvent(ThinkingBlockIndex, thinking.ToString(), Signature: null);

        // An announced tool call has to be run whatever the finish reason says: the
        // assistant turn already carries it, and the next request is rejected if its
        // result is missing. Aggregators in particular pass a provider's "stop" through
        // untouched on turns that did emit calls.
        yield return new ResponseCompletedEvent(
            WantsToolUse: finishReason == "tool_calls" || sawToolCall,
            new Usage(inputTokens, outputTokens),
            StopReason: sawToolCall ? StopReasons.ToolUse : StopReasons.FromOpenAiFinishReason(finishReason));
    }

    /// <summary>
    /// The chain of thought out of one delta. Three spellings are in use and a chunk
    /// carries at most one of them in practice, so the first that is present wins rather
    /// than being concatenated: <c>reasoning_content</c> (DeepSeek, Zhipu AI),
    /// <c>reasoning</c> (OpenRouter's plain string) and <c>reasoning_details</c>
    /// (OpenRouter's structured form, whose entries carry the text under "text").
    /// </summary>
    internal static string? ReadReasoning(JsonNode? delta)
    {
        if (delta?["reasoning_content"].AsText() is { Length: > 0 } content)
            return content;
        if (delta?["reasoning"] is JsonValue plain &&
            plain.GetValueKind() == JsonValueKind.String &&
            plain.AsText() is { Length: > 0 } reasoning)
            return reasoning;
        if (delta?["reasoning_details"] is not JsonArray details)
            return null;

        var joined = new StringBuilder();
        foreach (var entry in details)
        {
            if (entry?["text"].AsText() is { Length: > 0 } text)
                joined.Append(text);
        }

        return joined.Length > 0 ? joined.ToString() : null;
    }

    internal static JsonObject BuildRequestBody(LlmRequest request, OpenAiCompatibleDialect dialect)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        foreach (var message in HarnessSystemTurns.Fold(request.Messages))
        {
            foreach (var node in BuildMessages(message))
                messages.Add(node);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            [dialect.MaxTokensField] = request.MaxOutputTokens,
            ["messages"] = messages,
        };
        if (dialect.RequestsUsageInStream)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        dialect.ApplyThinking?.Invoke(body, request);
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray([.. request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema.DeepClone(),
                },
            })]);
        }
        return body;
    }

    /// <summary>
    /// One neutral message can expand to several wire messages: tool results become
    /// individual role:"tool" messages; assistant text and tool calls merge into one.
    /// </summary>
    private static IEnumerable<JsonNode> BuildMessages(ChatMessage message)
    {
        if (message.Role == Role.User)
        {
            var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
            foreach (var result in toolResults)
            {
                yield return new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = result.ToolCallId,
                    ["content"] = result.IsError ? $"ERROR: {result.Content}" : result.Content,
                };
            }
            var text = message.GetText();
            var images = message.Content.OfType<ImageBlock>().ToList();
            var resultImages = ToolResultImages.Collect(toolResults);
            if (images.Count > 0 || resultImages.Count > 0)
            {
                // Vision models take the array form with data-URI images.
                var parts = new JsonArray();
                if (text.Length > 0)
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                if (resultImages.Count > 0)
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = ToolResultImages.Caption(toolResults) });
                foreach (var image in images.Concat(resultImages))
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = $"data:{image.MediaType};base64,{image.Base64Data}",
                        },
                    });
                }
                yield return new JsonObject { ["role"] = "user", ["content"] = parts };
            }
            else if (text.Length > 0 || toolResults.Count == 0)
            {
                yield return new JsonObject { ["role"] = "user", ["content"] = text };
            }
            yield break;
        }

        var assistant = new JsonObject { ["role"] = "assistant" };
        var assistantText = message.GetText();
        assistant["content"] = assistantText.Length > 0 ? assistantText : null;
        var calls = message.ToolCalls.ToList();
        if (calls.Count > 0)
        {
            assistant["tool_calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson.Length > 0 ? call.ArgumentsJson : "{}",
                },
            })]);
        }
        yield return assistant;
    }
}
