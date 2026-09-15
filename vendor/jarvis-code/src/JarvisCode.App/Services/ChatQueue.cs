using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The composer's queued-message stack, ported from the reference desktop's own
/// (<c>c11959232-DM8o5ho4.js</c>: its <c>bT</c> preview builder, its collapsed and
/// expanded headers, and the <c>wT</c> row's action menu) together with the Chat
/// surface's discard confirmation (<c>ca2ef848d-D8BWZk64.js</c>, its <c>Gf</c>).
/// </summary>
public static partial class ChatQueue
{
    /// <summary>The header, collapsed or expanded: the count and nothing else.</summary>
    public static string CountLabel(int count) =>
        // ezsVmYezso
        count == 1 ? "1 message queued" : $"{count} messages queued";

    /// <summary>What the collapsed stack shows beside the count.</summary>
    public static string NextLabel(string preview) => $"Next: {preview}";   // BR1hQiWywY

    /// <summary>
    /// The sentence the reference gives an assistive reader when a message is
    /// queued. It is not drawn on screen there — the count and the preview are.
    /// </summary>
    public static string QueuedStatus(int count) =>
        // Fmpj1gVq4t
        count == 1
            ? "1 message queued. Will send after the current response."
            : $"{count} messages queued. Will send after the current response.";

    public const string QueuedMessages = "Queued messages";                 // wytZxjTMDw
    public const string QueuedMessageActions = "Queued message actions";    // BTu5Yx926X
    public const string CollapseQueuedMessages = "Collapse queued messages"; // KZVLRZPNPH
    public const string ReorderQueuedMessage = "Reorder queued message";    // 9kugsu9NjU
    public const string EditInComposer = "Edit in composer";                // lWwW3BF7Wa
    public const string SendNow = "Send now";                               // drJdjjprFp
    public const string RemoveFromQueue = "Remove from queue";              // pu9H1Nruwc
    public const string ClearAll = "Clear all";                             // QW+Q5NHmeF
    public const string DiscardQueuedMessage = "Discard queued message";    // mash/vpxVG
    public const string DiscardTitle = "Discard queued message?";           // MHHIKnYir+
    public const string DiscardBody =
        "This message will be removed from the queue and won’t be sent.";   // DGQ+GomQem
    public const string Discard = "Discard";                                // nmpevlUATU
    public const string Image = "[Image]";                                  // nKQRbs00nP

    /// <summary>
    /// One queued message as one line: the text, then a marker per attachment — a
    /// file as <c>@name</c> and an image as <c>[Image]</c> — with runs of
    /// whitespace collapsed. A message with nothing to show reads as an ellipsis,
    /// which is the reference's own fallback rather than an empty row.
    /// </summary>
    public static string Preview(string text, IEnumerable<(bool IsImage, string Name)> attachments)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(text);
        }

        foreach (var (isImage, name) in attachments)
        {
            parts.Add(isImage ? Image : "@" + name);
        }

        var line = Whitespace().Replace(string.Join(' ', parts), " ").Trim();
        return line.Length > 0 ? line : "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
