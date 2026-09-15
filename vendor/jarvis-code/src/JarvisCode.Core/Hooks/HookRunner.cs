using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Hooks;

/// <summary>
/// Loads hook definitions from {cwd}/.jarvis/hooks.json plus an optional
/// user-level hooks.json and executes them. Each hook receives a JSON payload
/// on stdin ({event, tool, arguments, result?}) and runs in the working
/// directory. A pre_tool_use hook that exits non-zero blocks the tool call,
/// with its stderr (or stdout) shown as the reason.
/// </summary>
public sealed class HookRunner(
    IReadOnlyList<HookDefinition> hooks,
    string workingDirectory,
    IReadOnlyList<FunctionHook>? functionHooks = null,
    PromptHookEvaluator? promptEvaluator = null,
    HttpHookSender? httpSender = null,
    McpHookCaller? mcpCaller = null,
    HttpHookPolicy? httpPolicy = null,
    SdkHookCaller? sdkHookCaller = null) : IToolHooks
{
    public const string ProjectRelativePath = ".jarvis/hooks.json";
    private const int DefaultTimeoutSeconds = 30;
    private const int MaxTimeoutSeconds = 120;

    public IReadOnlyList<HookDefinition> Hooks => hooks;

    /// <summary>Hooks the host registered in process (the reference's own built-ins).</summary>
    public IReadOnlyList<FunctionHook> FunctionHooks => functionHooks ?? [];

    /// <summary>A runner with in-process hooks added.</summary>
    public HookRunner WithFunctions(IEnumerable<FunctionHook> extra) =>
        new(hooks, workingDirectory, [.. FunctionHooks, .. extra], promptEvaluator,
            httpSender, mcpCaller, httpPolicy, sdkHookCaller) { ExecutionObserved = ExecutionObserved, Diagnostics = Diagnostics };

    /// <summary>A runner that can judge prompt hooks (needs a model and the conversation).</summary>
    public HookRunner WithPromptEvaluator(PromptHookEvaluator? evaluator) =>
        new(hooks, workingDirectory, functionHooks, evaluator, httpSender, mcpCaller, httpPolicy, sdkHookCaller)
        { ExecutionObserved = ExecutionObserved, Diagnostics = Diagnostics };

    /// <summary>
    /// A runner that can reach the two hook kinds leaving this process: an HTTP
    /// endpoint and a tool on an MCP server. Without them those hooks refuse
    /// rather than pretending to have run.
    /// </summary>
    public HookRunner WithTransports(
        HttpHookSender? sender, McpHookCaller? caller, HttpHookPolicy? policy = null) =>
        new(hooks, workingDirectory, functionHooks, promptEvaluator, sender, caller, policy ?? httpPolicy, sdkHookCaller)
        { ExecutionObserved = ExecutionObserved, Diagnostics = Diagnostics };

    public static HookRunner Load(
        string workingDirectory, string? userConfigPath, IEnumerable<string>? extraConfigFiles = null)
    {
        var definitions = new List<HookDefinition>();
        if (userConfigPath is not null)
            definitions.AddRange(LoadFile(userConfigPath));
        foreach (var extra in extraConfigFiles ?? [])
            definitions.AddRange(LoadFile(extra));
        definitions.AddRange(LoadFile(Path.Combine(workingDirectory, ProjectRelativePath)));
        return new HookRunner(definitions, workingDirectory);
    }

    public static HookRunner LoadOnlyFiles(string workingDirectory, IEnumerable<string> files) =>
        new([.. files.SelectMany(LoadFile)], workingDirectory);

    /// <summary>A runner with extra definitions appended (a skill's frontmatter hooks joining the session).</summary>
    public HookRunner WithExtra(IEnumerable<HookDefinition> extra)
    {
        var combined = new List<HookDefinition>(hooks);
        combined.AddRange(extra);
        return new HookRunner(
            combined, workingDirectory, functionHooks, promptEvaluator, httpSender, mcpCaller, httpPolicy, sdkHookCaller)
        { ExecutionObserved = ExecutionObserved, Diagnostics = Diagnostics };
    }

    public HookRunner WithSdkCallbacks(IEnumerable<HookDefinition> extra, SdkHookCaller caller) =>
        new([.. hooks.Where(hook => hook.Kind != HookKind.Callback), .. extra], workingDirectory, functionHooks, promptEvaluator, httpSender, mcpCaller, httpPolicy, caller)
        { ExecutionObserved = ExecutionObserved, Diagnostics = Diagnostics };

    private static readonly AsyncLocal<string?> CurrentToolId = new();
    public static IDisposable EnterToolCall(string callId)
    {
        var previous = CurrentToolId.Value;
        CurrentToolId.Value = callId;
        return new ToolCallScope(previous);
    }
    private sealed class ToolCallScope(string? previous) : IDisposable
    { public void Dispose() => CurrentToolId.Value = previous; }

    /// <summary>The snake_case event names hooks.json (and skill hooks) use.</summary>
    public static HookEvent? ParseEventName(string? eventName) => eventName switch
    {
                    "pre_tool_use" => HookEvent.PreToolUse,
                    "post_tool_use" => HookEvent.PostToolUse,
                    "turn_completed" => HookEvent.TurnCompleted,
                    "user_prompt_submit" => HookEvent.UserPromptSubmit,
                    "session_start" => HookEvent.SessionStart,
                    "session_end" => HookEvent.SessionEnd,
                    "stop" => HookEvent.Stop,
                    "subagent_stop" => HookEvent.SubagentStop,
                    "subagent_start" => HookEvent.SubagentStart,
                    "notification" => HookEvent.Notification,
                    "pre_compact" => HookEvent.PreCompact,
                    "permission_request" => HookEvent.PermissionRequest,
                    "post_tool_use_failure" => HookEvent.PostToolUseFailure,
                    "post_tool_batch" => HookEvent.PostToolBatch,
                    "user_prompt_expansion" => HookEvent.UserPromptExpansion,
                    "stop_failure" => HookEvent.StopFailure,
                    "post_compact" => HookEvent.PostCompact,
                    "permission_denied" => HookEvent.PermissionDenied,
                    "setup" => HookEvent.Setup,
                    "elicitation" => HookEvent.Elicitation,
                    "elicitation_result" => HookEvent.ElicitationResult,
                    "config_change" => HookEvent.ConfigChange,
                    "worktree_create" => HookEvent.WorktreeCreate,
                    "worktree_remove" => HookEvent.WorktreeRemove,
                    "instructions_loaded" => HookEvent.InstructionsLoaded,
                    "cwd_changed" => HookEvent.CwdChanged,
                    "file_changed" => HookEvent.FileChanged,
                    "directory_added" => HookEvent.DirectoryAdded,
                    "message_display" => HookEvent.MessageDisplay,
                    "task_created" => HookEvent.TaskCreated,
                    "task_completed" => HookEvent.TaskCompleted,
                    "teammate_idle" => HookEvent.TeammateIdle,
                    "pre_model_switch" => HookEvent.PreModelSwitch,
                    "post_model_switch" => HookEvent.PostModelSwitch,
        _ => Enum.TryParse<HookEvent>(eventName, true, out var parsed) ? parsed : null,
    };

    /// <summary>Clamps a configured timeout into the runner's allowed range.</summary>
    public static int ClampTimeout(int? timeoutSeconds) =>
        Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds);

    private static IEnumerable<HookDefinition> LoadFile(string path)
    {
        try { return File.Exists(path) ? ParseConfiguration(File.ReadAllText(path)) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Loads an in-memory settings overlay without writing its other fields or credentials to disk.</summary>
    public static IReadOnlyList<HookDefinition> ParseConfiguration(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var entries = FlattenEntries(root?["hooks"]);
            var pluginRoot = root?["$jarvis_plugin_root"]?.GetValue<string>();
            var pluginData = root?["$jarvis_plugin_data"]?.GetValue<string>();
            var projectDirectory = root?["$jarvis_project_dir"]?.GetValue<string>();

            var definitions = new List<HookDefinition>();
            foreach (var entry in entries.OfType<JsonObject>())
            {
                var hookEvent = ParseEventName(JsonArgs.GetString(entry, "event"));
                if (hookEvent is null)
                    continue;
                int timeout = ClampTimeout(
                    JsonArgs.GetInt(entry, "timeoutSeconds") ?? JsonArgs.GetInt(entry, "timeout"));
                var toolMatch = JsonArgs.GetString(entry, "toolMatch");

                // An LLM prompt hook carries a condition instead of a command:
                // the model judges it and answers {ok, reason}.
                if (string.Equals(JsonArgs.GetString(entry, "type"), "prompt",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var prompt = JsonArgs.GetString(entry, "prompt");
                    if (string.IsNullOrWhiteSpace(prompt))
                        continue;
                    definitions.Add(new HookDefinition(
                        hookEvent.Value, toolMatch, prompt, timeout, HookKind.Prompt, prompt,
                        JsonArgs.GetString(entry, "model"),
                        JsonArgs.GetBool(entry, "continueOnBlock"),
                        JsonArgs.GetString(entry, "statusMessage")));
                    continue;
                }

                var type = JsonArgs.GetString(entry, "type");

                // The hook POSTs the payload to a URL and reads the answer back.
                if (string.Equals(type, "http", StringComparison.OrdinalIgnoreCase))
                {
                    var url = JsonArgs.GetString(entry, "url");
                    if (string.IsNullOrWhiteSpace(url))
                        continue;
                    definitions.Add(new HookDefinition(
                        hookEvent.Value, toolMatch, url, timeout, HookKind.Http,
                        StatusMessage: JsonArgs.GetString(entry, "statusMessage"),
                        Http: new HttpHookSpec(
                            url,
                            ReadStringMap(entry["headers"] as JsonObject),
                            ReadStringList(entry["allowedEnvVars"] as JsonArray))));
                    continue;
                }

                // The hook calls a tool on a configured MCP server.
                if (string.Equals(type, "mcp_tool", StringComparison.OrdinalIgnoreCase))
                {
                    var server = JsonArgs.GetString(entry, "server");
                    var toolName = JsonArgs.GetString(entry, "tool");
                    if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(toolName))
                        continue;
                    definitions.Add(new HookDefinition(
                        hookEvent.Value, toolMatch, $"{server}/{toolName}", timeout, HookKind.McpTool,
                        StatusMessage: JsonArgs.GetString(entry, "statusMessage"),
                        Mcp: new McpHookSpec(server, toolName, entry["input"]?.DeepClone())));
                    continue;
                }

                var command = JsonArgs.GetString(entry, "command");
                if (string.IsNullOrWhiteSpace(command))
                    continue;
                definitions.Add(new HookDefinition(hookEvent.Value, toolMatch, command, timeout));
            }
            return [.. definitions.Select(definition => definition with
            { PluginRoot = pluginRoot, PluginData = pluginData, ProjectDirectory = projectDirectory })];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken hooks file must never break the agent; it simply yields no hooks.
            return [];
        }
    }

    private static JsonArray FlattenEntries(JsonNode? source)
    {
        if (source is JsonArray array) return array;
        var result = new JsonArray();
        if (source is not JsonObject groups) return result;
        foreach (var (eventName, matchers) in groups)
        {
            foreach (var matcher in (matchers as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var hook in (matcher["hooks"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var copy = (JsonObject)hook.DeepClone();
                    copy["event"] = eventName;
                    copy["toolMatch"] = matcher["matcher"]?.DeepClone();
                    result.Add(copy);
                }
            }
        }
        return result;
    }

    /// <summary>A JSON object of strings, for a hook entry's headers.</summary>
    private static IReadOnlyDictionary<string, string>? ReadStringMap(JsonObject? node)
    {
        if (node is null)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in node)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var text))
            {
                map[key] = text;
            }
        }

        return map;
    }

    /// <summary>A JSON array of strings, for a hook entry's allowedEnvVars.</summary>
    private static IReadOnlyList<string>? ReadStringList(JsonArray? node)
    {
        if (node is null)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in node)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
            {
                list.Add(text);
            }
        }

        return list;
    }

    public async Task<HookDecision> BeforeToolAsync(ITool tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        string? permission = null;
        foreach (var hook in Matching(HookEvent.PreToolUse, tool.Name))
        {
            var payload = new JsonObject
            {
                ["event"] = "pre_tool_use",
                ["tool"] = tool.Name,
                ["arguments"] = arguments.DeepClone(),
            };
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode != 0)
            {
                var reason = FirstNonEmpty(execution.Stderr, execution.Stdout,
                    $"hook exited with code {execution.ExitCode}");
                if (StopsImmediately(execution.CallbackOutput)) throw new HookStopRequestedException(reason.Trim());
                return new HookDecision(false, $"Blocked by pre_tool_use hook: {reason.Trim()}");
            }
            if (execution.CallbackOutput?["hookSpecificOutput"] is JsonObject specific)
            {
                if (specific["updatedInput"] is JsonObject updated)
                {
                    var validation = Validation.JsonSchemaValidation.ValidateInstance(tool.InputSchema, updated);
                    if (!validation.IsValid) return new HookDecision(false, "Hook updatedInput is invalid: " + validation.Error);
                    arguments.Clear();
                    foreach (var (key, value) in updated) arguments[key] = value?.DeepClone();
                }
                var behavior = JsonArgs.GetString(specific, "permissionDecision");
                if (behavior is "allow" or "ask")
                    permission = behavior;
            }
        }
        return HookDecision.Allow with { PermissionBehavior = permission };
    }

    public async Task<string?> AfterToolAsync(
        ITool tool, JsonObject arguments, ToolResult result, CancellationToken cancellationToken)
    {
        var payload = PostToolPayload("post_tool_use", tool.Name, arguments, result);
        StringBuilder? extra = null;
        foreach (var hook in Matching(HookEvent.PostToolUse, tool.Name))
        {
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.CallbackOutput?["hookSpecificOutput"]?["additionalContext"] is JsonValue contextValue &&
                contextValue.TryGetValue<string>(out var hookContext) && !string.IsNullOrEmpty(hookContext))
                (extra ??= new StringBuilder()).AppendLine(hookContext);
        }

        // In-process hooks answer with context for the model rather than an exit
        // code; the reference's own preview-verification nudges arrive this way.
        foreach (var function in MatchingFunctions(HookEvent.PostToolUse, tool.Name))
        {
            FunctionHookResult outcome;
            try
            {
                outcome = await RunFunctionAsync(function, payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (outcome.AdditionalContext is { Length: > 0 } context)
            {
                if (extra is null)
                    extra = new StringBuilder(context);
                else
                    extra.Append('\n').Append('\n').Append(context);
            }
        }

        if (result.IsError)
        {
            foreach (var hook in Matching(HookEvent.PostToolUseFailure, tool.Name))
            {
                await RunHookAsync(
                    hook, PostToolPayload("post_tool_use_failure", tool.Name, arguments, result), cancellationToken);
            }
        }

        return extra?.ToString();
    }

    private IEnumerable<FunctionHook> MatchingFunctions(HookEvent hookEvent, string? toolName) =>
        FunctionHooks.Where(f => f.Event == hookEvent &&
            (f.ToolMatch is null || toolName is null ||
             Regex.IsMatch(toolName, f.ToolMatch, RegexOptions.IgnoreCase)));

    private static JsonObject PostToolPayload(string eventName, string toolName, JsonObject arguments, ToolResult result) =>
        new()
        {
            ["event"] = eventName,
            ["tool"] = toolName,
            ["arguments"] = arguments.DeepClone(),
            ["result"] = new JsonObject
            {
                ["is_error"] = result.IsError,
                ["content"] = result.Content.Length > 4000 ? result.Content[..4000] : result.Content,
            },
        };

    /// <summary>
    /// Runs the user_prompt_submit hooks before a prompt starts a turn. The
    /// first non-zero exit blocks the prompt (stderr, then stdout, as the
    /// reason); stdout from allowed hooks is joined into additional context
    /// the caller attaches to the user message.
    /// </summary>
    public async Task<PromptSubmitResult> RunUserPromptSubmitAsync(string prompt, CancellationToken cancellationToken)
    {
        var context = new StringBuilder();
        foreach (var hook in hooks.Where(h => h.Event == HookEvent.UserPromptSubmit))
        {
            var payload = new JsonObject
            {
                ["event"] = "user_prompt_submit",
                ["prompt"] = prompt,
            };
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode != 0)
            {
                var reason = FirstNonEmpty(execution.Stderr, execution.Stdout,
                    $"hook exited with code {execution.ExitCode}");
                return new PromptSubmitResult(false, reason.Trim(), null);
            }

            if (!string.IsNullOrWhiteSpace(execution.Stdout))
            {
                if (context.Length > 0)
                    context.AppendLine();
                context.Append(execution.Stdout.Trim());
            }
        }
        return context.Length > 0 ? new PromptSubmitResult(true, null, context.ToString()) : PromptSubmitResult.Allow;
    }

    /// <summary>
    /// Runs the user_prompt_expansion hooks before a typed slash command's
    /// expansion is sent (reference UserPromptExpansion): a non-zero exit
    /// blocks the expansion, stdout from allowed hooks joins as additional
    /// context. The payload carries the reference fields.
    /// </summary>
    public async Task<PromptSubmitResult> RunUserPromptExpansionAsync(
        string expansionType, string commandName, string commandArgs, string commandSource,
        string prompt, CancellationToken cancellationToken)
    {
        var context = new StringBuilder();
        foreach (var hook in hooks.Where(h => h.Event == HookEvent.UserPromptExpansion))
        {
            var payload = new JsonObject
            {
                ["event"] = "user_prompt_expansion",
                ["expansion_type"] = expansionType,
                ["command_name"] = commandName,
                ["command_args"] = commandArgs,
                ["command_source"] = commandSource,
                ["prompt"] = prompt,
            };
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode != 0)
            {
                var reason = FirstNonEmpty(execution.Stderr, execution.Stdout,
                    $"hook exited with code {execution.ExitCode}");
                return new PromptSubmitResult(false, reason.Trim(), null);
            }

            if (!string.IsNullOrWhiteSpace(execution.Stdout))
            {
                if (context.Length > 0)
                    context.AppendLine();
                context.Append(execution.Stdout.Trim());
            }
        }
        return context.Length > 0 ? new PromptSubmitResult(true, null, context.ToString()) : PromptSubmitResult.Allow;
    }

    public async Task RunTurnCompletedAsync(CancellationToken cancellationToken)
    {
        foreach (var hook in hooks.Where(h => h.Event == HookEvent.TurnCompleted))
            await RunHookAsync(hook, new JsonObject { ["event"] = "turn_completed" }, cancellationToken);
    }

    public bool Has(HookEvent hookEvent) => hooks.Any(h => h.Event == hookEvent);

    /// <summary>
    /// Runs the session_start hooks; stdout from each joins into extra context
    /// the caller attaches to the session's first message.
    /// </summary>
    /// <param name="source">
    /// The reference's SessionStart <c>source</c>: startup, resume, clear, compact or fork.
    /// </param>
    public async Task<string?> RunSessionStartAsync(
        string sessionId, CancellationToken cancellationToken, string source = "startup")
    {
        var context = new StringBuilder();
        foreach (var hook in hooks.Where(h => h.Event == HookEvent.SessionStart))
        {
            var payload = new JsonObject
            {
                ["event"] = "session_start",
                ["session_id"] = sessionId,
                ["source"] = source,
            };
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode == 0 && !string.IsNullOrWhiteSpace(execution.Stdout))
            {
                if (context.Length > 0)
                    context.AppendLine();
                context.Append(execution.Stdout.Trim());
            }
        }
        return context.Length > 0 ? context.ToString() : null;
    }

    /// <summary>
    /// Runs the stop hooks when a turn wants to end. A non-zero exit blocks
    /// stopping: the reason (stderr, then stdout) goes back to the model and
    /// the turn continues. stop_hook_active tells hooks the continuation they
    /// asked for already happened, so they can avoid looping forever.
    /// </summary>
    public async Task<HookDecision> RunStopAsync(bool stopHookActive, CancellationToken cancellationToken) =>
        (await RunStopHooksAsync(HookEvent.Stop, stopHookActive, cancellationToken)).Decision;

    /// <summary>
    /// Runs the stop hooks and reports both halves of the verdict: whether the
    /// turn may end, and whether a prompt hook judged the condition impossible —
    /// which the reference treats as a reason to give up on it rather than to
    /// keep working, since retrying cannot help.
    /// </summary>
    public async Task<StopHookOutcome> RunStopHooksAsync(
        HookEvent stopEvent,
        bool stopHookActive,
        CancellationToken cancellationToken,
        Func<HookDefinition, bool>? skip = null)
    {
        string eventName = stopEvent == HookEvent.SubagentStop ? "subagent_stop" : "stop";
        string? evaluated = null;
        var payload = new JsonObject { ["event"] = eventName, ["stop_hook_active"] = stopHookActive };
        foreach (var hook in hooks.Where(h => h.Event == stopEvent))
        {
            // A hook the caller is standing down for this turn — a goal whose
            // evaluation is deferred while background work runs.
            if (skip?.Invoke(hook) == true)
            {
                continue;
            }

            if (hook.Kind == HookKind.Prompt)
            {
                if (promptEvaluator is null || hook.Prompt is not { Length: > 0 } condition)
                    continue;

                PromptHookVerdict? verdict;
                var hookId = ObserveStart(hook.Event, hook.Command);
                try
                {
                    verdict = await promptEvaluator(
                        new PromptHookRequest(
                            stopEvent, condition, payload.ToJsonString(), hook.Model,
                            TimeSpan.FromSeconds(hook.TimeoutSeconds), IsStopCondition: true),
                        cancellationToken);
                    Observe(new HookLifecycleEvent(hookId, hook.Event, hook.Command, "response",
                        verdict is { Ok: false } ? 1 : 0, verdict?.Reason ?? ""));
                }
                catch (OperationCanceledException ex)
                {
                    Observe(new HookLifecycleEvent(hookId, hook.Event, hook.Command, "response", 1, Stderr: ex.Message));
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Observe(new HookLifecycleEvent(hookId, hook.Event, hook.Command, "response", 1, Stderr: ex.Message));
                    // An evaluator that failed has no opinion, exactly as a
                    // command hook that could not run has none.
                    continue;
                }

                if (verdict is null)
                    continue;

                // Answered: the caller can tell an evaluated condition from one
                // nothing could judge, which is what makes "met" mean met.
                evaluated = condition;
                if (verdict.Ok)
                    continue;

                return new StopHookOutcome(
                    new HookDecision(hook.ContinueOnBlock, PromptHooks.BlockingError(condition, verdict.Reason)),
                    verdict.Impossible,
                    condition);
            }

            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode != 0)
            {
                var reason = FirstNonEmpty(execution.Stderr, execution.Stdout,
                    $"hook exited with code {execution.ExitCode}");
                return new StopHookOutcome(new HookDecision(StopsImmediately(execution.CallbackOutput), reason.Trim())
                { StopImmediately = StopsImmediately(execution.CallbackOutput) }, Impossible: false, null);
            }
        }

        // In-process Stop hooks are observational: the reference uses them to
        // reset per-turn state, not to block.
        foreach (var function in MatchingFunctions(stopEvent, null))
        {
            try
            {
                await RunFunctionAsync(function, payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same rule as above: a hook that threw has no opinion.
            }
        }

        return new StopHookOutcome(HookDecision.Allow, Impossible: false, evaluated);
    }

    /// <summary>
    /// Runs the pre_compact hooks for a compaction that is about to start. A
    /// non-zero exit refuses it and the reason reaches the user, which is what
    /// the reference's "Compaction blocked by PreCompact hook" reports; the
    /// trigger is "auto" or "manual", as it is there.
    /// </summary>
    public async Task<HookDecision> RunPreCompactAsync(
        string trigger, string sessionId, CancellationToken cancellationToken)
    {
        foreach (var hook in hooks.Where(h => h.Event == HookEvent.PreCompact))
        {
            var payload = new JsonObject
            {
                ["event"] = "pre_compact",
                ["trigger"] = trigger,
                ["session_id"] = sessionId,
            };
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode != 0)
            {
                var reason = FirstNonEmpty(execution.Stderr, execution.Stdout,
                    $"hook exited with code {execution.ExitCode}");
                return new HookDecision(false, reason.Trim());
            }
        }

        return HookDecision.Allow;
    }

    /// <summary>
    /// Runs the pre_model_switch hooks for a switch that has not happened yet.
    /// A non-zero exit refuses it, as it does for stop and pre_compact; a hook
    /// that exits cleanly may still refuse by printing the reference's verdict
    /// ({"permissionDecision":"allow"|"deny"|"ask", "permissionDecisionReason":...}).
    /// Switching model forfeits the prompt cache, which is why the reference
    /// gives this one a veto at all.
    /// </summary>
    public async Task<HookDecision> RunPreModelSwitchAsync(
        ModelSwitch change, CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<bool>>? confirm = null)
    {
        foreach (var hook in hooks.Where(h => h.Event == HookEvent.PreModelSwitch))
        {
            var payload = change.ToPayload();
            payload["event"] = "pre_model_switch";
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode != 0)
            {
                var reason = FirstNonEmpty(execution.Stderr, execution.Stdout,
                    $"hook exited with code {execution.ExitCode}");
                return new HookDecision(false, reason.Trim());
            }

            if (ParseModelSwitchRefusal(execution.Stdout) is { } refusal)
            {
                if (refusal.PermissionBehavior == "ask" && confirm is not null &&
                    await confirm(refusal.BlockReason ?? "The model switch needs confirmation.", cancellationToken))
                    continue; // confirmation does not skip a later hook's explicit denial
                return refusal;
            }
        }

        return HookDecision.Allow;
    }

    /// <summary>
    /// A pre_model_switch hook's stdout verdict, or null when it has no opinion.
    ///
    /// "ask" requires the interactive caller's confirmation. A headless caller
    /// without a confirmation channel keeps the model unchanged.
    /// </summary>
    private static HookDecision? ParseModelSwitchRefusal(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(stdout.Trim()) is not JsonObject parsed)
            {
                return null;
            }

            var reason = JsonArgs.GetString(parsed, "permissionDecisionReason");
            return JsonArgs.GetString(parsed, "permissionDecision")?.ToLowerInvariant() switch
            {
                "deny" => new HookDecision(false, FirstNonEmpty(reason ?? "", "the pre_model_switch hook denied it")),
                "ask" => new HookDecision(false,
                    FirstNonEmpty(reason ?? "", "the pre_model_switch hook asked for confirmation")) { PermissionBehavior = "ask" },
                _ => null,
            };
        }
        catch (JsonException)
        {
            // A hook that prints something else is a hook with no opinion, not a
            // failure: its exit code already had the chance to refuse.
            return null;
        }
    }

    /// <summary>
    /// Runs the permission_request hooks for a call that is about to show the
    /// user a permission prompt. The first hook that prints a decision
    /// ({"decision":"allow"|"deny","reason":...}) settles the request without
    /// a prompt; hooks that fail, print nothing parseable, or exit non-zero
    /// have no opinion and the prompt appears as usual.
    /// </summary>
    public async Task<PermissionRequestHookResult?> RunPermissionRequestAsync(
        string toolName, JsonObject arguments, string risk, CancellationToken cancellationToken)
    {
        foreach (var hook in Matching(HookEvent.PermissionRequest, toolName))
        {
            var payload = new JsonObject
            {
                ["event"] = "permission_request",
                ["tool"] = toolName,
                ["arguments"] = arguments.DeepClone(),
                ["risk"] = risk,
            };
            var execution = await RunHookAsync(hook, payload, cancellationToken);
            if (execution.ExitCode != 0)
                continue;
            if (execution.CallbackOutput?["hookSpecificOutput"]?["decision"] is JsonObject sdkDecision &&
                JsonArgs.GetString(sdkDecision, "behavior") is { } behavior)
            {
                if (sdkDecision.ContainsKey("updatedInput") && sdkDecision["updatedInput"] is not JsonObject)
                    return new PermissionRequestHookResult(false, "The SDK permission hook supplied updatedInput that is not an object.");
                if (behavior is "allow" or "deny") return new PermissionRequestHookResult(behavior == "allow",
                    JsonArgs.GetString(sdkDecision, "message"))
                { UpdatedInput = sdkDecision["updatedInput"] is JsonObject updated ? (JsonObject)updated.DeepClone() : null };
            }
            if (ParsePermissionDecision(execution.Stdout) is { } result)
                return result;
        }
        return null;
    }

    private static PermissionRequestHookResult? ParsePermissionDecision(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        try
        {
            if (JsonNode.Parse(stdout.Trim()) is not JsonObject parsed)
                return null;
            var reason = JsonArgs.GetString(parsed, "reason");
            return JsonArgs.GetString(parsed, "decision")?.ToLowerInvariant() switch
            {
                "allow" => new PermissionRequestHookResult(true, reason),
                "deny" => new PermissionRequestHookResult(false, reason),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs an observational event's hooks — session_end, subagent_stop,
    /// subagent_start, notification, pre_compact. Exit codes carry no verdict.
    /// </summary>
    /// <summary>
    /// Fires an observational event. <paramref name="matchQuery"/> is what a
    /// hook's matcher is tested against for the events that carry one — the
    /// reference matches instructions_loaded on its load_reason, not on a tool
    /// name — and leaving it null runs every hook subscribed to the event.
    /// </summary>
    public async Task RunEventAsync(
        HookEvent hookEvent, JsonObject payload, CancellationToken cancellationToken,
        string? matchQuery = null)
    {
        var name = hookEvent switch
        {
            HookEvent.SessionEnd => "session_end",
            HookEvent.SubagentStop => "subagent_stop",
            HookEvent.SubagentStart => "subagent_start",
            HookEvent.Notification => "notification",
            HookEvent.PreCompact => "pre_compact",
            HookEvent.PostToolBatch => "post_tool_batch",
            HookEvent.UserPromptExpansion => "user_prompt_expansion",
            HookEvent.StopFailure => "stop_failure",
            HookEvent.PostCompact => "post_compact",
            HookEvent.PermissionDenied => "permission_denied",
            HookEvent.Setup => "setup",
            HookEvent.Elicitation => "elicitation",
            HookEvent.ElicitationResult => "elicitation_result",
            HookEvent.ConfigChange => "config_change",
            HookEvent.WorktreeCreate => "worktree_create",
            HookEvent.WorktreeRemove => "worktree_remove",
            HookEvent.InstructionsLoaded => "instructions_loaded",
            HookEvent.CwdChanged => "cwd_changed",
            HookEvent.FileChanged => "file_changed",
            HookEvent.DirectoryAdded => "directory_added",
            HookEvent.MessageDisplay => "message_display",
            HookEvent.TaskCreated => "task_created",
            HookEvent.TaskCompleted => "task_completed",
            HookEvent.TeammateIdle => "teammate_idle",
            HookEvent.PostModelSwitch => "post_model_switch",
            _ => throw new ArgumentOutOfRangeException(nameof(hookEvent), hookEvent,
                "RunEventAsync only serves the observational events."),
        };
        foreach (var hook in hooks.Where(h =>
                     h.Event == hookEvent && (matchQuery is null || MatchesTool(h.ToolMatch, matchQuery))))
        {
            var enriched = payload.DeepClone().AsObject();
            enriched["event"] = name;
            await RunHookAsync(hook, enriched, cancellationToken);
        }
    }

    private IEnumerable<HookDefinition> Matching(HookEvent hookEvent, string toolName) =>
        hooks.Where(h => h.Event == hookEvent && MatchesTool(h.ToolMatch, toolName));

    private static bool MatchesTool(string? pattern, string toolName)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;
        try
        {
            return Regex.IsMatch(toolName, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private sealed record HookExecution(int ExitCode, string Stdout, string Stderr)
    { public JsonObject? CallbackOutput { get; init; } }

    private static bool StopsImmediately(JsonObject? output) =>
        output?["continue"] is JsonValue value && value.TryGetValue<bool>(out var keepGoing) && !keepGoing;

    /// <summary>
    /// Runs one hook whatever kind it is. The three non-command kinds answer the
    /// same two things a command does — did it pass, and what did it say — so
    /// every event path above treats them alike: a refusal reads as a non-zero
    /// exit, and what it said reads as stdout.
    /// </summary>
    private async Task<HookExecution> RunHookAsync(
        HookDefinition hook, JsonObject payload, CancellationToken cancellationToken)
    {
        var id = ObserveStart(hook.Event, hook.Command);
        HookExecution execution;
        try
        {
            execution = hook.Kind switch
            {
                HookKind.Prompt => await RunPromptHookAsync(hook, payload, cancellationToken),
                HookKind.Http => await RunHttpHookAsync(hook, payload, cancellationToken),
                HookKind.McpTool => await RunMcpHookAsync(hook, payload, cancellationToken),
                HookKind.Callback => await RunSdkHookAsync(hook, payload, cancellationToken),
                _ => await RunCommandHookAsync(hook, payload, cancellationToken),
            };
        }
        catch (Exception ex)
        {
            Observe(new HookLifecycleEvent(id, hook.Event, hook.Command, "response", 1, Stderr: ex.Message));
            throw;
        }

        Observe(new HookLifecycleEvent(id, hook.Event, hook.Command, "response",
            execution.ExitCode, execution.Stdout, execution.Stderr));

        if (MalformedJsonObject(execution.Stdout) is { } malformed)
        {
            Diagnostics?.Invoke($"{hook.Event} hook output invalid: {malformed}");
        }

        return execution;
    }

    /// <summary>
    /// Where the host shows what a hook did wrong — the reference's "{Event} hook
    /// output invalid: …" line. Null drops the diagnostics.
    /// </summary>
    public Action<string>? Diagnostics { get; set; }

    /// <summary>Opt-in output-stream observer. Failures cannot change hook execution.</summary>
    public Action<HookLifecycleEvent>? ExecutionObserved { get; set; }

    private async Task<HookExecution> RunSdkHookAsync(HookDefinition hook, JsonObject payload, CancellationToken cancellationToken)
    {
        if (sdkHookCaller is null) return Blocked("The SDK hook host is not connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, hook.TimeoutSeconds)));
        JsonObject? answer;
        try { answer = await sdkHookCaller(hook, payload, CurrentToolId.Value, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Blocked("The SDK hook callback timed out."); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Blocked("The SDK hook callback failed: " + ex.Message); }
        if (answer is null) return Blocked("The SDK hook host disconnected or failed to answer.");
        var specific = answer["hookSpecificOutput"] as JsonObject;
        var deny = answer["continue"] is JsonValue keep && keep.TryGetValue<bool>(out var continuing) && !continuing ||
            JsonArgs.GetString(answer, "decision") == "block" ||
            specific is not null && JsonArgs.GetString(specific, "permissionDecision") == "deny";
        var reason = JsonArgs.GetString(answer, "stopReason") ?? JsonArgs.GetString(answer, "reason") ??
            (specific is null ? null : JsonArgs.GetString(specific, "permissionDecisionReason")) ?? "Blocked by SDK hook.";
        var stdout = hook.Event is HookEvent.SessionStart or HookEvent.UserPromptSubmit or HookEvent.UserPromptExpansion
            ? specific is null ? "" : JsonArgs.GetString(specific, "additionalContext") ?? ""
            : answer.ToJsonString();
        return new HookExecution(deny ? 1 : 0, stdout, deny ? reason : "") { CallbackOutput = answer };
    }

    private void Observe(HookLifecycleEvent value)
    {
        try { ExecutionObserved?.Invoke(value); }
        catch (Exception) { /* Observers cannot veto or break a hook. */ }
    }

    private string ObserveStart(HookEvent hookEvent, string name)
    {
        var id = Guid.NewGuid().ToString();
        Observe(new HookLifecycleEvent(id, hookEvent, name, "started"));
        return id;
    }

    private async Task<FunctionHookResult> RunFunctionAsync(
        FunctionHook function, JsonObject payload, CancellationToken cancellationToken)
    {
        const string name = "function";
        var id = ObserveStart(function.Event, name);
        try
        {
            var result = await function.Run(payload, cancellationToken);
            Observe(new HookLifecycleEvent(id, function.Event, name, "response", 0,
                result.AdditionalContext ?? ""));
            return result;
        }
        catch (Exception ex)
        {
            Observe(new HookLifecycleEvent(id, function.Event, name, "response", 1, Stderr: ex.Message));
            throw;
        }
    }

    /// <summary>
    /// The reference's check on hook stdout: output that opens like a JSON object
    /// but does not parse is almost always string concatenation that forgot to
    /// escape, and it says so rather than reading the text as prose.
    /// </summary>
    public static string? MalformedJsonObject(string? stdout)
    {
        var trimmed = stdout?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{')
        {
            return null;
        }

        try
        {
            JsonNode.Parse(trimmed);
            return null;
        }
        catch (JsonException ex)
        {
            return "Hook output looks like a JSON object but is not valid JSON — " + ex.Message +
                   ". Emit the payload with a JSON encoder (jq, ConvertTo-Json, json.dumps) rather than string " +
                   "concatenation so backslashes and quotes inside strings are escaped.";
        }
    }

    private static HookExecution Blocked(string reason) => new(1, "", reason);

    /// <summary>
    /// A prompt hook outside Stop: the model judges the condition from the
    /// payload alone, with the generic wording, and a verdict of false blocks
    /// unless the hook asked to continue anyway.
    /// </summary>
    private async Task<HookExecution> RunPromptHookAsync(
        HookDefinition hook, JsonObject payload, CancellationToken cancellationToken)
    {
        if (promptEvaluator is null || hook.Prompt is not { Length: > 0 } condition)
        {
            // Nothing can answer it, so it has no opinion — never a block.
            return new HookExecution(0, "", "");
        }

        PromptHookVerdict? verdict;
        try
        {
            verdict = await promptEvaluator(
                new PromptHookRequest(
                    hook.Event, condition, payload.ToJsonString(), hook.Model,
                    TimeSpan.FromSeconds(hook.TimeoutSeconds), IsStopCondition: false),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HookExecution(0, "", "");
        }

        if (verdict is null || verdict.Ok || hook.ContinueOnBlock)
        {
            return new HookExecution(0, verdict?.Reason ?? "", "");
        }

        return Blocked(PromptHooks.BlockingError(condition, verdict.Reason));
    }

    private async Task<HookExecution> RunHttpHookAsync(
        HookDefinition hook, JsonObject payload, CancellationToken cancellationToken)
    {
        if (hook.Http is not { Url.Length: > 0 } spec)
        {
            return Blocked("HTTP hook has no url");
        }

        if (!RemoteHooks.AllowsHttpHooks(hook.Event))
        {
            // Skipped rather than posted, and not a block: these events fire
            // outside a turn, where the reference refuses HTTP hooks outright.
            return new HookExecution(0, "", RemoteHooks.HttpNotSupported(spec.Url, hook.Event));
        }

        if (!RemoteHooks.IsUrlAllowed(spec.Url, httpPolicy?.AllowedUrls))
        {
            return Blocked(RemoteHooks.UrlNotAllowed(spec.Url));
        }

        if (httpSender is null)
        {
            return Blocked("HTTP hooks are not available in this host.");
        }

        var allowed = RemoteHooks.AllowedEnvNames(spec.AllowedEnvVars, httpPolicy?.AllowedEnvVars);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
        };
        foreach (var (name, value) in spec.Headers ?? new Dictionary<string, string>())
        {
            headers[name] = RemoteHooks.InterpolateHeaderValue(
                value, allowed, Environment.GetEnvironmentVariable);
        }

        HookTransportResult result;
        try
        {
            result = await httpSender(
                new HttpHookRequest(
                    spec.Url, payload.ToJsonString(), headers, TimeSpan.FromSeconds(hook.TimeoutSeconds)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Blocked($"HTTP hook error: {ex.Message}");
        }

        return FromTransport(result);
    }

    private async Task<HookExecution> RunMcpHookAsync(
        HookDefinition hook, JsonObject payload, CancellationToken cancellationToken)
    {
        if (hook.Mcp is not { Server.Length: > 0, Tool.Length: > 0 } spec)
        {
            return Blocked("mcp_tool hook has no server or tool");
        }

        if (mcpCaller is null)
        {
            return Blocked(RemoteHooks.McpUnavailable(hook.Event));
        }

        HookTransportResult result;
        try
        {
            result = await mcpCaller(
                new McpHookRequest(
                    spec.Server, spec.Tool,
                    RemoteHooks.InterpolateInput(spec.Input, payload),
                    TimeSpan.FromSeconds(hook.TimeoutSeconds)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Blocked($"mcp_tool hook error: {ex.Message}");
        }

        return FromTransport(result);
    }

    /// <summary>
    /// Maps a transport answer onto the command shape. An aborted call is not a
    /// refusal — nothing judged it — so it passes with the reason on stderr.
    /// </summary>
    private static HookExecution FromTransport(HookTransportResult result)
    {
        if (result.Aborted)
        {
            return new HookExecution(0, "", "hook timed out");
        }

        if (result.Ok)
        {
            return new HookExecution(0, result.Body, "");
        }

        var reason = FirstNonEmpty(
            result.Error ?? "", result.Body,
            result.StatusCode is { } status ? $"hook returned status {status}" : "hook failed");
        return new HookExecution(1, result.Body, reason);
    }

    private async Task<HookExecution> RunCommandHookAsync(
        HookDefinition hook, JsonObject payload, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (hook.PluginRoot is { Length: > 0 } pluginRoot)
        {
            startInfo.Environment["CLAUDE_PLUGIN_ROOT"] = pluginRoot;
            startInfo.Environment["JARVIS_PLUGIN_ROOT"] = pluginRoot;
        }
        if (hook.PluginData is { Length: > 0 } pluginData) startInfo.Environment["CLAUDE_PLUGIN_DATA"] = pluginData;
        if (hook.ProjectDirectory is { Length: > 0 } project) startInfo.Environment["CLAUDE_PROJECT_DIR"] = project;
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(hook.Command);
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(hook.Command);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new HookExecution(-1, "", $"hook process failed to start: {ex.Message}");
        }

        try
        {
            await process.StandardInput.WriteAsync(payload.ToJsonString());
        }
        catch (IOException)
        {
            // The hook may exit without reading stdin.
        }
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(hook.TimeoutSeconds));
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new HookExecution(-1, "", $"hook timed out after {hook.TimeoutSeconds}s");
        }
        return new HookExecution(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
