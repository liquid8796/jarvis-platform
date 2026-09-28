using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows;

/// <summary>Blender's string-returning tools can report failure without MCP isError.</summary>
public static class BlenderMcpResult
{
    public static string PlainText(McpCallResult result) =>
        result.StructuredContent?["result"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text : result.Text;

    public static JsonObject? ParseObjectPrefix(string text)
    {
        if (text.Length > 512 * 1024 || !text.AsSpan().TrimStart().StartsWith("{")) return null;
        try
        {
            // get_addon_status may append optional prose after its JSON document.
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { MaxDepth = 32 });
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    public static bool IsError(string name, McpCallResult result)
    {
        if (result.IsError) return true;
        var text = PlainText(result).TrimStart();
        if (text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Error ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Failed to ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Rejected by safe mode", StringComparison.OrdinalIgnoreCase)) return true;
        if (name != "get_tripo_status" && text.StartsWith("Tripo is only available with", StringComparison.Ordinal)) return true;
        return HasFailure(result.StructuredContent as JsonObject) || HasFailure(ParseObjectPrefix(text));
    }

    private static bool HasFailure(JsonObject? body, int depth = 0)
    {
        if (body is null || depth > 32) return false;
        if (body["error"] is JsonNode error && error.ToJsonString() is not ("null" or "false" or "\"\"")) return true;
        foreach (var key in new[] { "success", "succeed" })
            if (body[key] is JsonValue flag && flag.TryGetValue<bool>(out var ok) && !ok) return true;
        if (body["status"] is JsonValue state && state.TryGetValue<string>(out var status) &&
            (status.Equals("error", StringComparison.OrdinalIgnoreCase) || status.Equals("failed", StringComparison.OrdinalIgnoreCase))) return true;
        // Only known result envelopes, never arbitrary scene/object properties named 'error'.
        if (body["Response"] is JsonObject response && response["Error"] is JsonObject) return true;
        return body["result"] is JsonObject inner && HasFailure(inner, depth + 1);
    }
}
