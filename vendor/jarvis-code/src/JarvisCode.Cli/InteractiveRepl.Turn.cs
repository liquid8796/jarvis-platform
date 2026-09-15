using System.Diagnostics;
using System.IO;
using System.Text;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Cli;

/// <summary>
/// Running a turn: what a submitted line becomes, how the transcript is
/// printed while it streams, and the three prompts the engine raises through
/// the turn context — permissions, plan approval and AskUserQuestion.
/// </summary>
internal sealed partial class InteractiveRepl
{
    /// <summary>
    /// Enter: run the line, or queue it when a turn already has the floor. A
    /// command is awaited rather than left running — its output belongs to this
    /// keystroke, and a loop that moved on could exit before it printed.
    /// </summary>
    private async Task SubmitComposerAsync()
    {
        if (_composer.IsEmpty)
        {
            return;
        }

        var mode = _composer.Mode;
        var (display, expanded, pasted) = _composer.TakeSubmission();
        _completion = null;
        _navigator.Reset();
        _history.Add(display, _session.WorkingDirectory, _session.Id, pasted);
        _navigator.Reset();

        if (mode == Repl.Input.ComposerMode.Bash)
        {
            await RunShellLineAsync(expanded.TrimStart().TrimStart('!'), CancellationToken.None);
            return;
        }

        if (mode == Repl.Input.ComposerMode.Memory)
        {
            RememberLine(expanded.TrimStart().TrimStart('#').Trim());
            return;
        }

        if (expanded.StartsWith('/') && !options.DisableSlashCommands)
        {
            var parts = expanded[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
            await RunCommandAsync(parts[0], parts.Length > 1 ? parts[1] : "", CancellationToken.None);
            return;
        }

        if (_turn is { IsCompleted: false })
        {
            _queued.Add(expanded);
            return;
        }

        Emit(_ansi.Dim("> " + display.ReplaceLineEndings(" ")));
        StartTurn(expanded);
    }


    /// <summary>ctrl+x enter: queue the line even when nothing is running.</summary>
    private void QueueComposer()
    {
        if (_composer.IsEmpty)
        {
            return;
        }

        var (display, expanded, pasted) = _composer.TakeSubmission();
        _history.Add(display, _session.WorkingDirectory, _session.Id, pasted);
        _queued.Add(expanded);
        _completion = null;
    }

    private void StartTurn(string prompt, ChatMessage? inputMessage = null)
    {
        var owner = State;
        lock (owner.Sync)
        {
        if (owner._turn is { IsCompleted: false })
        {
            if (inputMessage?.IsMeta == true) _notifications.Enqueue(inputMessage);
            else _queued.Add(prompt);
            return;
        }
        _suggestedPrompt = null;
        _turnCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionLifetime?.Token ?? _hostLifetime.Token);
        _sessionMailbox?.NotifyBusy();
        _turnClock.Restart();
        _turnTokens = 0;
        _thinking = false;
        _answerPreview = "";
        _compacting = false;
        _phase = TurnPhase.Requesting;
        _verb = SpinnerVerbs.Pick(
            SpinnerVerbs.Resolve(null, replace: false), Environment.TickCount);
        var lifetime = _turnCts.Token;
        _turn = Task.Run(async () =>
        {
            _executingSession.Value = owner;
            await RunPromptAsync(prompt, lifetime, inputMessage);
        }, CancellationToken.None);
        _ = _turn.ContinueWith(completed => InSession(owner, () =>
        {
            lock (owner.Sync)
            {
                if (ReferenceEquals(owner._turn, completed)) owner._turn = null;
                if (!_hostLifetime.IsCancellationRequested) { DrainQueue(); DeliverNotifications(); }
            }
            _notificationReady.Release();
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task RunPromptAsync(string prompt, CancellationToken cancellationToken, ChatMessage? inputMessage = null)
    {
        var markdown = new MarkdownTerminal(_ansi, _console.Width);
        var sink = new StreamingMarkdownSink(markdown);
        var pendingRows = new List<string>();
        var usageAccumulator = new TurnUsageAccumulator();

        void Flush()
        {
            foreach (var block in sink.Take())
            {
                pendingRows.Add(block);
            }

            if (pendingRows.Count == 0)
            {
                return;
            }

            Emit(string.Join("\n\n", pendingRows));
            pendingRows.Clear();
        }

        var events = new CliTurnEvents
        {
            TextPreview = preview =>
            {
                _answerPreview = preview;
                if (preview.Length > 0)
                {
                    _phase = TurnPhase.Responding;
                    _thinking = false;
                }
            },
            TextDelta = delta =>
            {
                _answerPreview = "";
                _phase = TurnPhase.Responding;
                _thinking = false;
                sink.Append(delta);
                Flush();
            },
            ThinkingDelta = _ =>
            {
                _thinking = true;
                _phase = TurnPhase.Thinking;
                if (_verbose)
                {
                    _phase = TurnPhase.Thinking;
                }
            },
            AssistantMessageCompleted = _ =>
            {
                _answerPreview = "";
                sink.Flush();
                Flush();
            },
            ToolStarted = started =>
            {
                if (services.ChromeEnabled && services.ChromeTools().Any(tool => tool.Name == started.ToolName)) _browserOwner = State;
                _phase = TurnPhase.ToolUse;
                var label = started.CallDescription is { Length: > 0 } description
                    ? description
                    : started.ToolName;
                Emit(_ansi.Color("claude", ToolRows.Header(label)));
            },
            ToolCompleted = completed =>
            {
                var lines = ToolRows.ResultLines(completed.Result, _console.Width);
                if (lines.Count == 0)
                {
                    return;
                }

                Emit(string.Join('\n', lines.Select(line =>
                    completed.IsError ? _ansi.Color("error", line) : _ansi.Dim(line))));
            },
            ToolDenied = denied =>
                Emit(_ansi.Color("error", $"{ToolRows.ResultIndent}{ToolRows.Connector}{denied.ToolName} denied")),
            SubagentEvent = (_, entry) =>
            {
                if (entry is ToolExecutionStarted started && services.ChromeEnabled &&
                    services.ChromeTools().Any(tool => tool.Name == started.ToolName)) _browserOwner = State;
            },
            UsageReported = (total, _) =>
            {
                _turnTokens = total.TotalInputTokens + total.OutputTokens;
                var delta = usageAccumulator.Observe(total);
                _sessionInputTokens += delta.InputTokens;
                _sessionOutputTokens += delta.OutputTokens;
                _sessionCacheReadInputTokens += delta.CacheReadInputTokens;
                _sessionCacheCreationInputTokens += delta.CacheCreationInputTokens;
                _sessionUsageIsEstimated |= total.IsEstimated;
            },
            Notice = notice => EmitNotice($"{Glyphs.Dot} {notice}"),
        };

        try
        {
            var result = await _runner.RunAsync(_session, prompt, events, cancellationToken, inputMessage);
            sink.Flush();
            Flush();
            ReportTurnEnd(result);
            if (options.PromptSuggestions == true && result.Reason == TurnEndReason.Completed && inputMessage?.IsMeta != true)
                _suggestedPrompt = await CliPromptSuggestions.GenerateAsync(services.App.Providers.Get(_model.ProviderId),
                    _model, _session, options.Betas, cancellationToken);
            if (_queued.Count == 0 && _notifications.IsEmpty) _sessionMailbox?.NotifyIdle(result.FinalText);
        }
        catch (OperationCanceledException)
        {
            sink.Flush();
            Flush();
            Emit(_ansi.Color("error", "[Request interrupted by user]"));
            Emit(_ansi.Dim(StatusRow.InterruptedRow));
        }
        catch (CliError error)
        {
            EmitError(error.Message);
        }
        finally
        {
            _answerPreview = "";
            _turnClock.Stop();
            _turnCts?.Dispose();
            _turnCts = null;
            _model = services.Factory.ResolveModel(_session) ?? _model;
        }
    }

    private void ReportTurnEnd(CliTurnResult result)
    {
        switch (result.Reason)
        {
            case TurnEndReason.Completed:
                return;
            case TurnEndReason.Cancelled:
                Emit(_ansi.Color("error", "[Request interrupted by user]"));
                return;
            case TurnEndReason.BlockingLimit or TurnEndReason.RapidRefillBreaker:
                EmitError(result.Detail ?? AgentOrchestrator.PromptTooLongMessage);
                return;
            case TurnEndReason.HookStopped:
                EmitNotice(result.Detail ?? "A hook stopped the turn.");
                return;
            default:
                if (TurnEndReasons.IsError(result.Reason))
                {
                    EmitError($"Error: {result.Detail ?? "the turn failed"}");
                }

                return;
        }
    }

    /// <summary>Sends the next queued prompt once the floor is free, as the reference drains its queue.</summary>
    private void DrainQueue()
    {
        if (_queued.Count == 0 || _turn is { IsCompleted: false })
        {
            return;
        }

        var next = _queued[0];
        _queued.RemoveAt(0);
        Emit(_ansi.Dim("> " + next.ReplaceLineEndings(" ")));
        StartTurn(next);
    }

    /// <summary>
    /// A turn runner wired to this front-end: the dialogs the engine raises,
    /// the worker manager background agents report to, and the session-scoped
    /// switches /goal and /pause-memory set.
    /// </summary>
    private CliTurnRunner NewRunner()
    {
        if (_runner is not null) return _runner;
        EnsureSessionRuntime();
        var owner = State;
        var pending = _sessionPending!;
        var mailbox = _sessionMailbox!;
        var lifetime = _sessionLifetime!.Token;
        return new CliTurnRunner(services, options, _gate)
        {
            AskUser = ShowQuestionAsync, PlanApproval = ShowPlanApprovalAsync, Workers = _workers,
            SessionGoal = _sessionGoal, MemoryPaused = _memoryPaused,
            ExtraTools =
            [
                new ListAgentsTool(_ => Task.FromResult(CliSessionListing.Build(_workers, mailbox, _session.Id))),
                new ScheduleWakeupTool(pending.Schedule, pending.StopWakeups),
                .. ReportingTools.CreateHeadless((text, _) => Emit(text)),
            ],
            TransformSetup = setup => setup with { Context = setup.Context with
            {
                ToolContext = setup.Context.ToolContext with
                {
                    SessionLifetime = lifetime,
                    SendToSessionAsync = (target, message) => mailbox.SendAsync(target, message, lifetime),
                    SubscribeToSessionIdleAsync = target => mailbox.SubscribeAsync(target, lifetime),
                    MonitorEvent = (id, description, line) => QueueNotification(ChatMessage.FromUserText(
                        TaskNotifications.Wrap(TaskNotifications.Element(id, "running", description, line))) with { IsMeta = true }, lifetime, owner),
                },
            } },
        };
    }

    private void EnsureSessionRuntime()
    {
        if (_runtimeSessionId == _session.Id) return;
        DisposeSessionRuntime();
        _runtimeSessionId = _session.Id;
        _sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_hostLifetime.Token);
        _workers = new Core.Agent.AgentWorkerManager();
        var boundSession = _session;
        var lifetime = _sessionLifetime.Token;
        var owner = State;
        _workerFinished = (info, result) => InSession(owner, () => OnWorkerFinished(info, result, lifetime));
        _workers.WorkerFinished += _workerFinished;
        void Deliver(ChatMessage message) { if (!lifetime.IsCancellationRequested) QueueNotification(message, lifetime, owner); }
        _sessionPending = new CliPendingWork(lifetime, Deliver);
        _sessionMailbox = new LocalSessionMailbox(services.App.Paths.Root, _session.Id, () => boundSession.Title,
            (peer, message) => Deliver(ChatMessage.FromUserText(TaskNotifications.Wrap(
                $"Message from local session {peer.Title} ({peer.SessionId}):\n{message}")) with { IsMeta = true }),
            notice => Deliver(ChatMessage.FromUserText(notice.Message) with { IsMeta = true }));
        _sessionCrons = new CliSessionCrons(new Core.Routines.RoutineStore(services.App.Paths.RoutinesFile), _session.Id,
            () => (owner._turn is null || owner._turn.IsCompleted) && owner._queued.Count == 0 && owner._notifications.IsEmpty,
            prompt => Deliver(ChatMessage.FromUserText(prompt) with { IsMeta = true }),
            workingDirectory: _session.WorkingDirectory);
    }

    private void DisposeSessionRuntime()
    {
        _sessionLifetime?.Cancel();
        if (_workerFinished is not null) _workers.WorkerFinished -= _workerFinished;
        _workers.KillAll();
        _queued.Clear();
        _notifications.Clear();
        _prMonitor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _prMonitor = null;
        if (_gate is not null) _gate.PrAutoFixActive = false;
        if (_runner?.LastHooks is { } hooks && hooks.Has(Core.Hooks.HookEvent.SessionEnd))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { hooks.RunEventAsync(Core.Hooks.HookEvent.SessionEnd,
                new System.Text.Json.Nodes.JsonObject { ["reason"] = "prompt_input_exit", ["session_id"] = _session.Id }, timeout.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        _sessionPending?.Dispose();
        _sessionCrons?.Dispose();
        _sessionMailbox?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _sessionLifetime?.Dispose();
        _sessionLifetime = null; _sessionPending = null; _sessionCrons = null; _sessionMailbox = null;
        _runtimeSessionId = null;
    }

    /// <summary>
    /// A background agent finished: the reference delivers its report as a
    /// task notification on a hidden user message, immediately when the session
    /// is idle and after the current response otherwise.
    /// </summary>

    private void OnWorkerFinished(JarvisCode.Core.Agent.WorkerInfo info, ToolResult result, CancellationToken lifetime)
    {
        if (lifetime.IsCancellationRequested) return;
        var summary = $"Agent {info.Name ?? info.Id} finished";
        var notification = SystemReminders.TaskNotification(
            info.Id, result.IsError ? "failed" : "completed", summary, result.Content);
        EmitNotice($"{Glyphs.Dot} {summary}");
        QueueNotification(SystemReminders.HarnessMessage(notification) with { IsMeta = true }, lifetime);
    }


    private void QueueNotification(ChatMessage message, CancellationToken sessionLifetime = default, SessionState? target = null)
    {
        if (_hostLifetime.IsCancellationRequested || sessionLifetime.IsCancellationRequested) return;
        var owner = target ?? State;
        owner._notifications.Enqueue(message);
        _ = Task.Run(() => InSession(owner, () => { lock (owner.Sync) DeliverNotifications(); }));
        _notificationReady.Release();
    }

    /// <summary>Runs the next notification as its own turn once the floor is free.</summary>
    private void DeliverNotifications()
    {
        if (_notifications.IsEmpty || _turn is { IsCompleted: false })
        {
            return;
        }

        if (_notifications.TryDequeue(out var notification)) StartTurn(notification.GetText(), notification);
    }

    /// <summary>The gate's prompt callback: raises the dialog and waits for the answer.</summary>
    private Task<PermissionDecision> PromptForPermissionAsync(
        PermissionPrompt prompt, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<PermissionDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _permissionAnswer = answer;
        _permission = new PermissionPromptDialog(prompt, AlwaysAllowLabel(prompt), _ansi);
        cancellationToken.Register(() => answer.TrySetResult(PermissionDecision.Deny));
        return answer.Task;
    }

    /// <summary>
    /// The standing permission this call could earn, in the reference's own
    /// wording: a shell call earns its command in this directory, anything else
    /// earns the tool.
    /// </summary>
    private string? AlwaysAllowLabel(PermissionPrompt prompt) =>
        prompt.Risk == CallRisk.Escalated
            ? null
            : prompt.ToolName is "Bash" or "PowerShell"
                ? PermissionPromptDialog.DontAskShellLabel(prompt.ToolName, _session.WorkingDirectory)
                : PermissionPromptDialog.DontAskAnyLabel(prompt.ToolName);

    /// <summary>ExitPlanMode's approval, rendered as the reference's plan dialog.</summary>
    private Task<PlanApprovalDecision> ShowPlanApprovalAsync(string plan, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<PlanApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _planAnswer = answer;
        _plan = new PlanApproval(
            plan, PlanModeTools.PlanFilePath(_session.WorkingDirectory, _session.Id), _ansi);
        cancellationToken.Register(() => answer.TrySetResult(new PlanApprovalDecision(false)));
        return answer.Task;
    }

    /// <summary>AskUserQuestion's card.</summary>
    private Task<UserQuestionAnswers?> ShowQuestionAsync(
        IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<UserQuestionAnswers?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _questionAnswer = answer;
        _question = new AskUserQuestionDialog(questions, _ansi).Start();
        cancellationToken.Register(() => answer.TrySetResult(null));
        return answer.Task;
    }

    /// <summary>
    /// <c>!command</c>: the reference runs it here rather than sending it to the
    /// model, and shows the output under the line.
    /// </summary>
    private async Task RunShellLineAsync(string command, CancellationToken cancellationToken)
    {
        command = command.Trim();
        if (command.Length == 0)
        {
            return;
        }

        Emit(_ansi.Color("bashBorder", "! " + command));
        // Whichever shell this front-end was launched from, which is the one
        // ShellEnvironment already uses to decide the prompt's Shell line.
        bool bash = ShellEnvironment.LaunchedFromBash;
        var info = new ProcessStartInfo(bash ? "bash" : "powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _session.WorkingDirectory,
        };
        foreach (var argument in bash
                     ? (string[])["-lc", command]
                     : ["-NoProfile", "-NonInteractive", "-Command", command])
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                EmitError($"Could not start {info.FileName}");
                return;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (stdout + stderr).TrimEnd();
            if (output.Length > 0)
            {
                Emit(string.Join('\n', ToolRows.ResultLines(output, _console.Width).Select(_ansi.Dim)));
            }

            if (process.ExitCode != 0)
            {
                EmitError($"{ToolRows.ResultIndent}{ToolRows.Connector}[exited with code {process.ExitCode}]");
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            EmitError(ex.Message);
        }
    }

    /// <summary><c>#note</c>: the reference writes the line into the project's memory.</summary>
    private void RememberLine(string note)
    {
        if (note.Length == 0)
        {
            return;
        }

        try
        {
            var directory = Core.Memory.ProjectMemory.DirectoryFor(
                services.App.Paths.MemoryRoot, _session.WorkingDirectory);
            Directory.CreateDirectory(directory);
            var index = Path.Combine(directory, "MEMORY.md");
            var existing = File.Exists(index) ? File.ReadAllText(index) : "# Memory index\n";
            File.WriteAllText(index, existing.TrimEnd('\n') + "\n- " + note + "\n");
            EmitNotice($"{ToolRows.Remembered}: {note}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EmitError($"Could not write memory: {ex.Message}");
        }
    }

    /// <summary>The command list the "/" completion filters, including this session's skills.</summary>
    private IReadOnlyList<SlashMenuItem> ComposerCommands()
    {
        var items = new List<SlashMenuItem>();
        foreach (var command in ReplCommandTable.Commands)
        {
            items.Add(new SlashMenuItem
            {
                Kind = SlashMenuItemKind.Button,
                Label = command.Name,
                SkillDescription = command.Description,
                ArgumentHint = command.ArgumentHint ?? "",
                Aliases = [.. command.Names.Skip(1)],
                AcceptsArgs = command.ArgumentHint is { Length: > 0 },
            });
        }

        foreach (var skill in LoadSkills())
        {
            items.Add(new SlashMenuItem
            {
                Kind = SlashMenuItemKind.Skill,
                Label = skill.Name,
                SkillId = skill.Name,
                SkillDescription = skill.Description,
                ArgumentHint = skill.ArgumentHint ?? "",
            });
        }

        return items;
    }

    private IReadOnlyList<Core.Customization.SkillDefinition> LoadSkills()
    {
        try
        {
            var ui = services.App.UiSettings.Current;
            var scope = options.Bare ? services.Customizations with { DisableAutomaticDiscovery = false } : services.Customizations;
            return SkillCatalog.ForComposer(
                SkillCatalog.Enabled(scope.ResolveSkills(_session.WorkingDirectory, services.App.Paths, ui), ui));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
