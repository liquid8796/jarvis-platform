namespace JarvisCode.Cli;

/// <summary>
/// The reference CLI's public tool names mapped onto this engine's registry
/// names — the same table the skill frontmatter mapper uses, kept here for the
/// CLI's --tools/--allowedTools/--disallowedTools grammar ("Bash(git *) Edit",
/// comma or space separated).
/// </summary>
internal static class ToolNames
{
    private static readonly Dictionary<string, string> CliToEngine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bash"] = "Bash",
        ["PowerShell"] = "PowerShell",
        ["Shell"] = "PowerShell",
        ["Read"] = "Read",
        ["Edit"] = "Edit",
        ["MultiEdit"] = "Edit",
        ["Write"] = "Write",
        ["Glob"] = "Glob",
        ["Grep"] = "Grep",
        ["WebFetch"] = "WebFetch",
        ["WebSearch"] = "WebSearch",
        ["Task"] = "Agent",
        ["Agent"] = "Agent",
        ["TodoWrite"] = "todo_write",
        ["NotebookEdit"] = "NotebookEdit",
        ["Skill"] = "Skill",
        ["SlashCommand"] = "Skill",
        ["TaskOutput"] = "TaskOutput",
        ["BashOutput"] = "TaskOutput",
        ["KillShell"] = "TaskStop",
        ["TaskStop"] = "TaskStop",
        ["Monitor"] = "Monitor",
        ["ReadDocument"] = "read_document",
        ["ListDirectory"] = "list_directory",
        ["AskUserQuestion"] = "AskUserQuestion",
        ["SendMessage"] = "SendMessage",
        ["Memory"] = "memory",
    };

    /// <summary>Maps one entry's tool part; unknown names pass through lowercased-as-is.</summary>
    public static string Map(string cliName) =>
        CliToEngine.TryGetValue(cliName, out var mapped) ? mapped : cliName;

    /// <summary>
    /// "Bash(git *)" → ("Bash", "git *"); "Edit" → ("Edit", null). The
    /// reference's "prefix:*" command patterns are prefix globs.
    /// </summary>
    public static (string Tool, string? Pattern) ParseEntry(string entry)
    {
        entry = entry.Trim();
        string tool = entry;
        string? pattern = null;
        int open = entry.IndexOf('(');
        if (open > 0 && entry.EndsWith(')'))
        {
            tool = entry[..open];
            pattern = entry[(open + 1)..^1].Trim();
            if (pattern.EndsWith(":*", StringComparison.Ordinal))
            {
                pattern = pattern[..^2] + "*";
            }

            if (pattern.Length == 0)
            {
                pattern = null;
            }
        }

        return (Map(tool), pattern);
    }

    /// <summary>--allowedTools/--disallowedTools entries → permission-gate rule lines.</summary>
    public static IReadOnlyList<string> ToRuleLines(IEnumerable<string> entries, string action)
    {
        var lines = new List<string>();
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var (tool, pattern) = ParseEntry(raw);
            if (tool.Length == 0 || tool.Contains(' '))
            {
                continue;
            }

            lines.Add(pattern is null ? $"{action} {tool}" : $"{action} {tool} {pattern}");
        }

        return lines;
    }

    /// <summary>
    /// The --tools filter: "" disables all tools, "default" keeps the full set,
    /// names keep only those tools (reference names accepted). Returns null for
    /// "keep everything". <paramref name="declared"/> says whether --tools was
    /// passed at all — `--tools ""` reaches here as an empty list (the comma
    /// split drops the empty entry) and must disable every tool, not keep them.
    /// </summary>
    public static ISet<string>? BuildToolFilter(IReadOnlyList<string> entries, bool declared = true)
    {
        if (!declared || entries.Any(static e => e.Equals("default", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Length == 0)
            {
                continue;
            }

            kept.Add(Map(entry));
        }

        return kept;
    }
}
