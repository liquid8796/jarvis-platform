using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;

namespace JarvisCode.Core.Tools.BuiltIn;

internal static class FileSystemDefaults
{
    /// <summary>Directories that search tools skip: VCS internals and build/dependency output.</summary>
    public static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build", ".next", "target", "__pycache__",
    };

    public static bool IsExcluded(string directoryName) => ExcludedDirectories.Contains(directoryName);

    /// <summary>Read/write cap (claw-code file-ops guard): files above this never load into memory whole.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly TimeSpan GitListTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Byte-order marks that prove a file is text even though its bytes contain NULs:
    /// in UTF-16 and UTF-32 every ASCII character carries one, so the NUL heuristic
    /// below would otherwise write those files off as binary.
    /// </summary>
    private static readonly byte[][] TextByteOrderMarks =
    [
        [0xFF, 0xFE, 0x00, 0x00],
        [0x00, 0x00, 0xFE, 0xFF],
        [0xEF, 0xBB, 0xBF],
        [0xFF, 0xFE],
        [0xFE, 0xFF],
    ];

    /// <summary>Error message when the file at <paramref name="filePath"/> exceeds the cap; null when it fits.</summary>
    public static string? CheckReadableSize(string filePath)
    {
        long length;
        try
        {
            length = new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // Let the actual read produce the real error.
        }
        if (length <= MaxFileBytes)
            return null;
        return $"File is {length / (1024.0 * 1024.0):0.#} MB — larger than the {MaxFileBytes / (1024 * 1024)} MB limit. " +
               "Use grep to search inside it, or shell commands to slice it.";
    }

    /// <summary>
    /// Heuristic binary check: a NUL byte in the first 8KB means "not text", unless the
    /// file opens with a text byte-order mark — StreamReader decodes those correctly.
    /// </summary>
    public static bool LooksBinary(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> buffer = stackalloc byte[8192];
            int read = stream.Read(buffer);
            var head = buffer[..read];
            foreach (var mark in TextByteOrderMarks)
            {
                if (head.StartsWith(mark))
                    return false;
            }

            return head.Contains((byte)0);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Enumerates files under <paramref name="root"/>, skipping excluded directories.</summary>
    public static IEnumerable<string> EnumerateFiles(string root) => EnumerateFiles(root, skipExcluded: true);

    private static IEnumerable<string> EnumerateFiles(string root, bool skipExcluded)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
            foreach (var subdirectory in subdirectories)
            {
                if (!skipExcluded || !IsExcluded(Path.GetFileName(subdirectory)))
                    pending.Push(subdirectory);
            }
        }
    }

    /// <summary>
    /// The files a search tool should look at under <paramref name="root"/>: git's own
    /// view of the tree when it is a work tree — so .gitignore is honoured exactly
    /// rather than approximated — falling back to a plain walk otherwise, minus the
    /// excluded directories and narrowed by <paramref name="Glob"/>. Sorted, so which
    /// files a capped search reaches does not depend on directory order.
    /// <paramref name="unrestricted"/> drops both filters and searches everything.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CollectSearchableFilesAsync(
        string root, string? glob, bool unrestricted, CancellationToken cancellationToken)
    {
        IEnumerable<string> files;
        if (unrestricted)
        {
            files = EnumerateFiles(root, skipExcluded: false);
        }
        else
        {
            files = await TryListGitFilesAsync(root, cancellationToken) ?? EnumerateFiles(root, skipExcluded: true);
            files = files.Where(file => !IsUnderExcludedDirectory(root, file));
        }

        var collected = files.ToList();
        if (!string.IsNullOrWhiteSpace(glob))
            collected = FilterByGlob(root, collected, glob);

        collected.Sort(StringComparer.OrdinalIgnoreCase);
        return collected;
    }

    private static bool IsUnderExcludedDirectory(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments[..^1].Any(IsExcluded);
    }

    /// <summary>Keeps the files whose path relative to the root matches the glob.</summary>
    private static List<string> FilterByGlob(string root, List<string> files, string glob)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(glob);
        var relative = files
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
        var matched = matcher.Match(relative);
        if (!matched.HasMatches)
            return [];

        var keep = new HashSet<string>(matched.Files.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);
        return [.. files.Where((_, index) => keep.Contains(relative[index]))];
    }

    /// <summary>
    /// The paths git lists under <paramref name="root"/> — tracked plus untracked that
    /// no .gitignore excludes. Null when git is missing, the directory is not a work
    /// tree, or the query times out; the caller then walks the tree itself.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> TryListGitFilesAsync(
        string root, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("-z");
        startInfo.ArgumentList.Add("--cached");
        startInfo.ArgumentList.Add("--others");
        startInfo.ArgumentList.Add("--exclude-standard");
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(GitListTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone; either way the caller falls back to walking the tree.
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        var listing = await stdout;
        await stderr;
        if (process.ExitCode != 0)
            return null;

        var result = new List<string>();
        foreach (var entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            // Tracked-but-deleted paths are listed too; only real files can be searched.
            if (File.Exists(full))
                result.Add(full);
        }

        return result;
    }
}
