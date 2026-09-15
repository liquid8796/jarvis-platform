using JarvisCode.Core.Models;

namespace JarvisCode.Providers;

/// <summary>
/// Folds <see cref="ChatMessage.HarnessSystemTurn"/> messages back into the user
/// turn they accompany.
///
/// Only the Anthropic wire has a mid-conversation <c>system</c> role (the
/// reference sends its agent-type and skill listings that way, under the
/// <c>mid-conversation-system-2026-04-07</c> beta). Every other wire either has
/// no such role or requires user/assistant turns to alternate strictly —
/// Gemini's <c>contents</c> being the strict case — so leaving the extra message
/// standing there would turn a listing into a second consecutive user turn.
///
/// Folding reproduces exactly what those adapters sent before the role existed:
/// the listing as further blocks on the neighbouring user message.
/// </summary>
public static class HarnessSystemTurns
{
    /// <summary>
    /// The reference's wrapper for harness text that rides a user message rather
    /// than a system turn: a classic-prompt model on the Anthropic wire receives
    /// the same <c>&lt;total_tokens&gt;</c> block a lean model gets as a system
    /// message, wrapped in these tags after the tool results (measured on
    /// claude-opus-4-5, CLI 2.1.257).
    /// </summary>
    public static string WrapAsReminder(string text) => $"<system-reminder>\n{text}\n</system-reminder>";

    /// <param name="wrap">
    /// Applied to each folded text block; null leaves the text as it was, which
    /// is what the non-Anthropic wires have always sent.
    /// </param>
    public static IReadOnlyList<ChatMessage> Fold(
        IReadOnlyList<ChatMessage> messages, Func<string, string>? wrap = null)
    {
        var anyToFold = false;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].HarnessSystemTurn)
            {
                anyToFold = true;
                break;
            }
        }

        if (!anyToFold)
        {
            return messages;
        }

        var folded = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (!message.HarnessSystemTurn)
            {
                folded.Add(message);
                continue;
            }

            // Join the message before it when that is a user turn; otherwise it
            // stands on its own as the plain user message it already is, which
            // is what an adapter that ignored the flag would have sent.
            IReadOnlyList<ContentBlock> content = wrap is null
                ? message.Content
                : [.. message.Content.Select(block =>
                    block is TextBlock text ? new TextBlock(wrap(text.Text)) : block)];
            if (folded.Count > 0 && folded[^1] is { Role: Role.User, HarnessSystemTurn: false } previous)
            {
                folded[^1] = previous with { Content = [.. previous.Content, .. content] };
            }
            else
            {
                folded.Add(message with { HarnessSystemTurn = false, Content = content });
            }
        }

        return folded;
    }
}
