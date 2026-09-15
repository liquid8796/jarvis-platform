using JarvisCode.Core.Models;

namespace JarvisCode.App.Services;

/// <summary>
/// Writes the durations a turn measured onto the thinking blocks of the message it
/// just stored, so a session reopened later can still say how long a message
/// thought for instead of falling back to the reference's untimed header.
/// </summary>
public static class ThinkingDurations
{
    /// <summary>
    /// The message's content with each thinking block carrying its measured
    /// duration, or null when this message is not the one that produced
    /// <paramref name="thoughtText"/> — a turn stopped before its message was
    /// stored must not stamp its durations onto an older message.
    /// </summary>
    public static IReadOnlyList<ContentBlock>? Apply(
        IReadOnlyList<ContentBlock> content, IReadOnlyList<TimeSpan> spans, string thoughtText)
    {
        var stored = content.OfType<ThinkingBlock>().ToList();
        if (stored.Count == 0 || spans.Count == 0)
        {
            return null;
        }

        if (!string.Equals(
                string.Concat(stored.Select(block => block.Thinking)), thoughtText, StringComparison.Ordinal))
        {
            return null;
        }

        var updated = new List<ContentBlock>(content.Count);
        var seen = 0;
        foreach (var block in content)
        {
            if (block is not ThinkingBlock thinking)
            {
                updated.Add(block);
                continue;
            }

            // Runs and blocks line up one for one; when a provider splits them
            // differently, the remainder folds onto the last block so the header
            // still totals what was actually measured.
            var span = seen < spans.Count ? spans[seen] : TimeSpan.Zero;
            if (seen == stored.Count - 1 && spans.Count > stored.Count)
            {
                span = spans.Skip(seen).Aggregate(TimeSpan.Zero, static (total, next) => total + next);
            }

            updated.Add(thinking with { DurationSeconds = span.TotalSeconds });
            seen++;
        }

        return updated;
    }
}
