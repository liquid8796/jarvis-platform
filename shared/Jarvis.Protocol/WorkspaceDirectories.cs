namespace Jarvis.Protocol;

/// <summary>Selected project folders and default working directory; not an access boundary.</summary>
public sealed class WorkspaceDirectories
{
    public IReadOnlyList<string> Directories { get; }
    // Empty is an explicit unbound state, never Environment.CurrentDirectory.
    public string Primary => Directories.FirstOrDefault() ?? string.Empty;
    public bool HasWorkspace => Directories.Count > 0;
    public IReadOnlyList<string> Additional => Directories.Skip(1).ToArray();
    public WorkspaceDirectories(string? primary, IEnumerable<string>? additional = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Directories = Array.AsReadOnly(new[] { primary }.Concat(additional ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Normalize(directory!)).Distinct(comparer).ToArray());
    }
    public static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Choose an existing working directory.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Working directory does not exist: {full}");
        return full;
    }
    public string RequirePrimary() => HasWorkspace ? Primary
        : throw new InvalidOperationException("WORKSPACE_REQUIRED: choose a workspace with workspace__set or provide an absolute workingDirectory/path.");

    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return RequirePrimary();
        if (Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(path, RequirePrimary());
    }

    public static string ResolvePath(string? path, string? workspace)
    {
        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(workspace))
            throw new InvalidOperationException("WORKSPACE_REQUIRED: choose a workspace with workspace__set or provide an absolute workingDirectory/path.");
        return string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(workspace) : Path.GetFullPath(path, workspace);
    }
}
