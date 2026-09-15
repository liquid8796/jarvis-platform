namespace JarvisCode.App.Controls;

/// <summary>Chat/Code stream ceilings measured from the installed desktop.</summary>
internal static class StreamingMarkdown
{
    internal static string HoldBack(string markdown, bool protectInlineConstructs = true) =>
        markdown[..ReferenceStreamCeiling.Ceiling(markdown, protectInlineConstructs)];
}

/// <summary>
/// Which way a block of text reads. The reference stamps <c>dir</c> on every
/// heading, paragraph, list and table from the first 80 characters of its text,
/// so one right-to-left paragraph lays out correctly inside a transcript that
/// is otherwise left to right.
/// </summary>
internal static class TextDirection
{
    /// <summary>The reference samples this many characters before deciding.</summary>
    private const int SampleLength = 80;

    internal static bool IsRightToLeft(IEnumerable<Core.Markdown.InlineRun> runs)
    {
        int seen = 0;
        foreach (var run in runs)
        {
            foreach (var c in run.Text)
            {
                if (seen++ >= SampleLength)
                {
                    return false;
                }

                if (IsStrongRightToLeft(c))
                {
                    return true;
                }

                if (char.IsLetter(c))
                {
                    return false; // A strong left-to-right letter settles it.
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Hebrew through N'Ko (U+0590-U+08FF) plus the Hebrew and Arabic
    /// presentation forms. Written as code points: the characters themselves
    /// do not survive a round trip through every editor and shell.
    /// </summary>
    private static bool IsStrongRightToLeft(char c)
    {
        int point = c;
        return point is >= 0x0590 and <= 0x08FF   // Hebrew through N'Ko
            or >= 0xFB1D and <= 0xFDFF            // Hebrew and Arabic presentation forms A
            or >= 0xFE70 and <= 0xFEFF;           // Arabic presentation forms B
    }
}
