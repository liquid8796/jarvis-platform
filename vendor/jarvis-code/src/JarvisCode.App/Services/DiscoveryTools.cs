using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// This app's own skill and plugin discovery: the skills on disk (project, user
/// and plugin) and the plugins installed here plus the cloned git marketplaces.
///
/// <para>
/// The names are snake_case and carry "local" because these are not the
/// reference's tools. Its ListSkills / SearchSkills / ListPlugins /
/// SearchPlugins query the user's claude.ai catalog through its own signed-in
/// client; these read a disk. Sharing the reference's names made the two look
/// interchangeable, which is the one kind of difference nothing looks wrong
/// about until somebody assumes the behaviour follows the name. The old names
/// stay as aliases so a stored session still replays, and are never advertised.
/// </para>
/// </summary>
public static class DiscoveryTools
{
    public static IReadOnlyList<ITool> Create(
        Func<string> cwd, string userSkillsDirectory, string pluginsDirectory, string appRoot) =>
        [
            new ListSkillsTool(cwd, userSkillsDirectory, pluginsDirectory),
            new SearchSkillsTool(cwd, userSkillsDirectory, pluginsDirectory),
            new ListPluginsTool(pluginsDirectory, appRoot),
            new SearchPluginsTool(pluginsDirectory, appRoot),
        ];

    private static IReadOnlyList<SkillDefinition> LoadSkills(
        string cwd, string userSkillsDirectory, string pluginsDirectory)
    {
        try
        {
            var skills = Skills.Load(cwd, userSkillsDirectory);
            var plugins = Plugins.Load(pluginsDirectory);
            return [.. skills, .. plugins.Skills];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string DescribeSkill(SkillDefinition skill) =>
        $"/{skill.Name} ({skill.Source}) — {skill.Description}";

    internal static bool Matches(string query, params string?[] haystacks)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length > 0 && terms.All(term =>
            haystacks.Any(h => h?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
    }

    private sealed class ListSkillsTool(Func<string> cwd, string userSkillsDirectory, string pluginsDirectory) : ITool, IAliasedTool
    {
        public string Name => "list_local_skills";

        /// <summary>The reference's name for a tool of its own, kept resolvable.</summary>
        public IReadOnlyList<string> Aliases => ["ListSkills"];

        public string Description =>
            "Lists the skills available in this session (project, user, and plugin skills) with their descriptions. " +
            "Invoke one with the skill tool.";

        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) => "ListSkills()";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var skills = LoadSkills(cwd(), userSkillsDirectory, pluginsDirectory);
            return Task.FromResult(ToolResult.Success(skills.Count == 0
                ? "No skills are installed."
                : string.Join('\n', skills.Select(DescribeSkill))));
        }
    }

    private sealed class SearchSkillsTool(Func<string> cwd, string userSkillsDirectory, string pluginsDirectory) : ITool, IAliasedTool
    {
        public string Name => "search_local_skills";

        /// <summary>The reference's name for a tool of its own, kept resolvable.</summary>
        public IReadOnlyList<string> Aliases => ["SearchSkills"];

        public string Description =>
            "Searches the installed skills by keyword (name, description, body). Use it when the user's task might " +
            "already be covered by a skill.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Keywords to match" },
            },
            ["required"] = new JsonArray("query"),
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) => $"SearchSkills({JsonArgs.GetString(arguments, "query") ?? "?"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var query = JsonArgs.GetString(arguments, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult(ToolResult.Error("query is required."));
            var matches = LoadSkills(cwd(), userSkillsDirectory, pluginsDirectory)
                .Where(s => Matches(query, s.Name, s.Description, s.Body))
                .ToList();
            return Task.FromResult(ToolResult.Success(matches.Count == 0
                ? $"No installed skill matches '{query}'."
                : string.Join('\n', matches.Select(DescribeSkill))));
        }
    }

    private sealed class ListPluginsTool(string pluginsDirectory, string appRoot) : ITool, IAliasedTool
    {
        public string Name => "list_local_plugins";

        /// <summary>The reference's name for a tool of its own, kept resolvable.</summary>
        public IReadOnlyList<string> Aliases => ["ListPlugins"];

        public string Description =>
            "Lists the installed plugins (commands/agents/skills each contributes) and the configured plugin " +
            "marketplaces.";

        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) => "ListPlugins()";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var installed = Plugins.Load(pluginsDirectory).Installed;
            var marketplaces = PluginMarketplaces.List(appRoot);
            var text = new System.Text.StringBuilder();
            text.AppendLine(installed.Count == 0
                ? "No plugins are installed."
                : "Installed plugins:\n" + string.Join('\n', installed.Select(static p =>
                    $"  {p.Name} — {p.CommandCount} command(s), {p.AgentCount} agent(s), {p.SkillCount} skill(s)" +
                    $"{(p.HasHooks ? ", hooks" : "")}{(p.HasMcp ? ", mcp" : "")}")));
            if (marketplaces.Count > 0)
            {
                text.AppendLine("Marketplaces: " + string.Join(", ", marketplaces.Select(static m => m.Name)) +
                    " — SearchPlugins finds their plugins; the user installs from Customize › Personal plugins.");
            }

            return Task.FromResult(ToolResult.Success(text.ToString().TrimEnd()));
        }
    }

    private sealed class SearchPluginsTool(string pluginsDirectory, string appRoot) : ITool, IAliasedTool
    {
        public string Name => "search_local_plugins";

        /// <summary>The reference's name for a tool of its own, kept resolvable.</summary>
        public IReadOnlyList<string> Aliases => ["SearchPlugins"];

        public string Description =>
            "Searches installed plugins and the configured marketplaces' plugins by keyword. Installation happens " +
            "in Customize › Personal plugins — point the user there rather than installing yourself.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Keywords to match" },
            },
            ["required"] = new JsonArray("query"),
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) => $"SearchPlugins({JsonArgs.GetString(arguments, "query") ?? "?"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var query = JsonArgs.GetString(arguments, "query");
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult(ToolResult.Error("query is required."));

            var lines = new List<string>();
            foreach (var plugin in Plugins.Load(pluginsDirectory).Installed)
            {
                if (Matches(query, plugin.Name))
                    lines.Add($"{plugin.Name} (installed)");
            }

            foreach (var marketplace in PluginMarketplaces.List(appRoot))
            {
                foreach (var plugin in PluginMarketplaces.Plugins(marketplace))
                {
                    if (Matches(query, plugin.Name, plugin.Description))
                        lines.Add($"{plugin.Name} ({marketplace.Name} marketplace) — {plugin.Description ?? "no description"}");
                }
            }

            return Task.FromResult(ToolResult.Success(lines.Count == 0
                ? $"Nothing matches '{query}' in the installed plugins or configured marketplaces."
                : string.Join('\n', lines)));
        }
    }
}
