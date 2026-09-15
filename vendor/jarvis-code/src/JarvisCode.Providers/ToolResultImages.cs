using JarvisCode.Core.Models;

namespace JarvisCode.Providers;

/// <summary>
/// Anthropic is the only wire format here that carries images inside a tool
/// result. Every other API takes images on a user message only, so a tool that
/// returns pixels (screenshot, browser capture) has them ride in the user turn
/// that follows its result — otherwise the model receives the caption alone and
/// is blind to what the tool actually returned.
/// </summary>
internal static class ToolResultImages
{
    public static List<ImageBlock> Collect(IEnumerable<ToolResultBlock> results) =>
        [.. results.SelectMany(result => result.Images ?? [])];

    /// <summary>Ties loose images back to the call that produced them.</summary>
    public static string Caption(string toolName) => $"Image output from the {toolName} tool call.";

    public static string Caption(IEnumerable<ToolResultBlock> results)
    {
        var names = results
            .Where(result => result.Images is { Count: > 0 })
            .Select(result => result.ToolName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Count switch
        {
            0 => "",
            1 => Caption(names[0]),
            _ => $"Image output from these tool calls: {string.Join(", ", names)}.",
        };
    }
}
