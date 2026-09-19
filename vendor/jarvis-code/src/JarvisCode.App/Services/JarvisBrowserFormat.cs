using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// An element reference handed to the model. Refs are assigned per document, so
/// a page and its iframes each start at 1 — the frame id is carried alongside to
/// keep them apart. The main frame (0) renders as the reference's plain
/// "ref_12"; an iframe's reads "ref_f3r12".
/// </summary>
public readonly record struct ElementRef(int Value, int FrameId = 0, string? DocumentId = null)
{
    private static readonly Regex FramePattern =
        new(@"^f(?<frame>\d+)r(?<ref>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ElementRef? Parse(JsonNode? node)
    {
        if (JarvisBrowserFormat.AsNumber(node) is { } number)
        {
            return new ElementRef((int)number);
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        text = text.Trim();
        if (text.StartsWith("ref_", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }
        string? documentId = null;
        if (text.StartsWith("d", StringComparison.Ordinal) && text.IndexOf('_') is var separator && separator > 1)
        {
            documentId = text[1..separator];
            text = text[(separator + 1)..];
        }

        if (FramePattern.Match(text) is { Success: true } match)
        {
            return new ElementRef(
                int.Parse(match.Groups["ref"].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["frame"].ValueSpan, CultureInfo.InvariantCulture), documentId);
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? new ElementRef(parsed, 0, documentId)
            : null;
    }

    /// <summary>Writes the ref (and its frame, when not the main one) into a request.</summary>
    public void ApplyTo(JsonObject args)
    {
        args["ref"] = Value;
        if (DocumentId is not null) args["documentId"] = DocumentId;
        if (FrameId != 0)
        {
            args["frameId"] = FrameId;
        }
    }

    public override string ToString() => "ref_" + (DocumentId is null ? "" : $"d{DocumentId}_") +
        (FrameId == 0 ? $"{Value}" : $"f{FrameId}r{Value}");
}

/// <summary>
/// Pure formatting for the Jarvis Browser inspection tools: the extension
/// returns structured JSON, these turn it into the text the model reads.
/// </summary>
public static class JarvisBrowserFormat
{
    /// <summary>
    /// Renders the per-frame a11y node lists as one indented YAML-style tree.
    /// The main frame leads; each same-origin iframe that contributed nodes
    /// follows under its own header, so its refs read as belonging to it.
    /// </summary>
    public static string RenderTree(JsonObject data, int maxChars)
    {
        var builder = new StringBuilder();
        var frames = Frames(data);
        if (frames.Count == 0)
        {
            return "The page returned no readable elements. It may still be loading, or be a browser page " +
                   "(chrome://, the store) that extensions cannot read — try get_page_text.";
        }

        var main = frames.FirstOrDefault();
        builder.Append("Page: ").AppendLine(main?["title"]?.GetValue<string>() ?? "");
        builder.Append("URL: ").AppendLine(main?["url"]?.GetValue<string>() ?? "");
        builder.AppendLine();

        var truncated = false;
        foreach (var frame in frames)
        {
            var frameId = (int)(AsNumber(frame["frameId"]) ?? 0);
            if (frameId != 0)
            {
                builder.AppendLine().Append("## iframe [frameId ").Append(frameId).Append("] ")
                    .AppendLine(frame["url"]?.GetValue<string>() ?? "");
            }

            foreach (var node in (frame["nodes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (builder.Length >= maxChars)
                {
                    truncated = true;
                    break;
                }

                builder.AppendLine(RenderNode(node, frameId));
            }

            truncated |= frame["truncated"]?.GetValue<bool>() == true;
            if (builder.Length >= maxChars)
            {
                break;
            }
        }

        if (truncated)
        {
            builder.AppendLine("[tree truncated — pass ref_id to focus a subtree, or raise max_chars]");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The frame list, main frame first. The pre-iframe single-frame shape is
    /// accepted too: the extension in the user's browser is updated by hand, so
    /// an app running ahead of it still reads the page instead of showing
    /// nothing.
    /// </summary>
    internal static List<JsonObject> Frames(JsonObject data)
    {
        if (data["frames"] is JsonArray frames)
        {
            return frames.OfType<JsonObject>()
                .OrderBy(f => AsNumber(f["frameId"]) ?? 0)
                .ToList();
        }

        return data["nodes"] is not null ? [data] : [];
    }

    public static string RenderNode(JsonObject node, int frameId = 0)
    {
        var depth = AsNumber(node["depth"]) ?? 0;
        var line = new StringBuilder(new string(' ', (int)depth * 2)).Append("- ");
        var role = node["role"]?.GetValue<string>() ?? "generic";
        var name = node["name"]?.GetValue<string>();

        if (role == "text")
        {
            return line.Append("text: \"").Append(name).Append('"').ToString();
        }

        line.Append(role);
        if (!string.IsNullOrEmpty(name))
        {
            line.Append(" \"").Append(name).Append('"');
        }

        if (AsNumber(node["ref"]) is { } reference)
        {
            line.Append(" [").Append(new ElementRef((int)reference, frameId, node["documentId"]?.GetValue<string>())).Append(']');
        }

        if (node["checked"] is not null)
        {
            line.Append(node["checked"]!.GetValue<bool>() ? " (checked)" : " (unchecked)");
        }
        else if (node["value"]?.GetValue<string>() is { Length: > 0 } value)
        {
            line.Append(" (value: \"").Append(value).Append("\")");
        }

        return line.ToString();
    }

    /// <summary>The reference's cap: more than this and the model is told to narrow the query.</summary>
    private const int MaxFindMatches = 20;

    /// <summary>
    /// Words that describe the kind of control rather than its label, and the
    /// roles they stand for — "search bar" has to reach a searchbox whose name
    /// is "Search the docs", which a substring match never would.
    /// </summary>
    private static readonly Dictionary<string, string[]> RoleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = ["button"],
        ["link"] = ["link"],
        ["checkbox"] = ["checkbox"],
        ["radio"] = ["radio"],
        ["tab"] = ["tab"],
        ["search"] = ["searchbox", "textbox"],
        ["searchbar"] = ["searchbox", "textbox"],
        ["searchbox"] = ["searchbox", "textbox"],
        ["field"] = ["textbox", "searchbox", "combobox"],
        ["input"] = ["textbox", "searchbox", "combobox"],
        ["textbox"] = ["textbox"],
        ["box"] = ["textbox", "searchbox", "combobox", "checkbox"],
        ["bar"] = ["searchbox", "textbox"],
        ["dropdown"] = ["combobox"],
        ["select"] = ["combobox"],
        ["combobox"] = ["combobox"],
        ["menu"] = ["menuitem", "menu"],
        ["option"] = ["option"],
        ["image"] = ["image"],
        ["heading"] = ["heading"],
        ["slider"] = ["slider"],
        ["toggle"] = ["switch", "checkbox"],
        ["switch"] = ["switch"],
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "for", "to", "on", "in", "with", "and", "or", "my", "this", "that", "any",
    };

    /// <summary>
    /// Finds elements by a plain-language description: the phrase itself scores
    /// highest, then every word of it, then the kind of control the words name.
    /// Ref-carrying nodes only, across every frame, best first.
    /// </summary>
    public static string FindMatches(JsonObject data, string query)
    {
        var phrase = query.Trim();
        var words = phrase
            .Split([' ', '\t', '\n', ',', '.', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w))
            .ToList();
        // A word's first role is the one it really means ("search" -> searchbox);
        // the rest are the fallbacks a page might have used instead.
        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            if (!RoleWords.TryGetValue(word, out var roles))
            {
                continue;
            }

            for (var i = 0; i < roles.Length; i++)
            {
                var weight = i == 0 ? 25 : 15;
                wanted[roles[i]] = Math.Max(wanted.GetValueOrDefault(roles[i]), weight);
            }
        }
        var labelWords = words.Where(w => !RoleWords.ContainsKey(w)).ToList();

        var scored = new List<(int Score, string Line)>();
        foreach (var frame in Frames(data))
        {
            var frameId = (int)(AsNumber(frame["frameId"]) ?? 0);
            foreach (var node in (frame["nodes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (node["ref"] is null)
                {
                    continue;
                }

                var role = node["role"]?.ToString() ?? "";
                var text = $"{node["name"]} {node["value"]}".Trim();
                var score = 0;

                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }

                if (labelWords.Count > 0)
                {
                    var hits = labelWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
                    score += hits * 20;
                    // Every word of the description present is a much stronger
                    // signal than most of them: "add to cart" beats "add".
                    if (hits == labelWords.Count)
                    {
                        score += 30;
                    }
                }

                if (wanted.Count > 0 && wanted.TryGetValue(role, out var roleScore))
                {
                    score += roleScore;
                }
                else if (wanted.Count > 0 && labelWords.Count == 0)
                {
                    // A query that is only a kind ("search bar") must not match
                    // every element on the page just because it has a name.
                    continue;
                }

                if (score == 0 && !role.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scored.Add((score, "- " + RenderNode(node, frameId).TrimStart(' ', '-')));
            }
        }

        if (scored.Count == 0)
        {
            return $"No elements match \"{query}\". Try read_page for the full tree.";
        }

        var best = scored
            .OrderByDescending(m => m.Score)
            .Take(MaxFindMatches)
            .Select(m => m.Line)
            .ToList();
        var more = scored.Count > MaxFindMatches
            ? $"\n{scored.Count - MaxFindMatches} more match — use a more specific query."
            : "";
        return $"{best.Count} match(es) for \"{query}\":\n{string.Join("\n", best)}{more}";
    }

    /// <summary>Console entries, oldest first; onlyErrors and pattern narrow, limit keeps the tail.</summary>
    public static string FormatConsole(JsonObject data, bool onlyErrors, string? pattern, int limit)
    {
        if (data["justAttached"]?.GetValue<bool>() == true)
        {
            return "Console capture just started for this tab — nothing recorded yet. " +
                   "Reload the page (navigate) and read again to capture its load.";
        }

        var lines = new List<string>();
        if (data["entries"] is JsonArray entries)
        {
            foreach (var entry in entries.OfType<JsonObject>())
            {
                var level = entry["level"]?.GetValue<string>() ?? "log";
                if (onlyErrors && level != "error")
                {
                    continue;
                }

                var text = entry["text"]?.GetValue<string>() ?? "";
                if (pattern is not null && !Matches(text, pattern))
                {
                    continue;
                }

                var url = entry["url"]?.GetValue<string>();
                lines.Add($"[{level}] {text}{(string.IsNullOrEmpty(url) ? "" : $" ({url})")}");
            }
        }

        if (lines.Count == 0)
        {
            return onlyErrors ? "No console errors recorded." : "No console messages recorded.";
        }

        var skipped = Math.Max(0, lines.Count - limit);
        var shown = lines.Skip(skipped);
        var header = skipped > 0 ? $"[{skipped} older message(s) not shown]\n" : "";
        return header + string.Join("\n", shown);
    }

    /// <summary>Network request list, oldest first: [status] METHOD url (type, size).</summary>
    public static string FormatNetwork(JsonObject data, string? urlPattern, int limit)
    {
        if (data["justAttached"]?.GetValue<bool>() == true)
        {
            return "Network capture just started for this tab — nothing recorded yet. " +
                   "Reload the page (navigate) and read again to capture its requests.";
        }

        var lines = new List<string>();
        if (data["requests"] is JsonArray requests)
        {
            foreach (var request in requests.OfType<JsonObject>())
            {
                var url = request["url"]?.GetValue<string>() ?? "";
                if (urlPattern is not null && !Matches(url, urlPattern))
                {
                    continue;
                }

                var method = request["method"]?.GetValue<string>() ?? "GET";
                var status = request["failed"]?.GetValue<bool>() == true
                    ? $"failed: {request["errorText"]?.GetValue<string>() ?? "error"}"
                    : request["finished"]?.GetValue<bool>() != true
                        ? "pending"
                        : ((int)(AsNumber(request["status"]) ?? 0)).ToString();
                var type = request["type"]?.GetValue<string>();
                var size = AsNumber(request["size"]) is { } bytes ? HumanSize(bytes) : null;
                var detail = string.Join(", ", new[] { type, size }.Where(static s => !string.IsNullOrEmpty(s)));
                lines.Add($"[{status}] {method} {url}{(detail.Length > 0 ? $" ({detail})" : "")}" +
                          $" id={request["requestId"]?.GetValue<string>()}");
            }
        }

        if (lines.Count == 0)
        {
            return "No network requests recorded.";
        }

        var skipped = Math.Max(0, lines.Count - limit);
        var header = skipped > 0 ? $"[{skipped} older request(s) not shown]\n" : "";
        return header + string.Join("\n", lines.Skip(skipped)) +
               "\n\nPass request_id to fetch a response body.";
    }

    /// <summary>Regex when the pattern parses, else a case-insensitive substring.</summary>
    internal static bool Matches(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    internal static string HumanSize(double bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024 * 1024):0.#}MB",
        >= 1024 => $"{bytes / 1024:0.#}kB",
        _ => $"{bytes:0}B",
    };

    /// <summary>Accepts a ref as "ref_12", "12", the number 12, or "ref_f3r12" for an iframe.</summary>
    public static ElementRef? ParseRef(JsonNode? node) => ElementRef.Parse(node);

    /// <summary>
    /// Reads any numeric JsonNode. Parsed JSON always converts to double, but
    /// nodes built in memory keep their CLR type and refuse cross-type reads.
    /// </summary>
    public static double? AsNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var asDouble))
        {
            return asDouble;
        }

        if (value.TryGetValue<int>(out var asInt))
        {
            return asInt;
        }

        return value.TryGetValue<long>(out var asLong) ? asLong : null;
    }
}
