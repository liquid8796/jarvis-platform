namespace JarvisCode.Core.Models;

/// <summary>A single conversation turn: an ordered list of content blocks with an author.</summary>
public sealed record ChatMessage(Role Role, IReadOnlyList<ContentBlock> Content)
{
    /// <summary>SDK identity for a user turn; provider adapters do not put this local bookkeeping on the wire.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SdkUserMessageId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SdkCheckpointTurnNumber { get; init; }
    public static ChatMessage FromUserText(string text) =>
        new(Role.User, [new TextBlock(text)]);

    public static ChatMessage FromToolResults(IEnumerable<ToolResultBlock> results)
    {
        // Tool-injected follow-up text (a skill's loaded instructions) rides the
        // same user turn as its own text blocks, after every tool_result — the
        // Anthropic wire requires results first, and the other adapters forward
        // the text to the user turn that follows the tool messages.
        var list = results.ToList();
        var blocks = new List<ContentBlock>(list);
        blocks.AddRange(list
            .Where(r => !string.IsNullOrEmpty(r.FollowUpText))
            .Select(r => (ContentBlock)new TextBlock(r.FollowUpText!)));
        return new(Role.User, blocks);
    }

    /// <summary>
    /// Number of leading content blocks the harness authored rather than the user
    /// (system-reminders, hook output). Provenance bookkeeping for the client that
    /// composed the message; nothing in the engine reads it and no provider is
    /// shown it. Meaningful only while <see cref="HarnessSectionHash"/> still
    /// matches the content.
    /// </summary>
    public int HarnessNoteCount { get; init; }

    /// <summary>Trailing harness-authored blocks, counted the same way.</summary>
    public int HarnessTailCount { get; init; }

    /// <summary>
    /// Fingerprint binding the harness section counts to the exact content they
    /// were computed against, so a rewrite invalidates the counts rather than
    /// leaving them pointing at moved bytes. Null on messages composed before the
    /// counts existed, which read as unstamped.
    /// </summary>
    public string? HarnessSectionHash { get; init; }

    /// <summary>
    /// Marks a message the harness composed as a mid-conversation *system* turn
    /// rather than a user one — what the reference CLI sends its agent-type and
    /// skill listings as, under the <c>mid-conversation-system-2026-04-07</c>
    /// beta (measured: its request puts them in a trailing message whose role is
    /// literally "system"). Additive and opt-in: an adapter that does not
    /// understand the distinction keeps serializing the message as the user turn
    /// it already was, so no provider changes behaviour by ignoring it.
    /// </summary>
    public bool HarnessSystemTurn { get; init; }

    /// <summary>
    /// Marks a user turn the harness submitted rather than the person — the
    /// reference's <c>isMeta</c>. It sends the text as an ordinary user message
    /// on the wire, so the model reads it as an instruction, while everything
    /// that asks "did the user say something" answers no.
    ///
    /// Measured across 817 of the reference's own transcripts: it marks a skill
    /// body it injected, its local-command caveat, an image placeholder, and
    /// "Continue from where you left off." — every one a turn the harness wrote
    /// on the user's behalf. Its desktop transcript renders such a message as an
    /// event rather than a user bubble, and its "is this a real user message"
    /// predicates exclude it (<c>type==="user" &amp;&amp; !isSynthetic &amp;&amp; !isMeta</c>).
    /// </summary>
    public bool IsMeta { get; init; }

    /// <summary>Concatenated text of all text blocks; empty when the message has none.</summary>
    public string GetText() =>
        string.Concat(Content.OfType<TextBlock>().Select(b => b.Text));

    /// <summary>Text for display/export, including citations attached to each claim.</summary>
    public string GetDisplayText() =>
        string.Concat(Content.OfType<TextBlock>().Select(CitationMarkdown.Format));

    public IEnumerable<ToolCallBlock> ToolCalls => Content.OfType<ToolCallBlock>();
}
