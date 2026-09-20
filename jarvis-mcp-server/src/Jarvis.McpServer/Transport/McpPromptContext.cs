using Jarvis.Protocol;
using ModelContextProtocol.Protocol;

namespace Jarvis.McpServer.Transport;

/// <summary>Append optional plain text as a separate tool-result block, preserving the actual output and schema.</summary>
public static class McpPromptContext
{
    public static bool AppendTo(ICollection<ContentBlock> content, UserPromptContext? context, bool isError = false)
    {
        if (isError || context is null) return false;
        try
        {
            content.Add(new TextContentBlock { Text = context.ToContextText() });
            return true;
        }
        catch (ArgumentException)
        {
            // Invalid optional metadata must not turn a completed mutation into an apparent failure.
            return false;
        }
    }
}
