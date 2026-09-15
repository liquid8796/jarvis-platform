using JarvisCode.Core.Models;

namespace JarvisCode.Core.Tools;

/// <summary>
/// Outcome of one tool invocation, as text fed back to the model — optionally
/// with images (e.g. a screenshot) for providers that support vision in tool
/// results. Images default to none, so existing tools are unaffected.
/// </summary>
public sealed record ToolResult(string Content, bool IsError, IReadOnlyList<ImageBlock>? Images = null)
{
    /// <summary>
    /// Extra text the tool injects into the conversation alongside its result —
    /// the reference's synthetic user messages (a skill's instructions ride the
    /// user turn as their own text block, not the tool_result). Null for
    /// ordinary tools.
    /// </summary>
    public string? FollowUpText { get; init; }

    public static ToolResult Success(string content) => new(content, IsError: false);

    public static ToolResult Error(string message) => new(message, IsError: true);

    public static ToolResult WithImage(string content, ImageBlock image) => new(content, IsError: false, [image]);
}
