using System.Text;
using System.Text.RegularExpressions;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Utilities;

/// <summary>
/// Handles @path mentions in the user's prompt: extracts the referenced files
/// and builds the outgoing message with their contents attached, so the model
/// sees the files without having to call read tools first.
/// </summary>
public static partial class FileMentions
{
    public const int MaxAttachedFiles = 5;
    private const int MaxCharsPerFile = 40_000;

    // @"quoted path with spaces" or @bare/path — '@' at the start or after whitespace.
    [GeneratedRegex("""(?<=^|\s)@(?:"(?<quoted>[^"]+)"|(?<bare>[^\s"@]+))""")]
    private static partial Regex MentionPattern();

    public sealed record MentionResult(ChatMessage Message, IReadOnlyList<string> AttachedPaths);

    /// <summary>Raw path tokens mentioned in the text (not checked for existence).</summary>
    public static IReadOnlyList<string> ExtractPaths(string text)
    {
        var paths = new List<string>();
        foreach (Match match in MentionPattern().Matches(text))
        {
            var token = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            if (token.Length > 0 && !paths.Contains(token, StringComparer.OrdinalIgnoreCase))
                paths.Add(token);
        }
        return paths;
    }

    /// <summary>
    /// Builds the user message: the typed text plus one attachment block per
    /// mentioned file that exists and is text (capped per file and in count).
    /// </summary>
    public static MentionResult BuildUserMessage(string text, string workingDirectory)
    {
        var blocks = new List<ContentBlock> { new TextBlock(text) };
        var attached = new List<string>();
        foreach (var token in ExtractPaths(text))
        {
            if (attached.Count >= MaxAttachedFiles)
                break;
            // The reference refuses a network attachment outright; here the mention
            // is left as typed text, never read.
            if (NetworkPaths.IsNetworkPath(token))
                continue;
            string resolved;
            try
            {
                resolved = Path.GetFullPath(
                    Path.IsPathRooted(token) ? token : Path.Combine(workingDirectory, token));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                continue;
            }
            if (!File.Exists(resolved) || FileSystemDefaults.LooksBinary(resolved))
                continue;

            string content;
            try
            {
                content = File.ReadAllText(resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (content.Length > MaxCharsPerFile)
                content = content[..MaxCharsPerFile] + "\n… [file truncated]";

            var builder = new StringBuilder();
            builder.AppendLine($"[Attached file: {resolved}]");
            builder.AppendLine("```");
            builder.AppendLine(content.TrimEnd());
            builder.Append("```");
            blocks.Add(new TextBlock(builder.ToString()));
            attached.Add(resolved);
        }
        return new MentionResult(new ChatMessage(Role.User, blocks), attached);
    }

    /// <summary>
    /// Relative paths of candidate files and directories under the root, for
    /// @-mention completion. Directories end with '/' and are derived from the
    /// listed files' ancestors, so they follow the same ignore rules.
    /// </summary>
    public static IReadOnlyList<string> ListCandidateEntries(string root, int maxEntries = 5000)
    {
        var entries = new List<string>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in FileSystemDefaults.EnumerateFiles(root))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                entries.Add(relative);
                for (var slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
                    directories.Add(relative[..(slash + 1)]);
                if (entries.Count + directories.Count >= maxEntries)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partially listed tree is still useful for completion.
        }
        entries.AddRange(directories);
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    /// <summary>Relative paths of candidate files under the root, for @-mention completion.</summary>
    public static IReadOnlyList<string> ListCandidateFiles(string root, int maxFiles = 5000)
    {
        var files = new List<string>();
        try
        {
            foreach (var file in FileSystemDefaults.EnumerateFiles(root))
            {
                files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                if (files.Count >= maxFiles)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partially listed tree is still useful for completion.
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
