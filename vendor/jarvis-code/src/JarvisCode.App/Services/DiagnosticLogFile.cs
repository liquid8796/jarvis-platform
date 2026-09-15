using System.IO;
using System.Text;

namespace JarvisCode.App.Services;

/// <summary>
/// Appends the engine's diagnostic trace to a per-day file under the profile.
/// Each line is written straight through rather than buffered: the failures this
/// exists to catch — a stream that hangs, a process that is killed — never get a
/// chance to drain a buffer.
/// </summary>
public sealed class DiagnosticLogFile(string directory)
{
    /// <summary>Days of history kept; anything older is pruned at startup.</summary>
    private const int RetainedDays = 7;

    private const string FilePrefix = "jarvis-";
    private const string FileExtension = ".log";

    /// <summary>No byte-order mark: the file is meant to be tailed and pasted as plain text.</summary>
    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();

    /// <summary>Today's file. Recomputed per write so a run across midnight rolls over.</summary>
    public string CurrentFile => Path.Combine(directory, $"{FilePrefix}{DateTime.Now:yyyy-MM-dd}{FileExtension}");

    public void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(CurrentFile, line, FileEncoding);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that throws would break the very turn it is here to explain.
            }
        }
    }

    /// <summary>Drops files older than <see cref="RetainedDays"/>; returns how many went.</summary>
    public int PruneOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-RetainedDays);
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            foreach (var file in Directory.GetFiles(directory, $"{FilePrefix}*{FileExtension}"))
            {
                if (File.GetLastWriteTime(file) >= cutoff)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another instance may hold it; the next run tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only — never worth failing startup over.
        }

        return removed;
    }
}
