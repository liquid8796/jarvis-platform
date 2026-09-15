using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Kimi;

/// <summary>
/// Kimi (Moonshot AI) adapter for the OpenAI-compatible chat completions API at
/// api.moonshot.ai. Differences from the OpenAI adapter: temperature, top_p, n and
/// presence_penalty are fixed server-side and rejected on the wire, so none are
/// sent; the reasoning dial is <c>reasoning_effort</c> on a low/high/max scale that
/// has no "off"; and reasoning models stream their chain of thought as
/// <c>reasoning_content</c> deltas alongside the answer.
/// </summary>
public sealed class KimiProvider(HttpClient http, IApiKeySource keys) : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "kimi";

    private const string Endpoint = "https://api.moonshot.ai/v1/chat/completions";

    /// <summary>Reasoning streams as one block; wire tool indices shift past it.</summary>
    private const int ThinkingBlockIndex = 0;
    private const int ToolCallIndexOffset = 1;

    public string Id => ProviderId;

    public string DisplayName => "Kimi (Moonshot AI)";

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request) =>
        new("POST", Endpoint, ProviderHttp.BearerPreviewHeaders(keys, ProviderId), BuildBody(request));

    private static JsonObject BuildBody(LlmRequest request) =>
        RequestBodyOverride.Apply(BuildRequestBody(request), request.BodyOverride);

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, ProviderId, DisplayName, requiresApiKey: true,
            key =>
            {
                var httpRequest = ProviderHttp.CreateJsonPost(Endpoint, body);
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

        await foreach (var sse in SseReader.ReadEventsAsync(stream, cancellationToken, ProviderId))
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
            var reasoning = delta?["reasoning_content"].AsText();
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
        // result is missing.
        yield return new ResponseCompletedEvent(
            WantsToolUse: finishReason == "tool_calls" || sawToolCall,
            new Usage(inputTokens, outputTokens),
            StopReason: sawToolCall ? StopReasons.ToolUse : StopReasons.FromOpenAiFinishReason(finishReason));
    }

    internal static JsonObject BuildRequestBody(LlmRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
        };
        foreach (var message in request.Messages)
        {
            foreach (var node in BuildMessages(message))
                messages.Add(node);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            // Kimi takes the OpenAI-style field; the classic max_tokens is not accepted.
            ["max_completion_tokens"] = request.MaxOutputTokens,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = messages,
        };
        if (SupportsReasoningEffort(request.ModelId))
            body["reasoning_effort"] = ReasoningEffort(request.ThinkingEffort);
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
    /// K3 always reasons, so "off" cannot be honored. It maps to the smallest effort
    /// the API accepts rather than being omitted, which would leave the server on its
    /// "max" default and make Off the slowest and most expensive setting of the four.
    /// </summary>
    internal static string ReasoningEffort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Off or ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "high",
        _ => "max",
    };

    /// <summary>
    /// Only the K3 family documents reasoning_effort; sending it to the K2 models
    /// would risk a 400 on a parameter they never advertised.
    /// </summary>
    internal static bool SupportsReasoningEffort(string modelId) =>
        modelId.StartsWith("kimi-k3", StringComparison.OrdinalIgnoreCase);

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
                // The multi-modal models take the array form with data-URI images.
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
