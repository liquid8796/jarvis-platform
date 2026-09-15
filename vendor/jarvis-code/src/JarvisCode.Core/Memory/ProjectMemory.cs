using System.Security.Cryptography;
using System.Text;

namespace JarvisCode.Core.Memory;

/// <summary>
/// Persistent per-project memory: a folder of markdown files keyed by the
/// working directory, surviving across sessions. MEMORY.md is the index the
/// system prompt carries; the agent maintains it through the memory tool.
/// </summary>
public static class ProjectMemory
{
    public const string IndexFileName = "MEMORY.md";
    private const int MaxIndexCharsInPrompt = 8_000;

    /// <summary>Stable folder per project: {root}/{folder-name}-{hash8}.</summary>
    public static string DirectoryFor(string memoryRoot, string workingDirectory)
    {
        var normalized = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8].ToLowerInvariant();
        var name = new string(
            (Path.GetFileName(normalized) is { Length: > 0 } tail ? tail : "root")
            .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return Path.Combine(memoryRoot, $"{name}-{hash}");
    }

    /// <summary>The index content for the system prompt, or null when empty/absent.</summary>
    public static string? ReadIndexForPrompt(string memoryDirectory)
    {
        try
        {
            var path = Path.Combine(memoryDirectory, IndexFileName);
            if (!File.Exists(path))
                return null;
            var content = File.ReadAllText(path).Trim();
            if (content.Length == 0)
                return null;
            return content.Length > MaxIndexCharsInPrompt
                ? content[..MaxIndexCharsInPrompt] + "\n… [memory index truncated]"
                : content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a memory file name, rejecting anything that escapes the memory
    /// directory or is not a markdown file.
    /// </summary>
    public static string? ResolveFile(string memoryDirectory, string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? IndexFileName : fileName.Trim();
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return null;
        if (name.Any(c => c is '/' or '\\' or ':') || name.Contains(".."))
            return null;
        var full = Path.GetFullPath(Path.Combine(memoryDirectory, name));
        var root = Path.GetFullPath(memoryDirectory);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
