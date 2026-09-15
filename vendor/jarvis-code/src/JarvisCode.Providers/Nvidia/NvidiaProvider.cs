using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Nvidia;

/// <summary>
/// NVIDIA Build / NIM adapter for the OpenAI-compatible chat completions API —
/// the hosted catalog at integrate.api.nvidia.com and self-hosted NIM containers
/// share one wire format. Differences from the OpenAI adapter: NIM takes the
/// classic <c>max_tokens</c> (not max_completion_tokens), switches thinking on
/// through a knob that differs from model to model, and streams reasoning
/// models' chain of thought as <c>reasoning_content</c> deltas. The base URL is
/// configurable so a local NIM container can be used; keys are only required
/// for NVIDIA's hosted endpoints.
/// </summary>
public sealed class NvidiaProvider(HttpClient http, IApiKeySource keys, IProviderEndpoints endpoints)
    : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "nvidia";

    /// <summary>NVIDIA's hosted catalog (build.nvidia.com); requests there need an API key.</summary>
    public const string HostedBaseUrl = "https://integrate.api.nvidia.com/v1";

    /// <summary>
    /// Unconfigured installs talk to the hosted catalog; point the base URL at a
    /// self-hosted NIM container (e.g. http://localhost:8000) to use that instead.
    /// </summary>
    public const string DefaultBaseUrl = HostedBaseUrl;

    private const string HostedHost = "api.nvidia.com";
    private const string ChatPath = "/chat/completions";
    private const string VersionSegment = "/v1";

    /// <summary>Reasoning streams as one block; wire tool indices shift past it.</summary>
    private const int ThinkingBlockIndex = 0;
    private const int ToolCallIndexOffset = 1;

    public string Id => ProviderId;

    public string DisplayName => "NVIDIA Build";

    /// <summary>
    /// Self-hosted NIM containers serve unauthenticated requests, so only NVIDIA's
    /// hosted endpoints demand a key — a local server then never fails with a
    /// missing-key error it does not need.
    /// </summary>
    public bool RequiresApiKey => IsHostedEndpoint(endpoints.GetBaseUrl(ProviderId));

    /// <summary>True when the configured base URL points at an NVIDIA-hosted endpoint.</summary>
    public static bool IsHostedEndpoint(string? baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        if (!Uri.TryCreate(root, UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals(HostedHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith($".{HostedHost}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Accepts the API root with or without the version segment ("http://host:8000"
    /// and ".../v1" both work) as well as a full chat-completions URL pasted whole.
    /// </summary>
    internal static string ResolveChatEndpoint(string? baseUrl)
    {
        var root = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/');
        if (root.EndsWith(ChatPath, StringComparison.OrdinalIgnoreCase))
            return root;
        if (!root.EndsWith(VersionSegment, StringComparison.OrdinalIgnoreCase))
            root += VersionSegment;
        return root + ChatPath;
    }

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request) =>
        new("POST", ResolveChatEndpoint(endpoints.GetBaseUrl(ProviderId)),
            ProviderHttp.BearerPreviewHeaders(keys, ProviderId),
            BuildBody(request));

    private static JsonObject BuildBody(LlmRequest request) =>
        RequestBodyOverride.Apply(BuildRequestBody(request), request.BodyOverride);

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        var url = ResolveChatEndpoint(endpoints.GetBaseUrl(ProviderId));
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, ProviderId, DisplayName, RequiresApiKey,
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

        // Some NIM models finish with "stop" even when they emitted tool calls.
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
        bool replayReasoning = ReplaysReasoning.Contains(request.ModelId);
        foreach (var message in request.Messages)
        {
            foreach (var node in BuildMessages(message, replayReasoning))
                messages.Add(node);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            // NIM implements the classic field; max_completion_tokens is OpenAI-only.
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = messages,
        };
        ApplyThinkingKnob(body, request);
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

    /// <summary>How one model is told to think; NIM has no switch shared by all of them.</summary>
    private enum ThinkingKnob
    {
        /// <summary>Kimi K3: "low" | "high" | "max"; its thinking cannot be turned off.</summary>
        ReasoningEffort,

        /// <summary>Nemotron 3 Super: a boolean through the chat template.</summary>
        EnableThinkingKwarg,

        /// <summary>DeepSeek V4: the same boolean under a different kwarg name.</summary>
        ThinkingKwarg,

        /// <summary>Nemotron 3 Nano: a token budget rather than a switch.</summary>
        ReasoningBudget,
    }

    /// <summary>
    /// The knob each model documents on build.nvidia.com (checked 2026-08-28).
    /// Matching is by exact id and a model that is absent — gpt-oss-120b documents
    /// no knob, and self-hosted NIMs serve whatever they were built with — is sent
    /// nothing, because an unrecognized field is a 400 on a strict endpoint.
    /// </summary>
    private static readonly Dictionary<string, ThinkingKnob> ThinkingKnobs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["moonshotai/kimi-k3"] = ThinkingKnob.ReasoningEffort,
            ["nvidia/nemotron-3-super-120b-a12b"] = ThinkingKnob.EnableThinkingKwarg,
            ["deepseek-ai/deepseek-v4-pro-0813"] = ThinkingKnob.ThinkingKwarg,
            ["nvidia/nemotron-3-nano-30b-a3b"] = ThinkingKnob.ReasoningBudget,
        };

    /// <summary>
    /// Models trained with preserved thinking history, whose model card requires the
    /// previous reasoning back on tool-use turns. Replaying it anywhere else risks a
    /// 400 from an endpoint that never emitted the field in the first place.
    /// </summary>
    private static readonly HashSet<string> ReplaysReasoning =
        new(StringComparer.OrdinalIgnoreCase) { "moonshotai/kimi-k3" };

    private static void ApplyThinkingKnob(JsonObject body, LlmRequest request)
    {
        if (!ThinkingKnobs.TryGetValue(request.ModelId, out var knob))
            return;
        bool wantsThinking = request.ThinkingEffort != ThinkingEffort.Off;
        switch (knob)
        {
            // The effort ladder has no off rung, so Off leaves the server default
            // alone rather than pretending the model can stop thinking.
            case ThinkingKnob.ReasoningEffort when wantsThinking:
                body["reasoning_effort"] = request.ThinkingEffort switch
                {
                    ThinkingEffort.Low => "low",
                    ThinkingEffort.Medium => "high",
                    _ => "max",
                };
                break;
            case ThinkingKnob.EnableThinkingKwarg:
                body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = wantsThinking };
                break;
            case ThinkingKnob.ThinkingKwarg:
                body["chat_template_kwargs"] = new JsonObject { ["thinking"] = wantsThinking };
                break;
            // Zero is not a documented budget, so Off sends nothing here either.
            case ThinkingKnob.ReasoningBudget when wantsThinking:
                body["reasoning_budget"] = request.ThinkingEffort switch
                {
                    ThinkingEffort.Low => 4_096,
                    ThinkingEffort.Medium => 8_192,
                    _ => 16_384,
                };
                break;
        }
    }

    /// <summary>
    /// One neutral message can expand to several wire messages: tool results become
    /// individual role:"tool" messages; assistant text and tool calls merge into one.
    /// </summary>
    private static IEnumerable<JsonNode> BuildMessages(ChatMessage message, bool replayReasoning)
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
        if (replayReasoning)
        {
            var reasoning = string.Concat(message.Content.OfType<ThinkingBlock>().Select(b => b.Thinking));
            if (reasoning.Length > 0)
                assistant["reasoning_content"] = reasoning;
        }
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
