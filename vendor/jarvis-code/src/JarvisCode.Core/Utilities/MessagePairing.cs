using JarvisCode.Core.Models;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Last-resort repair of tool-call/result pairing before a request is sent (ported
/// from claw-code's sanitize_tool_message_pairing, extended both ways): orphaned
/// tool results — whose call is not in the nearest preceding assistant message —
/// are dropped, and dangling tool calls — left behind by an interrupted turn —
/// get synthetic error results, because providers reject both shapes with a 400.
/// Returns the original list when nothing needs fixing.
/// </summary>
public static class MessagePairing
{
    public const string InterruptedResult = "[Interrupted — the tool never ran. Re-issue the call if it is still needed.]";

    public static IReadOnlyList<ChatMessage> Sanitize(IReadOnlyList<ChatMessage> messages)
    {
        List<ChatMessage>? repaired = null;
        var pending = new Dictionary<string, string>(StringComparer.Ordinal); // id -> tool name

        void Emit(ChatMessage message)
        {
            repaired?.Add(message);
        }

        void StartRepair(int upTo)
        {
            if (repaired is null)
            {
                repaired = new List<ChatMessage>(messages.Count + 2);
                for (int j = 0; j < upTo; j++)
                    repaired.Add(messages[j]);
            }
        }

        void FlushPending(int index)
        {
            if (pending.Count == 0)
                return;
            StartRepair(index);
            repaired!.Add(ChatMessage.FromToolResults(
                pending.Select(entry => new ToolResultBlock(entry.Key, entry.Value, InterruptedResult, IsError: true))));
            pending.Clear();
        }

        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == Role.Assistant)
            {
                // A new assistant turn while calls are still unanswered means the
                // previous turn was interrupted — close it out first.
                FlushPending(i);
                pending.Clear();
                foreach (var call in message.ToolCalls)
                    pending[call.Id] = call.Name;
                Emit(message);
                continue;
            }

            List<ContentBlock>? filtered = null;
            for (int b = 0; b < message.Content.Count; b++)
            {
                if (message.Content[b] is not ToolResultBlock result)
                    continue;
                if (pending.Remove(result.ToolCallId))
                    continue;
                // Orphan: no matching call in the nearest preceding assistant message.
                StartRepair(i);
                filtered ??= [.. message.Content];
                filtered.Remove(result);
            }
            if (filtered is not null)
            {
                if (filtered.Count > 0)
                    Emit(message with { Content = filtered });
                // A message left empty by the repair is dropped entirely.
            }
            else
            {
                Emit(message);
            }
        }
        FlushPending(messages.Count);

        return repaired ?? messages;
    }
}
