using System.IO;

namespace JarvisCode.App.Services;

/// <summary>
/// Which folders the user has said this app may work in.
///
/// The reference asks once per workspace, before it starts a session there, and
/// refuses to run until the answer is yes (its "Workspace trust needed" session
/// error). The question and its copy are ported in
/// <see cref="SessionDialogs.TrustTitle"/>; this is the store and the rule.
/// </summary>
public static class WorkspaceTrust
{
    /// <summary>
    /// A folder is identified by its full path with a trailing separator
    /// dropped, compared the way Windows compares paths.
    /// </summary>
    public static string Key(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return directory;
        }
    }

    /// <summary>
    /// Whether a session may run here. A session with no folder of its own has
    /// nothing to trust and is never asked.
    /// </summary>
    public static bool IsTrusted(IReadOnlyList<string> trusted, string directory) =>
        directory.Length == 0 ||
        trusted.Contains(Key(directory), StringComparer.OrdinalIgnoreCase);

    /// <summary>Records the answer. Trusting a folder trusts everything under it, as the reference does.</summary>
    public static void Trust(List<string> trusted, string directory)
    {
        var key = Key(directory);
        if (!trusted.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            trusted.Add(key);
        }
    }

    /// <summary>
    /// The rules the trust prompt lists under "Execution allowed by:" — the
    /// settings files that would let this folder run commands. The reference
    /// lists whatever granted execution; here that is the project's own
    /// settings files, named only when they exist.
    /// </summary>
    public static IReadOnlyList<string> ExecutionSources(string directory)
    {
        var sources = new List<string>();
        foreach (var relative in new[]
                 {
                     Path.Combine(".jarvis", "settings.json"),
                     Path.Combine(".jarvis", "settings.local.json"),
                     Path.Combine(".claude", "settings.json"),
                     Path.Combine(".claude", "settings.local.json"),
                 })
        {
            try
            {
                if (File.Exists(Path.Combine(directory, relative)))
                {
                    sources.Add(relative.Replace('\\', '/'));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A path this machine will not answer for is simply not listed.
            }
        }

        return sources;
    }
}
