using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>One show_widget call, as the widget row and the runtime need it.</summary>
public sealed record VisualizeWidgetCall(
    string? Title, string WidgetCode, IReadOnlyList<string> LoadingMessages);

/// <summary>
/// Reading a <c>show_widget</c> call out of a transcript.
///
/// The reference does not draw a tool row beside the widget: its MCP-app row
/// <em>is</em> the widget (its <c>tO</c> in <c>c360a9e1c-DUoNQd2W.js</c>), so
/// this app's transcript replaces the call's row with one too, and this is what
/// decides which calls those are and what the row shows.
/// </summary>
public static class VisualizeWidgetCalls
{
    /// <summary>
    /// The partial-input path only needs the widget's string fields. Read their
    /// decoded prefix without inventing a closing HTML/JSON token; a split
    /// escape contributes nothing until the following fragment completes it.
    /// Nested objects and strings inside them cannot masquerade as arguments.
    /// </summary>
    public static VisualizeWidgetCall ParsePartial(string json)
    {
        string? title = null;
        var code = "";
        var depth = 0;
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] is '{' or '[') { depth++; continue; }
            if (json[i] is '}' or ']') { depth--; continue; }
            if (json[i] != '"') continue;
            var key = ReadString(json, ref i, out var closed);
            if (depth != 1 || !closed) continue;
            var valueStart = i + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
            if (valueStart >= json.Length || json[valueStart++] != ':') continue;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
            if (valueStart >= json.Length || json[valueStart] != '"') continue;
            i = valueStart;
            var value = ReadString(json, ref i, out _);
            if (key == "title") title = value;
            else if (key is "widget_code" or "code") code = value;
        }
        return new VisualizeWidgetCall(title, code, []);
    }

    private static string ReadString(string json, ref int position, out bool closed)
    {
        var value = new System.Text.StringBuilder();
        closed = false;
        for (position++; position < json.Length; position++)
        {
            var current = json[position];
            if (current == '"') { closed = true; break; }
            if (current != '\\') { value.Append(current); continue; }
            if (++position >= json.Length) break;
            if (json[position] == 'u')
            {
                if (position + 4 >= json.Length || !ushort.TryParse(json.AsSpan(position + 1, 4),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var character)) break;
                value.Append((char)character);
                position += 4;
            }
            else value.Append(json[position] switch
            {
                'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f',
                var escaped => escaped,
            });
        }
        return value.ToString();
    }

    /// <summary>The wire name a show_widget call arrives under.</summary>
    public static readonly string ShowWidgetWireName =
        InternalMcpServers.WireName(InternalMcpServerNames.Visualize, "show_widget");

    /// <summary>
    /// True for the visualize server's widget tool, under its wire name or the
    /// bare one a stored session may carry.
    /// </summary>
    public static bool IsShowWidget(string toolName) =>
        string.Equals(toolName, ShowWidgetWireName, StringComparison.Ordinal);

    /// <summary>
    /// The call's three arguments. Everything is optional here on purpose: a row
    /// is drawn for a call the model made, and a missing or malformed argument
    /// leaves an empty widget rather than no row at all.
    /// </summary>
    public static VisualizeWidgetCall Parse(string? argumentsJson)
    {
        JsonObject? args = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                args = JsonNode.Parse(argumentsJson) as JsonObject;
            }
            catch (JsonException)
            {
                args = null;
            }
        }

        return new VisualizeWidgetCall(
            Text(args?["title"]),
            Text(args?["widget_code"]) ?? "",
            args?["loading_messages"] is JsonArray messages
                ? [.. messages.Select(Text).OfType<string>()]
                : []);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
