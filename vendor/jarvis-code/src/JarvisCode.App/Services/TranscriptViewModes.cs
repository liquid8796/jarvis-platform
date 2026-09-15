using JarvisCode.App.Views.Panels;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference app's transcript-view semantics (ion-dist 1.40609): the mode
/// order Normal → Thinking → Verbose → Summary, thinking blocks visible in
/// Thinking and Verbose ("transcriptModeShowsThinking"), the Ctrl+O cycle that
/// skips Thinking while the session has no thinking yet, and the per-session
/// sticky map capped at the reference's 100 rows.
/// </summary>
public static class TranscriptViewModes
{
    /// <summary>The reference's mode list, in cycle order.</summary>
    public static readonly IReadOnlyList<TranscriptViewMode> Order =
    [
        TranscriptViewMode.Normal,
        TranscriptViewMode.Thinking,
        TranscriptViewMode.Verbose,
        TranscriptViewMode.Summary,
    ];

    /// <summary>The reference keeps the newest 100 per-session modes.</summary>
    public const int MaxStampedSessions = 100;

    /// <summary>Thinking blocks (and their recaps) are visible in Thinking and Verbose.</summary>
    public static bool ShowsThinking(TranscriptViewMode mode) =>
        mode is TranscriptViewMode.Thinking or TranscriptViewMode.Verbose;

    /// <summary>
    /// The Ctrl+O step: the next mode in order, wrapping, with Thinking skipped
    /// while the session has none. A current mode that is itself unavailable
    /// lands on Normal, exactly like the reference's indexOf(-1) + 1.
    /// </summary>
    public static TranscriptViewMode Next(TranscriptViewMode current, bool sessionHasThinking)
    {
        var modes = Order.Where(m => m != TranscriptViewMode.Thinking || sessionHasThinking).ToList();
        return modes[(modes.IndexOf(current) + 1) % modes.Count];
    }

    /// <summary>The stored spelling ("normal" | "thinking" | "verbose" | "summary").</summary>
    public static string Wire(TranscriptViewMode mode) => mode switch
    {
        TranscriptViewMode.Thinking => "thinking",
        TranscriptViewMode.Verbose => "verbose",
        TranscriptViewMode.Summary => "summary",
        _ => "normal",
    };

    /// <summary>Reads a stored spelling back; anything unknown is Normal, the reference default.</summary>
    public static TranscriptViewMode Parse(string? stored) => stored switch
    {
        "thinking" => TranscriptViewMode.Thinking,
        "verbose" => TranscriptViewMode.Verbose,
        "summary" => TranscriptViewMode.Summary,
        _ => TranscriptViewMode.Normal,
    };

    /// <summary>
    /// Stamps sessionId → mode into the sticky map, moving the session to the
    /// newest slot and dropping the oldest rows past the cap. The map is rebuilt
    /// because Dictionary order is insertion order only until a removal reuses a
    /// slot, and the cap must evict the oldest row, not an arbitrary one.
    /// </summary>
    public static void Stamp(Dictionary<string, string> map, string sessionId, TranscriptViewMode mode)
    {
        var rows = map.Where(row => row.Key != sessionId).ToList();
        map.Clear();
        for (var i = Math.Max(0, rows.Count - (MaxStampedSessions - 1)); i < rows.Count; i++)
        {
            map[rows[i].Key] = rows[i].Value;
        }

        map[sessionId] = Wire(mode);
    }
}
