using System.Text;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Drops the escape sequences a program writes for the terminal it believes it has.
///
/// A background run is captured with its stdout intact, and tools that colour their output — vite,
/// npm, tsc — write it whether or not anything is there to render it. Shown in a plain text box the
/// sequences surface as their own letters ("[32m[1mVITE"), because the escape that starts them
/// draws as nothing at all. Only the escape marks them: text that merely contains "[32m" is
/// somebody's data and is left alone.
/// </summary>
public static class AnsiText
{
    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    /// <summary>Lowest and highest byte of a CSI parameter or intermediate, before its final byte.</summary>
    private const char CsiBodyFirst = '\u0020';
    private const char CsiBodyLast = '\u003f';

    /// <summary>Intermediates of a plain escape, as in "ESC ( B".</summary>
    private const char IntermediateFirst = '\u0020';
    private const char IntermediateLast = '\u002f';

    public static string Strip(string text)
    {
        if (text.IndexOf(Escape) < 0)
        {
            return text;
        }

        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == Escape)
            {
                i = EndOfSequence(text, i);
                continue;
            }

            clean.Append(text[i]);
        }

        return clean.ToString();
    }

    /// <summary>
    /// The index of the sequence's last character, for the caller to step past — or the length of
    /// the text when the sequence is cut short, which is what a half-written line looks like.
    /// </summary>
    private static int EndOfSequence(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return text.Length;
        }

        return text[start + 1] switch
        {
            // CSI: parameters and intermediates, then one final byte. This is the colour.
            '[' => AfterRun(text, start + 2, CsiBodyFirst, CsiBodyLast),
            // OSC and its siblings carry a string — a window title, a hyperlink — and run until a
            // bell or a string terminator rather than to a single final byte.
            ']' or 'P' or 'X' or '^' or '_' => EndOfString(text, start + 2),
            _ => AfterRun(text, start + 1, IntermediateFirst, IntermediateLast),
        };
    }

    private static int AfterRun(string text, int from, char first, char last)
    {
        var i = from;
        while (i < text.Length && text[i] >= first && text[i] <= last)
        {
            i++;
        }

        return i < text.Length ? i : text.Length;
    }

    private static int EndOfString(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == Bell)
            {
                return i;
            }

            if (text[i] == Escape && i + 1 < text.Length && text[i + 1] == '\\')
            {
                return i + 1;
            }
        }

        return text.Length;
    }
}
