using System.Security.Cryptography;
using System.Text;

namespace Jarvis.Agent.Core.Autonomous.Verification;

/// <summary>A content revision, including untracked source and assets, for rendered QA freshness.</summary>
public sealed record FrontendWorkspaceState(string Revision, IReadOnlyDictionary<string, string> Files,
    bool Complete, string? Error = null)
{
    private const int MaxFiles = 20000;
    private const long MaxBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "artifacts", "dist", "build", ".next", ".nuxt",
        "coverage", "TestResults", ".cache", ".turbo", ".venv", "venv", "__pycache__"
    };

    public static FrontendWorkspaceState Capture(string project, CancellationToken cancellationToken = default)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        try
        {
            var root = Path.GetFullPath(project);
            if (!Directory.Exists(root)) return new("", files, false, "Workspace no longer exists.");
            var pending = new Stack<string>();
            pending.Push(root);
            var visited = 0;
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > MaxFiles) return new("", files, false, "Workspace directory budget exceeded.");
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    var name = Path.GetFileName(path);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    if (isDirectory && IgnoredDirectories.Contains(name)) continue;
                    // A source symlink can change outside the observed workspace; never certify it from a partial scan.
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                        return new("", files, false, "Source contains a linked path; select a workspace without untracked linked source.");
                    if (isDirectory) { pending.Push(path); continue; }
                    var before = new FileInfo(path);
                    var length = before.Length;
                    var modified = before.LastWriteTimeUtc;
                    bytes += length;
                    if (files.Count >= MaxFiles || bytes > MaxBytes)
                        return new("", files, false, "Workspace source exceeds the bounded QA revision scan.");
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var hash = Convert.ToHexString(SHA256.HashData(stream));
                    var after = new FileInfo(path);
                    if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified)
                        return new("", files, false, "Source changed while calculating its QA revision. Retry verification.");
                    files[Path.GetRelativePath(root, path).Replace('\\', '/')] = hash;
                }
            }
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (path, hash) in files)
                digest.AppendData(Encoding.UTF8.GetBytes(path + "\0" + hash + "\n"));
            return new(Convert.ToHexString(digest.GetHashAndReset()), files, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new("", files, false, "Unable to establish a complete source revision: " + ex.GetType().Name);
        }
    }

    public IReadOnlyList<string> ChangedSince(IReadOnlyDictionary<string, string>? baseline)
    {
        if (baseline is null) return [];
        return Files.Keys.Concat(baseline.Keys).Distinct(StringComparer.Ordinal)
            .Where(path => !Files.TryGetValue(path, out var current) || !baseline.TryGetValue(path, out var previous) || current != previous)
            .Order(StringComparer.Ordinal).ToArray();
    }
}
