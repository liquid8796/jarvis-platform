using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
using System.Text;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The bytes of one reference build, searchable for the text this port copied
/// out of it.
///
/// A corpus is a flat byte array rather than a list of files: the questions
/// these tests ask are all "does the reference still contain this sentence",
/// never "which file is it in", and one array is what makes a thousand of those
/// questions take seconds instead of minutes.
/// </summary>
internal sealed class ReferenceCorpus
{
    /// <summary>Between concatenated files, so no match can straddle two of them.</summary>
    private static readonly byte[] Separator = [0xFF, 0x00, 0xFF, 0x00];

    private readonly byte[] _bytes;

    private ReferenceCorpus(string name, byte[] bytes)
    {
        Name = name;
        _bytes = bytes;
    }

    public string Name { get; }

    public long Length => _bytes.LongLength;

    public static ReferenceCorpus Load(string name, IEnumerable<string> files)
    {
        var parts = new List<byte[]>();
        foreach (var file in files)
        {
            try
            {
                parts.Add(File.ReadAllBytes(file));
            }
            catch (IOException)
            {
                // A file that cannot be read contributes nothing; the tests that
                // depend on this corpus report what they could not find, and an
                // empty corpus is caught by the guard in ContainsAll.
            }
        }

        var total = parts.Sum(static p => (long)p.Length) + ((long)Separator.Length * parts.Count);
        var bytes = new byte[total];
        int offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(bytes, offset);
            offset += part.Length;
            Separator.CopyTo(bytes, offset);
            offset += Separator.Length;
        }

        return new ReferenceCorpus(name, bytes);
    }

    /// <summary>
    /// True when the reference build still carries this text. Several encodings
    /// are tried, because one string can be stored many ways in a shipped build:
    /// UTF-8 in a packed script, UTF-16 in a native string table, with its
    /// quotes backslash-escaped when it sits inside a JavaScript string literal,
    /// and with every non-ASCII character written as a unicode escape, which is
    /// what a minified bundle carries for an em dash or an ellipsis.
    /// </summary>
    /// <summary>
    /// <see cref="Contains(string)"/>, but tolerant of a line the reference
    /// stores with a hole in it.
    /// </summary>
    /// <remarks>
    /// The build keeps prose as template literals: it holds
    /// <c>via the ${Tn} tool</c> where the sent text reads "via the Agent tool".
    /// A whole-line search therefore reports a line as missing when only the
    /// interpolation differs, which reads as drift and is not. When the whole
    /// line is absent this looks for the longest contiguous fragment of it the
    /// corpus does have: a real interpolation leaves a long run on at least one
    /// side, while text the reference simply does not carry leaves none.
    ///
    /// Only the two ends are probed, not every substring: searching a quarter of
    /// a gigabyte is not free, and an interpolation sits between text, so one
    /// end survives it. A hole inside <em>both</em> ends is reported as missing
    /// rather than guessed at — the safe direction for a check whose job is to
    /// notice drift.
    ///
    /// <paramref name="minimumFragment"/> is the floor for that run — short
    /// enough to clear a hole near one end, long enough that a generic phrase
    /// cannot satisfy it by accident.
    /// </remarks>
    public bool ContainsAllowingInterpolation(string literal, int minimumFragment = 40)
    {
        if (Contains(literal))
        {
            return true;
        }

        return literal.Length >= minimumFragment
            && (Contains(literal[..minimumFragment]) || Contains(literal[^minimumFragment..]));
    }

    public bool Contains(string literal)
    {
        if (literal.Length == 0)
        {
            return true;
        }

        var span = _bytes.AsSpan();
        if (span.IndexOf(Encoding.UTF8.GetBytes(literal)) >= 0 ||
            span.IndexOf(Encoding.Unicode.GetBytes(literal)) >= 0)
        {
            return true;
        }

        var escaped = literal
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        // A backtick inside a JavaScript template literal is escaped the same way
        // a quote is, and the reference writes most of its long prose as templates.
        var backticked = escaped.Replace("`", "\\`", StringComparison.Ordinal);
        if (backticked != escaped && span.IndexOf(Encoding.UTF8.GetBytes(backticked)) >= 0)
        {
            return true;
        }

        if (escaped != literal && span.IndexOf(Encoding.UTF8.GetBytes(escaped)) >= 0)
        {
            return true;
        }

        // Which quote a literal is stored in decides which quote is escaped
        // inside it: a double-quoted one escapes ", a template escapes ` and
        // leaves " raw, and a single-quoted one escapes ' instead. All four are
        // tried, because the reference picks per string — a sentence carrying a
        // backtick is stored double-quoted, one carrying a double quote is
        // stored single-quoted, and the bundler chooses whichever is shorter.
        var singleQuoted = escaped
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        foreach (var quoting in new[] { escaped, backticked, literal, singleQuoted })
        {
            // A minifier writes non-ASCII as \uXXXX, and a character below
            // U+0100 as a two-digit \xHH — how the reference carries an em dash,
            // an ellipsis or a middle dot.
            var unicodeEscaped = UnicodeEscape(quoting, latin1AsHex: false);
            if (Found(unicodeEscaped, quoting))
            {
                return true;
            }

            if (Found(UnicodeEscape(quoting, latin1AsHex: true), unicodeEscaped))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One candidate encoding, plus its angle-bracket-escaped forms.</summary>
    private bool Found(string candidate, string previous)
    {
        if (candidate != previous && _bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(candidate)) >= 0)
        {
            return true;
        }

        foreach (var form in AngleEscaped(candidate))
        {
            if (_bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(form)) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The same text with its angle brackets written as hex escapes.
    ///
    /// A bundler escapes <c>&lt;</c> and <c>&gt;</c> inside string literals so a
    /// payload can never close the script tag around it, and it does so in the
    /// same literal that carries <c>…</c> for an ellipsis — mixed escaping
    /// no single whole-string transform above reproduces. Both letter cases are
    /// offered because which one a build emits is the bundler's choice, not the
    /// source's; a string with no angle brackets yields nothing to try.
    /// </summary>
    private static IEnumerable<string> AngleEscaped(string literal)
    {
        if (!literal.Contains('<', StringComparison.Ordinal) &&
            !literal.Contains('>', StringComparison.Ordinal))
        {
            yield break;
        }

        yield return literal
            .Replace("<", "\\x3c", StringComparison.Ordinal)
            .Replace(">", "\\x3e", StringComparison.Ordinal);
        yield return literal
            .Replace("<", "\\x3C", StringComparison.Ordinal)
            .Replace(">", "\\x3E", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes every non-ASCII character as a \u escape, the form a minified
    /// bundle carries for text its source file wrote as itself.
    /// </summary>
    private static string UnicodeEscape(string literal, bool latin1AsHex)
    {
        StringBuilder? builder = null;
        for (int i = 0; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c < 0x80)
            {
                builder?.Append(c);
                continue;
            }

            builder ??= new StringBuilder(literal.Length + 16).Append(literal, 0, i);
            if (latin1AsHex && c < 0x100)
            {
                builder.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        return builder?.ToString() ?? literal;
    }

    /// <summary>
    /// Byte offsets of every UTF-8 occurrence of a literal, which is where a
    /// declaration is read from rather than merely detected.
    ///
    /// Offsets belong to this corpus, never to a build: a packaging run moves
    /// everything, so nothing here may be remembered between sessions. The
    /// anchor is the literal; the offset is only how far the reader got.
    /// </summary>
    public IReadOnlyList<int> Occurrences(string literal, int limit = 8192)
    {
        var needle = Encoding.UTF8.GetBytes(literal);
        var found = new List<int>();
        int start = 0;
        while (found.Count < limit)
        {
            var index = _bytes.AsSpan(start).IndexOf(needle);
            if (index < 0)
            {
                break;
            }

            found.Add(start + index);
            start += index + 1;
        }

        return found;
    }

    /// <summary>
    /// A window around an offset as Latin-1 text: one byte becomes one char, so
    /// an index into the result still means the same place in the corpus, which
    /// UTF-8 decoding of a window cut mid-sequence would not.
    /// </summary>
    public string Window(int offset, int before, int after)
    {
        var from = Math.Max(0, offset - before);
        var to = Math.Min(_bytes.Length, offset + after);
        return Encoding.Latin1.GetString(_bytes, from, to - from);
    }

    /// <summary>The subset that is no longer in the reference, searched in parallel.</summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> candidates)
    {
        var literals = candidates as IReadOnlyCollection<string> ?? [.. candidates];
        if (_bytes.Length == 0)
        {
            return [.. literals];
        }

        var missing = new ConcurrentBag<string>();
        Parallel.ForEach(literals, literal =>
        {
            if (!Contains(literal))
            {
                missing.Add(literal);
            }
        });

        return [.. missing.OrderBy(static l => l, StringComparer.Ordinal)];
    }
}

/// <summary>
/// The two reference builds this machine has, as searchable corpora: the
/// standalone CLI binary, and the packaged desktop app.
///
/// They are separate on purpose. Text ported from the CLI must be in the CLI,
/// and text ported from the desktop app must be in the desktop app — a check
/// that pooled both would pass on a string that moved from one product to the
/// other, which is exactly the drift worth catching.
/// </summary>
internal static class ReferenceCorpora
{
    private static readonly Lazy<ReferenceCorpus> LazyCli = new(() =>
        ReferenceCorpus.Load("CLI", ReferenceInstall.CliPath is { } path ? [path] : []));

    private static readonly Lazy<ReferenceCorpus> LazyDesktop = new(LoadDesktop);

    /// <summary>The standalone `claude.exe`: one packed binary, script and string table.</summary>
    public static ReferenceCorpus Cli => LazyCli.Value;

    /// <summary>
    /// The packaged desktop app: its Electron archive, its renderer chunks and
    /// its string catalogue — the three places a user-visible string can live.
    /// </summary>
    public static ReferenceCorpus Desktop => LazyDesktop.Value;

    private static ReferenceCorpus LoadDesktop()
    {
        if (ReferenceInstall.AppDirectory is not { } app)
        {
            return ReferenceCorpus.Load("desktop app", []);
        }

        var files = new List<string>();
        var asar = Path.Combine(app, "resources", "app.asar");
        if (File.Exists(asar))
        {
            files.Add(asar);
        }

        var ionDist = Path.Combine(app, "resources", "ion-dist");
        if (Directory.Exists(ionDist))
        {
            files.AddRange(Directory.EnumerateFiles(ionDist, "*.js", SearchOption.AllDirectories));
            files.AddRange(Directory.EnumerateFiles(ionDist, "*.json", SearchOption.AllDirectories));
        }

        return ReferenceCorpus.Load("desktop app", files);
    }
}
