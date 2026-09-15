using System.IO;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Memory;

namespace JarvisCode.App.Services;

/// <summary>One memory file, as the Memory page lists it.</summary>
public sealed record MemoryEntry(string Path, string Title, string? Description, string Body)
{
    /// <summary>The file's own name, which is what the delete button is labelled by.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The Customize › Memory page (c71860c77-BRN4k43v): the memory files this
/// machine holds, each opening on the text under its frontmatter, plus the
/// switch that stops them being read or updated. The reference reads its own
/// Cowork memory store; this one reads the per-project folder under the
/// profile's memory root, which is where <see cref="ProjectMemory"/> keeps it.
/// </summary>
public static class CustomizeMemory
{
    /// <summary>The files a project's memory folder holds, index first, then by name.</summary>
    public static IReadOnlyList<MemoryEntry> Load(string memoryDirectory)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(memoryDirectory))
                return [];
            files = Directory.GetFiles(memoryDirectory, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var entries = new List<MemoryEntry>();
        foreach (var file in files
            .OrderBy(f => System.IO.Path.GetFileName(f)
                .Equals(ProjectMemory.IndexFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            if (Read(file) is { } entry)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>Reads one file into a row, or null when it cannot be read.</summary>
    public static MemoryEntry? Read(string path)
    {
        try
        {
            return Parse(path, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The title, description and body a memory file shows. A file written by
    /// the memory tool carries frontmatter naming it; one without takes its file
    /// name, which is what the index does.
    /// </summary>
    public static MemoryEntry Parse(string path, string content)
    {
        var parsed = Frontmatter.ParseRich(content);
        var name = Frontmatter.Get(parsed.Fields, "name")?.Trim();
        var description = Frontmatter.Get(parsed.Fields, "description")?.Trim();
        var title = string.IsNullOrEmpty(name)
            ? System.IO.Path.GetFileNameWithoutExtension(path)
            : name;
        var body = parsed.Body.Trim();
        return new MemoryEntry(
            path, title, string.IsNullOrEmpty(description) ? null : description,
            body.Length > 0 ? body : content.Trim());
    }

    /// <summary>Deletes one memory file; false when the file would not go.</summary>
    public static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The line under the switch, which says what the current state means.</summary>
    public static string SwitchDescription(bool enabled) => enabled
        ? "Jarvis will read and update these memories during Code sessions."
        : "Paused. Existing memories are kept but won’t be read or updated in new sessions.";
}
