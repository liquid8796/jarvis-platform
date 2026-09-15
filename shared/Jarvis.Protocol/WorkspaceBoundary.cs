namespace Jarvis.Protocol;

/// <summary>Filesystem tool boundary. This is NOT a sandbox for a shell, browser or GUI application.</summary>
public sealed class WorkspaceBoundary
{
    public string Root { get; }
    private readonly IReadOnlyList<string> _roots;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public WorkspaceBoundary(string root, IEnumerable<string>? additionalDirectories = null)
    {
        var directories = new WorkspaceDirectories(root, additionalDirectories);
        Root = directories.Primary;
        _roots = directories.Directories;
        foreach (var directory in _roots) RejectLinks(directory);
    }
    public string Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Root;
        // Block UNC, alternate data streams and Windows device paths before canonicalization.
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\") || path.AsSpan(Math.Min(path.Length, 2)).Contains(':')))
            throw new UnauthorizedAccessException("UNC/device paths and alternate streams are not allowed.");
        var full = Path.GetFullPath(path, Root);
        if (!_roots.Any(root => full.Equals(root, _comparison) ||
            full.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, _comparison)))
            throw new UnauthorizedAccessException("Path is outside the selected workspace directories.");
        RejectLinks(full);
        return full;
    }
    private static void RejectLinks(string full)
    {
        for (var node = new FileInfo(full) as FileSystemInfo; node is not null;)
        {
            if ((File.Exists(node.FullName) || Directory.Exists(node.FullName)) &&
                (File.GetAttributes(node.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symbolic links and junctions are not permitted.");
            var parent = Path.GetDirectoryName(node.FullName);
            node = string.IsNullOrEmpty(parent) ? null : new DirectoryInfo(parent);
        }
    }
}
