using System.IO;
using System.Text;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Services;

internal static class CustomOutputStyles
{
    internal static IReadOnlyList<OutputStyles.Style> Load(string? workingDirectory, string? profileRoot,
        string? legacyUserRoot = null, IEnumerable<string>? explicitPaths = null)
    {
        var styles = new Dictionary<string, OutputStyles.Style>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        var legacy = legacyUserRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        if (legacy.Length > 0) directories.Add(Path.Combine(legacy, "output-styles"));
        if (!string.IsNullOrWhiteSpace(profileRoot)) directories.Add(Path.Combine(profileRoot, "output-styles"));
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            directories.Add(Path.Combine(workingDirectory, ".claude", "output-styles"));
            directories.Add(Path.Combine(workingDirectory, ".jarvis", "output-styles"));
        }
        if (explicitPaths is not null) directories.AddRange(explicitPaths);
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory) && !File.Exists(directory)) continue;
            try
            {
                foreach (var path in (File.Exists(directory) ? [directory] : Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories)).Order(StringComparer.Ordinal))
                {
                    if (new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                    var parsed = Frontmatter.ParseRich(File.ReadAllText(path));
                    var name = Frontmatter.Get(parsed.Fields, "name")?.Trim();
                    if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(parsed.Body)) continue;
                    styles[name] = new OutputStyles.Style(name, Frontmatter.Get(parsed.Fields, "description") ?? "",
                        parsed.Body, null)
                    {
                        KeepCodingInstructions = string.Equals(Frontmatter.Get(parsed.Fields, "keep-coding-instructions"), "true", StringComparison.OrdinalIgnoreCase),
                        FilePath = path,
                    };
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return [.. styles.Values.OrderBy(style => style.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static string Save(string directory, string name, string description, string prompt, bool keepCodingInstructions)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 80 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("Enter a name of 1–80 characters without filename separators.", nameof(name));
        if (OutputStyles.All.Any(style => style.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            name.Equals(OutputStyles.DefaultName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a name different from a built-in output style.", nameof(name));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Write the output style instructions.", nameof(prompt));
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, name + ".md"));
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid output style path.");
        var text = "---\nname: |-\n  " + name + "\ndescription: |-\n  " + description.Trim().Replace("\r\n", "\n").Replace("\n", "\n  ") +
            "\nkeep-coding-instructions: " + (keepCodingInstructions ? "true" : "false") + "\n---\n\n" +
            prompt.Replace("\r\n", "\n").Trim() + "\n";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        return path;
    }
}
