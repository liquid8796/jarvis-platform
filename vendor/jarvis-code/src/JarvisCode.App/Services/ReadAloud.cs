using System.Speech.Synthesis;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The Chat message action the reference calls "Read aloud", which becomes "Pause"
/// while it is speaking. The reference speaks through a cloud voice; this reads the
/// answer with the Windows synthesizer, which is what an offline app has.
///
/// The markdown is flattened first: a screen reader saying "asterisk asterisk" over
/// every bold run is worse than not offering the action at all.
/// </summary>
public sealed partial class ReadAloud : IDisposable
{
    public const string Read = "Read aloud";   // FiwV/oi580
    public const string Pause = "Pause";       // tFFMkFDBMO

    private SpeechSynthesizer? _synthesizer;

    /// <summary>Whether something is being read right now.</summary>
    public bool IsSpeaking { get; private set; }

    /// <summary>Raised when speech starts or stops, so a button can swap its label.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Starts reading, or pauses what is already being read.</summary>
    /// <returns>An error message when the synthesizer is unavailable, else null.</returns>
    public string? Toggle(string markdown)
    {
        if (IsSpeaking)
        {
            Stop();
            return null;
        }

        var text = Speakable(markdown);
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            if (_synthesizer is null)
            {
                _synthesizer = new SpeechSynthesizer();
                _synthesizer.SetOutputToDefaultAudioDevice();
                _synthesizer.SpeakCompleted += (_, _) =>
                {
                    IsSpeaking = false;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                };
            }

            _synthesizer.SpeakAsync(text);
            IsSpeaking = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException
                                       or System.IO.FileNotFoundException)
        {
            return "Speech is unavailable on this machine.";
        }
    }

    public void Stop()
    {
        if (_synthesizer is not null)
        {
            _synthesizer.SpeakAsyncCancelAll();
        }

        IsSpeaking = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _synthesizer?.Dispose();
        _synthesizer = null;
    }

    /// <summary>
    /// The answer as something worth hearing: fenced code goes (it reads as noise),
    /// links keep their text, and the marks that only mean something on screen —
    /// emphasis, headings, list bullets, table rules — are dropped.
    /// </summary>
    public static string Speakable(string markdown)
    {
        var text = Fences().Replace(markdown, " ");
        text = Images().Replace(text, "");
        text = Links().Replace(text, "$1");
        text = InlineCode().Replace(text, "$1");
        text = Emphasis().Replace(text, "");
        text = HeadingsAndBullets().Replace(text, "");
        text = TableRules().Replace(text, " ");
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"```[\s\S]*?```|~~~[\s\S]*?~~~")]
    private static partial Regex Fences();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Images();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Links();

    [GeneratedRegex("`([^`]*)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*|__|\*|_|~~")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?m)^\s{0,3}(#{1,6}\s+|[-*+]\s+|>\s?|\d+\.\s+)")]
    private static partial Regex HeadingsAndBullets();

    [GeneratedRegex(@"(?m)^\s*\|?[\s:|-]{3,}\|?\s*$")]
    private static partial Regex TableRules();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
