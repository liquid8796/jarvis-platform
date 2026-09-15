namespace JarvisCode.App.Services;

/// <summary>
/// How a message promoted to a chapter is titled, ported from the reference's
/// own <c>kT</c> over its <c>DT = 40</c> (desktop 1.46388.2.0): the first line
/// with anything on it, trimmed, cut at forty characters with an ellipsis.
///
/// <para>
/// The seed it is given is the turn's own text, which the reference builds by
/// joining every text item of the turn with two spaces - so a turn that answered
/// in several runs still reads as one sentence before it is cut.
/// </para>
/// </summary>
public static class ChapterTitles
{
    /// <summary>The reference's <c>DT</c>: how much of the seed becomes a title.</summary>
    public const int MaxLength = 40;

    /// <summary>The title a pinned turn gets, from the text that turn produced.</summary>
    public static string FromSeed(string? seed)
    {
        if (string.IsNullOrEmpty(seed))
        {
            return "";
        }

        // The first line with something on it, or the whole seed when there is
        // none - which is the reference's own `?? e.trim()` fallback.
        var line = seed.Split('\n').FirstOrDefault(static l => l.Trim().Length > 0) ?? seed;
        line = line.Trim();
        return line.Length > MaxLength ? line[..MaxLength] + "…" : line;
    }

    /// <summary>
    /// The seed itself: every text run of a turn joined by two spaces, which is
    /// the reference's <c>LY</c>.
    /// </summary>
    public static string Seed(IEnumerable<string> texts) => string.Join("  ", texts);
}
