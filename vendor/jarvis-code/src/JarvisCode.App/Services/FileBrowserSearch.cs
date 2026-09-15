using System.IO;

namespace JarvisCode.App.Services;

/// <summary>One file the name search matched, with the characters that matched it.</summary>
/// <param name="Path">The path as it was indexed (relative, forward slashes).</param>
/// <param name="Score">The reference's rank fraction: 0 is the best hit, 1 the worst.</param>
/// <param name="Positions">The indexes in <paramref name="Path"/> the query matched.</param>
public sealed record FileNameHit(string Path, double Score, IReadOnlyList<int> Positions);

/// <summary>One line the content search matched.</summary>
public sealed record FileContentHit(string RelativePath, string AbsPath, int Line, int Column, string Preview);

/// <summary>
/// The Files pane's name search, ported from the reference's own file index (ion-dist
/// chunk cd089cf92-CPpbZ5h_.js, its Aa class and Pa bonus function). It is a subsequence
/// matcher rather than the Fuse the slash menu uses: every query character must appear in
/// order, adjacency pays 4, a gap costs 3 plus its length, a character after a separator
/// pays 8, a camelCase boundary pays 6, and a short path pays up to 32.
/// </summary>
public sealed class FileNameIndex
{
    private string[] _paths = [];
    private string[] _lowerPaths = [];
    private int[] _charBits = [];
    private int[] _pathLengths = [];
    private IReadOnlyList<FileNameHit> _topLevel = [];

    /// <summary>The reference caps the query it will match at 64 characters.</summary>
    public const int MaxQueryLength = 64;

    /// <summary>How many top-level entries the empty query answers with.</summary>
    public const int TopLevelCacheSize = 100;

    /// <summary>Indexes a list of paths, keeping the first of any duplicates.</summary>
    public void Load(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var path in paths)
        {
            if (path.Length > 0 && seen.Add(path))
            {
                kept.Add(path);
            }
        }

        _paths = [.. kept];
        _lowerPaths = new string[_paths.Length];
        _charBits = new int[_paths.Length];
        _pathLengths = new int[_paths.Length];
        for (int i = 0; i < _paths.Length; i++)
        {
            var lower = _paths[i].ToLowerInvariant();
            _lowerPaths[i] = lower;
            _pathLengths[i] = lower.Length;
            var bits = 0;
            foreach (var ch in lower)
            {
                if (ch is >= 'a' and <= 'z')
                {
                    bits |= 1 << (ch - 'a');
                }
            }

            _charBits[i] = bits;
        }

        _topLevel = BuildTopLevel(_paths);
    }

    /// <summary>
    /// The reference's topLevelCache: the first path segment of each path, deduplicated,
    /// sorted shortest first and then lexicographically, capped at 100.
    /// </summary>
    private static IReadOnlyList<FileNameHit> BuildTopLevel(IReadOnlyList<string> paths)
    {
        var segments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var end = path.Length;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] is '/' or '\\')
                {
                    end = i;
                    break;
                }
            }

            var head = path[..end];
            if (head.Length > 0)
            {
                segments.Add(head);
                if (segments.Count >= TopLevelCacheSize)
                {
                    break;
                }
            }
        }

        return
        [
            .. segments
                .OrderBy(s => s.Length)
                .ThenBy(s => s, StringComparer.Ordinal)
                .Take(TopLevelCacheSize)
                .Select(s => new FileNameHit(s, 0, [])),
        ];
    }

    /// <summary>The reference's search: the best <paramref name="limit"/> paths, best first.</summary>
    public IReadOnlyList<FileNameHit> Search(string query, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        if (query.Length == 0)
        {
            return [.. _topLevel.Take(limit)];
        }

        // A query with any capital in it is matched case-sensitively; an all-lowercase one
        // matches the lowercased index.
        var caseSensitive = !string.Equals(query, query.ToLowerInvariant(), StringComparison.Ordinal);
        var needle = caseSensitive ? query : query.ToLowerInvariant();
        var length = Math.Min(needle.Length, MaxQueryLength);
        var queryBits = 0;
        for (int i = 0; i < length; i++)
        {
            var ch = needle[i];
            if (ch is >= 'a' and <= 'z')
            {
                queryBits |= 1 << (ch - 'a');
            }
        }

        var bound = 24 * length + 8 + 32;
        var positions = new int[length];
        var kept = new List<(string Path, int Score, int[] Positions)>();
        var worst = double.NegativeInfinity;

        for (int i = 0; i < _paths.Length; i++)
        {
            if ((_charBits[i] & queryBits) != queryBits)
            {
                continue;
            }

            var haystack = caseSensitive ? _paths[i] : _lowerPaths[i];
            var at = haystack.IndexOf(needle[0]);
            if (at < 0)
            {
                continue;
            }

            positions[0] = at;
            var gaps = 0;
            var adjacency = 0;
            var previous = at;
            var matched = true;
            for (int q = 1; q < length; q++)
            {
                at = previous + 1 <= haystack.Length ? haystack.IndexOf(needle[q], previous + 1) : -1;
                if (at < 0)
                {
                    matched = false;
                    break;
                }

                positions[q] = at;
                var gap = at - previous - 1;
                if (gap == 0)
                {
                    adjacency += 4;
                }
                else
                {
                    gaps += 3 + gap;
                }

                previous = at;
            }

            if (!matched)
            {
                continue;
            }

            // The reference's early out: even the best remaining bonus could not beat the
            // worst hit already kept.
            if (kept.Count == limit && bound + adjacency - gaps <= worst)
            {
                continue;
            }

            var path = _paths[i];
            var score = 16 * length + adjacency - gaps;
            score += Bonus(path, positions[0], first: true);
            for (int q = 1; q < length; q++)
            {
                score += Bonus(path, positions[q], first: false);
            }

            score += Math.Max(0, 32 - (_pathLengths[i] >> 2));

            var snapshot = positions[..length];
            if (kept.Count < limit)
            {
                kept.Add((path, score, snapshot));
                if (kept.Count == limit)
                {
                    kept.Sort((a, b) => a.Score.CompareTo(b.Score));
                    worst = kept[0].Score;
                }
            }
            else if (score > worst)
            {
                var low = 0;
                var high = kept.Count;
                while (low < high)
                {
                    var middle = (low + high) >> 1;
                    if (kept[middle].Score < score)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                kept.Insert(low, (path, score, snapshot));
                kept.RemoveAt(0);
                worst = kept[0].Score;
            }
        }

        kept.Sort((a, b) => b.Score.CompareTo(a.Score));
        var span = Math.Max(kept.Count, 1);
        var results = new List<FileNameHit>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
        {
            var fraction = (double)i / span;
            // The reference pushes anything with "test" in its path one rank further down.
            var score = kept[i].Path.Contains("test", StringComparison.Ordinal)
                ? Math.Min(1.05 * fraction, 1)
                : fraction;
            results.Add(new FileNameHit(kept[i].Path, score, kept[i].Positions));
        }

        return results;
    }

    /// <summary>
    /// The reference's Pa: a matched character pays 8 at the start of the path or right
    /// after a separator, 6 at a camelCase boundary, and nothing otherwise.
    /// </summary>
    internal static int Bonus(string path, int index, bool first)
    {
        if (index == 0)
        {
            return first ? 8 : 0;
        }

        var previous = path[index - 1];
        if (previous is '/' or '\\' or '-' or '_' or '.' or ' ')
        {
            return 8;
        }

        return previous is >= 'a' and <= 'z' && path[index] is >= 'A' and <= 'Z' ? 6 : 0;
    }
}

/// <summary>
/// The Files pane's "?" search: the lines of the project's files that contain the query.
/// The reference asks its host for at most 200 hits, which is the cap kept here.
/// </summary>
public static class FileContentSearch
{
    /// <summary>The reference's own cap on how many matches the pane asks for.</summary>
    public const int MaxHits = 200;

    /// <summary>The reference's own debounce for a content query, in milliseconds.</summary>
    public const int ContentDebounceMs = 250;

    /// <summary>And for a name query, which is answered locally and so waits less.</summary>
    public const int NameDebounceMs = 120;

    /// <summary>
    /// A query that opens with "?" is a content search; the rest of it, with its leading
    /// space trimmed, is what to look for.
    /// </summary>
    public static bool IsContentQuery(string query) => query.TrimStart().StartsWith('?');

    public static string ContentTerm(string query)
    {
        var trimmed = query.TrimStart();
        return trimmed.StartsWith('?') ? trimmed[1..].TrimStart() : "";
    }

    /// <summary>Searches the given files for the term, newest hit last, capped at 200.</summary>
    public static IReadOnlyList<FileContentHit> Search(
        string root, IEnumerable<string> relativePaths, string term, int limit = MaxHits)
    {
        if (term.Length == 0)
        {
            return [];
        }

        var hits = new List<FileContentHit>();
        foreach (var relative in relativePaths)
        {
            if (hits.Count >= limit)
            {
                break;
            }

            var absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            string[] lines;
            try
            {
                if (!File.Exists(absolute) || new FileInfo(absolute).Length > 2 * 1024 * 1024)
                {
                    continue;
                }

                lines = File.ReadAllLines(absolute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (int i = 0; i < lines.Length && hits.Count < limit; i++)
            {
                var column = lines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (column >= 0)
                {
                    hits.Add(new FileContentHit(relative, absolute, i + 1, column + 1, lines[i]));
                }
            }
        }

        return hits;
    }

    /// <summary>Groups hits by file the way the pane renders them: one file row, then its lines.</summary>
    public static IReadOnlyList<(string RelativePath, IReadOnlyList<FileContentHit> Matches)> Group(
        IEnumerable<FileContentHit> hits)
    {
        var groups = new Dictionary<string, List<FileContentHit>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var hit in hits)
        {
            if (!groups.TryGetValue(hit.RelativePath, out var list))
            {
                list = [];
                groups[hit.RelativePath] = list;
                order.Add(hit.RelativePath);
            }

            list.Add(hit);
        }

        return [.. order.Select(path => (path, (IReadOnlyList<FileContentHit>)groups[path]))];
    }
}
