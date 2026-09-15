using System.IO;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// Topic-based memory recall, the automemory half the index alone doesn't give:
/// when a prompt shares enough words with one of the project's memory files,
/// that file's content rides the message as a system-reminder. MEMORY.md itself
/// is always in the system prompt, so only the detail files are scored.
/// </summary>
public static class MemoryRecall
{
    internal const int MaxRecalledFiles = 2;
    internal const int MaxCharsPerFile = 2_500;
    internal const int MinScore = 2;
    private const int MaxFileBytes = 64_000;

    /// <summary>
    /// A reminder block carrying the best-matching memory files for this
    /// prompt, or null when nothing scores high enough.
    /// </summary>
    public static string? BuildReminder(string? memoryDirectory, string prompt)
    {
        if (memoryDirectory is null || !Directory.Exists(memoryDirectory) || string.IsNullOrWhiteSpace(prompt))
            return null;

        var keywords = Tokenize(prompt);
        if (keywords.Count == 0)
            return null;

        var scored = new List<(string File, string Content, int Score)>();
        foreach (var path in Directory.EnumerateFiles(memoryDirectory, "*.md"))
        {
            var name = Path.GetFileName(path);
            if (name.Equals(JarvisCode.Core.Memory.ProjectMemory.IndexFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            string content;
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                    continue;
                content = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var haystack = (name + "\n" + content).ToLowerInvariant();
            int score = keywords.Count(haystack.Contains);
            if (score >= MinScore)
                scored.Add((name, content, score));
        }

        if (scored.Count == 0)
            return null;

        var body = new StringBuilder(
            "These project memory files look related to the request. They reflect what was true when written — " +
            "verify anything load-bearing before relying on it.");
        foreach (var (file, content, _) in scored
                     .OrderByDescending(s => s.Score)
                     .ThenBy(s => s.File, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxRecalledFiles))
        {
            body.AppendLine().AppendLine().Append("## ").AppendLine(file);
            var trimmed = content.Trim();
            body.Append(trimmed.Length <= MaxCharsPerFile ? trimmed : trimmed[..MaxCharsPerFile] + "\n… (truncated)");
        }

        return SystemReminders.WrapContext("recalledMemories", body.ToString());
    }

    /// <summary>Distinct lowercase words of 4+ letters; short prompts recall nothing noisy.</summary>
    private static IReadOnlyList<string> Tokenize(string prompt)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        foreach (var c in prompt + " ")
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }
            if (current.Length >= 4)
                words.Add(current.ToString());
            current.Clear();
        }
        return [.. words];
    }
}
