using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Models;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli;

internal static class CliSdkControls
{
    internal static string OutputStyle(CliServices services, Session session) =>
        services.Customizations.OutputStyleOverride ?? (services.Customizations.DisableOutputStyles ? OutputStyles.DefaultName :
            services.App.UiSettings.Current.SessionOutputStyles.GetValueOrDefault(session.Id, services.App.UiSettings.Current.OutputStyle));

    internal static JsonObject InitializeInfo(CliServices services, CliOptions options, Session session)
    {
        var styles = services.Customizations.DisableAutomaticDiscovery
            ? OutputStyles.All.Concat(services.Customizations.PluginOutputStylePaths.SelectMany(plugin =>
                CustomOutputStyles.Load(null, null, "", [plugin.Path]).Select(style => style with { Name = plugin.Plugin + ":" + style.Name })))
            : OutputStyles.Available(session.WorkingDirectory, services.App.Paths.Root, services.Customizations.PluginOutputStylePaths);
        var commands = new JsonArray();
        if (!options.DisableSlashCommands)
        {
            foreach (var command in Repl.ReplCommandTable.Commands.Where(command => command.Kind != Repl.ReplCommandKind.Unavailable))
                commands.Add(new JsonObject { ["name"] = command.Name, ["description"] = command.Description,
                    ["argumentHint"] = command.ArgumentHint ?? "", ["supportsNonInteractive"] = command.Kind == Repl.ReplCommandKind.Prompt });
            foreach (var skill in services.Customizations.ResolveSkills(session.WorkingDirectory, services.App.Paths, services.App.UiSettings.Current))
                if (!commands.OfType<JsonObject>().Any(command => command["name"]?.GetValue<string>() == skill.Name))
                    commands.Add(new JsonObject { ["name"] = skill.Name, ["description"] = skill.Description,
                        ["argumentHint"] = "", ["supportsNonInteractive"] = true });
        }
        return new JsonObject
        {
            ["commands"] = commands, ["output_style"] = OutputStyle(services, session),
            ["available_output_styles"] = new JsonArray([.. new[] { OutputStyles.DefaultName }.Concat(options.SafeMode ? [] : styles.Select(style => style.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(name => (JsonNode?)JsonValue.Create(name))]),
            ["models"] = new JsonArray([.. services.App.Settings.Models.Select(model => (JsonNode)new JsonObject
            { ["value"] = model.ModelId, ["displayName"] = model.DisplayName, ["description"] = model.ProviderId })]),
        };
    }

    public static async Task<JsonObject> RewindFilesAsync(CliServices services, Session session,
        string userMessageId, CancellationToken cancellationToken)
    {
        var message = session.Messages.Concat(session.ArchivedMessages).FirstOrDefault(entry => entry.SdkUserMessageId == userMessageId)
            ?? throw new CliError("No user message with that UUID exists in this session.");
        if (message.SdkCheckpointTurnNumber is not { } turn) throw new CliError("No checkpoint is associated with that user message.");
        var result = await services.App.Checkpoints.RewindAsync(session.Id, turn, cancellationToken);
        if (result?.Skipped.Count > 0) throw new CliError("Some checkpoint files could not be restored: " + string.Join(", ", result.Skipped));
        return new JsonObject { ["canRewind"] = true,
            ["filesChanged"] = new JsonArray([.. (result?.RestoredFiles ?? []).Select(path => (JsonNode?)JsonValue.Create(path))]) };
    }

    public static JsonObject ContextUsage(CliServices services, CliTurnRunner runner, Session session, ModelInfo model)
    {
        static long Tokens(string? text) => ContextBreakdown.EstimateTokens(text);
        var setup = runner.LastSetup;
        var tools = setup?.Context.Tools.All.ToArray() ?? [];
        var prompt = Tokens(setup?.Context.SystemPrompt);
        var toolTokens = tools.Sum(tool => Tokens(tool.Name) + Tokens(tool.Description) + Tokens(tool.InputSchema.ToJsonString()));
        var messages = runner.LastMessages.Sum(message => message.Content.Sum(block => block switch
        {
            TextBlock text => Tokens(text.Text), ToolCallBlock call => Tokens(call.Name) + Tokens(call.ArgumentsJson),
            ToolResultBlock result => Tokens(result.Content), ThinkingBlock thinking => Tokens(thinking.Thinking),
            ImageBlock => 1500L, _ => 0,
        }));
        var estimates = new[] { prompt, toolTokens, messages };
        var estimate = estimates.Sum();
        var total = runner.LastContextTokens > 0 ? runner.LastContextTokens : estimate;
        var allocated = estimates.Select(tokens => estimate > 0 ? (long)Math.Floor(tokens * (double)total / estimate) : 0).ToArray();
        allocated[^1] += total - allocated.Sum();
        var names = new[] { "System prompt", "Tools", "Messages" };
        var colors = new[] { "blue", "cyan", "green" };
        var categories = new JsonArray([.. names.Select((name, index) => (JsonNode)new JsonObject
        { ["name"] = name, ["tokens"] = allocated[index], ["color"] = colors[index], ["estimated"] = true })]);
        var settings = services.App.Settings.Current;
        var window = ContextWindows.ResolveWindow(model.MaxContextTokens, settings.AutoCompactWindow,
            Environment.GetEnvironmentVariable(TurnContextFactory.AutoCompactWindowVariable), model.ModelId);
        var maximum = ContextWindows.EffectiveWindow(window.Window, ContextWindows.OutputReserveCap);
        var percentage = maximum > 0 ? Math.Min(100, total * 100.0 / maximum) : 0;
        var cells = new JsonArray();
        for (var row = 0; row < 5; row++)
            cells.Add(new JsonArray([.. Enumerable.Range(0, 20).Select(column => (JsonNode)new JsonObject
            { ["filled"] = row * 20 + column < percentage, ["color"] = row * 20 + column < percentage ? "green" : "gray" })]));
        return new JsonObject
        {
            ["categories"] = categories, ["totalTokens"] = total, ["maxTokens"] = maximum,
            ["rawMaxTokens"] = model.MaxContextTokens, ["percentage"] = percentage, ["model"] = model.ModelId,
            ["isAutoCompactEnabled"] = settings.AutoCompactEnabled,
            ["isEstimated"] = !Core.Providers.ProviderCapabilities.For(services.App.Providers.Get(model.ProviderId)).ReportsExactUsage,
            ["autoCompactThreshold"] = ContextWindows.CompactThreshold(maximum),
            ["memoryFiles"] = new JsonArray([.. runner.InstructionTokens.Select(entry => (JsonNode)new JsonObject
            { ["path"] = entry.Key, ["type"] = "instructions", ["tokens"] = entry.Value })]),
            ["mcpTools"] = new JsonArray([.. tools.Where(tool => tool.Name.StartsWith("mcp__", StringComparison.Ordinal)).Select(tool => (JsonNode)new JsonObject
            { ["name"] = tool.Name, ["serverName"] = tool.Name.Split("__")[1], ["tokens"] = Tokens(tool.Description) + Tokens(tool.InputSchema.ToJsonString()), ["isLoaded"] = true })]),
            ["agents"] = new JsonArray([.. CliAgents.Load(services, session.WorkingDirectory).Select(agent => (JsonNode)new JsonObject
            { ["agentType"] = agent.Name, ["source"] = "custom", ["tokens"] = Tokens(agent.Description) + Tokens(agent.SystemPrompt) })]),
            ["gridRows"] = cells,
            ["estimationMethod"] = "Category estimates use characters/4 and are scaled to the provider-reported total when available.",
        };
    }
}
