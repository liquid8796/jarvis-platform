using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.Http;

namespace JarvisCode.Providers.Gemini;

/// <summary>
/// Google Gemini adapter (generativelanguage.googleapis.com, streamGenerateContent
/// with alt=sse). Gemini function calls arrive complete (not token-streamed) and
/// carry no call id, so ids are synthesized for the neutral model; tool results are
/// correlated back by function name, which the API uses instead of ids.
/// </summary>
public sealed class GeminiProvider(HttpClient http, IApiKeySource keys) : ILlmProvider, IRequestInspector
{
    public const string ProviderId = "gemini";
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    public string Id => ProviderId;

    public string DisplayName => "Google Gemini";

    /// <summary>The model rides the path, so the endpoint is per request.</summary>
    private static string StreamEndpoint(string modelId) =>
        $"{BaseUrl}/{Uri.EscapeDataString(modelId)}:streamGenerateContent?alt=sse";

    /// <inheritdoc />
    public ProviderRequestPreview PreviewRequest(LlmRequest request) =>
        new("POST", StreamEndpoint(request.ModelId),
            [
                new("content-type", "application/json"),
                new("x-goog-api-key", RequestBodyOverride.RedactedValue),
            ],
            BuildBody(request));

    private static JsonObject BuildBody(LlmRequest request) =>
        RequestBodyOverride.Apply(BuildRequestBody(request), request.BodyOverride);

    public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = StreamEndpoint(request.ModelId);
        var body = BuildBody(request);
        await using var stream = await ProviderHttp.SendForStreamAsync(
            http, keys, ProviderId, DisplayName, requiresApiKey: true,
            key =>
            {
                var httpRequest = ProviderHttp.CreateJsonPost(url, body);
                httpRequest.Headers.Add("x-goog-api-key", key);
                return httpRequest;
            },
            cancellationToken);

        long inputTokens = 0;
        long outputTokens = 0;
        bool sawFunctionCall = false;
        int callIndex = 0;

        await foreach (var sse in SseReader.ReadEventsAsync(stream, cancellationToken, ProviderId))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(sse.Data);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (node?["usageMetadata"] is JsonObject usage)
            {
                inputTokens = usage["promptTokenCount"]?.GetValue<long>() ?? inputTokens;
                outputTokens = usage["candidatesTokenCount"]?.GetValue<long>() ?? outputTokens;
            }

            if (node?["error"] is JsonObject error)
            {
                throw new ProviderException(
                    $"{DisplayName}: {error["message"].AsText() ?? "stream error"}");
            }

            if (node?["candidates"] is not JsonArray { Count: > 0 } candidates ||
                candidates[0]?["content"]?["parts"] is not JsonArray parts)
                continue;

            foreach (var part in parts)
            {
                var text = part?["text"].AsText();
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new TextDeltaEvent(text);
                    continue;
                }
                if (part?["functionCall"] is JsonObject functionCall)
                {
                    sawFunctionCall = true;
                    var name = functionCall["name"].AsText() ?? "";
                    int index = callIndex++;
                    yield return new ToolCallStartedEvent(index, $"gemini_call_{index}_{name}", name);
                    var args = functionCall["args"]?.ToJsonString() ?? "{}";
                    yield return new ToolCallArgumentsDeltaEvent(index, args);
                }
            }
        }

        yield return new ResponseCompletedEvent(sawFunctionCall, new Usage(inputTokens, outputTokens));
    }

    internal static JsonObject BuildRequestBody(LlmRequest request)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemPrompt }),
            },
            ["contents"] = new JsonArray(
                [.. HarnessSystemTurns.Fold(request.Messages).Select(BuildContent)]),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = request.MaxOutputTokens },
        };
        // Only thinking-capable Gemini generations accept thinkingConfig; older ones
        // reject unknown generationConfig fields with INVALID_ARGUMENT.
        if (request.ThinkingEffort != ThinkingEffort.Off && SupportsThinking(request.ModelId))
        {
            ((JsonObject)body["generationConfig"]!)["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = request.ThinkingEffort switch
                {
                    ThinkingEffort.Low => 2_048,
                    ThinkingEffort.Medium => 8_192,
                    _ => 16_384,
                },
            };
        }
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray([.. request.Tools.Select(tool => (JsonNode)new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ConvertSchema(tool.InputSchema),
                })]),
            });
        }
        return body;
    }

    private static bool SupportsThinking(string modelId) =>
        modelId.Contains("2.5", StringComparison.OrdinalIgnoreCase) ||
        modelId.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase);

    private static JsonNode BuildContent(ChatMessage message)
    {
        var parts = new JsonArray();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Length > 0:
                    parts.Add(new JsonObject { ["text"] = text.Text });
                    break;
                case ToolCallBlock call:
                    parts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["args"] = ParseArgs(call.ArgumentsJson),
                        },
                    });
                    break;
                case ToolResultBlock result:
                    parts.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = result.ToolName,
                            ["response"] = new JsonObject
                            {
                                [result.IsError ? "error" : "result"] = result.Content,
                            },
                        },
                    });
                    if (result.Images is { Count: > 0 } resultImages)
                    {
                        // A functionResponse takes JSON only, so the pixels go beside it
                        // as inlineData parts of the same user content.
                        parts.Add(new JsonObject { ["text"] = ToolResultImages.Caption(result.ToolName) });
                        foreach (var resultImage in resultImages)
                        {
                            parts.Add(new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = resultImage.MediaType,
                                    ["data"] = resultImage.Base64Data,
                                },
                            });
                        }
                    }

                    break;
                case ImageBlock image:
                    parts.Add(new JsonObject
                    {
                        ["inlineData"] = new JsonObject
                        {
                            ["mimeType"] = image.MediaType,
                            ["data"] = image.Base64Data,
                        },
                    });
                    break;
            }
        }
        if (parts.Count == 0)
            parts.Add(new JsonObject { ["text"] = "(empty)" });

        return new JsonObject
        {
            ["role"] = message.Role == Role.User ? "user" : "model",
            ["parts"] = parts,
        };
    }

    private static JsonNode ParseArgs(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>
    /// Converts a standard JSON Schema to Gemini's OpenAPI-style Schema: type names
    /// are uppercased and only the fields Gemini understands are kept.
    /// </summary>
    internal static JsonObject ConvertSchema(JsonObject schema)
    {
        var converted = new JsonObject();
        if (schema["type"]?.GetValue<string>() is { } type)
            converted["type"] = type.ToUpperInvariant();
        if (schema["description"]?.GetValue<string>() is { } description)
            converted["description"] = description;
        if (schema["enum"] is JsonArray enumValues)
            converted["enum"] = enumValues.DeepClone();
        if (schema["items"] is JsonObject items)
            converted["items"] = ConvertSchema(items);
        if (schema["properties"] is JsonObject properties)
        {
            var convertedProperties = new JsonObject();
            foreach (var (name, value) in properties)
            {
                if (value is JsonObject propertySchema)
                    convertedProperties[name] = ConvertSchema(propertySchema);
            }
            converted["properties"] = convertedProperties;
        }
        if (schema["required"] is JsonArray required)
            converted["required"] = required.DeepClone();
        return converted;
    }
}
