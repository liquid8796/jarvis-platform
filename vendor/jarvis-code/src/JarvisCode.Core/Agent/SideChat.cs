using System.Text;
using JarvisCode.Core.Models;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Side chat: a lightweight fork of the main session for a quick side question.
/// It sees an excerpt of the main transcript but its replies never land there.
/// Runs without tools, so a single model call answers each question.
/// </summary>
public static class SideChat
{
    public const int DefaultExcerptMaxChars = 8_000;
    public const int DefaultExcerptMaxMessages = 12;
    private const int ToolResultPreviewChars = 200;

    public static string BuildSystemPrompt(string workingDirectory, string contextExcerpt)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine(
            "You are running in a side chat — a lightweight fork of the user's main coding session. " +
            "The user has a quick side question; answer it directly and concisely. Nothing you say here " +
            "lands in the main conversation, and you have no tools — answer from the context below and " +
            "your own knowledge, and say so plainly when the context is not enough to be sure.");
        prompt.AppendLine();
        prompt.AppendLine($"Main session working directory: {workingDirectory}");
        if (contextExcerpt.Length > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("# Main session transcript (most recent part)");
            prompt.AppendLine(contextExcerpt);
        }
        return prompt.ToString();
    }

    /// <summary>
    /// Renders the tail of the main conversation as plain text: newest
    /// <paramref name="maxMessages"/> messages, trimmed from the front until the
    /// whole excerpt fits in <paramref name="maxChars"/>.
    /// </summary>
    public static string BuildContextExcerpt(
        IReadOnlyList<ChatMessage> messages,
        int maxChars = DefaultExcerptMaxChars,
        int maxMessages = DefaultExcerptMaxMessages)
    {
        var rendered = new List<string>();
        foreach (var message in messages.Skip(Math.Max(0, messages.Count - maxMessages)))
        {
            var text = RenderMessage(message);
            if (text.Length > 0)
                rendered.Add(text);
        }

        // Keep the most recent messages when over budget.
        int start = rendered.Count;
        int total = 0;
        while (start > 0 && total + rendered[start - 1].Length + 1 <= maxChars)
        {
            total += rendered[start - 1].Length + 1;
            start--;
        }
        return string.Join("\n", rendered.Skip(start));
    }

    private static string RenderMessage(ChatMessage message)
    {
        var label = message.Role == Role.User ? "User" : "Assistant";
        var parts = new List<string>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock { Text.Length: > 0 } text:
                    parts.Add(text.Text);
                    break;
                case ToolCallBlock call:
                    parts.Add($"[called tool {call.Name}]");
                    break;
                case ToolResultBlock result:
                    var preview = result.Content.Length > ToolResultPreviewChars
                        ? result.Content[..ToolResultPreviewChars] + "…"
                        : result.Content;
                    parts.Add($"[{result.ToolName} {(result.IsError ? "error" : "result")}: {preview}]");
                    break;
                case ImageBlock:
                    parts.Add("[attached image]");
                    break;
                // Thinking and raw provider blocks carry nothing a side answer needs.
            }
        }
        return parts.Count == 0 ? "" : $"{label}: {string.Join("\n", parts)}";
    }
}
