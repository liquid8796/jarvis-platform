using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.Cli;

internal static class CliPromptSections
{
    private static readonly HashSet<string> DynamicHeadings = new(StringComparer.Ordinal)
    { "# Environment", "# Memory", "# auto memory", "# Scratchpad Directory" };

    public static (string Static, string Dynamic) Split(string prompt)
    {
        var sections = Regex.Split(prompt.Replace("\r\n", "\n"), "(?m)(?=^# [^#])");
        var stable = new StringBuilder();
        var dynamic = new StringBuilder();
        foreach (var section in sections)
        {
            if (section.Length == 0) continue;
            var heading = section.Split('\n', 2)[0].TrimEnd();
            if (DynamicHeadings.Contains(heading)) dynamic.Append(section);
            else
            {
                var git = section.IndexOf("\ngitStatus:", StringComparison.Ordinal);
                if (git >= 0) { stable.Append(section[..git]); dynamic.Append(section[(git + 1)..]); }
                else if (section.StartsWith("gitStatus:", StringComparison.Ordinal)) dynamic.Append(section);
                else stable.Append(section);
            }
        }
        return (stable.ToString().TrimEnd(), dynamic.ToString().TrimEnd());
    }
}
