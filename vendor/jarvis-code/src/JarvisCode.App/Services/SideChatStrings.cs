namespace JarvisCode.App.Services;

/// <summary>One line of a side chat.</summary>
/// <param name="FromUser">Whether the user wrote it.</param>
/// <param name="Text">What it says.</param>
public sealed record SideChatLine(bool FromUser, string Text);

/// <summary>
/// What the Chat surface's side-chat panel says, ported from the reference's own
/// panel (<c>ca2ef848d-D8BWZk64.js</c>, its <c>ch</c> header, <c>lh</c> body and the
/// composer beside them). The panel is read-only over the conversation, which is
/// what <see cref="JarvisCode.Core.Agent.SideChat"/> already models: it is given an
/// excerpt of the main transcript, runs without tools, and its replies never land in
/// the conversation it is reading.
/// </summary>
public static class SideChatStrings
{
    /// <summary>The composer's placeholder.</summary>
    public const string Placeholder = "Ask a quick question…";        // SUYgJ2XuFk

    // One literal, however long: the strings manifest looks for the sentence in the
    // file, and a concatenation would hide it.
    /// <summary>The empty state, which says exactly what the panel is for.</summary>
    public const string EmptyState = "Ask on the side without touching the main chat. Jarvis sees the full context, and nothing here is added to it."; // ctWkktQM0M

    public const string CopyAnswer = "Copy answer";                    // 9Kp4ZgNuBk
    public const string BranchToNewChat = "Branch to new chat";        // 9904KlI+jU
    public const string ReadOnly = "read-only";                        // GX8+SGUXDd
    public const string Thinking = "Thinking…";                        // P1QHV0AhYD
    public const string Send = "Send";                                 // 9WRlF4R2gm

    /// <summary>The header line: how much of the main chat the side chat can see.</summary>
    public static string SeesMessages(int count) =>
        // 3XI+xKIxnt
        count == 1 ? "Sees 1 message from main chat" : $"Sees {count} messages from main chat";

    /// <summary>
    /// How far from the bottom the reader may be before new lines stop pulling the
    /// view down — the reference's own 48 pixels.
    /// </summary>
    public const double PinThreshold = 48;

    /// <summary>The transcript a branch takes with it: everything through this answer.</summary>
    public static IReadOnlyList<SideChatLine> Branch(IReadOnlyList<SideChatLine> lines, int index) =>
        [.. lines.Take(Math.Clamp(index + 1, 0, lines.Count))];
}
