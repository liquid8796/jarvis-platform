using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference's two suggestion tools, SuggestSkills and SuggestPluginInstall:
/// a card offering something the user could add, which they then enable out of
/// band rather than the model installing it for them.
///
/// The tool docs are the reference's own, because they are what tells a model
/// when *not* to call these — the reference spends most of both prompts on it,
/// and a suggestion nobody asked for is the failure mode they guard against.
/// What is behind them is not: the reference searches the user's claude.ai
/// catalog through session.host with their account credentials, and there is no
/// such catalog here, so these read the marketplaces this machine has cloned and
/// the skills already on disk that are switched off. That divergence is declared
/// in the parity suite's reference-surface-deltas.tsv, under the same names.
/// </summary>
public static class SuggestionTools
{
    /// <summary>The reference's cap on how many plugins one card may carry.</summary>
    private const int MaxPlugins = 16;

    /// <summary>The reference's cap on the header tying a suggestion to the request.</summary>
    private const int MaxContextLabel = 128;

    /// <summary>How many matches a card shows before it stops being a card.</summary>
    private const int MaxSuggestions = 8;

    public static IReadOnlyList<ITool> Create(
        Action<string> render,
        Func<string> cwd,
        string userSkillsDirectory,
        string pluginsDirectory,
        string appRoot,
        Func<UiSettings> uiSettings) =>
        [
            new SuggestSkillsTool(render, cwd, userSkillsDirectory, pluginsDirectory, appRoot, uiSettings),
            new SuggestPluginInstallTool(render),
        ];

    /// <summary>One thing a card can offer: where it comes from, and what it is.</summary>
    private readonly record struct Suggestion(string Name, string Source, string? Description);

    private sealed class SuggestSkillsTool(
        Action<string> render,
        Func<string> cwd,
        string userSkillsDirectory,
        string pluginsDirectory,
        string appRoot,
        Func<UiSettings> uiSettings) : ITool, IAliasedTool
    {
        public string Name => "suggest_local_skills";

        /// <summary>The reference's name for a tool of its own, kept resolvable.</summary>
        public IReadOnlyList<string> Aliases => ["SuggestSkills"];

        public string Description => "Render a card of standalone skills the user can add (not yet enabled).";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["keywords"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Topic keywords drawn from the task itself.",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
                ["trigger"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "How this suggestion started.",
                    ["enum"] = new JsonArray("proactive", "user_asked"),
                },
            },
            ["required"] = new JsonArray("keywords"),
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) =>
            $"SuggestSkills({string.Join(", ", Keywords(arguments))})";

        public Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var keywords = Keywords(arguments);
            if (keywords.Count == 0)
            {
                return Task.FromResult(ToolResult.Error("keywords is required."));
            }

            var proactive = JsonArgs.GetString(arguments, "trigger") != "user_asked";
            var matches = Search(keywords);
            if (matches.Count == 0)
            {
                // The reference's own instruction to the caller, in the result
                // rather than the prompt: an empty proactive search is not news.
                return Task.FromResult(ToolResult.Success(proactive
                    ? "No skills to suggest. This was proactive — continue without mentioning that you searched."
                    : "No skills to suggest: nothing switched off, and nothing in the cloned marketplaces matches."));
            }

            var text = new StringBuilder("Skills you could add:");
            foreach (var match in matches)
            {
                text.AppendLine();
                text.Append($"  {match.Name} ({match.Source})");
                if (match.Description is { Length: > 0 } description)
                {
                    text.Append(" — ").Append(Trim(description));
                }
            }

            text.AppendLine();
            text.Append("Enable a switched-off skill in Customize › Skills; install a marketplace plugin in " +
                        "Customize › Personal plugins.");

            render(text.ToString());
            return Task.FromResult(ToolResult.Success(
                $"Suggested {matches.Count} skill(s) — they render as a card; do not repeat them in prose."));
        }

        /// <summary>
        /// What "not yet enabled" means on this machine: a skill that is on disk
        /// and switched off, and a skill inside a marketplace plugin that is not
        /// installed. Both are one click from being available, which is the
        /// property the reference's card is about.
        /// </summary>
        private IReadOnlyList<Suggestion> Search(IReadOnlyList<string> keywords)
        {
            var found = new List<Suggestion>();
            var settings = uiSettings();

            try
            {
                foreach (var skill in Skills.Load(cwd(), userSkillsDirectory))
                {
                    if (SkillCatalog.OverrideFor(settings, skill.Name) == "off" &&
                        MatchesAny(keywords, skill.Name, skill.Description, skill.WhenToUse))
                    {
                        found.Add(new Suggestion(skill.Name, "switched off", skill.Description));
                    }
                }
            }
            catch (IOException)
            {
                // A skills directory that cannot be read contributes nothing.
            }

            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in Plugins.Load(pluginsDirectory).Installed)
            {
                installed.Add(plugin.Name);
            }

            foreach (var marketplace in PluginMarketplaces.List(appRoot))
            {
                foreach (var plugin in PluginMarketplaces.Plugins(marketplace))
                {
                    if (installed.Contains(plugin.Name))
                    {
                        continue;
                    }

                    foreach (var skill in SkillsIn(plugin))
                    {
                        if (MatchesAny(keywords, skill, plugin.Name, plugin.Description))
                        {
                            found.Add(new Suggestion(
                                $"{plugin.Name}:{skill}", $"{marketplace.Name} marketplace", plugin.Description));
                        }
                    }
                }
            }

            return [.. found
                .DistinctBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestions)];
        }

        /// <summary>The skill names a marketplace plugin would contribute if it were installed.</summary>
        private static IReadOnlyList<string> SkillsIn(MarketplacePlugin plugin)
        {
            try
            {
                var directory = Path.Combine(plugin.Directory, "skills");
                if (!Directory.Exists(directory))
                {
                    return [];
                }

                return
                [
                    .. Directory.GetDirectories(directory).Select(Path.GetFileName).OfType<string>(),
                    .. Directory.GetFiles(directory, "*.md").Select(Path.GetFileNameWithoutExtension).OfType<string>(),
                ];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        private static bool MatchesAny(IReadOnlyList<string> keywords, params string?[] haystacks) =>
            keywords.Any(keyword => DiscoveryTools.Matches(keyword, haystacks));

        private static IReadOnlyList<string> Keywords(JsonObject arguments) =>
            arguments["keywords"] is JsonArray array
                ? [.. array.Select(static k => k?.ToString() ?? "").Where(static k => k.Trim().Length > 0)]
                : [];
    }

    private sealed class SuggestPluginInstallTool(Action<string> render) : ITool
    {
        /// <summary>The reference's own result line, which is also the instruction that follows the card.</summary>
        private const string Note =
            "Plugin card rendered. The user enables the plugin out of band — call ListPlugins on follow-up to " +
            "discover what was actually installed.";

        public string Name => "SuggestPluginInstall";

        public string Description => "Render an inline plugin install card from search_local_plugins results.";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["contextLabel"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Short header tying the suggestion to the user request.",
                },
                ["plugins"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Plugins sourced from search_local_plugins results.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["pluginId"] = new JsonObject { ["type"] = "string" },
                            ["pluginName"] = new JsonObject { ["type"] = "string" },
                            ["description"] = new JsonObject { ["type"] = "string" },
                            ["skills"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["name"] = new JsonObject { ["type"] = "string" },
                                        ["description"] = new JsonObject { ["type"] = "string" },
                                    },
                                    ["required"] = new JsonArray("name"),
                                },
                            },
                        },
                        ["required"] = new JsonArray("pluginId", "pluginName", "description"),
                    },
                },
            },
            ["required"] = new JsonArray("contextLabel", "plugins"),
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) =>
            $"SuggestPluginInstall({(arguments["plugins"] as JsonArray)?.Count ?? 0})";

        public Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            if (arguments["plugins"] is not JsonArray plugins || plugins.Count == 0)
            {
                return Task.FromResult(ToolResult.Error("plugins must carry at least one entry."));
            }

            if (plugins.Count > MaxPlugins)
            {
                return Task.FromResult(ToolResult.Error(
                    $"plugins carries {plugins.Count} entries; the card shows at most {MaxPlugins}."));
            }

            var label = (JsonArgs.GetString(arguments, "contextLabel") ?? "").Trim();
            if (label.Length == 0)
            {
                return Task.FromResult(ToolResult.Error("contextLabel is required."));
            }

            var text = new StringBuilder(Trim(label, MaxContextLabel));
            foreach (var plugin in plugins.OfType<JsonObject>())
            {
                var name = JsonArgs.GetString(plugin, "pluginName") ?? JsonArgs.GetString(plugin, "pluginId") ?? "?";
                text.AppendLine();
                text.Append($"  {name}");
                if (JsonArgs.GetString(plugin, "description") is { Length: > 0 } description)
                {
                    text.Append(" — ").Append(Trim(description));
                }

                if (plugin["skills"] is JsonArray skills && skills.Count > 0)
                {
                    var names = skills
                        .OfType<JsonObject>()
                        .Select(static s => JsonArgs.GetString(s, "name"))
                        .OfType<string>()
                        .ToList();
                    if (names.Count > 0)
                    {
                        text.AppendLine();
                        text.Append("      skills: ").Append(string.Join(", ", names));
                    }
                }
            }

            text.AppendLine();
            text.Append("Install from Customize › Personal plugins.");

            render(text.ToString());
            return Task.FromResult(ToolResult.Success(Note));
        }
    }

    private static string Trim(string text, int limit = 200)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= limit ? single : single[..limit].TrimEnd() + "…";
    }
}
