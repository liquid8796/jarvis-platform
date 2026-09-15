namespace JarvisCode.App.Services;

/// <summary>One word of a revealed paragraph, with the space that followed it.</summary>
/// <param name="Text">The word itself, with the bold markers removed.</param>
/// <param name="IsBold">Whether it fell inside a <c>**…**</c> span.</param>
/// <param name="TrailingSpace">The whitespace that followed it, kept verbatim.</param>
public readonly record struct FadeInWord(string Text, bool IsBold, string TrailingSpace);

/// <summary>
/// How the onboarding paragraphs arrive, ported from the reference desktop's own
/// reveal component (the whole of <c>c4b7ce3b5-DNSas4Nx.js</c> and its stylesheet
/// <c>c4b7ce3b5-B43ptWJb.css</c>, desktop 1.44121.2.0).
///
/// It is not a typewriter: the paragraph is split into words and each word fades
/// from 0 to 1 over <see cref="WordFadeMs"/> at its own delay, and the schedule
/// <em>decelerates</em> — the gap is <c>max(50, 150 / max(1, remaining / 2))</c>, so
/// the opening words land on the 50ms floor and the last few take 150ms each. The
/// component reports completion <see cref="TailMs"/> after the final word starts,
/// which is what lets the reference show its next paragraph only once this one is
/// finished.
/// </summary>
public static class FadeInWords
{
    /// <summary>Its <c>s</c>: the pause after the last word before completion is reported.</summary>
    public const double TailMs = 400;

    /// <summary>Its <c>c</c>: the numerator of the per-word gap.</summary>
    public const double BaseGapMs = 150;

    /// <summary>Its <c>l</c>: the floor no gap goes below.</summary>
    public const double MinimumGapMs = 50;

    /// <summary>How long one word takes to fade in, from its own stylesheet.</summary>
    public const double WordFadeMs = 400;

    /// <summary>
    /// Its <c>u</c>: splits a paragraph into words, consuming <c>**</c> as a bold
    /// toggle. Whitespace attaches to the word before it, and whitespace with no word
    /// before it merges into the previous word's trailing run rather than becoming a
    /// word of its own.
    /// </summary>
    public static IReadOnlyList<FadeInWord> Split(string text)
    {
        List<FadeInWord> words = [];
        var bold = false;
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                bold = !bold;
                i += 2;
                continue;
            }

            var wordStart = i;
            while (i < text.Length
                   && !char.IsWhiteSpace(text[i])
                   && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*'))
            {
                i++;
            }

            var word = text[wordStart..i];

            var spaceStart = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var space = text[spaceStart..i];

            if (word.Length > 0)
            {
                words.Add(new FadeInWord(word, bold, space));
            }
            else if (space.Length > 0 && words.Count > 0)
            {
                var last = words[^1];
                words[^1] = last with { TrailingSpace = last.TrailingSpace + space };
            }
        }

        return words;
    }

    /// <summary>
    /// Its <c>d</c>: the cumulative start delay of each word. The gap for a word is
    /// computed from how many words are still to come, including itself.
    /// </summary>
    public static IReadOnlyList<double> Delays(int wordCount)
    {
        var delays = new double[Math.Max(0, wordCount)];
        var at = 0.0;
        for (var index = 0; index < delays.Length; index++)
        {
            delays[index] = at;
            var remaining = wordCount - index;
            at += Math.Max(MinimumGapMs, BaseGapMs / Math.Max(1, remaining / 2.0));
        }

        return delays;
    }

    /// <summary>
    /// The schedule the reference builds: each word's delay, rounded and divided by
    /// the speed multiplier, or all zeros when the reveal is instant.
    /// </summary>
    public static IReadOnlyList<double> Schedule(int wordCount, bool instant, double speedMultiplier = 1)
    {
        if (instant)
        {
            return new double[Math.Max(0, wordCount)];
        }

        var divisor = Math.Max(1, speedMultiplier);
        return [.. Delays(wordCount).Select(d => Math.Round(d / divisor, MidpointRounding.AwayFromZero))];
    }

    /// <summary>
    /// When completion is reported: the last word's delay plus the tail, or zero for
    /// an instant reveal. An empty paragraph still reports after the tail, as its
    /// <c>(g[g.length-1] ?? 0)</c> does.
    /// </summary>
    public static double CompleteAfterMs(int wordCount, bool instant, double speedMultiplier = 1)
    {
        if (instant)
        {
            return 0;
        }

        var schedule = Schedule(wordCount, instant: false, speedMultiplier);
        var last = schedule.Count > 0 ? schedule[^1] : 0;
        return last + Math.Round(TailMs / Math.Max(1, speedMultiplier), MidpointRounding.AwayFromZero);
    }
}
