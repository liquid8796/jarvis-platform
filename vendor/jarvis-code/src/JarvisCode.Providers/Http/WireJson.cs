using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Providers.Http;

/// <summary>
/// Reading a string out of a provider's response the way the reference client
/// reads it.
///
/// A stream that splits an astral character — an emoji, most often — across two
/// deltas has to send each half as its own escape: one delta ends
/// <c>"…\ud83d"</c> and the next opens <c>"\ude80…"</c>. Both are well-formed
/// JSON, and the reference is JavaScript, where <c>JSON.parse</c> yields a
/// string holding the half and concatenating the next delta puts the character
/// back together.
///
/// <see cref="System.Text.Json"/> refuses that: <see cref="JsonElement.GetString"/>
/// throws <see cref="InvalidOperationException"/> for an unpaired surrogate in
/// either direction ("missing low surrogate" for the leading half, "Invalid
/// surrogate value" for the trailing one), which killed the whole turn on a
/// stream the reference reads without noticing. A .NET string can hold the half
/// perfectly well, so the value is unescaped here instead, and concatenation
/// repairs the pair exactly as it does there.
/// </summary>
internal static class WireJson
{
    /// <summary>
    /// The string a JSON value carries, or null when the value is absent.
    /// Behaves as <c>GetValue&lt;string&gt;()</c> in every other respect — a
    /// value that is not a JSON string still throws, since that is a shape
    /// error in the response rather than a character this runtime dislikes.
    /// </summary>
    public static string? AsText(this JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException) when (IsJsonString(node))
        {
            return Unescape(node.GetValue<JsonElement>().GetRawText());
        }
    }

    private static bool IsJsonString(JsonNode node) =>
        node is JsonValue value &&
        value.TryGetValue<JsonElement>(out var element) &&
        element.ValueKind == JsonValueKind.String;

    /// <summary>
    /// Decodes one JSON string token, quotes included. The parser has already
    /// validated it, so every backslash introduces a known escape and every
    /// <c>\u</c> is followed by four hex digits; the only rule dropped here is
    /// the surrogate pairing this runtime adds and the format does not.
    /// </summary>
    private static string Unescape(string raw)
    {
        var text = new StringBuilder(raw.Length);
        for (int i = 1; i < raw.Length - 1; i++)
        {
            if (raw[i] != '\\')
            {
                text.Append(raw[i]);
                continue;
            }

            switch (raw[++i])
            {
                case 'u':
                    text.Append((char)ushort.Parse(
                        raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                // The three that stand for themselves: \" \\ \/
                default: text.Append(raw[i]); break;
            }
        }

        return text.ToString();
    }
}
