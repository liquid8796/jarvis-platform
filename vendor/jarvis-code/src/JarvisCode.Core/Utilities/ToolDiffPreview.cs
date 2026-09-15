using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Builds the change preview shown in permission prompts for the file-editing
/// tools. Null means "no diff available" and the prompt falls back to raw
/// arguments; a failed preview must never block the permission flow.
/// </summary>
public static class ToolDiffPreview
{
    public sealed record Preview(string FilePath, IReadOnlyList<DiffLine> Lines);

    public static Preview? TryCreate(string toolName, JsonObject arguments, string workingDirectory)
    {
        try
        {
            return toolName switch
            {
                "Edit" => PreviewEdit(arguments, workingDirectory),
                "Write" => PreviewWrite(arguments, workingDirectory),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static Preview? PreviewEdit(JsonObject arguments, string workingDirectory)
    {
        var path = ResolvePath(arguments, workingDirectory);
        var oldString = JsonArgs.GetString(arguments, "old_string");
        var newString = JsonArgs.GetString(arguments, "new_string");
        if (path is null || string.IsNullOrEmpty(oldString) || newString is null || !File.Exists(path))
            return null;

        var oldContent = File.ReadAllText(path);
        if (!oldContent.Contains(oldString, StringComparison.Ordinal))
            return null; // The tool will report the mismatch; nothing sensible to preview.

        var newContent = JsonArgs.GetBool(arguments, "replace_all")
            ? oldContent.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(oldContent, oldString, newString);
        var lines = LineDiff.Compute(oldContent, newContent);
        return lines is null ? null : new Preview(path, lines);
    }

    private static Preview? PreviewWrite(JsonObject arguments, string workingDirectory)
    {
        var path = ResolvePath(arguments, workingDirectory);
        var content = JsonArgs.GetString(arguments, "content");
        if (path is null || content is null)
            return null;

        var oldContent = File.Exists(path) ? File.ReadAllText(path) : "";
        var lines = LineDiff.Compute(oldContent, content);
        return lines is null ? null : new Preview(path, lines);
    }

    private static string ReplaceFirst(string content, string oldString, string newString)
    {
        int index = content.IndexOf(oldString, StringComparison.Ordinal);
        return content[..index] + newString + content[(index + oldString.Length)..];
    }

    private static string? ResolvePath(JsonObject arguments, string workingDirectory)
    {
        var path = JsonArgs.GetString(arguments, "file_path");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
    }
}
