namespace JarvisCode.Core.Customization;

/// <summary>A user-defined subagent type: extra system prompt plus a tool policy.</summary>
public sealed record CustomAgentDefinition(
    string Name,
    string Description,
    bool ReadOnlyTools,
    string SystemPrompt,
    /// <summary>
    /// The agent's own turn cap, from a <c>max-turns</c>/<c>maxTurns</c> field.
    /// Null — the default, and what every built-in agent type uses — means the
    /// agent runs until it is done, as the reference's do.
    /// </summary>
    int? MaxTurns = null,
    /// <summary>
    /// The model the definition names (<c>model:</c>), an id or one of the
    /// reference's family aliases (sonnet, opus, haiku, fable). Null inherits
    /// the parent's, or the configured subagent default.
    /// </summary>
    string? Model = null,
    /// <summary>
    /// The prompt-cache TTL the definition asks for
    /// (<c>experimental.cacheTtl</c>: "5m" or "1h"), used when no subagent TTL
    /// setting is configured. Null keeps the vendor default.
    /// </summary>
    string? CacheTtl = null)
{
    public IReadOnlyList<string>? Tools { get; init; }
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];
}

/// <summary>
/// Loads custom agent definitions from markdown files: {cwd}/.jarvis/agents/*.md
/// plus a user-level directory. Frontmatter: name (defaults to the file name),
/// description, tools: readonly|all (default readonly). The body becomes an
/// extra system-prompt section for the subagent.
/// </summary>
public static class CustomAgents
{
    public const string ProjectSubdirectory = ".jarvis/agents";

    public static IReadOnlyList<CustomAgentDefinition> Load(string workingDirectory, string? userDirectory)
    {
        var byName = new Dictionary<string, CustomAgentDefinition>(StringComparer.OrdinalIgnoreCase);
        if (userDirectory is not null)
        {
            foreach (var agent in LoadDirectory(userDirectory))
                byName[agent.Name] = agent;
        }
        foreach (var agent in LoadDirectory(Path.Combine(workingDirectory, ProjectSubdirectory)))
            byName[agent.Name] = agent;
        return [.. byName.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static IEnumerable<CustomAgentDefinition> LoadDirectory(string directory)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
                return [];
            files = Directory.GetFiles(directory, "*.md");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var agents = new List<CustomAgentDefinition>();
        foreach (var file in files)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var (fields, body) = Frontmatter.Parse(content);
            if (body.Length == 0)
                continue;
            var name = (fields.TryGetValue("name", out var n) ? n : Path.GetFileNameWithoutExtension(file))
                .Trim().ToLowerInvariant();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace))
                continue;
            var description = fields.TryGetValue("description", out var d) ? d : "Custom agent";
            bool readOnly = !fields.TryGetValue("tools", out var tools) ||
                            tools.Equals("readonly", StringComparison.OrdinalIgnoreCase);
            var namedTools = tools is null || tools.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                             tools.Equals("readonly", StringComparison.OrdinalIgnoreCase) ? null
                : tools.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim('"', '\'')).ToArray();
            int? maxTurns = null;
            if ((fields.TryGetValue("max-turns", out var turns) || fields.TryGetValue("maxTurns", out turns)) &&
                int.TryParse(turns.Trim(), out int parsedTurns) && parsedTurns > 0)
            {
                maxTurns = parsedTurns;
            }
            var model = fields.TryGetValue("model", out var declaredModel) && declaredModel.Trim().Length > 0
                ? declaredModel.Trim()
                : null;
            agents.Add(new CustomAgentDefinition(
                name, description, readOnly, body, maxTurns, model, CacheTtlOf(fields, content)) { Tools = namedTools });
        }
        return agents;
    }

    /// <summary>
    /// The reference's <c>experimental.cacheTtl</c>: nested under an
    /// <c>experimental:</c> block in the frontmatter, or written flat as
    /// <c>experimental.cacheTtl</c> / <c>cacheTtl</c>. Only the two values the
    /// vendor accepts are kept.
    /// </summary>
    private static string? CacheTtlOf(IReadOnlyDictionary<string, string> fields, string content)
    {
        string? value = null;
        if (fields.TryGetValue("experimental.cacheTtl", out var flat) || fields.TryGetValue("cacheTtl", out flat))
        {
            value = flat;
        }
        else
        {
            // A nested `experimental:` block: its indented lines up to the cacheTtl key.
            var match = System.Text.RegularExpressions.Regex.Match(
                content,
                @"^experimental:[ \t]*\r?\n(?:[ \t]+\S.*\r?\n)*?[ \t]+cacheTtl:[ \t]*[""']?([^""'\r\n]+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            if (match.Success)
            {
                value = match.Groups[1].Value;
            }
        }

        value = value?.Trim().Trim('"', '\'');
        return value is "5m" or "1h" ? value : null;
    }
}
