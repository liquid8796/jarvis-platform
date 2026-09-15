namespace Jarvis.Protocol;

/// <summary>Selected project folders and default working directory; not an access boundary.</summary>
public sealed class WorkspaceDirectories
{
    public IReadOnlyList<string> Directories { get; }
    public string Primary => Directories[0];
    public IReadOnlyList<string> Additional => Directories.Skip(1).ToArray();
    public WorkspaceDirectories(string primary, IEnumerable<string>? additional = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Directories = Array.AsReadOnly(new[] { primary }.Concat(additional ?? [])
            .Select(Normalize).Distinct(comparer).ToArray());
    }
    public static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Choose an existing working directory.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Working directory does not exist: {full}");
        return full;
    }
    public string Resolve(string? path) => string.IsNullOrWhiteSpace(path) ? Primary : Path.GetFullPath(path, Primary);
}
