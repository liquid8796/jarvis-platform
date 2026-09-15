using System.IO;
using JarvisCode.App.Composition;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Customization;
using JarvisCode.Core.Memory;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// One slice of the context window in the breakdown. A deferred slice is
/// listed but counted out of the totals, as the reference counts it out.
/// </summary>
public sealed record ContextCategory(string Label, long Tokens, int Count = -1, bool IsDeferred = false);

/// <summary>One row of the detail tables /context prints under the category table.</summary>
public sealed record ContextDetail(string Name, string Source, long Tokens);

/// <summary>What the context-window popup and /context render.</summary>
public sealed record ContextSnapshot(
    long Cap,
    long Window,
    AutoCompactWindowSource WindowSource,
    long UsedTokens,
    long FreeTokens,
    IReadOnlyList<ContextCategory> Categories)
{
    public bool IsEstimated { get; init; }
    /// <summary>The model the numbers were measured against.</summary>
    public string ModelName { get; init; } = "";

    public IReadOnlyList<ContextDetail> McpTools { get; init; } = [];

    public IReadOnlyList<ContextDetail> Agents { get; init; } = [];

    public IReadOnlyList<ContextDetail> MemoryFiles { get; init; } = [];

    public IReadOnlyList<ContextDetail> Skills { get; init; } = [];

    public double UsedPercent => Cap <= 0 ? 0 : Math.Min(100, UsedTokens * 100.0 / Cap);

    /// <summary>
    /// The reference's <c>d</c>: over the raw window it is a hard limit when
    /// the model's own window decided it, and an overshot compaction window
    /// when anything else did.
    /// </summary>
    public bool IsOverLimit => UsedTokens > Cap;
}

/// <summary>
/// Estimates how the active session's context window is spent, the way the
/// reference app's context popup breaks it down: messages, system tools, MCP
/// tools, skills, memory files, system prompt and custom agents. Counts are
/// char/4 estimates; when the provider has reported a real context size, the
/// messages slice absorbs the difference so the total matches the pill.
/// </summary>
public static class ContextBreakdown
{
    public static long EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static ContextSnapshot Compute(AppServices services, ChatViewModel viewModel)
    {
        var model = viewModel.CurrentModel;
        long cap = model?.MaxContextTokens ?? 0;
        var session = viewModel.Session;
        var cwd = session.WorkingDirectory;
        var paths = services.Paths;

        IReadOnlyList<SkillDefinition> skills = [];
        IReadOnlyList<CustomAgentDefinition> agents = [];
        var pluginAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? memoryIndex = null;
        string? memoryIndexPath = null;
        int memoryFileCount = 0;
        string systemPrompt = "";
        try
        {
            var plugins = PluginLibrary.LoadUser(paths);
            var ui = services.UiSettings.Current;
            skills = SkillCatalog.ForListing(
                SkillCatalog.Enabled(SkillCatalog.LoadAll(cwd, paths), ui), ui, viewModel.SkillState);
            agents = [.. CustomAgents.Load(cwd, paths.UserAgentsDirectory), .. plugins.Agents];
            foreach (var pluginAgent in plugins.Agents)
                pluginAgents.Add(pluginAgent.Name);
            var memoryDirectory = ProjectMemory.DirectoryFor(paths.MemoryRoot, cwd);
            memoryIndex = ProjectMemory.ReadIndexForPrompt(memoryDirectory);
            if (Directory.Exists(memoryDirectory))
            {
                memoryFileCount = Directory.EnumerateFiles(memoryDirectory, "*.md").Count();
                memoryIndexPath = Path.Combine(memoryDirectory, "MEMORY.md");
            }

            if (model is not null)
            {
                // The same sections the turn sends, so the pill counts what the
                // request will actually carry. PathFor rather than Ensure: a
                // breakdown is a reading, and must not create directories.
                systemPrompt = ReferencePromptBuilder.Build(
                    cwd, model, skills: null, memoryDirectory: memoryDirectory,
                    additionalDirectories: session.AdditionalDirectories,
                    gitStatus: TurnContextFactory.GitStatusForSession(session.Id, cwd),
                    scratchpadDirectory: SessionScratchpad.PathFor(cwd, session.Id),
                    hostSections: HostPromptSections.Render(new HostPromptSections.Capabilities(
                        ClickableFileLinks: !services.Headless,
                        RunButtonOnShellFences: !services.Headless,
                        HasInAppBrowser: !services.Headless,
                        HasChromeBrowserSurface: services.Browser.IsConnected)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The popup is informational; partial numbers beat an error.
        }

        long DefinitionTokens(ITool tool) =>
            EstimateTokens(tool.Name) + EstimateTokens(tool.Description) +
            EstimateTokens(tool.InputSchema.ToJsonString());

        var mcpTools = services.Mcp.Tools;
        long mcpTokens = mcpTools.Sum(DefinitionTokens);
        // Whenever tool search engages those tools ride it instead of the tools
        // block — listed, but not spent. Same answer the factory reaches.
        bool mcpDeferred = mcpTools.Count > 0 &&
            JarvisCode.Core.Tools.ToolSearchAvailability.IsEnabled(model?.ModelId);

        long systemToolTokens = BuiltInToolDocs(services, model).Sum(DefinitionTokens) +
            viewModel.ExtraTools.Sum(DefinitionTokens);

        // The reference listing rides a system-reminder: the slice is its
        // budgeted rendering, not the raw name+description sums.
        long skillTokens = skills.Count == 0 ? 0 : EstimateTokens(SkillInvocation.BuildListing(
            skills, SkillCatalog.UsageScores(services.UiSettings.Current),
            budgetChars: Math.Max(1, (int)((model?.MaxContextTokens ?? 200_000) * 4L *
                SkillInvocation.ListingBudgetFraction))));
        long agentTokens = agents.Sum(a => EstimateTokens(a.Name) + EstimateTokens(a.Description) + 3);
        long memoryTokens = EstimateTokens(memoryIndex);
        long promptTokens = EstimateTokens(systemPrompt);

        long messageTokens = session.Messages.Sum(message => message.Content.Sum(block => block switch
        {
            JarvisCode.Core.Models.TextBlock text => EstimateTokens(text.Text),
            ToolCallBlock call => EstimateTokens(call.Name) + EstimateTokens(call.ArgumentsJson),
            ToolResultBlock result => EstimateTokens(result.Content),
            ThinkingBlock thinking => EstimateTokens(thinking.Thinking),
            _ => 4,
        }));

        long fixedTokens = (mcpDeferred ? 0 : mcpTokens) +
            systemToolTokens + skillTokens + memoryTokens + promptTokens + agentTokens;
        // A provider-reported context size is truth; the messages slice absorbs it.
        long realUsed = viewModel.LastContextTokens;
        if (realUsed > fixedTokens + messageTokens)
        {
            messageTokens = realUsed - fixedTokens;
        }

        var settings = services.Settings.Current;
        var window = ContextWindows.ResolveWindow(
            (int)cap,
            settings.AutoCompactWindow,
            Environment.GetEnvironmentVariable(TurnContextFactory.AutoCompactWindowVariable),
            model?.ModelId);

        // The reference's buffer slice: the room auto-compaction keeps free when
        // something other than the model's own window set it, three thousand
        // tokens when auto-compaction is off, and nothing at all in between.
        long bufferTokens = 0;
        string? bufferLabel = null;
        if (!(settings.AutoCompactEnabled && window.Source == AutoCompactWindowSource.Auto))
        {
            if (settings.AutoCompactEnabled)
            {
                long threshold = ContextWindows.CompactThreshold(
                    ContextWindows.EffectiveWindow(window.Window, ContextWindows.OutputReserveCap));
                bufferTokens = Math.Max(0, window.Window - threshold);
                bufferLabel = AutocompactBufferLabel;
            }
            else
            {
                bufferTokens = ContextWindows.BlockedReserveTokens;
                bufferLabel = CompactBufferLabel;
            }
        }

        // The reference's push order, each slice omitted when it is empty.
        List<ContextCategory> categories = [];
        void Add(string label, long tokens, int count = -1, bool deferred = false)
        {
            if (tokens > 0)
                categories.Add(new ContextCategory(label, tokens, count, deferred));
        }

        Add("System prompt", promptTokens);
        Add("System tools", systemToolTokens);
        if (!mcpDeferred)
            Add("MCP tools", mcpTokens, mcpTools.Count);
        else
            Add("MCP tools (deferred)", mcpTokens, mcpTools.Count, deferred: true);
        Add("Custom agents", agentTokens, agents.Count);
        Add("Memory files", memoryTokens, memoryFileCount);
        Add("Skills", skillTokens, skills.Count);
        Add("Messages", messageTokens);

        long used = categories.Where(static c => !c.IsDeferred).Sum(static c => c.Tokens);
        if (bufferLabel is not null)
            categories.Add(new ContextCategory(bufferLabel, bufferTokens));
        long free = Math.Max(0, window.Window - used - bufferTokens);
        categories.Add(new ContextCategory(FreeSpaceLabel, free));

        return new ContextSnapshot(cap, window.Window, window.Source, used, free, categories)
        {
            IsEstimated = model is not null && !JarvisCode.Core.Providers.ProviderCapabilities
                .For(services.Providers.Get(model.ProviderId)).ReportsExactUsage,
            ModelName = model?.ModelId ?? "",
            // An MCP tool is named mcp__{server}__{tool}; the server is the middle part.
            McpTools = [.. mcpTools.Select(tool => new ContextDetail(
                McpToolName(tool.Name), McpServerName(tool.Name), DefinitionTokens(tool)))],
            Agents = [.. agents.Select(agent => new ContextDetail(
                agent.Name,
                pluginAgents.Contains(agent.Name) ? "plugin" : "project",
                EstimateTokens(agent.Name) + EstimateTokens(agent.Description) + 3))],
            MemoryFiles = memoryTokens > 0 && memoryIndexPath is not null
                ? [new ContextDetail("index", memoryIndexPath, memoryTokens)]
                : [],
            Skills = [.. skills.Select(skill => new ContextDetail(
                skill.Name, skill.Source,
                EstimateTokens(skill.Name) + EstimateTokens(skill.Description)))],
        };
    }

    private static string McpServerName(string toolName)
    {
        var parts = toolName.Split("__", 3);
        return parts.Length >= 2 && toolName.StartsWith("mcp__", StringComparison.Ordinal) ? parts[1] : "";
    }

    private static string McpToolName(string toolName)
    {
        var parts = toolName.Split("__", 3);
        return parts.Length == 3 && toolName.StartsWith("mcp__", StringComparison.Ordinal) ? parts[2] : toolName;
    }

    /// <summary>The reference's <c>wie</c>.</summary>
    public const string AutocompactBufferLabel = "Autocompact buffer";

    /// <summary>Its <c>Tie</c>, shown in the buffer's place when auto-compaction is off.</summary>
    public const string CompactBufferLabel = "Compact buffer";

    /// <summary>Its <c>N$</c>.</summary>
    public const string FreeSpaceLabel = "Free space";

    /// <summary>The same built-in set the factory joins into a Code turn.</summary>
    private static IEnumerable<ITool> BuiltInToolDocs(AppServices services, ModelInfo? model)
    {
        var settings = services.Settings.Current;
        List<ITool> tools =
        [
            new JarvisCode.Core.Tools.BuiltIn.ReadFileTool(),
            new JarvisCode.Core.Tools.BuiltIn.WriteFileTool(),
            new JarvisCode.Core.Tools.BuiltIn.EditFileTool(),
            new JarvisCode.Core.Tools.BuiltIn.ListDirectoryTool(),
            new JarvisCode.Core.Tools.BuiltIn.GlobTool(),
            new JarvisCode.Core.Tools.BuiltIn.GrepTool(),
            new JarvisCode.Core.Tools.BuiltIn.ShellTool(),
            new JarvisCode.Core.Tools.BuiltIn.ShellTool(JarvisCode.Core.Tools.BuiltIn.ShellKind.Bash),
            model is null
                ? new JarvisCode.Core.Tools.BuiltIn.WebFetchTool(services.Http)
                : new VendorWebFetchTool(
                    services.Http, services.Providers.Get(model.ProviderId), model.ModelId,
                    JarvisCode.Core.Providers.ThinkingEffort.Off),
            new JarvisCode.Core.Tools.BuiltIn.TodoTool(),
            new JarvisCode.Core.Tools.BuiltIn.MemoryTool(),
            new JarvisCode.Core.Tools.BuiltIn.SkillTool(),
            new JarvisCode.Core.Tools.BuiltIn.TaskOutputTool(),
            new JarvisCode.Core.Tools.BuiltIn.TaskKillTool(),
            new JarvisCode.Core.Tools.BuiltIn.GitStatusTool(),
            new JarvisCode.Core.Tools.BuiltIn.GitDiffTool(),
            new JarvisCode.Core.Tools.BuiltIn.GitLogTool(),
            new JarvisCode.Core.Tools.BuiltIn.GitShowTool(),
            new JarvisCode.Core.Tools.BuiltIn.GitBlameTool(),
            new JarvisCode.Core.Tools.BuiltIn.AskUserQuestionTool(),
            new SendMessageTool(),
            new SubagentTool(services.Orchestrator),
        ];
        if (settings.EnableWebSearch)
        {
            tools.Add(new JarvisCode.Core.Tools.BuiltIn.WebSearchTool(services.Http, settings.SearxngBaseUrl));
        }
        else if (model is not null && VendorWebSearchTool.IsEnabled(
                     model.ProviderId,
                     model.ModelId,
                     services.Providers.Get(model.ProviderId)
                         is not JarvisCode.Providers.LlmApi.LlmApiProvider relay ||
                     relay.Protocol == JarvisCode.Providers.LlmApi.LlmApiProtocol.Anthropic))
        {
            // Estimation only — the effort and session id never reach a request here.
            tools.Add(new VendorWebSearchTool(
                services.Providers.Get(model.ProviderId), model.ModelId,
                JarvisCode.Core.Providers.ThinkingEffort.Off, sessionId: ""));
        }

        // The Agent doc has a per-model form, so the estimate reads the same
        // profile the turn will.
        return ReferenceToolDocs.Apply(tools, profile: PromptModelProfile.For(model?.ModelId ?? ""));
    }
}
