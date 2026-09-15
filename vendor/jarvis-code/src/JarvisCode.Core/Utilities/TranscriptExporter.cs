using System.Text;
using JarvisCode.Core.Models;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Core.Utilities;

/// <summary>Renders a session as a shareable markdown document for /export.</summary>
public static class TranscriptExporter
{
    private const int MaxToolResultChars = 2_000;

    public static string Render(Session session)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {session.Title}");
        builder.AppendLine();
        builder.AppendLine($"- Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- Model: {session.ModelId ?? "unknown"}");
        builder.AppendLine($"- Working directory: {session.WorkingDirectory}");
        builder.AppendLine();

        foreach (var message in session.Messages)
        {
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextBlock text when text.Text.Length > 0:
                        builder.AppendLine(message.Role == Role.User ? "## 👤 User" : "## 🤖 Assistant");
                        builder.AppendLine();
                        builder.AppendLine(CitationMarkdown.Format(text));
                        builder.AppendLine();
                        break;
                    case ToolCallBlock call:
                        builder.AppendLine($"> 🔧 `{call.Name}` {Truncate(call.ArgumentsJson, 200)}");
                        builder.AppendLine();
                        break;
                    case ToolResultBlock result:
                        builder.AppendLine(
                            $"<details><summary>Result of {result.ToolName}{(result.IsError ? " (error)" : "")}</summary>");
                        builder.AppendLine();
                        builder.AppendLine("```");
                        builder.AppendLine(Truncate(result.Content, MaxToolResultChars));
                        builder.AppendLine("```");
                        builder.AppendLine();
                        builder.AppendLine("</details>");
                        builder.AppendLine();
                        break;
                    case ImageBlock:
                        builder.AppendLine("> 🖼 (image attached)");
                        builder.AppendLine();
                        break;
                    // Thinking and raw provider blocks are internal state, not transcript.
                }
            }
        }
        // Exports are made to be shared; scrub anything key-shaped that tool output leaked.
        return Security.SecretRedactor.Redact(builder.ToString());
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
