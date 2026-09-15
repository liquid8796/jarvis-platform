using JarvisCode.Core.Models;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Keeps the conversation inside the model's context window by dropping the
/// oldest turns when the estimated size exceeds the budget. Tool-call/result
/// pairing is preserved because messages are removed whole, oldest first, and
/// a marker message tells the model that history was elided.
/// </summary>
public static class ContextTrimmer
{
    public const string TruncationMarker = "[Older conversation history was truncated to fit the context window.]";

    /// <summary>
    /// Returns the original list when it fits, otherwise a trimmed copy. The most
    /// recent messages are always kept, as is at least the final user message.
    /// </summary>
    public static IList<ChatMessage> Trim(IList<ChatMessage> messages, int maxContextTokens)
    {
        int budget = (int)(maxContextTokens * 0.75);
        if (TokenEstimator.Estimate(messages) <= budget)
            return messages;

        var kept = new LinkedList<ChatMessage>();
        int total = TokenEstimator.Estimate(TruncationMarker);
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            int cost = TokenEstimator.Estimate(messages[i]);
            if (total + cost > budget && kept.Count > 0)
                break;
            kept.AddFirst(messages[i]);
            total += cost;
        }

        // Never let the trimmed history start with a tool-result message whose
        // tool calls were dropped — providers reject orphaned results.
        while (kept.First is not null &&
               kept.First.Value.Role == Role.User &&
               kept.First.Value.Content.Any(b => b is ToolResultBlock))
        {
            kept.RemoveFirst();
        }
        if (kept.Count == 0)
            kept.AddFirst(messages[^1]);

        var result = new List<ChatMessage> { ChatMessage.FromUserText(TruncationMarker) };
        result.AddRange(kept);
        return result;
    }
}
