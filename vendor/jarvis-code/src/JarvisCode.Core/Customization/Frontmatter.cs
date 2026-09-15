namespace JarvisCode.Core.Customization;

/// <summary>
/// The scalar fields, list fields, and raw nested blocks of one "---" frontmatter
/// header. Keys keep their as-written spelling; look them up through
/// <see cref="Frontmatter.Get"/> / <see cref="Frontmatter.GetList"/>, which match
/// the reference CLI's key normalization (case, '-' and '_' are insignificant).
/// </summary>
public sealed record ParsedFrontmatter(
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Lists,
    IReadOnlyDictionary<string, string> Blocks,
    string Body)
{
    public static readonly ParsedFrontmatter Empty =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "");
}

/// <summary>
/// Minimal "---" frontmatter parser for command/agent/skill definition files:
/// key: value lines between two --- fences, everything after is the body.
/// Supports quoted scalars, YAML block scalars (">-", "|", …), block lists
/// ("- item" lines), inline arrays ("[a, b]"), and captures deeper-nested
/// structures raw (for keys like "hooks" whose consumers parse them further).
/// </summary>
public static class Frontmatter
{
    public static (IReadOnlyDictionary<string, string> Fields, string Body) Parse(string content)
    {
        var parsed = ParseRich(content);
        return (parsed.Fields, parsed.Body);
    }

    public static ParsedFrontmatter ParseRich(string content)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return new ParsedFrontmatter(fields, lists, blocks, content.Trim());

        int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return new ParsedFrontmatter(fields, lists, blocks, content.Trim());

        var lines = normalized[4..end].Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            // Only top-level keys open entries; indented lines belong to the
            // entry above and are consumed by its own reader below.
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0)
                continue;

            // YAML block scalars (">-", "|", …) span the following indented lines;
            // folded joins them with spaces, literal keeps the newlines.
            if (value is ">" or ">-" or ">+" or "|" or "|-" or "|+")
            {
                bool literal = value[0] == '|';
                var parts = new List<string>();
                while (i + 1 < lines.Length &&
                       (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    i++;
                    parts.Add(lines[i].Trim());
                }
                while (parts.Count > 0 && parts[^1].Length == 0)
                    parts.RemoveAt(parts.Count - 1);
                if (!literal)
                    parts.RemoveAll(p => p.Length == 0);
                if (parts.Count > 0)
                    fields[key] = string.Join(literal ? "\n" : " ", parts);
                continue;
            }

            if (value.Length == 0)
            {
                // A bare "key:" opens a block list or a nested structure.
                var nested = new List<string>();
                while (i + 1 < lines.Length &&
                       (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                {
                    i++;
                    nested.Add(lines[i]);
                }
                while (nested.Count > 0 && nested[^1].Trim().Length == 0)
                    nested.RemoveAt(nested.Count - 1);
                if (nested.Count == 0)
                    continue;

                var firstContent = nested.First(n => n.Trim().Length > 0).Trim();
                if (firstContent.StartsWith("- ", StringComparison.Ordinal) || firstContent == "-")
                {
                    // Simple list: only the top-level "- item" entries; an item
                    // carrying nested structure keeps its first line's text.
                    int itemIndent = nested.First(n => n.Trim().Length > 0)
                        .TakeWhile(char.IsWhiteSpace).Count();
                    var items = new List<string>();
                    foreach (var row in nested)
                    {
                        var trimmed = row.Trim();
                        int indent = row.TakeWhile(char.IsWhiteSpace).Count();
                        if (indent == itemIndent && trimmed.StartsWith('-'))
                        {
                            var item = Unquote(trimmed[1..].Trim());
                            if (item.Length > 0)
                                items.Add(item);
                        }
                    }
                    if (items.Count > 0)
                        lists[key] = items;
                    // The raw block is kept too, for consumers that parse
                    // structured lists (hooks).
                    blocks[key] = Dedent(nested);
                }
                else
                {
                    blocks[key] = Dedent(nested);
                }
                continue;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                var items = value[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Unquote)
                    .Where(item => item.Length > 0)
                    .ToList();
                if (items.Count > 0)
                    lists[key] = items;
                fields[key] = value;
                continue;
            }

            fields[key] = Unquote(value);
        }
        int bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..];
        return new ParsedFrontmatter(fields, lists, blocks, body.Trim());
    }

    /// <summary>
    /// Looks a key up the way the reference CLI does: case-insensitively with
    /// '-' and '_' insignificant, so "when-to-use", "when_to_use" and
    /// "whenToUse" all resolve the same field.
    /// </summary>
    public static string? Get(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (fields.TryGetValue(key, out var direct))
            return direct;
        var wanted = Normalize(key);
        foreach (var (k, v) in fields)
        {
            if (Normalize(k) == wanted)
                return v;
        }
        return null;
    }

    /// <summary>
    /// The list value for a key (normalized like <see cref="Get"/>): a block or
    /// inline list when present, else a comma-separated scalar split apart.
    /// </summary>
    public static IReadOnlyList<string>? GetList(ParsedFrontmatter parsed, string key)
    {
        var wanted = Normalize(key);
        foreach (var (k, v) in parsed.Lists)
        {
            if (Normalize(k) == wanted)
                return v;
        }
        if (Get(parsed.Fields, key) is { Length: > 0 } scalar)
        {
            var items = scalar
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Unquote)
                .Where(item => item.Length > 0)
                .ToList();
            return items.Count > 0 ? items : null;
        }
        return null;
    }

    /// <summary>The raw nested block for a key (normalized like <see cref="Get"/>).</summary>
    public static string? GetBlock(ParsedFrontmatter parsed, string key)
    {
        var wanted = Normalize(key);
        foreach (var (k, v) in parsed.Blocks)
        {
            if (Normalize(k) == wanted)
                return v;
        }
        return null;
    }

    /// <summary>"true"/"false" (any case) → bool; anything else has no opinion.</summary>
    public static bool? GetBool(IReadOnlyDictionary<string, string> fields, string key) =>
        Get(fields, key)?.Trim().ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };

    internal static string Normalize(string key) =>
        key.Replace("-", "").Replace("_", "").ToLowerInvariant();

    private static string Unquote(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static string Dedent(IReadOnlyList<string> lines)
    {
        int minIndent = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
                continue;
            minIndent = Math.Min(minIndent, line.TakeWhile(char.IsWhiteSpace).Count());
        }
        if (minIndent is 0 or int.MaxValue)
            return string.Join('\n', lines);
        return string.Join('\n', lines.Select(l => l.Length >= minIndent ? l[minIndent..] : l.TrimStart()));
    }
}
