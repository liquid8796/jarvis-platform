using System.Text.RegularExpressions;

namespace JarvisCode.Cli.Repl.Input;

/// <summary>One collapsed paste: the placeholder in the composer stands for this content.</summary>
internal sealed record PastedContent(int Id, string Type, string Content);

/// <summary>
/// The reference's paste collapsing (CLI 2.1.257, its <c>aue</c>/<c>Hu</c>/<c>m4e</c>):
/// a paste that is long or spans more lines than the box will show becomes
/// <c>[Pasted text #N +M lines]</c> in the composer with the content kept beside
/// it, images become <c>[Image #N]</c>, and pasting again while the hint is up
/// expands the newest placeholder back into the text — the reference's
/// "paste again to expand". Every placeholder is expanded before the prompt is
/// sent.
/// </summary>
internal sealed partial class PasteCollapse
{
    /// <summary>Longer than this collapses however few lines it has.</summary>
    public const int MaxInlineChars = 800;

    /// <summary>The reference's <c>lue</c>: a placeholder past this is never expanded back in place.</summary>
    public const int MaxExpandChars = 100_000;

    private readonly Dictionary<int, PastedContent> _contents = [];
    private int _nextId;
    private int _hintId = -1;

    public IReadOnlyDictionary<int, PastedContent> Contents => _contents;

    /// <summary>The reference's <c>MGr</c>: every placeholder shape, with the id captured.</summary>
    [GeneratedRegex(@"\[(?:Pasted text|Image|Audio|\.\.\.Truncated text) #(\d+)(?: \+\d+ lines)?(?:\.)*\]")]
    private static partial Regex Placeholder();

    /// <summary>The reference's line cap: <c>max(0, min(rows - 10, 2))</c>.</summary>
    public static int MaxInlineLines(int rows) => Math.Max(0, Math.Min(rows - 10, 2));

    /// <summary>The reference's <c>jY</c>: line breaks in any of the three spellings.</summary>
    public static int LineBreaks(string text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
            else if (text[i] == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
        }

        return count;
    }

    /// <summary>Whether this paste collapses at the given terminal height.</summary>
    public static bool ShouldCollapse(string text, int rows) =>
        text.Length > MaxInlineChars || LineBreaks(text) > MaxInlineLines(rows);

    /// <summary>The reference's <c>aue</c>.</summary>
    public static string TextPlaceholder(int id, int lineBreaks) =>
        lineBreaks == 0 ? $"[Pasted text #{id}]" : $"[Pasted text #{id} +{lineBreaks} lines]";

    /// <summary>The reference's <c>p4e</c>.</summary>
    public static string ImagePlaceholder(int id) => $"[Image #{id}]";

    public int NextId(string composerText)
    {
        int id = ++_nextId;
        while (composerText.Contains($"#{id}", StringComparison.Ordinal) || _contents.ContainsKey(id))
        {
            id = ++_nextId;
        }

        return id;
    }

    /// <summary>Registers a collapsed text paste and returns its placeholder.</summary>
    public string CollapseText(string composerText, string text)
    {
        int id = NextId(composerText);
        _contents[id] = new PastedContent(id, "text", text);
        _hintId = id;
        return TextPlaceholder(id, LineBreaks(text));
    }

    /// <summary>Registers an image paste and returns its placeholder.</summary>
    public string CollapseImage(string composerText)
    {
        int id = NextId(composerText);
        _contents[id] = new PastedContent(id, "image", "");
        _hintId = -1;
        return ImagePlaceholder(id);
    }

    /// <summary>True while the newest placeholder is still in the box and small enough to expand.</summary>
    public bool HasExpandableRepeat(string composerText) => Newest(composerText) is not null;

    /// <summary>
    /// The reference's <c>m4e</c>: pasting again while the hint is up replaces
    /// the newest text placeholder with the content it stands for. Returns the
    /// new composer text, or null when there is nothing to expand.
    /// </summary>
    public string? TryExpandRepeat(string composerText, int cursor, string pasted, out int newCursor)
    {
        newCursor = cursor;
        if (Newest(composerText) is not { } newest)
        {
            return null;
        }

        var (id, index, length) = newest;
        var content = _contents[id].Content;
        _contents.Remove(id);
        _hintId = -1;
        newCursor = index + content.Length;
        return composerText[..index] + content + composerText[(index + length)..];
    }

    private (int Id, int Index, int Length)? Newest(string composerText)
    {
        if (_hintId < 0)
        {
            return null;
        }

        (int Id, int Index, int Length)? best = null;
        foreach (Match match in Placeholder().Matches(composerText))
        {
            int id = int.Parse(match.Groups[1].Value);
            if (!_contents.TryGetValue(id, out var content) || content.Type != "text" ||
                content.Content.Length > MaxExpandChars)
            {
                continue;
            }

            if (best is null || id > best.Value.Id)
            {
                best = (id, match.Index, match.Length);
            }
        }

        return best;
    }

    /// <summary>Every placeholder replaced by its content: what the model receives.</summary>
    public string Expand(string composerText) =>
        Placeholder().Replace(composerText, match =>
        {
            int id = int.Parse(match.Groups[1].Value);
            return _contents.TryGetValue(id, out var content) && content.Type == "text"
                ? content.Content
                : match.Value;
        });

    /// <summary>The text paste records the placeholders in this text still refer to (what history stores).</summary>
    public IReadOnlyDictionary<string, string> RecordsIn(string composerText)
    {
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Placeholder().Matches(composerText))
        {
            int id = int.Parse(match.Groups[1].Value);
            if (_contents.TryGetValue(id, out var content) && content.Type == "text")
            {
                records[id.ToString()] = content.Content;
            }
        }

        return records;
    }

    /// <summary>Drops the contents whose placeholders are no longer in the composer.</summary>
    public void Prune(string composerText)
    {
        var present = Placeholder().Matches(composerText)
            .Select(match => int.Parse(match.Groups[1].Value)).ToHashSet();
        foreach (var id in _contents.Keys.Where(id => !present.Contains(id)).ToList())
        {
            _contents.Remove(id);
        }

        if (_hintId >= 0 && !present.Contains(_hintId))
        {
            _hintId = -1;
        }
    }

    /// <summary>Puts a history entry's paste records back behind its placeholders.</summary>
    public void Restore(IReadOnlyDictionary<string, string> records)
    {
        foreach (var (key, content) in records)
        {
            if (int.TryParse(key, out int id))
            {
                _contents[id] = new PastedContent(id, "text", content);
                _nextId = Math.Max(_nextId, id);
            }
        }
    }

    public void Clear()
    {
        _contents.Clear();
        _hintId = -1;
    }
}
