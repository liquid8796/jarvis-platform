using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Providers;

namespace JarvisCode.Cli;

/// <summary>The headless front-end: text/JSON runs and a bidirectional SDK stream.</summary>
internal static class PrintRunner
{
    internal static void ValidateOptions(CliOptions options)
    {
        if (options.InputFormat == "stream-json" && options.OutputFormat != "stream-json")
            throw new CliError("Error: --input-format stream-json requires --output-format stream-json.");
        if (options.ReplayUserMessages && options.InputFormat != "stream-json")
            throw new CliError("Error: --replay-user-messages requires --input-format stream-json and --output-format stream-json.");
        if ((options.IncludePartialMessages || options.IncludeHookEvents || options.ForwardSubagentText) &&
            options.OutputFormat != "stream-json")
            throw new CliError("Error: stream event options require --output-format stream-json.");
    }

    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken, TextReader? standardInput = null,
        Func<IProviderRegistry, IProviderRegistry>? decorateProviders = null)
    {
        ValidateOptions(options);
        var structured = options.JsonSchema is { } schemaJson ? new CliStructuredOutput(schemaJson) : null;
        var prompt = options.Prompt;
        var streamingInput = options.InputFormat == "stream-json";
        using var nativeInput = streamingInput && standardInput is null
            ? new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true) : null;
        if (!streamingInput && prompt.Length == 0 && Console.IsInputRedirected)
            prompt = (await Console.In.ReadToEndAsync(cancellationToken)).Trim();
        if (!streamingInput && prompt.Length == 0)
        {
            Console.Error.WriteLine("Error: Input must be provided either through stdin or as a prompt argument when using --print");
            return 1;
        }

        CliServices? servicesReference = null;
        var ledger = new CliUsageLedger((provider, model) => servicesReference?.App.Settings.Models
            .FirstOrDefault(entry => entry.ProviderId == provider && entry.ModelId == model), options.MaxBudgetUsd);
        using var services = CliServices.Create(options,
            registry => new CliMeteredRegistry(decorateProviders?.Invoke(registry) ?? registry, ledger));
        servicesReference = services;
        var cwd = Directory.GetCurrentDirectory();
        await services.InitializeAsync(options, cwd, cancellationToken);
        var session = await services.ResolveSessionAsync(options, cwd, cancellationToken);
        if (options.Model is { Length: > 0 } modelName)
            session.ModelId = services.ResolveModel(modelName).ModelId;
        else if (CliAgents.Primary(services, options, cwd)?.Model is { Length: > 0 } profileModel)
            session.ModelId = services.ResolveModel(profileModel).ModelId;
        var model = services.Factory.ResolveModel(session) ?? throw new CliError(
            "Error: No model is configured. Add an API key and pick a default model in Settings, or pass --model/--settings.");
        var requestedModelId = model.ModelId;
        ledger.RequirePrice(model.ProviderId, model.ModelId);
        await services.ConnectMcpAsync(options, cwd, cancellationToken);
        if (options.AutoConnectIde && !services.Customizations.DisableLsp)
            Console.Error.WriteLine(await IdeServices.AutoConnectAsync(cwd, cancellationToken));

        var configuredMode = options.PermissionModeName ?? services.App.Settings.Current.PermissionModeName;
        var (mode, dontAsk) = (options with { PermissionModeName = configuredMode }).ResolvePermissionMode();
        if (configuredMode is null && !options.Bare && !options.SafeMode &&
            services.CliSettings.Sources.Contains("project") && !options.DangerouslySkipPermissions &&
            ProjectPermissions.LoadDefaultMode(cwd) is { } projectMode)
            mode = projectMode;
        if (options.Restricted && mode == PermissionMode.Bypass)
            throw new CliError(RestrictedMode.BypassRefused);
        var gate = CreateGate(services, options, session, mode);
        var runner = new CliTurnRunner(services, options, gate);
        var stream = new StreamJson(Console.Out, session.Id);
        var streamJson = options.OutputFormat == "stream-json";
        var sdk = streamingInput ? new CliSdkSession(services.App.Mcp, session.Id, cwd,
            Path.Combine(services.App.Paths.SessionsDirectory, "code", session.Id + ".json"),
            CliSdkSession.ReadConfiguredServerNames(options, cwd, services.App.Paths.UserMcpFile),
            disableHooks: options.Bare || options.SafeMode, disableCustomizations: options.SafeMode) : null;
        var initEmitted = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var work = new CliPendingWork(lifetime.Token);
        var workers = new AgentWorkerManager();
        runner.Workers = workers;
        var pendingWorkerReports = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        workers.WorkerStarted += info =>
        { pendingWorkerReports[info.Id] = 0; if (streamJson) stream.EmitTask(info, started: true); };
        workers.WorkerFinished += (info, result) =>
        {
            work.Post(ChatMessage.FromUserText(TaskNotifications.Wrap(TaskNotifications.Element(info.Id,
                result.IsError ? "failed" : "completed", info.PromptPreview, result.Content))) with { IsMeta = true });
            if (streamJson) stream.EmitTask(info, started: false);
            pendingWorkerReports.TryRemove(info.Id, out _);
        };
        var childModels = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        CancellationTokenSource? activeTurn = null;
        var stateLock = new object();
        bool running = false;
        void StopBrowserTurn() { lock (stateLock) activeTurn?.Cancel(); workers.KillAll(); }
        services.App.Browser.StopRequested += StopBrowserTurn;
        await using var mailbox = new LocalSessionMailbox(services.App.Paths.Root, session.Id, () => session.Title,
            (peer, message) => work.Post(ChatMessage.FromUserText(TaskNotifications.Wrap(
                $"Message from local session {peer.Title} ({peer.SessionId}):\n{message}")) with { IsMeta = true }),
            notice => work.Post(ChatMessage.FromUserText(notice.Message) with { IsMeta = true }));
        using var crons = new CliSessionCrons(new Core.Routines.RoutineStore(services.App.Paths.RoutinesFile), session.Id,
            () => !running && !work.HasReady,
            message => work.Post(ChatMessage.FromUserText(message) with { IsMeta = true }), workingDirectory: session.WorkingDirectory);
        async Task<JsonObject> HandleControl(JsonObject request, CancellationToken token)
        {
            var subtype = request["subtype"]?.GetValue<string>();
            JsonObject response;
            switch (subtype)
            {
                case "initialize":
                    await sdk!.InitializeAsync(request, token);
                    response = CliSdkControls.InitializeInfo(services, options, session);
                    break;
                case "mcp_status":
                    response = sdk!.ServerStatus();
                    break;
                case "mcp_reconnect":
                    await sdk!.ReconnectAsync(request["serverName"]?.GetValue<string>() ?? "", token);
                    response = new JsonObject();
                    break;
                case "mcp_toggle":
                    await sdk!.ToggleAsync(request["serverName"]?.GetValue<string>() ?? "",
                        request["enabled"]?.GetValue<bool>() ?? false, token);
                    response = new JsonObject();
                    break;
                case "interrupt":
                    lock (stateLock) activeTurn?.Cancel();
                    response = new JsonObject();
                    break;
                case "rewind_files":
                    lock (stateLock)
                        if (running || workers.RunningCount > 0) throw new CliError("Stop the active turn and tasks before rewinding files.");
                    response = await CliSdkControls.RewindFilesAsync(services, session,
                        request["user_message_id"]?.GetValue<string>() ?? "", token);
                    break;
                case "stop_task":
                    var taskId = request["task_id"]?.GetValue<string>() ?? "";
                    if (!workers.Kill(taskId) && !(services.App.BackgroundTasks.Get(taskId)?.SessionId == session.Id && services.App.BackgroundTasks.Kill(taskId)))
                        throw new CliError("No running task with that ID belongs to this session.");
                    response = new JsonObject();
                    break;
                case "get_context_usage":
                    response = CliSdkControls.ContextUsage(services, runner, session, model);
                    break;
                case "set_permission_mode":
                    var chosen = request["mode"]?.GetValue<string>();
                    if (!new[] { "acceptEdits", "auto", "bypassPermissions", "manual", "default", "dontAsk", "plan" }.Contains(chosen))
                        throw new CliError("Unknown permission mode.");
                    var (nextMode, nextDontAsk) = (options with { PermissionModeName = chosen, DangerouslySkipPermissions = false }).ResolvePermissionMode();
                    if (options.Restricted && nextMode == PermissionMode.Bypass)
                        throw new CliError(RestrictedMode.BypassRefused);
                    if (nextMode == PermissionMode.Plan) gate.EnterPlanMode();
                    else { if (gate.Mode == PermissionMode.Plan) gate.ExitPlanMode(); gate.Mode = nextMode; }
                    dontAsk = nextDontAsk;
                    response = new JsonObject();
                    break;
                case "set_model":
                    ModelInfo selected;
                    lock (stateLock)
                    {
                        if (running) throw new CliError("Change the model between turns, or interrupt the active turn first.");
                        selected = services.ResolveModel(request["model"]?.GetValue<string>() ?? services.App.Settings.Current.DefaultModelId ?? "");
                        ledger.RequirePrice(selected.ProviderId, selected.ModelId);
                    }
                    var modelChange = Core.Hooks.ModelSwitch.From(session.ModelId ?? model.ModelId, selected.ModelId,
                        selected.ModelId, Core.Hooks.ModelSwitchSource.Sdk, runner.LastContextTokens,
                        runner.LastUsageAt, DateTimeOffset.Now, targetModel: selected);
                    var switchHooks = sdk!.ApplyHooks(runner.LastHooks ?? services.LoadHooks(cwd));
                    if (modelChange is not null)
                    {
                        var decision = await switchHooks.RunPreModelSwitchAsync(modelChange, token);
                        if (!decision.Allowed) throw new CliError("Model unchanged: " + decision.BlockReason);
                    }
                    lock (stateLock)
                    {
                        if (running) throw new CliError("A turn started while the model change was being checked; retry between turns.");
                        session.ModelId = selected.ModelId;
                        model = selected;
                        requestedModelId = selected.ModelId;
                        runner.ModelSelected = true;
                    }
                    if (modelChange is not null)
                        await switchHooks.RunEventAsync(Core.Hooks.HookEvent.PostModelSwitch, modelChange.ToPayload(), token);
                    response = new JsonObject();
                    break;
                default:
                    throw new CliError($"Unsupported SDK control request: {subtype ?? "(missing subtype)"}.");
            }
            return response;
        }

        using var input = streamingInput ? new StreamJsonInput(standardInput ?? nativeInput ?? Console.In, stream, HandleControl, options.ReplayUserMessages) : null;
        if (input is not null) sdk!.Attach(input);
        async Task SelectAutomaticallyAsync(string modelId, CancellationToken token)
        {
            var selected = services.ResolveModel(modelId);
            ledger.RequirePrice(selected.ProviderId, selected.ModelId);
            var change = Core.Hooks.ModelSwitch.From(session.ModelId ?? model.ModelId, selected.ModelId, modelId,
                Core.Hooks.ModelSwitchSource.Auto, runner.LastContextTokens, runner.LastUsageAt, DateTimeOffset.Now, targetModel: selected);
            session.ModelId = selected.ModelId;
            model = selected;
            if (change is not null)
            {
                var hooks = runner.LastHooks ?? services.LoadHooks(cwd);
                if (sdk is not null) hooks = sdk.ApplyHooks(hooks);
                await hooks.RunEventAsync(Core.Hooks.HookEvent.PostModelSwitch, change.ToPayload(), token);
            }
        }
        SdkPermissionBridge? bridge = input is null ? null : new SdkPermissionBridge(gate, input);
        if (options.PermissionPromptTool is { Length: > 0 } permissionTool && permissionTool != "stdio")
        {
            bridge = new SdkPermissionBridge(gate, async (request, token) =>
            {
                var matches = services.App.Mcp.Tools.OfType<Core.Mcp.McpToolAdapter>()
                    .Where(tool => tool.Name == permissionTool || tool.SourceToolName == permissionTool).ToArray();
                if (matches.Length != 1) return new JsonObject { ["behavior"] = "deny", ["message"] =
                    $"Permission tool '{permissionTool}' is not connected or is ambiguous; use its full MCP tool name." };
                var result = await matches[0].ExecuteAsync(new JsonObject
                { ["tool_name"] = request["tool_name"]?.DeepClone(), ["input"] = request["input"]?.DeepClone() },
                    new Core.Tools.ToolExecutionContext { WorkingDirectory = session.WorkingDirectory,
                        SessionId = session.Id, AdditionalDirectories = session.AdditionalDirectories,
                        SessionLifetime = lifetime.Token, IsNonInteractiveSession = true }, token);
                if (result.IsError) return new JsonObject { ["behavior"] = "deny", ["message"] = result.Content };
                try { return JsonNode.Parse(result.Content) as JsonObject; }
                catch (System.Text.Json.JsonException) { return new JsonObject
                { ["behavior"] = "deny", ["message"] = "The permission tool did not return a JSON permission verdict." }; }
            });
        }
        if (bridge is not null && options.PermissionPrompts == "host")
            gate.PromptAsync = (question, token) => dontAsk
                ? Task.FromResult(PermissionDecision.Deny) : bridge.PromptAsync(question, token);
        if (bridge is not null) bridge.Interrupt = () => { lock (stateLock) activeTurn?.Cancel(); };
        if (bridge is not null)
            bridge.UpdatePermissions = new SdkPermissionUpdates(services, options, gate, session, value => dontAsk = value).ApplyAsync;

        runner.ExtraTools =
        [
            new ListAgentsTool(_ => Task.FromResult(CliSessionListing.Build(workers, mailbox, session.Id))),
            new ScheduleWakeupTool(work.Schedule, work.StopWakeups),
            .. ReportingTools.CreateHeadless((text, findings) =>
            {
                if (streamJson) stream.WriteLine(new JsonObject
                { ["type"] = "system", ["subtype"] = "review_findings", ["findings"] = findings.DeepClone(),
                    ["session_id"] = session.Id, ["uuid"] = Guid.NewGuid().ToString() });
                else Console.Error.WriteLine(text);
            }),
        ];

        runner.TransformSetup = setup =>
        {
            var context = setup.Context;
            if (sdk is not null)
            {
                var hooks = sdk.ApplyHooks(setup.TurnHooks);
                gate.PermissionRequestHookAsync = hooks.Has(Core.Hooks.HookEvent.PermissionRequest)
                    ? (tool, args, risk, token) => hooks.RunPermissionRequestAsync(tool, args, risk, token) : null;
                context = context with
                {
                    Hooks = hooks,
                    ToolContext = context.ToolContext with { FireHook = (hookEvent, payload) =>
                        sdk.Observe(hooks, hookEvent, payload, lifetime.Token) },
                    CompactionBlockedAsync = hooks.Has(Core.Hooks.HookEvent.PreCompact)
                        ? async trigger => (await hooks.RunPreCompactAsync(trigger, session.Id, lifetime.Token)) is { Allowed: false } refused
                            ? refused.BlockReason ?? "Blocked by PreCompact hook." : null : null,
                };
                setup = setup with { TurnHooks = hooks };
                if (context.ToolContext.Subagents is { } hookAgents)
                    context = context with { ToolContext = context.ToolContext with { Subagents = hookAgents with { Hooks = hooks } } };
            }
            var mainPrompt = context.SystemPrompt;
            var provider = context.Provider;
            if (structured is not null)
                provider = structured.Wrap(provider, notice =>
                { if (options.Verbose) Console.Error.WriteLine(notice); }, request => request.SystemPrompt == mainPrompt);
            if (options.IncludePartialMessages)
            {
                var partial = new PartialJsonMessage(stream);
                provider = new CliObservedProvider(provider, partial.Begin, partial.Observe,
                    request => request.SystemPrompt == mainPrompt);
            }
            var toolContext = context.ToolContext with
            {
                SessionLifetime = lifetime.Token,
                SendToSessionAsync = (target, message) => mailbox.SendAsync(target, message, lifetime.Token),
                SubscribeToSessionIdleAsync = target => mailbox.SubscribeAsync(target, lifetime.Token),
                MonitorEvent = (id, description, line) => work.Post(ChatMessage.FromUserText(
                    TaskNotifications.Wrap(TaskNotifications.Element(id, "running", description, line)))
                    with { IsMeta = true }),
            };
            if (bridge is not null && toolContext.Subagents is { } subagents)
                toolContext = toolContext with { Subagents = subagents with { PermissionGate = bridge } };
            return setup with { Context = context with
            { Provider = provider, PermissionGate = bridge ?? (IPermissionGate)gate, ToolContext = toolContext } };
        };

        var events = new CliTurnEvents
        {
            SetupPrepared = setup =>
            {
                model = setup.Model;
                if (!streamJson || initEmitted) return;
                initEmitted = true;
                stream.EmitInit(cwd, setup.Context.Tools.All.Select(t => t.Name),
                    services.App.Mcp.ConnectedToolCounts.Select(kv => (kv.Key, "connected"))
                        .Concat(services.ChromeTools().Count > 0 ? new[] { (InternalMcpServerNames.ClaudeInChrome, "connected") } : []), model.ModelId,
                    options.PermissionModeName ?? (mode == PermissionMode.Bypass ? "bypassPermissions" : "default"),
                    "settings", SubagentTool.BuiltInAgentTypes.Concat(CliAgents.Load(services, session.WorkingDirectory).Select(agent => agent.Name)).Distinct(),
                    runner.SkillState.AnnouncedListingNames,
                    outputStyle: CliSdkControls.OutputStyle(services, session),
                    commands: CliSdkControls.InitializeInfo(services, options, session)["commands"]!.AsArray().Select(command => command!["name"]!.GetValue<string>()),
                    plugins: services.PluginRoots.Select(plugin => (plugin.Key, plugin.Value)));
            },
            AssistantMessageCompleted = streamJson ? message => stream.EmitAssistant(message, model.ModelId) : null,
            ToolCompleted = streamJson ? completed => stream.EmitToolResult(completed.CallId, completed.Result, completed.IsError,
                images: completed.Images)
                : options.Verbose ? completed => Console.Error.WriteLine($"[tool] {completed.ToolName}: {FirstLine(completed.Result)}") : null,
            ToolStarted = !streamJson && options.Verbose
                ? started => Console.Error.WriteLine($"[tool] {started.ToolName} {started.CallDescription}".TrimEnd()) : null,
            Notice = options.Verbose ? notice => Console.Error.WriteLine($"[info] {notice}") : null,
            HookObserved = options.IncludeHookEvents ? stream.EmitHook : null,
            SubagentEvent = (parentId, evt) =>
            {
                if (evt is SubagentModelSelected selected) { childModels[parentId] = selected.ModelId; return; }
                if (!options.ForwardSubagentText && sdk?.ForwardSubagentText != true) return;
                if (evt is AssistantMessageCompleted message)
                    stream.EmitAssistant(message.Message, childModels.GetValueOrDefault(parentId) ?? model.ModelId, parentId);
                else if (evt is ToolExecutionCompleted completed)
                    stream.EmitToolResult(completed.CallId, completed.Result, completed.IsError, parentId, completed.Images);
            },
        };

        Task? pump = null;
        Task? forwarding = null;
        if (prompt.Length > 0) work.Post(new CliInputMessage(ChatMessage.FromUserText(prompt), Guid.NewGuid().ToString()));
        if (input is not null)
        {
            pump = Task.Run(() => input.PumpAsync(lifetime.Token), CancellationToken.None);
            forwarding = ForwardInputAsync(input, work, lifetime.Token);
        }
        else work.CompleteInput();

        int exitCode = 0;
        try
        {
            while (await work.NextAsync(() => !pendingWorkerReports.IsEmpty || workers.List().Any(x => x.Status == WorkerStatus.Running) ||
                crons.HasPendingWork ||
                services.App.BackgroundTasks.List().Any(x => string.Equals(x.Kind, "Monitor", StringComparison.OrdinalIgnoreCase) &&
                    x.Status == Core.BackgroundTasks.BackgroundTaskStatus.Running),
                lifetime.Token) is { } next)
            {
                Core.Utilities.DiagnosticLog.Write("CLI: user input dequeued");
                if (sdk is not null) await sdk.Ready.WaitAsync(lifetime.Token);
                if (sdk is not null)
                {
                    runner.ExcludeDynamicSections = options.ExcludeDynamicSections || sdk.ExcludeDynamicSections;
                    services.Customizations = services.Customizations with
                    {
                        Agents = [.. sdk.Agents.Concat(services.Customizations.Agents).DistinctBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)],
                        Skills = sdk.Skills is null ? services.Customizations.Skills :
                            [.. services.Customizations.ResolveSkills(session.WorkingDirectory, services.App.Paths, services.App.UiSettings.Current)
                                .Where(skill => sdk.Skills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase))],
                    };
                }
                if (options.FallbackModel is not null && !next.Message.IsMeta && model.ModelId != requestedModelId)
                    await SelectAutomaticallyAsync(requestedModelId, lifetime.Token);
                structured?.BeginTurn();
                var clock = Stopwatch.StartNew();
                CliTurnResult result;
                var fallbacks = new Queue<string>((options.FallbackModel ?? "").Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                using var turn = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (stateLock) { activeTurn = turn; running = true; }
                mailbox.NotifyBusy();
                try
                {
                    while (true)
                    {
                        int before = session.Messages.Count;
                        var identified = next.Message with { SdkUserMessageId = next.Uuid,
                            SdkCheckpointTurnNumber = services.App.Checkpoints.NextTurnNumber(session.Id) };
                        result = await runner.RunAsync(session, next.Message.GetText(), events, turn.Token, identified);
                        if (sdk is not null) await sdk.DrainObservationsAsync();
                        if (ledger.BudgetReached)
                        { result = result with { Reason = TurnEndReason.BudgetExhausted, Detail = ledger.BudgetDetail }; break; }
                        if (structured?.FailureDetail is { } schemaError)
                        { result = result with { Reason = TurnEndReason.StructuredOutputRetryExhausted, Detail = schemaError }; break; }
                        // A terminal safety failure is not a failed attempt to replay. Retain
                        // its actual tool results and never try another model automatically.
                        if (result.Reason != TurnEndReason.Error || !result.CanRetry || fallbacks.Count == 0) break;
                        while (session.Messages.Count > before) session.Messages.RemoveAt(session.Messages.Count - 1);
                        await SelectAutomaticallyAsync(fallbacks.Dequeue(), turn.Token);
                        Console.Error.WriteLine($"[info] Falling back to {model.ModelId}");
                    }
                }
                finally { lock (stateLock) { running = false; activeTurn = null; } }
                clock.Stop();
                var error = TurnEndReasons.IsError(result.Reason);
                var resultText = error ? result.Detail ?? "unknown error" :
                    result.FinalText.Length > 0 ? result.FinalText : result.Detail ?? "";
                var suggestion = options.PromptSuggestions == true && !error && !ledger.BudgetReached && next.Message.IsMeta != true
                    ? await CliPromptSuggestions.GenerateAsync(services.App.Providers.Get(model.ProviderId), model,
                        session, options.Betas, lifetime.Token) : null;
                if (options.OutputFormat == "text") Console.WriteLine(resultText);
                else if (error || pendingWorkerReports.IsEmpty && workers.RunningCount == 0 && !work.HasBackgroundReady)
                {
                    var accounting = ledger.Snapshot();
                    stream.WriteLine(stream.BuildResult(error, Subtype(result.Reason), resultText,
                        clock.ElapsedMilliseconds, accounting.DurationApiMs, Math.Max(1, accounting.Calls),
                        accounting.Usage, model.ModelId, model.ProviderId, model.MaxContextTokens,
                        result.PermissionDenials, TurnEndReasons.WireName(result.Reason), accounting,
                        structured?.Value, structured?.HasValue == true && !error));
                }
                if (error) exitCode = 1;
                if (streamJson && suggestion is not null) stream.WriteLine(CliPromptSuggestions.Frame(session.Id, suggestion));
                if (!work.HasReady) mailbox.NotifyIdle(result.FinalText);
                if (services.ChromeEnabled && workers.RunningCount == 0) services.App.Browser.EndSession();
                if (ledger.BudgetReached || structured?.FailureDetail is not null) break;
            }
        }
        finally
        {
            services.App.Browser.StopRequested -= StopBrowserTurn;
            if (services.ChromeEnabled) services.App.Browser.EndSession();
            if (runner.LastHooks is { } hooks && hooks.Has(Core.Hooks.HookEvent.SessionEnd))
            {
                using var end = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await hooks.RunEventAsync(Core.Hooks.HookEvent.SessionEnd,
                    new JsonObject { ["reason"] = "prompt_input_exit" }, end.Token); }
                catch (OperationCanceledException) { }
            }
            lifetime.Cancel();
            workers.KillAll();
            input?.Dispose();
            if (forwarding is not null) await forwarding;
            if (pump is not null) await pump;
        }
        return exitCode;
    }

    private static async Task ForwardInputAsync(StreamJsonInput input, CliPendingWork work, CancellationToken token)
    {
        try
        {
            await foreach (var message in input.Messages.ReadAllAsync(token)) work.Post(message);
            work.CompleteInput();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { work.CompleteInput(); }
        catch (Exception ex) { work.CompleteInput(ex); }
    }

    private static UiPermissionGate CreateGate(CliServices services, CliOptions options,
        Core.Sessions.Session session, PermissionMode mode) => new()
    {
        Mode = mode, WorkingDirectory = session.WorkingDirectory,
        AdditionalDirectories = session.AdditionalDirectories,
        SuppliedRuleLines = services.App.Settings.Current.PermissionRuleLines,
        RestrictToWorkspace = options.Restricted, IgnoreSettingsFiles = true,
        BlockReadsOutsideWorkingDirectories = services.App.Settings.Current.BlockReadsOutsideWorkingDirectories,
        PersistBlockReadsAsync = () =>
        { services.App.Settings.Current.BlockReadsOutsideWorkingDirectories = true; services.App.Settings.Save(); return Task.CompletedTask; },
    };

    private static string Subtype(TurnEndReason reason) => reason switch
    {
        TurnEndReason.BudgetExhausted => "error_max_budget_usd",
        TurnEndReason.StructuredOutputRetryExhausted => "error_max_structured_output_retries",
        TurnEndReason.MaxIterationsReached => "error_max_turns",
        _ => TurnEndReasons.IsError(reason) ? "error_during_execution" : "success",
    };

    private static string FirstLine(string text) => text.Split(['\r', '\n'], 2)[0];
}
