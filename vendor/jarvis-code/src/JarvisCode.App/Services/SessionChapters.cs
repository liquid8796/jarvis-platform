namespace JarvisCode.App.Services;

/// <summary>
/// One chapter mark_chapter recorded: the divider drawn in the transcript and
/// the row shown in the floating table of contents.
/// </summary>
public sealed record SessionChapter(string Title, string? Summary, DateTimeOffset MarkedAt)
{
    /// <summary>
    /// The index of the transcript item the divider sits above, so clicking the
    /// table of contents can scroll back to where the chapter began.
    /// </summary>
    public int TranscriptIndex { get; init; }
}
