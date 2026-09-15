using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Tools;

namespace JarvisCode.Cli;

internal static class CliAgents
{
    public static IReadOnlyList<CustomAgentDefinition> Parse(string? json)
    {
        if (json is null) return [];
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new CliError("--agents must be a JSON object."); }
        catch (JsonException ex) { throw new CliError("Invalid --agents JSON: " + ex.Message); }
        var agents = new List<CustomAgentDefinition>();
        foreach (var (name, value) in root)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || value is not JsonObject agent)
                throw new CliError("Each --agents entry needs a non-empty name and a definition object.");
            var prompt = agent["prompt"]?.GetValue<string>();
            var description = agent["description"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(description))
                throw new CliError($"Agent '{name}' requires description and prompt.");
            var model = agent["model"]?.GetValue<string>();
            int? maximum = agent["maxTurns"]?.GetValue<int>() ?? agent["max-turns"]?.GetValue<int>();
            if (maximum is <= 0) throw new CliError($"Agent '{name}' maxTurns must be positive.");
            var tools = StringList(agent["tools"]);
            var disallowed = StringList(agent["disallowedTools"]) ?? [];
            agents.Add(new CustomAgentDefinition(name, description, false, prompt, maximum,
                model is "inherit" ? null : model)
            { Tools = tools, DisallowedTools = disallowed });
        }
        return agents;
    }

    private static IReadOnlyList<string>? StringList(JsonNode? value) => value switch
    {
        null => null,
        JsonArray array => [.. array.Select(item => ToolNames.Map(item?.GetValue<string>() ??
            throw new CliError("Agent tools must be strings.")))],
        JsonValue scalar when scalar.TryGetValue<string>(out var text) =>
            [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ToolNames.Map)],
        _ => throw new CliError("Agent tools must be an array or a comma-separated string."),
    };

    public static IReadOnlyList<CustomAgentDefinition> Load(CliServices services, string cwd) =>
        [.. services.Customizations.Agents.Concat(services.Customizations.Plugins.Agents)
            .Concat(services.Customizations.DisableAutomaticDiscovery || services.Customizations.DisableAgents ? [] :
                CustomAgents.Load(cwd, services.CliSettings.Sources.Contains("user") ? services.App.Paths.UserAgentsDirectory : null))
            .DistinctBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)];

    public static CustomAgentDefinition? Primary(CliServices services, CliOptions options, string cwd)
    {
        if (options.SafeMode) return null;
        var name = options.AgentProfile ?? services.CliSettings.Agent;
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Load(services, cwd).FirstOrDefault(agent => agent.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } custom)
            return custom;
        var builtin = SubagentTool.CanonicalAgentType(name);
        if (SubagentTool.IsBuiltInAgentType(builtin))
            return new CustomAgentDefinition(builtin, "Built-in agent", SubagentTool.IsReadOnlyBuiltIn(builtin),
                ReferenceSubagentPrompt.RoleFor(builtin) ?? ReferenceSubagentPrompt.GeneralPurposeRole,
                Model: SubagentTool.BuiltInModelAlias(builtin))
                { Tools = SubagentTool.BuiltInToolNames(builtin) };
        throw new CliError($"Unknown agent '{name}'. Available: " +
            string.Join(", ", SubagentTool.BuiltInAgentTypes.Concat(Load(services, cwd).Select(agent => agent.Name))));
    }

    public static TurnSetup ApplyPrimary(TurnSetup setup, CustomAgentDefinition? profile, int? explicitMaximum)
    {
        if (profile is null) return setup;
        var known = setup.Context.Tools.All.Concat(setup.Deferred?.DeferredNames
            .Select(name => setup.Context.Tools.Find(name)).OfType<ITool>() ?? []);
        var keep = known.DistinctBy(tool => tool.Name, StringComparer.Ordinal).Where(tool =>
            (tool.Name != "ToolSearch" || profile.Tools?.Contains("ToolSearch") == true) &&
            (!profile.ReadOnlyTools || tool.IsReadOnly) && (profile.Tools is null || profile.Tools.Contains(tool.Name)) &&
            !profile.DisallowedTools.Contains(tool.Name)).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        var registry = new ToolRegistry(keep);
        var tools = setup.Context.ToolContext;
        if (tools.Subagents is { } subagents) tools = tools with { Subagents = subagents with { ParentTools = registry } };
        return setup with { Deferred = null, Context = setup.Context with
        {
            SystemPrompt = setup.Context.SystemPrompt + "\n\n# Agent: " + profile.Name + "\n" + profile.SystemPrompt,
            Tools = registry, ToolContext = tools,
            MaxIterations = explicitMaximum ?? profile.MaxTurns ?? setup.Context.MaxIterations,
        } };
    }
}
