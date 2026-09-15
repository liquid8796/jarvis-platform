using System.Collections.Generic;
using System.Linq;

namespace JarvisCode.App.Services;

/// <summary>One weighted field of a <see cref="FuzzyIndex{T}"/>.</summary>
/// <param name="Name">The field's name, reported back on every match.</param>
/// <param name="Weight">Its relative weight; normalized against its siblings before scoring.</param>
public sealed record FuzzyKey(string Name, double Weight);

/// <summary>One field value that matched, with the Bitap score it matched at.</summary>
public sealed record FuzzyMatch(string Key, string Value, double Score);

/// <summary>One document that matched, with its combined score (lower is better).</summary>
public sealed record FuzzyResult<T>(T Item, int Index, double Score, IReadOnlyList<FuzzyMatch> Matches);

/// <summary>
/// The fuzzy search the reference desktop's slash-command menu runs on: the
/// Fuse.js build shipped in <c>ion-dist/assets/v1/vendor-utils-DWaswhiK.js</c>
/// (Claude 1.40609.0.0), ported constant for constant.
///
/// It is here rather than approximated because the menu's *order* is what the
/// user sees, and two of the sort's tiers read Fuse's own numbers: the score
/// bucket <c>floor(10 * score)</c>, and the rejection of a result that matched
/// only on the description key. A different scorer reorders the menu.
///
/// Three JavaScript details decide the numbers and are reproduced deliberately.
/// <c>Math.round</c> is half-up where .NET's default is half-to-even. The
/// zero-score guard multiplies by <c>Number.EPSILON</c> (2^-52), which is
/// nothing like .NET's <c>double.Epsilon</c>. And the bit arrays are 32-bit
/// signed, so a 32-character chunk overflows to the same value in both
/// languages, while reads past the end of one are <c>undefined</c> in
/// JavaScript and coerce to 0 through <c>|</c> and <c>&amp;</c> — which is what
/// <see cref="At"/> reproduces.
/// </summary>
public static class FuzzySearch
{
    /// <summary>Fuse's cap on one Bitap pattern; a longer pattern is searched in chunks of this size.</summary>
    public const int MaxPatternLength = 32;

    /// <summary>JavaScript's <c>Number.EPSILON</c> — the 2^-52 machine epsilon, not .NET's smallest subnormal.</summary>
    public const double JsEpsilon = 2.220446049250313E-16;

    /// <summary>Fuse's floor on a matched chunk's score.</summary>
    private const double MinScore = 0.001;

    /// <summary>JavaScript's <c>Math.round</c>: half up, where .NET's default rounds half to even.</summary>
    internal static double JsRound(double value) => Math.Floor(value + 0.5);

    /// <summary>
    /// The field-length norm: <c>round(1000 / tokens^(0.5 * weight)) / 1000</c>,
    /// where <c>tokens</c> is one plus the number of runs of spaces. Fuse caches
    /// this per token count; the cache is passed in so one index shares it.
    /// </summary>
    internal static double FieldNorm(string value, double fieldNormWeight, Dictionary<int, double> cache)
    {
        var tokens = 1;
        var inSpace = false;
        foreach (var ch in value)
        {
            if (ch == ' ')
            {
                if (!inSpace)
                {
                    tokens++;
                    inSpace = true;
                }
            }
            else
            {
                inSpace = false;
            }
        }

        if (cache.TryGetValue(tokens, out var cached))
        {
            return cached;
        }

        var norm = JsRound(1000d / Math.Pow(tokens, 0.5 * fieldNormWeight)) / 1000d;
        cache[tokens] = norm;
        return norm;
    }

    /// <summary>A JavaScript array read: out of range, and never-assigned holes, both read as 0.</summary>
    private static int At(int[] array, int index) =>
        index >= 0 && index < array.Length ? array[index] : 0;

    /// <summary>Fuse's <c>createPatternAlphabet</c>: one bit per position, per character.</summary>
    internal static Dictionary<char, int> PatternAlphabet(string pattern)
    {
        var mask = new Dictionary<char, int>();
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            mask[ch] = (mask.TryGetValue(ch, out var existing) ? existing : 0) | (1 << (pattern.Length - i - 1));
        }

        return mask;
    }

    /// <summary>Whether one chunk matched, and at what score.</summary>
    internal readonly record struct BitapResult(bool IsMatch, double Score);

    /// <summary>
    /// Fuse's Bitap over one chunk. <paramref name="pattern"/> is at most
    /// <see cref="MaxPatternLength"/> characters.
    /// </summary>
    internal static BitapResult Bitap(
        string text,
        string pattern,
        IReadOnlyDictionary<char, int> alphabet,
        int location,
        int distance,
        double threshold,
        bool findAllMatches,
        bool ignoreLocation)
    {
        var patternLength = pattern.Length;
        var textLength = text.Length;
        var expectedLocation = Math.Max(0, Math.Min(location, textLength));
        var currentThreshold = threshold;
        var bestLocation = expectedLocation;

        double ComputeScore(int errors, int at)
        {
            var accuracy = (double)errors / patternLength;
            if (ignoreLocation)
            {
                return accuracy;
            }

            var proximity = Math.Abs(expectedLocation - at);
            if (distance != 0)
            {
                return accuracy + ((double)proximity / distance);
            }

            return proximity != 0 ? 1d : accuracy;
        }

        // Exact occurrences first: each one lowers the threshold the fuzzy pass must beat.
        var index = IndexOf(text, pattern, bestLocation);
        while (index > -1)
        {
            currentThreshold = Math.Min(ComputeScore(0, index), currentThreshold);
            bestLocation = index + patternLength;
            index = IndexOf(text, pattern, bestLocation);
        }

        bestLocation = -1;
        var lastBitArray = Array.Empty<int>();
        var finalScore = 1d;
        var binMax = patternLength + textLength;
        var mask = 1 << (patternLength - 1);

        for (var errors = 0; errors < patternLength; errors++)
        {
            // How far from the expected location this error count may still reach.
            var binMin = 0;
            var binMid = binMax;
            while (binMin < binMid)
            {
                if (ComputeScore(errors, expectedLocation + binMid) <= currentThreshold)
                {
                    binMin = binMid;
                }
                else
                {
                    binMax = binMid;
                }

                binMid = (int)Math.Floor(((double)(binMax - binMin) / 2) + binMin);
            }

            binMax = binMid;
            var start = Math.Max(1, expectedLocation - binMid + 1);
            var finish = findAllMatches ? textLength : Math.Min(expectedLocation + binMid, textLength) + patternLength;

            var bitArray = new int[finish + 2];
            bitArray[finish + 1] = (1 << errors) - 1;

            for (var j = finish; j >= start; j--)
            {
                var currentLocation = j - 1;
                var charMatch = currentLocation < textLength && alphabet.TryGetValue(text[currentLocation], out var bits)
                    ? bits
                    : 0;

                bitArray[j] = ((At(bitArray, j + 1) << 1) | 1) & charMatch;
                if (errors != 0)
                {
                    bitArray[j] |= ((At(lastBitArray, j + 1) | At(lastBitArray, j)) << 1) | 1 | At(lastBitArray, j + 1);
                }

                if ((bitArray[j] & mask) != 0)
                {
                    finalScore = ComputeScore(errors, currentLocation);
                    if (finalScore <= currentThreshold)
                    {
                        currentThreshold = finalScore;
                        bestLocation = currentLocation;
                        if (bestLocation <= expectedLocation)
                        {
                            break;
                        }

                        start = Math.Max(1, (2 * expectedLocation) - bestLocation);
                    }
                }
            }

            if (ComputeScore(errors + 1, expectedLocation) > currentThreshold)
            {
                break;
            }

            lastBitArray = bitArray;
        }

        return new BitapResult(bestLocation >= 0, Math.Max(MinScore, finalScore));
    }

    /// <summary><c>String.prototype.indexOf</c>: a start index past the end is -1, not a throw.</summary>
    private static int IndexOf(string text, string pattern, int start) =>
        start > text.Length ? -1 : text.IndexOf(pattern, start, StringComparison.Ordinal);

    /// <summary>
    /// Fuse's <c>BitapSearch</c>: an exact-equality shortcut, then the pattern
    /// split into 32-character chunks whose scores are averaged.
    /// </summary>
    internal sealed class PatternSearcher
    {
        private readonly record struct Chunk(string Pattern, Dictionary<char, int> Alphabet, int StartIndex);

        private readonly string _pattern;
        private readonly List<Chunk> _chunks = [];
        private readonly int _location;
        private readonly int _distance;
        private readonly double _threshold;
        private readonly bool _findAllMatches;
        private readonly bool _ignoreLocation;

        public PatternSearcher(
            string pattern,
            int location,
            int distance,
            double threshold,
            bool findAllMatches,
            bool ignoreLocation)
        {
            _pattern = pattern.ToLowerInvariant();
            _location = location;
            _distance = distance;
            _threshold = threshold;
            _findAllMatches = findAllMatches;
            _ignoreLocation = ignoreLocation;

            if (_pattern.Length == 0)
            {
                return;
            }

            if (_pattern.Length > MaxPatternLength)
            {
                var i = 0;
                var remainder = _pattern.Length % MaxPatternLength;
                var whole = _pattern.Length - remainder;
                while (i < whole)
                {
                    Add(_pattern.Substring(i, MaxPatternLength), i);
                    i += MaxPatternLength;
                }

                if (remainder != 0)
                {
                    var start = _pattern.Length - MaxPatternLength;
                    Add(_pattern[start..], start);
                }
            }
            else
            {
                Add(_pattern, 0);
            }

            void Add(string chunk, int startIndex) =>
                _chunks.Add(new Chunk(chunk, PatternAlphabet(chunk), startIndex));
        }

        public BitapResult SearchIn(string text)
        {
            text = text.ToLowerInvariant();
            if (string.Equals(_pattern, text, StringComparison.Ordinal))
            {
                return new BitapResult(true, 0);
            }

            if (_chunks.Count == 0)
            {
                return new BitapResult(false, 1);
            }

            var total = 0d;
            var matched = false;
            foreach (var chunk in _chunks)
            {
                var result = Bitap(
                    text,
                    chunk.Pattern,
                    chunk.Alphabet,
                    _location + chunk.StartIndex,
                    _distance,
                    _threshold,
                    _findAllMatches,
                    _ignoreLocation);
                if (result.IsMatch)
                {
                    matched = true;
                }

                total += result.Score;
            }

            return new BitapResult(matched, matched ? total / _chunks.Count : 1d);
        }
    }
}

/// <summary>The options the reference passes Fuse; every default here is Fuse's own.</summary>
public sealed record FuzzyOptions
{
    public int Location { get; init; }

    public int Distance { get; init; } = 100;

    public double Threshold { get; init; } = 0.6;

    public bool FindAllMatches { get; init; }

    public bool IgnoreLocation { get; init; }

    public bool IgnoreFieldNorm { get; init; }

    public double FieldNormWeight { get; init; } = 1;
}

/// <summary>
/// Fuse's object-list index and search. Values come from
/// <c>getValues(item, keyName)</c>, which returns however many strings the
/// field holds — one for a scalar, several for an array — which is Fuse's own
/// handling: every non-blank element is indexed separately and contributes its
/// own factor to the combined score.
/// </summary>
public sealed class FuzzyIndex<T>
{
    private sealed record IndexedValue(string Value, double Norm);

    private sealed record Record(T Item, int Index, Dictionary<string, List<IndexedValue>> Fields);

    private readonly List<Record> _records = [];
    private readonly List<FuzzyKey> _keys;
    private readonly FuzzyOptions _options;

    public FuzzyIndex(
        IReadOnlyList<T> documents,
        IReadOnlyList<FuzzyKey> keys,
        Func<T, string, IReadOnlyList<string>> getValues,
        FuzzyOptions options)
    {
        _options = options;

        // Deliberately NOT normalized. Fuse's KeyStore does divide the declared
        // weights by their sum, but an object-list search scores against the
        // *index's* keys, which are the raw configs — the KeyStore is only
        // consulted on other paths. Normalizing here divides every exponent by
        // the weight total (13.5 for this menu), which leaves the ordering
        // intact and every score wrong; the recorded reference scores are what
        // caught it.
        _keys = [.. keys];

        var normCache = new Dictionary<int, double>();
        for (var i = 0; i < documents.Count; i++)
        {
            var fields = new Dictionary<string, List<IndexedValue>>(StringComparer.Ordinal);
            foreach (var key in _keys)
            {
                var indexed = new List<IndexedValue>();
                foreach (var value in getValues(documents[i], key.Name))
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    indexed.Add(new IndexedValue(value, FuzzySearch.FieldNorm(value, options.FieldNormWeight, normCache)));
                }

                fields[key.Name] = indexed;
            }

            _records.Add(new Record(documents[i], i, fields));
        }
    }

    /// <summary>
    /// Every document with at least one matching field, scored by Fuse's
    /// product rule and sorted by score ascending, ties broken by input order.
    /// </summary>
    public IReadOnlyList<FuzzyResult<T>> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. _records.Select(record => new FuzzyResult<T>(record.Item, record.Index, 0, []))];
        }

        var searcher = new FuzzySearch.PatternSearcher(
            query,
            _options.Location,
            _options.Distance,
            _options.Threshold,
            _options.FindAllMatches,
            _options.IgnoreLocation);

        var results = new List<FuzzyResult<T>>();
        foreach (var record in _records)
        {
            var matches = new List<FuzzyMatch>();
            var score = 1d;
            foreach (var key in _keys)
            {
                foreach (var value in record.Fields[key.Name])
                {
                    var hit = searcher.SearchIn(value.Value);
                    if (!hit.IsMatch)
                    {
                        continue;
                    }

                    matches.Add(new FuzzyMatch(key.Name, value.Value, hit.Score));
                    var factor = hit.Score == 0 ? FuzzySearch.JsEpsilon : hit.Score;
                    score *= Math.Pow(factor, key.Weight * (_options.IgnoreFieldNorm ? 1 : value.Norm));
                }
            }

            if (matches.Count > 0)
            {
                results.Add(new FuzzyResult<T>(record.Item, record.Index, score, matches));
            }
        }

        return [.. results.OrderBy(static result => result.Score).ThenBy(static result => result.Index)];
    }
}
