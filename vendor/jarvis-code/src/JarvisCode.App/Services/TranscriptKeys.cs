namespace JarvisCode.App.Services;

/// <summary>
/// What names a transcript row across a reopen. The reference keys a row by the
/// entry uuid its server assigned and stores that key in the viewport snapshot, so
/// coming back to a session finds the row the reader left off at and every height
/// already measured for the rows above it.
///
/// Nothing in this port's stored session carries a uuid, and the transcript is
/// rebuilt from the message list in the same order every time — so a row's position
/// in that list is its identity. A row a live turn produced keeps the throwaway key
/// <see cref="ViewModels.TranscriptItem"/> issues itself: it is new, so there is no
/// stored height for it to find.
/// </summary>
public static class TranscriptKeys
{
    /// <summary>
    /// The key for the <paramref name="ordinal"/>th row that message
    /// <paramref name="messageIndex"/> of the stored session produced.
    /// </summary>
    public static string ForHistory(int messageIndex, int ordinal) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"h{messageIndex}.{ordinal}");
}
