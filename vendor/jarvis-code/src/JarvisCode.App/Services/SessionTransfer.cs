using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>One entry of the Import history the settings page lists.</summary>
public sealed record ImportRecord(string Id, string Source, DateTimeOffset When, int Sessions, IReadOnlyList<string> SessionIds);

/// <summary>What one import or export moved.</summary>
public sealed record TransferResult(int Sessions, int Skipped, string? Path, string? Error);

/// <summary>
/// The Import &amp; export page's own work: copying Code and Chat sessions out of
/// another install's data folder, writing this install's own as a zip, and keeping
/// the history that lets an import be removed again.
///
/// The reference's wizard has three sources — a claude.ai account it signs into, a
/// claude.ai export zip, and another install's data folder — and only the third is
/// available without an account, so it is the one this build implements; a session
/// is copied, never moved, and re-importing does not duplicate, which is the
/// promise its own copy makes ("Sessions are copied, not moved…").
/// </summary>
public static class SessionTransfer
{
    /// <summary>The reference's three ranges, in its order (its <c>Rfvi9/BMNS</c>, <c>mgYBYoL6zD</c>, <c>mWKPwaLaI3</c>).</summary>
    public static readonly IReadOnlyList<(string Value, string Label, int Days)> Ranges =
    [
        ("30", "Last 30 days", 30),
        ("90", "Last 90 days", 90),
        ("all", "Everything", 0),
    ];

    public static int DaysFor(string range) =>
        Ranges.FirstOrDefault(r => r.Value == range).Days;

    /// <summary>A session file is in range when it was last written inside the window; 0 days means everything.</summary>
    public static bool InRange(DateTime lastWriteUtc, int days) =>
        days <= 0 || lastWriteUtc >= DateTime.UtcNow.AddDays(-days);

    /// <summary>
    /// Whether a folder looks like an app data folder this build can read
    /// sessions out of — its own, or the reference's, which both keep
    /// <c>sessions/{chat,code}</c> underneath.
    /// </summary>
    public static bool LooksLikeDataFolder(string folder) =>
        Directory.Exists(Path.Combine(folder, "sessions", "code")) ||
        Directory.Exists(Path.Combine(folder, "sessions", "chat"));

    private static IEnumerable<string> SessionFiles(string root, int days)
    {
        foreach (var surface in new[] { "code", "chat" })
        {
            var directory = Path.Combine(root, "sessions", surface);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (InRange(File.GetLastWriteTimeUtc(file), days))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>Copies every session in range out of another install's data folder into this one.</summary>
    public static TransferResult Import(ProfilePaths paths, string sourceFolder, int days)
    {
        if (string.Equals(Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return new TransferResult(0, 0, null, "That’s this app’s own data folder. Choose a different one.");
        }

        if (!LooksLikeDataFolder(sourceFolder))
        {
            return new TransferResult(0, 0, null,
                "Couldn’t find sessions in that folder. Choose the app’s data folder (its name starts with “Jarvis”).");
        }

        var copied = new List<string>();
        var skipped = 0;
        foreach (var file in SessionFiles(sourceFolder, days))
        {
            var surface = Path.GetFileName(Path.GetDirectoryName(file))!;
            var target = Path.Combine(paths.SessionsDirectory, surface, Path.GetFileName(file));
            if (File.Exists(target))
            {
                skipped++;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied.Add(surface + "/" + Path.GetFileName(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        if (copied.Count > 0)
        {
            Record(paths, new ImportRecord(
                Guid.NewGuid().ToString("N")[..8],
                sourceFolder,
                DateTimeOffset.Now,
                copied.Count,
                copied));
        }

        return new TransferResult(copied.Count, skipped, null, null);
    }

    /// <summary>Writes this install's sessions in range to a zip in the Downloads folder.</summary>
    public static TransferResult Export(ProfilePaths paths, int days)
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        try
        {
            Directory.CreateDirectory(downloads);
            var target = Path.Combine(downloads, $"jarvis-sessions-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            var files = SessionFiles(paths.Root, days).ToList();
            if (files.Count == 0)
            {
                return new TransferResult(0, 0, null, "No sessions in this range");
            }

            using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var surface = Path.GetFileName(Path.GetDirectoryName(file))!;
                    zip.CreateEntryFromFile(file, $"sessions/{surface}/{Path.GetFileName(file)}");
                }
            }

            return new TransferResult(files.Count, 0, target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TransferResult(0, 0, null, "Couldn’t write the export. Free some disk space or try again.");
        }
    }

    // ---- import history ----

    private static string HistoryFile(ProfilePaths paths) => Path.Combine(paths.Root, "import-history.json");

    public static IReadOnlyList<ImportRecord> History(ProfilePaths paths)
    {
        try
        {
            var file = HistoryFile(paths);
            if (!File.Exists(file))
            {
                return [];
            }

            var records = JsonSerializer.Deserialize<List<ImportRecord>>(File.ReadAllText(file));
            return records is null ? [] : [.. records.OrderByDescending(r => r.When)];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static void Write(ProfilePaths paths, IEnumerable<ImportRecord> records)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            File.WriteAllText(
                HistoryFile(paths),
                JsonSerializer.Serialize(records.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; failing to write it must not fail the import.
        }
    }

    private static void Record(ProfilePaths paths, ImportRecord record) =>
        Write(paths, History(paths).Append(record));

    /// <summary>Deletes the sessions one import brought in, then drops its history row.</summary>
    public static int Remove(ProfilePaths paths, ImportRecord record)
    {
        var removed = 0;
        foreach (var relative in record.SessionIds)
        {
            var path = Path.Combine(paths.SessionsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A session open elsewhere stays; the row still goes.
            }
        }

        Write(paths, History(paths).Where(r => r.Id != record.Id));
        return removed;
    }

    public static int RemoveAll(ProfilePaths paths)
    {
        var removed = History(paths).Sum(record => Remove(paths, record));
        Write(paths, []);
        return removed;
    }

    /// <summary>The reference's own relative clock for the "Last imported {when}" line.</summary>
    public static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} minutes ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} hours ago"
            : $"{(int)span.TotalDays} days ago";
    }

    /// <summary>A human byte size for the export line.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>How many sessions and bytes are in range, for the count line beneath the picker.</summary>
    public static (int Sessions, long Bytes) Measure(ProfilePaths paths, int days)
    {
        var sessions = 0;
        long bytes = 0;
        foreach (var file in SessionFiles(paths.Root, days))
        {
            sessions++;
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // A file that vanished mid-scan just doesn't count.
            }
        }

        return (sessions, bytes);
    }

    /// <summary>Unused today; kept so a caller can read a session id out of a copied file.</summary>
    public static string? SessionIdOf(string path)
    {
        try
        {
            return (string?)(JsonNode.Parse(File.ReadAllText(path))?["id"]);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
