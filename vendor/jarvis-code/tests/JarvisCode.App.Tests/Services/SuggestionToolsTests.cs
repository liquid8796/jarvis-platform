using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// SuggestSkills and SuggestPluginInstall: what they offer, what they refuse to
/// offer, and the fact that an empty proactive search tells the caller to say
/// nothing rather than to report a blank.
/// </summary>
public sealed class SuggestionToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("suggest-tools-").FullName;
    private readonly List<string> _rendered = [];
    private readonly UiSettings _ui = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string SkillsDirectory => Path.Combine(_root, "skills");

    private string PluginsDirectory => Path.Combine(_root, "plugins");

    private (ITool Skills, ITool Plugins) Tools()
    {
        var tools = SuggestionTools.Create(
            text => _rendered.Add(text),
            () => Path.Combine(_root, "cwd"),
            SkillsDirectory,
            PluginsDirectory,
            _root,
            () => _ui);
        return (tools[0], tools[1]);
    }

    private static ToolExecutionContext Context() => new() { WorkingDirectory = @"D:\repo" };

    private void WriteUserSkill(string name, string description)
    {
        var directory = Path.Combine(SkillsDirectory, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\n---\n\nBody.\n");
    }

    /// <summary>A marketplace with one plugin that ships one skill.</summary>
    private void WriteMarketplace(string marketplace, string plugin, string description, string skill)
    {
        var pluginDirectory = Path.Combine(_root, "marketplaces", marketplace, plugin);
        Directory.CreateDirectory(Path.Combine(pluginDirectory, "skills", skill));
        File.WriteAllText(
            Path.Combine(pluginDirectory, "skills", skill, "SKILL.md"),
            $"---\nname: {skill}\ndescription: {skill} does things\n---\n\nBody.\n");

        var manifestDirectory = Path.Combine(_root, "marketplaces", marketplace, ".claude-plugin");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(Path.Combine(manifestDirectory, "marketplace.json"), new JsonObject
        {
            ["plugins"] = new JsonArray(new JsonObject
            {
                ["name"] = plugin,
                ["description"] = description,
                ["source"] = $"./{plugin}",
            }),
        }.ToJsonString());
    }

    private static JsonObject Keywords(string keyword, string? trigger = null)
    {
        var arguments = new JsonObject { ["keywords"] = new JsonArray(keyword) };
        if (trigger is not null)
        {
            arguments["trigger"] = trigger;
        }

        return arguments;
    }

    [Fact]
    public async Task SuggestSkills_needsKeywords()
    {
        var result = await Tools().Skills.ExecuteAsync(new JsonObject(), Context(), default);

        Assert.True(result.IsError);
        Assert.Empty(_rendered);
    }

    [Fact]
    public async Task SuggestSkills_offersASkillTheUserSwitchedOff()
    {
        WriteUserSkill("deploy-notes", "Write the deployment notes in the house style");
        _ui.SkillOverrides["deploy-notes"] = "off";

        var result = await Tools().Skills.ExecuteAsync(Keywords("deployment"), Context(), default);

        Assert.False(result.IsError);
        Assert.Contains("Suggested 1 skill(s)", result.Content);
        var card = Assert.Single(_rendered);
        Assert.Contains("deploy-notes (switched off)", card);
        Assert.Contains("Write the deployment notes in the house style", card);
    }

    [Fact]
    public async Task SuggestSkills_leavesAnEnabledSkillAlone()
    {
        // "not yet enabled" is the whole point of the card: a skill already on
        // offer is not something to add.
        WriteUserSkill("deploy-notes", "Write the deployment notes in the house style");

        var result = await Tools().Skills.ExecuteAsync(Keywords("deployment"), Context(), default);

        Assert.False(result.IsError);
        Assert.Empty(_rendered);
    }

    [Fact]
    public async Task SuggestSkills_saysNothingWhenAProactiveSearchIsEmpty()
    {
        var proactive = await Tools().Skills.ExecuteAsync(Keywords("nothing-here"), Context(), default);
        Assert.False(proactive.IsError);
        Assert.Contains("continue without mentioning that you searched", proactive.Content);
        Assert.Empty(_rendered);

        var asked = await Tools().Skills.ExecuteAsync(
            Keywords("nothing-here", "user_asked"), Context(), default);
        Assert.False(asked.IsError);
        Assert.DoesNotContain("continue without mentioning", asked.Content);
    }

    [Fact]
    public async Task SuggestSkills_offersAMarketplaceSkillUntilItsPluginIsInstalled()
    {
        WriteMarketplace("acme", "linting", "Linting and formatting helpers", "tidy");

        var offered = await Tools().Skills.ExecuteAsync(Keywords("linting"), Context(), default);
        Assert.False(offered.IsError);
        Assert.Contains("linting:tidy (acme marketplace)", Assert.Single(_rendered));

        // Installed means available, so it stops being a suggestion.
        Directory.CreateDirectory(Path.Combine(PluginsDirectory, "linting"));
        _rendered.Clear();

        var afterInstall = await Tools().Skills.ExecuteAsync(Keywords("linting"), Context(), default);
        Assert.False(afterInstall.IsError);
        Assert.Empty(_rendered);
    }

    [Fact]
    public async Task SuggestPluginInstall_rendersTheCardAndReturnsTheReferenceNote()
    {
        var result = await Tools().Plugins.ExecuteAsync(new JsonObject
        {
            ["contextLabel"] = "For the linting you asked about",
            ["plugins"] = new JsonArray(new JsonObject
            {
                ["pluginId"] = "acme/linting",
                ["pluginName"] = "linting",
                ["description"] = "Linting and formatting helpers",
                ["skills"] = new JsonArray(new JsonObject { ["name"] = "tidy" }),
            }),
        }, Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(
            "Plugin card rendered. The user enables the plugin out of band — call ListPlugins on follow-up to " +
            "discover what was actually installed.",
            result.Content);

        var card = Assert.Single(_rendered);
        Assert.Contains("For the linting you asked about", card);
        Assert.Contains("linting — Linting and formatting helpers", card);
        Assert.Contains("skills: tidy", card);
    }

    [Fact]
    public async Task SuggestPluginInstall_refusesAnEmptyOrOversizedCard()
    {
        var (_, plugins) = Tools();

        var empty = await plugins.ExecuteAsync(
            new JsonObject { ["contextLabel"] = "x", ["plugins"] = new JsonArray() }, Context(), default);
        Assert.True(empty.IsError);

        var unlabelled = await plugins.ExecuteAsync(new JsonObject
        {
            ["contextLabel"] = "   ",
            ["plugins"] = new JsonArray(new JsonObject { ["pluginName"] = "a", ["description"] = "b" }),
        }, Context(), default);
        Assert.True(unlabelled.IsError);

        var many = new JsonArray();
        for (int i = 0; i < 17; i++)
        {
            many.Add(new JsonObject { ["pluginName"] = $"p{i}", ["description"] = "d" });
        }

        var oversized = await plugins.ExecuteAsync(
            new JsonObject { ["contextLabel"] = "x", ["plugins"] = many }, Context(), default);
        Assert.True(oversized.IsError);
        Assert.Contains("at most 16", oversized.Content);

        Assert.Empty(_rendered);
    }

    [Fact]
    public void ToolsCarryTheReferenceDescriptions()
    {
        var (skills, plugins) = Tools();

        Assert.Equal("suggest_local_skills", skills.Name);
        Assert.Equal(["SuggestSkills"], ((IAliasedTool)skills).Aliases);
        Assert.Equal(
            "Render a card of standalone skills the user can add (not yet enabled).", skills.Description);
        Assert.Equal("SuggestPluginInstall", plugins.Name);
        Assert.Equal(
            "Render an inline plugin install card from search_local_plugins results.", plugins.Description);
        Assert.True(skills.IsReadOnly);
        Assert.True(plugins.IsReadOnly);
    }
}
