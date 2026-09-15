using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.OpenAi;

/// <summary>
/// OpenAI Chat Completions adapter (api.openai.com/v1/chat/completions, SSE streaming).
/// Uses max_completion_tokens (required by the GPT-5 family) and stream_options to
/// receive token usage in the final chunk.
/// <para>
/// The identity and the endpoint are overridable so an OpenAI-compatible relay reuses
/// this wire instead of copying it. Left unset, everything behaves exactly as before:
/// OpenAI's own endpoint under the "openai" id.
/// </para>
/// </summary>
public sealed class OpenAiProvider(
    HttpClient http,
    IApiKeySource keys,
    string? providerId = null,
    string? displayName = null,
    Func<string>? endpoint = null) : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "openai";

    /// <summary>OpenAI's own endpoint, used whenever no override is supplied.</summary>
    public const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";

    private const string DefaultDisplayName = "OpenAI GPT";

    public string Id => providerId ?? ProviderId;

    public string DisplayName => displayName ?? DefaultDisplayName;

    /// <summary>Resolved per request, so a relay's base URL can be edited while the app runs.</summary>
    private string ChatCompletionsEndpoint => endpoint?.Invoke() ?? DefaultEndpoint;

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request) =>
        new("POST", ChatCompletionsEndpoint,
            ProviderHttp.BearerPreviewHeaders(keys, Id),
            BuildBody(request));

    private JsonObject BuildBody(LlmRequest request) =>
        RequestBodyOverride.Apply(BuildRequestBody(request), request.BodyOverride);

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        var url = ChatCompletionsEndpoint;
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, Id, DisplayName, requiresApiKey: true,
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
        // Tool-call starts are announced once name is known; argument fragments
        // arriving before that (never observed in practice) are buffered.
        var announced = new HashSet<int>();
        var pendingArgs = new Dictionary<int, string>();
        var callIds = new Dictionary<int, string>();
        var callNames = new Dictionary<int, string>();

        await foreach (var sse in SseReader.ReadEventsAsync(stream, cancellationToken, Id))
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
            var text = delta?["content"].AsText();
            if (!string.IsNullOrEmpty(text))
                yield return new TextDeltaEvent(text);

            if (delta?["tool_calls"] is not JsonArray toolCalls)
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
                    yield return new ToolCallStartedEvent(
                        index, callIds.GetValueOrDefault(index, $"call_{index}"), knownName);
                    if (pendingArgs.Remove(index, out var buffered) && buffered.Length > 0)
                        yield return new ToolCallArgumentsDeltaEvent(index, buffered);
                }

                var argsDelta = call?["function"]?["arguments"].AsText();
                if (string.IsNullOrEmpty(argsDelta))
                    continue;
                if (announced.Contains(index))
                    yield return new ToolCallArgumentsDeltaEvent(index, argsDelta);
                else
                    pendingArgs[index] = pendingArgs.GetValueOrDefault(index, "") + argsDelta;
            }
        }

        yield return new ResponseCompletedEvent(
            WantsToolUse: finishReason == "tool_calls",
            new Usage(inputTokens, outputTokens),
            StopReason: StopReasons.FromOpenAiFinishReason(finishReason));
    }

    internal static JsonObject BuildRequestBody(LlmRequest request)
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
            ["max_completion_tokens"] = request.MaxOutputTokens,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = messages,
        };
        // Only reasoning-capable families accept reasoning_effort; anything else 400s on it.
        if (request.ThinkingEffort != ThinkingEffort.Off && IsReasoningModel(request.ModelId))
        {
            body["reasoning_effort"] = request.ThinkingEffort switch
            {
                ThinkingEffort.Low => "low",
                ThinkingEffort.Medium => "medium",
                _ => "high",
            };
        }
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

    private static bool IsReasoningModel(string modelId) =>
        modelId.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);

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
                // With images the content must be the array form.
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
