using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>
/// The reference's cold-compact pass (its <c>ixe</c>/<c>o1n</c>/<c>$Rt</c>,
/// reached when <c>CLAUDE_CODE_COLD_COMPACT</c> is set): before a summarization
/// request goes out, every image and document becomes a one-word placeholder and
/// every tool argument and tool result is cut to a preview. The summary is about
/// what happened, and a screenshot's bytes are the most expensive way to say it.
/// </summary>
public static class CompactionStripper
{
    /// <summary>How much of a tool argument or result survives (the reference's <c>LRt</c>).</summary>
    public const int PreviewChars = 100;

    public const string ImagePlaceholder = "[image]";

    /// <summary>
    /// The reference's <c>CLAUDE_CODE_COLD_COMPACT</c>, read for raw truthiness
    /// the way it reads it: set to anything at all and the pass runs.
    /// </summary>
    public static bool EnabledByEnvironment =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_COLD_COMPACT"));

    /// <summary>
    /// The reference's <c>$Rt</c>: text past the preview length is cut and told
    /// how much was cut. The cut never splits a surrogate pair, so an emoji at
    /// the boundary is dropped whole rather than turned into half a character.
    /// </summary>
    public static string Preview(string text)
    {
        if (text.Length <= PreviewChars)
            return text;

        int cut = PreviewChars;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return $"{text[..cut]}…[truncated, original {text.Length} chars]";
    }

    /// <summary>Runs the pass over a conversation, leaving untouched messages as they were.</summary>
    public static List<ChatMessage> Strip(IEnumerable<ChatMessage> messages) =>
        [.. messages.Select(StripMessage)];

    private static ChatMessage StripMessage(ChatMessage message)
    {
        var blocks = new List<ContentBlock>(message.Content.Count);
        bool changed = false;
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ImageBlock:
                    blocks.Add(new TextBlock(ImagePlaceholder));
                    changed = true;
                    break;

                case ToolCallBlock call when call.ArgumentsJson.Length > 0:
                    var previewed = PreviewArguments(call.ArgumentsJson);
                    if (previewed == call.ArgumentsJson)
                    {
                        blocks.Add(block);
                        break;
                    }

                    blocks.Add(call with { ArgumentsJson = previewed });
                    changed = true;
                    break;

                case ToolResultBlock result:
                    // The reference turns each image inside a result into the
                    // placeholder first, then previews the joined text — so the
                    // placeholders are part of what the preview length counts.
                    var text = result.Images is { Count: > 0 } images
                        ? string.Concat(result.Content, string.Concat(
                            Enumerable.Repeat(ImagePlaceholder, images.Count)))
                        : result.Content;
                    var shortened = Preview(text);
                    if (shortened == result.Content && result.Images is null or { Count: 0 })
                    {
                        blocks.Add(block);
                        break;
                    }

                    blocks.Add(result with { Content = shortened, Images = null });
                    changed = true;
                    break;

                default:
                    blocks.Add(block);
                    break;
            }
        }

        return changed ? message with { Content = blocks } : message;
    }

    /// <summary>
    /// The reference's <c>pxe</c>: every string anywhere inside a tool call's
    /// arguments is previewed, and the shape around them is kept. Arguments that
    /// are not JSON at all are previewed whole.
    /// </summary>
    internal static string PreviewArguments(string argumentsJson)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(argumentsJson);
        }
        // See AgentOrchestrator: ArgumentException is the transcode refusal for
        // arguments left holding half of a surrogate pair.
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Preview(argumentsJson);
        }

        if (parsed is null)
            return argumentsJson;

        bool truncated = false;
        var previewed = PreviewNode(parsed, ref truncated);
        // Nothing was over the limit, so the arguments travel as the model wrote
        // them rather than as this serializer would rewrite them.
        return truncated ? previewed?.ToJsonString(RelaxedJson) ?? argumentsJson : argumentsJson;
    }

    /// <summary>
    /// JSON.stringify's escaping, which is what the reference's arguments carry.
    /// The default encoder escapes every non-ASCII character, which would turn
    /// the preview's own ellipsis into six characters and mangle any prose the
    /// call was carrying.
    /// </summary>
    private static readonly JsonSerializerOptions RelaxedJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonNode? PreviewNode(JsonNode? node, ref bool truncated)
    {
        switch (node)
        {
            case JsonObject obj:
                var mapped = new JsonObject();
                foreach (var property in obj)
                    mapped[property.Key] = PreviewNode(property.Value?.DeepClone(), ref truncated);
                return mapped;

            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                    items.Add(PreviewNode(item?.DeepClone(), ref truncated));
                return items;

            case JsonValue value when value.TryGetValue(out string? text) && text is not null:
                var preview = Preview(text);
                if (!ReferenceEquals(preview, text))
                    truncated = true;
                return JsonValue.Create(preview);

            default:
                return node?.DeepClone();
        }
    }
}
