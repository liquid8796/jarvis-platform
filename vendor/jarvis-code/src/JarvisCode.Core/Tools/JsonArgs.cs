using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Core.Tools;

/// <summary>
/// Tolerant readers for tool arguments. Models sometimes send integers as
/// floating point ("5000.0") or numbers as strings; strict GetValue&lt;int&gt;
/// would throw and fail the whole tool call.
/// </summary>
public static class JsonArgs
{
    public static int? GetInt(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out int intValue))
            return intValue;
        if (value.TryGetValue(out double doubleValue))
            return (int)doubleValue;
        if (value.TryGetValue(out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return (int)parsed;
        return null;
    }

    public static double? GetDouble(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out double doubleValue))
            return doubleValue;
        if (value.TryGetValue(out int intValue))
            return intValue;
        if (value.TryGetValue(out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    public static bool GetBool(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonValue value)
            return false;
        if (value.TryGetValue(out bool boolValue))
            return boolValue;
        return value.TryGetValue(out string? text) &&
               bool.TryParse(text, out var parsed) && parsed;
    }

    public static string? GetString(JsonObject arguments, string name)
    {
        if (arguments[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return text;
        // A number or bool where text was expected still has a usable representation.
        return value.ToJsonString().Trim('"');
    }
}
