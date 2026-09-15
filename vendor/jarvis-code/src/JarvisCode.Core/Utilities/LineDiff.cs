namespace JarvisCode.Core.Utilities;

public enum DiffKind
{
    Context,
    Added,
    Removed,
    /// <summary>Marker row standing in for a collapsed run of unchanged lines.</summary>
    Separator,
}

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>
/// Line-based diff for change previews. Common prefix/suffix are stripped first
/// so the LCS table only covers the changed middle; inputs whose middle exceeds
/// the cap yield null and the caller falls back to a plain-text preview.
/// </summary>
public static class LineDiff
{
    private const int MaxMiddleLines = 1500;
    private const int ContextRadius = 3;
    private const int CollapseThreshold = 8;

    public static IReadOnlyList<DiffLine>? Compute(string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');

        int prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
            prefix++;
        int suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               oldLines[^(suffix + 1)] == newLines[^(suffix + 1)])
            suffix++;

        var oldMiddle = oldLines[prefix..^suffix];
        var newMiddle = newLines[prefix..^suffix];
        if (oldMiddle.Length > MaxMiddleLines || newMiddle.Length > MaxMiddleLines)
            return null;

        var lines = new List<DiffLine>();
        foreach (var line in oldLines[..prefix])
            lines.Add(new DiffLine(DiffKind.Context, line));
        lines.AddRange(DiffMiddle(oldMiddle, newMiddle));
        foreach (var line in oldLines[^suffix..])
            lines.Add(new DiffLine(DiffKind.Context, line));

        return Collapse(lines);
    }

    /// <summary>LCS-based diff of the changed region.</summary>
    private static List<DiffLine> DiffMiddle(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length, m = newLines.Length;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = oldLines[i] == newLines[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b])
            {
                result.Add(new DiffLine(DiffKind.Context, oldLines[a]));
                a++;
                b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1])
            {
                result.Add(new DiffLine(DiffKind.Removed, oldLines[a]));
                a++;
            }
            else
            {
                result.Add(new DiffLine(DiffKind.Added, newLines[b]));
                b++;
            }
        }
        while (a < n)
            result.Add(new DiffLine(DiffKind.Removed, oldLines[a++]));
        while (b < m)
            result.Add(new DiffLine(DiffKind.Added, newLines[b++]));
        return result;
    }

    /// <summary>Long unchanged runs shrink to ±3 context lines around a separator.</summary>
    private static List<DiffLine> Collapse(List<DiffLine> lines)
    {
        var result = new List<DiffLine>();
        int index = 0;
        while (index < lines.Count)
        {
            if (lines[index].Kind != DiffKind.Context)
            {
                result.Add(lines[index++]);
                continue;
            }
            int runStart = index;
            while (index < lines.Count && lines[index].Kind == DiffKind.Context)
                index++;
            int runLength = index - runStart;
            bool atStart = runStart == 0;
            bool atEnd = index == lines.Count;
            int keepBefore = atStart ? 0 : ContextRadius;
            int keepAfter = atEnd ? 0 : ContextRadius;
            if (runLength <= Math.Max(CollapseThreshold, keepBefore + keepAfter))
            {
                for (int i = runStart; i < index; i++)
                    result.Add(lines[i]);
                continue;
            }
            for (int i = runStart; i < runStart + keepBefore; i++)
                result.Add(lines[i]);
            result.Add(new DiffLine(DiffKind.Separator,
                $"⋯ {runLength - keepBefore - keepAfter} unchanged lines"));
            for (int i = index - keepAfter; i < index; i++)
                result.Add(lines[i]);
        }
        return result;
    }
}
