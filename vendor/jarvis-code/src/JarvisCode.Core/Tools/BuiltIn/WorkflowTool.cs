using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Core.Agent;

namespace JarvisCode.Core.Tools.BuiltIn;

/// <summary>
/// The advisory size of the workflows the model writes — the reference's
/// `workflowSizeGuideline`, whose caps are 5 / 15 / 50 agents. It is a
/// guideline, not an enforced limit: nothing rejects a bigger script.
/// </summary>
public enum WorkflowSize
{
    Unrestricted,
    Small,
    Medium,
    Large,
}

/// <summary>The guideline's own wording, ported from the reference.</summary>
public static class WorkflowSizeGuideline
{
    /// <summary>The default when nothing is configured.</summary>
    public const WorkflowSize Default = WorkflowSize.Medium;

    /// <summary>Agent counts each size aims to stay under.</summary>
    public static int? Cap(WorkflowSize size) => size switch
    {
        WorkflowSize.Small => 5,
        WorkflowSize.Medium => 15,
        WorkflowSize.Large => 50,
        _ => null,
    };

    public static string Name(WorkflowSize size) => size switch
    {
        WorkflowSize.Small => "small",
        WorkflowSize.Medium => "medium",
        WorkflowSize.Large => "large",
        _ => "unrestricted",
    };

    public static WorkflowSize Parse(string? value) => value switch
    {
        "small" => WorkflowSize.Small,
        "medium" => WorkflowSize.Medium,
        "large" => WorkflowSize.Large,
        "unrestricted" => WorkflowSize.Unrestricted,
        _ => Default,
    };

    /// <summary>"medium — keep workflows under 15 agents".</summary>
    public static string Describe(WorkflowSize size) =>
        Cap(size) is { } cap ? $"{Name(size)} — keep workflows under {cap} agents" : Name(size);

    /// <summary>"medium (aim for &lt;15 agents)", the listing form.</summary>
    public static string Label(WorkflowSize size, bool isDefault)
    {
        if (isDefault && size != WorkflowSize.Unrestricted)
            return $"{Name(size)} (default)";
        return Cap(size) is { } cap ? $"{Name(size)} (aim for <{cap} agents)" : Name(size);
    }

    public const string NotAHardLimit =
        "This is a guideline, not a hard limit — follow it unless the user's prompt calls for a " +
        "different scale.";

    /// <summary>The block appended to the tool description; empty when unrestricted.</summary>
    public static string PromptBlock(WorkflowSize size, bool isDefault)
    {
        if (size == WorkflowSize.Unrestricted)
            return "";
        var opening = isDefault
            ? "This session has the default workflow size guideline:"
            : "A workflow size guideline is configured for this session:";
        var closing = isDefault
            ? " The user can raise or remove it with \"Dynamic workflow size\" in /config."
            : "";
        return $"\n\n{opening} {Describe(size)}. {NotAHardLimit}{closing}";
    }

    /// <summary>What the model is told when the size changes mid-session.</summary>
    public static string ChangeNotice(WorkflowSize size) =>
        size == WorkflowSize.Unrestricted
            ? "Workflow size is now unrestricted — no size guideline applies."
            : $"The workflow size guideline for this session changed: {Describe(size)}. {NotAHardLimit}";
}

/// <summary>What a workflow script declares about itself in <c>export const meta</c>.</summary>
public sealed record WorkflowMeta(
    string Name,
    string Description,
    string? WhenToUse = null,
    IReadOnlyList<JarvisCode.Core.Agent.WorkflowPhaseDeclaration>? Phases = null)
{
    /// <summary>
    /// The reference's phase listing for a saved workflow's slash command: a
    /// "Phases:" block of "- title: detail" lines, the detail dropped when the
    /// declaration carries none.
    /// </summary>
    public string PhasesPromptBlock() =>
        Phases is not { Count: > 0 } phases
            ? ""
            : "\n\nPhases:\n" + string.Join(
                "\n",
                phases.Select(p => $"- {p.Title}{(p.Detail is { Length: > 0 } d ? $": {d}" : "")}"));
}

/// <summary>
/// A host's handle on one workflow run: where its log lines, its live progress
/// and its completion go.
/// </summary>
public sealed record WorkflowRunHandle(
    Action<string> Log,
    Action<bool> Finished,
    Action<JarvisCode.Core.Agent.WorkflowProgressEntry>? Progress = null);

/// <summary>Everything a session lends the Workflow tool.</summary>
public sealed record WorkflowServices
{
    /// <summary>Saved workflows, looked up by name.</summary>
    public required WorkflowStore Store { get; init; }

    /// <summary>Run journals and persisted scripts live here; null disables resume.</summary>
    public string? RunDirectory { get; init; }

    /// <summary>The "Dynamic workflows" setting; off refuses with the reference's line.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Managed settings <c>disableWorkflows</c>: a harder off.</summary>
    public bool DisabledByManagedSettings { get; init; }

    /// <summary>Restricts the tool to saved workflows ({name, args} only).</summary>
    public bool NamedWorkflowsOnly { get; init; }

    /// <summary>Environment variable named in the named-only refusal.</summary>
    public string NamedOnlyVariable { get; init; } = "CLAUDE_WORKFLOW_NAME_ONLY";

    public WorkflowBudget? Budget { get; init; }

    /// <summary>Advisory size for the workflows the model writes.</summary>
    public WorkflowSize Size { get; init; } = WorkflowSizeGuideline.Default;

    /// <summary>True while no setting overrides the default size.</summary>
    public bool SizeIsDefault { get; init; } = true;

    /// <summary>
    /// Announces a run to the host so it can list it: given the run's title, it
    /// returns a sink for log lines and a completion callback.
    /// </summary>
    public Func<string, WorkflowRunHandle>? RunSink { get; init; }
}

/// <summary>
/// Runs a dynamic workflow — the reference CLI's Workflow tool (alias
/// RunWorkflow). The model writes deterministic JavaScript that orchestrates
/// subagents; this tool compiles it, runs it against the session's subagent
/// machinery, and returns whatever the script returned.
/// </summary>
public sealed partial class WorkflowTool(
    AgentOrchestrator orchestrator,
    WorkflowSize size = WorkflowSizeGuideline.Default,
    bool sizeIsDefault = true) : ITool, IAliasedTool
{
    public const string ToolName = "Workflow";

    /// <summary>The reference's alias for the same tool (its RunWorkflow).</summary>
    public const string AliasName = "run_workflow";

    public string Name => ToolName;

    public IReadOnlyList<string> Aliases => [AliasName];

    public string Description =>
        "Orchestrate subagents with a deterministic JavaScript workflow. Use it for multi-step orchestration " +
        "where control flow should be deterministic (loops, conditionals, fan-out) rather than model-driven: " +
        "the script decides what runs, in what order, and how results combine, while each agent() call is a " +
        "fresh subagent.\n\n" +
        "ONLY call this tool when the user has explicitly opted into multi-agent orchestration. Workflows can " +
        "spawn dozens of agents and consume a large amount of tokens; the user must request that scale, not " +
        "have it inferred. Explicit opt-in means one of:\n" +
        "- The user included the keyword \"ultracode\" in their prompt (you'll see a system-reminder " +
        "confirming it).\n" +
        "- Ultracode is on for the session (a system-reminder confirms it).\n" +
        "- The user directly asked you to run a workflow or use multi-agent orchestration in their own words " +
        "(\"use a workflow\", \"run a workflow\", \"fan out agents\", \"orchestrate this with subagents\"). The " +
        "ask must be in the user's words — a task that would merely benefit from a workflow does not count.\n" +
        "- The user invoked a skill or slash command whose instructions tell you to call the workflow tool.\n" +
        "- The user asked you to run a specific named or saved workflow.\n\n" +
        "For any other task — even one that would clearly benefit from parallelism — do NOT call this tool. " +
        "Use the Agent tool (if available) for individual subagents, or briefly describe what a " +
        "multi-agent workflow could do and how much it would roughly cost, and ask the user whether to run " +
        "it. Mention they can ask for one with \"use a workflow\" in a future message to skip the ask.\n\n" +
        "Pass the script inline via `script` — do not Write it to a file first. Every invocation persists its " +
        "script under the session directory and returns the path in the tool result; to iterate, edit that file " +
        "and re-invoke with `{scriptPath: \"<path>\"}` instead of resending the whole script. Invoke a saved " +
        "workflow with `{name, args}`.\n\n" +
        "Every script must begin with `export const meta = {...}`:\n" +
        "  export const meta = {\n" +
        "    name: 'find-flaky-tests',\n" +
        "    description: 'Find flaky tests and propose fixes',   // one-line, shown in the permission dialog\n" +
        "    phases: [\n" +
        "      { title: 'Scan', detail: 'grep test logs for retries' },\n" +
        "      { title: 'Fix', detail: 'one agent per flaky test' },\n" +
        "    ],\n" +
        "  }\n" +
        "The `meta` object must be a PURE LITERAL — no variables, function calls, spreads, or template " +
        "interpolation. Required fields: `name`, `description`. Optional: `whenToUse`, `phases`.\n\n" +
        "Script body hooks:\n" +
        "- agent(prompt: string, opts?: {label?: string, phase?: string, schema?: object, model?: string, " +
        "effort?: string, isolation?: 'worktree', agentType?: string}): Promise<any> — spawn a subagent. Without schema, returns its final text as a " +
        "string. With schema (a JSON Schema), the subagent must answer with an object matching it and agent() " +
        "returns the validated object — no parsing needed. Returns null if the subagent fails (filter with " +
        ".filter(Boolean)). opts.phase assigns the call to a progress group; use it inside pipeline()/parallel() " +
        "stages instead of the global phase() state. opts.effort overrides the reasoning effort for this " +
        "agent call ('low' | 'medium' | 'high' | 'xhigh' | 'max') — omit to inherit the session effort; use " +
        "'low' for cheap mechanical stages and higher tiers only for the hardest verify/judge stages. " +
        "opts.isolation: 'worktree' runs the agent in a fresh git worktree — EXPENSIVE (setup + disk per " +
        "agent), use ONLY when agents mutate files in parallel and would otherwise conflict; the worktree is " +
        "auto-removed if unchanged.\n" +
        "- pipeline(items, stage1, stage2, ...): Promise<any[]> — run each item through all stages " +
        "independently, NO barrier between stages. This is the DEFAULT for multi-stage work. Every stage " +
        "callback receives (prevResult, originalItem, index). A stage that throws drops that item to `null` and " +
        "skips its remaining stages.\n" +
        "- parallel(thunks: Array<() => Promise<any>>): Promise<any[]> — run tasks concurrently. This is a " +
        "BARRIER: awaits all thunks before returning. A thunk that throws (or whose agent errors) resolves to " +
        "`null`, so `.filter(Boolean)` before using the results. Use ONLY when you genuinely need all results " +
        "together.\n" +
        "- log(message: string): void — emit a progress message to the user.\n" +
        "- phase(title: string): void — start a new phase; subsequent agent() calls are grouped under it.\n" +
        "- args: any — the value passed as the tool's `args` input, verbatim (undefined if not provided). Pass " +
        "arrays/objects as actual JSON values, NOT as a JSON-encoded string.\n" +
        "- budget: {total: number|null, spent(): number, remaining(): number} — the turn's token target. " +
        "`total` is null when none was set and `remaining()` is then Infinity. The target is a HARD ceiling: " +
        "once spent() reaches total, further agent() calls throw.\n" +
        "- workflow(nameOrRef: string | {scriptPath: string}, args?: any): Promise<any> — run another saved " +
        "workflow inline and return its result. Nesting is one level only: workflow() inside a child throws.\n\n" +
        "DEFAULT TO pipeline(). Only reach for a barrier (parallel between stages) when stage N genuinely needs " +
        "cross-item context from all of stage N-1 — dedup across the full set, early-exit on an empty result, " +
        "or a prompt that references the other findings.\n\n" +
        "Scripts are plain JavaScript, NOT TypeScript — type annotations, interfaces and generics fail to " +
        "parse. The body runs in an async context, so use await directly. Standard JS built-ins are available " +
        "EXCEPT `Date.now()`/`Math.random()`/argless `new Date()`, which throw (they would break resume): pass " +
        "timestamps via `args`, stamp results after the workflow returns, and vary agent prompts by index for " +
        "randomness. No filesystem or Node.js API access.\n\n" +
        $"Concurrent agent() calls are capped at min(16, available CPUs - 2) per workflow — excess calls queue. " +
        $"Total agent count across a run is capped at {WorkflowEngine.MaxAgentsPerRun}, and one " +
        $"parallel()/pipeline() call accepts at most {WorkflowEngine.MaxItemsPerCall} items; passing more is an " +
        "explicit error, not a silent truncation.\n\n" +
        "## Resume\n\n" +
        "The tool result includes a runId. To resume after a stop or a script edit, relaunch with " +
        "`{scriptPath, resumeFromRunId}` — agent() calls the run already made return their recorded results " +
        "instantly, and anything new runs live. Read the run's journal.jsonl before diagnosing an unexpected " +
        "result: it records what each agent actually returned." +
        // The size guideline rides the end of the doc, as the reference appends it.
        WorkflowSizeGuideline.PromptBlock(size, sizeIsDefault);

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("script", SchemaBuilder.String(
                "The workflow script: `export const meta = {...}` followed by the body. Plain JavaScript.")),
            ("scriptPath", SchemaBuilder.String(
                "Path to a script file to run instead of `script` (from an earlier run's result, or one you wrote)")),
            ("name", SchemaBuilder.String("Name of a saved workflow to run")),
            // Declared by the reference and ignored by it — the script's meta block
            // is where a workflow's description and title live.
            ("description", SchemaBuilder.String("Ignored — set the workflow description in the script's `meta` block.")),
            ("title", SchemaBuilder.String("Ignored — set the workflow title in the script's `meta` block.")),
            ("args", new JsonObject
            {
                ["description"] = "Value handed to the script as its `args` global, verbatim",
            }),
            ("resumeFromRunId", SchemaBuilder.String(
                "Replay the recorded results of a previous run's matching agent() calls")),
        ]);

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        var name = JsonArgs.GetString(arguments, "name");
        if (!string.IsNullOrWhiteSpace(name))
            return $"Workflow({name})";
        var script = JsonArgs.GetString(arguments, "script");
        if (script is not null && ParseMeta(script) is { } meta)
            return $"Workflow({meta.Name}: {meta.Description})";
        return "Workflow(script)";
    }

    [GeneratedRegex(@"export\s+const\s+meta\s*=\s*(\{)", RegexOptions.Singleline)]
    private static partial Regex MetaDeclaration();

    /// <summary>
    /// Reads the script's `export const meta = {...}` header. The reference
    /// requires a pure literal, so this reads it as JSON5-ish text rather than
    /// evaluating anything.
    /// </summary>
    public static WorkflowMeta? ParseMeta(string script)
    {
        var match = MetaDeclaration().Match(script);
        if (!match.Success)
            return null;

        var start = match.Groups[1].Index;
        var depth = 0;
        var end = -1;
        for (int i = start; i < script.Length; i++)
        {
            if (script[i] == '{')
                depth++;
            else if (script[i] == '}' && --depth == 0)
            {
                end = i;
                break;
            }
        }

        if (end < 0)
            return null;

        var body = script[start..(end + 1)];
        var name = ReadField(body, "name");
        var description = ReadField(body, "description");
        if (name is null || description is null)
            return null;
        // Each phase is `{ title: '…', detail: '…' }` — detail optional, and on
        // either side of the title in the literal.
        var phases = Regex.Matches(body, @"\{(?<entry>[^{}]*\btitle\s*:[^{}]*)\}")
            .Select(m => new JarvisCode.Core.Agent.WorkflowPhaseDeclaration(
                ReadField(m.Groups["entry"].Value, "title") ?? "",
                ReadField(m.Groups["entry"].Value, "detail")))
            .Where(phase => phase.Title.Length > 0)
            .ToList();
        return new WorkflowMeta(name, description, ReadField(body, "whenToUse"),
            phases.Count == 0 ? null : phases);
    }

    private static string? ReadField(string body, string field)
    {
        var match = Regex.Match(body, field + @"\s*:\s*(['""])(?<value>(?:\\.|(?!\1).)*)\1");
        return match.Success ? match.Groups["value"].Value.Replace("\\'", "'").Replace("\\\"", "\"") : null;
    }

    /// <summary>Strips the meta declaration so the body can run as a plain script.</summary>
    public static string StripMeta(string script)
    {
        var match = MetaDeclaration().Match(script);
        if (!match.Success)
            return script;
        var start = match.Index;
        var braceStart = match.Groups[1].Index;
        var depth = 0;
        for (int i = braceStart; i < script.Length; i++)
        {
            if (script[i] == '{')
                depth++;
            else if (script[i] == '}' && --depth == 0)
            {
                var after = i + 1;
                if (after < script.Length && script[after] == ';')
                    after++;
                return script[..start] + script[after..];
            }
        }

        return script;
    }

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Workflows is not { } services)
            return ToolResult.Error("Dynamic workflows are not available in this context.");
        if (services.DisabledByManagedSettings)
            return ToolResult.Error("Dynamic workflows are disabled by managed settings (`disableWorkflows`).");
        if (!services.Enabled)
        {
            return ToolResult.Error("Dynamic workflows are not enabled for this session (org policy, launch " +
                "gate, or the \"Dynamic workflows\" setting in /config).");
        }

        var script = JsonArgs.GetString(arguments, "script");
        var scriptPath = JsonArgs.GetString(arguments, "scriptPath");
        var name = JsonArgs.GetString(arguments, "name");
        var resumeFromRunId = JsonArgs.GetString(arguments, "resumeFromRunId");

        if (services.NamedWorkflowsOnly)
        {
            var offenders = new[]
            {
                script is null ? null : "script",
                scriptPath is null ? null : "scriptPath",
                resumeFromRunId is null ? null : "resumeFromRunId",
            }.Where(x => x is not null).ToList();
            if (offenders.Count > 0)
            {
                return ToolResult.Error(
                    $"This session restricts the Workflow tool to named workflows ({services.NamedOnlyVariable} " +
                    $"is set). Not allowed here: {string.Join(", ", offenders)}. Invoke as {{name, args}} only.");
            }
        }

        if (script is null && scriptPath is null && name is null)
            return ToolResult.Error("Provide one of script, name, or scriptPath");

        if (script is null && name is not null)
        {
            var found = services.Store.Find(name);
            if (found is null)
            {
                var available = services.Store.Names();
                return ToolResult.Error($"Unknown workflow '{name}'." +
                    (available.Count == 0 ? "" : $" Available: {string.Join(", ", available)}."));
            }

            scriptPath = found;
        }

        if (script is null && scriptPath is not null)
        {
            try
            {
                script = await File.ReadAllTextAsync(context.ResolvePath(scriptPath), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ToolResult.Error($"Could not read the workflow script at {scriptPath}: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(script))
            return ToolResult.Error("The workflow script is empty.");

        var meta = ParseMeta(script);
        if (meta is null)
        {
            return ToolResult.Error(
                "The script must begin with `export const meta = {name: '...', description: '...'}` — a pure " +
                "literal with at least a name and a one-line description.");
        }

        if (context.Subagents is null)
            return ToolResult.Error("Subagents are not available in this context, so a workflow cannot run.");

        var runner = new SubagentWorkflowRunner(orchestrator, context);
        var engine = new WorkflowEngine();
        // The row exists before the run starts, so the declared phases announce
        // into a listing the user can already see.
        var sink = services.RunSink?.Invoke($"{meta.Name}: {meta.Description}");
        var options = new WorkflowRunOptions
        {
            Script = StripMeta(script),
            Args = arguments["args"]?.DeepClone(),
            Name = meta.Name,
            JournalDirectory = services.RunDirectory,
            ResumeFromRunId = resumeFromRunId,
            Budget = services.Budget,
            RunNested = (nestedName, nestedArgs, token) =>
                RunNestedAsync(services, context, nestedName, nestedArgs, token),
            Phases = meta.Phases,
            Progress = sink?.Progress,
        };

        var outcome = await engine.RunAsync(options, runner, cancellationToken);
        if (sink is { } run)
        {
            foreach (var line in outcome.Log)
                run.Log(line);
            run.Finished(outcome.Success);
        }
        var savedPath = PersistScript(services, outcome.RunId, script);

        var report = new StringBuilder();
        if (!outcome.Success)
        {
            report.AppendLine($"Workflow '{meta.Name}' failed: {outcome.Error}");
            report.AppendLine($"runId: {outcome.RunId}");
            if (savedPath is not null)
                report.AppendLine($"script: {savedPath}");
            AppendLog(report, outcome);
            return ToolResult.Error(report.ToString().TrimEnd());
        }

        var cached = outcome.CachedAgentCount > 0 ? $" ({outcome.CachedAgentCount} replayed)" : "";
        report.AppendLine(
            $"Workflow '{meta.Name}' finished in {outcome.Duration.TotalSeconds:0.0}s — " +
            $"{outcome.AgentCount} agent{(outcome.AgentCount == 1 ? "" : "s")}{cached}.");
        report.AppendLine($"runId: {outcome.RunId}");
        if (savedPath is not null)
            report.AppendLine($"script: {savedPath}");
        AppendLog(report, outcome);
        report.AppendLine();
        report.AppendLine(outcome.Result is null
            ? "The workflow returned nothing."
            : outcome.Result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return ToolResult.Success(context.Truncate(report.ToString().TrimEnd(), "workflow result"));
    }

    private static void AppendLog(StringBuilder report, WorkflowOutcome outcome)
    {
        if (outcome.Log.Count == 0)
            return;
        report.AppendLine();
        foreach (var line in outcome.Log)
            report.AppendLine(line);
    }

    private static string? PersistScript(WorkflowServices services, string runId, string script)
    {
        if (string.IsNullOrEmpty(services.RunDirectory))
            return null;
        try
        {
            var directory = Path.Combine(services.RunDirectory, runId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "workflow.js");
            File.WriteAllText(path, script);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<WorkflowOutcome> RunNestedAsync(
        WorkflowServices services,
        ToolExecutionContext context,
        string nameOrPath,
        JsonNode? args,
        CancellationToken cancellationToken)
    {
        var path = services.Store.Find(nameOrPath) ?? context.ResolvePath(nameOrPath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Unknown workflow '{nameOrPath}'.");
        var script = await File.ReadAllTextAsync(path, cancellationToken);
        var runner = new SubagentWorkflowRunner(orchestrator, context);
        return await new WorkflowEngine().RunAsync(new WorkflowRunOptions
        {
            Script = StripMeta(script),
            Args = args,
            Name = nameOrPath,
            JournalDirectory = services.RunDirectory,
            Budget = services.Budget,
            // Nesting is one level only: the child gets no RunNested of its own.
        }, runner, cancellationToken);
    }
}

/// <summary>
/// Backs a workflow script's <c>agent()</c> calls with real subagents, adding
/// the structured-output contract when the call passes a schema.
/// </summary>
internal sealed class SubagentWorkflowRunner(AgentOrchestrator orchestrator, ToolExecutionContext context)
    : IWorkflowAgentRunner
{
    public async Task<WorkflowAgentResult> RunAgentAsync(
        WorkflowAgentRequest request, CancellationToken cancellationToken)
    {
        var arguments = new JsonObject
        {
            ["prompt"] = BuildPrompt(request),
            ["agent_type"] = request.AgentType ?? SubagentTool.GeneralAgentType,
        };
        if (request.Model is { Length: > 0 } model)
            arguments["model"] = model;
        if (request.Label is { Length: > 0 } label)
            arguments["description"] = label;
        if (request.Isolation is { Length: > 0 } isolation)
            arguments["isolation"] = isolation;

        // opts.effort overrides the session effort for this call only; an
        // unknown value is refused rather than silently ignored.
        JarvisCode.Core.Providers.ThinkingEffort? effort = null;
        if (request.Effort is { Length: > 0 } requestedEffort)
        {
            effort = ParseEffort(requestedEffort);
            if (effort is null)
            {
                return new WorkflowAgentResult(false, "",
                    Error: $"Unknown effort '{requestedEffort}'. Use low, medium, high, xhigh or max.");
            }
        }

        var result = await new SubagentTool(orchestrator)
            .RunAsync(arguments, context, effort, cancellationToken);

        if (result.IsError)
            return new WorkflowAgentResult(false, result.Content, Error: result.Content);

        if (request.Schema is null)
            return new WorkflowAgentResult(true, result.Content);

        var structured = ExtractJson(result.Content);
        return structured is null
            ? new WorkflowAgentResult(false, result.Content,
                Error: "The agent did not return an object matching the schema.")
            : new WorkflowAgentResult(true, result.Content, structured);
    }

    /// <summary>The reference's five effort names for a workflow agent call.</summary>
    private static JarvisCode.Core.Providers.ThinkingEffort? ParseEffort(string value) =>
        value.ToLowerInvariant() switch
        {
            "low" => JarvisCode.Core.Providers.ThinkingEffort.Low,
            "medium" => JarvisCode.Core.Providers.ThinkingEffort.Medium,
            "high" => JarvisCode.Core.Providers.ThinkingEffort.High,
            "xhigh" => JarvisCode.Core.Providers.ThinkingEffort.XHigh,
            "max" => JarvisCode.Core.Providers.ThinkingEffort.Max,
            _ => null,
        };

    private static string BuildPrompt(WorkflowAgentRequest request)
    {
        if (request.Schema is null)
        {
            return request.Prompt +
                "\n\nYour final message IS the return value of this step — return the data itself, " +
                "with no preamble and no human-facing framing.";
        }

        return request.Prompt +
            "\n\n## Structured output\n" +
            "Your final message must be a single JSON object matching this schema, and nothing else — " +
            "no prose, no code fence, no explanation:\n" +
            request.Schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads the object out of a final message, fenced or bare.</summary>
    internal static JsonNode? ExtractJson(string text)
    {
        var trimmed = text.Trim();
        var fence = Regex.Match(trimmed, @"```(?:json)?\s*(?<body>[\s\S]*?)```");
        if (fence.Success)
            trimmed = fence.Groups["body"].Value.Trim();

        if (TryParse(trimmed) is { } direct)
            return direct;

        // A model that adds a sentence around the object still gives a usable
        // answer, so the outermost braces are worth one attempt.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return TryParse(trimmed[start..(end + 1)]);
        return null;
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
