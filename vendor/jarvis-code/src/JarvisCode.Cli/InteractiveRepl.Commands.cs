using System.IO;
using System.Text;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Models;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli;

/// <summary>
/// The REPL's slash commands: the reference's surface, each running locally
/// against the same services the desktop drives, with the account- and
/// product-bound ones refusing by name.
/// </summary>
internal sealed partial class InteractiveRepl
{
    private bool _exiting;

    /// <summary>Runs a typed command. Unknown names fall through to the skill catalogue.</summary>
    private async Task RunCommandAsync(string name, string argument, CancellationToken cancellationToken)
    {
        var command = ReplCommandTable.Find(name);
        if (command is null)
        {
            await RunSkillOrUnknownAsync(name, argument, cancellationToken);
            return;
        }

        if (command.Kind == ReplCommandKind.Unavailable)
        {
            EmitError(ReplCommandTable.NotAvailable(command.Name, command.Unavailable ?? "unavailable"));
            return;
        }
        if (_turn is { IsCompleted: false } && command.Name is "clear" or "resume" or "rewind" or "branch" or "cd" or "background" or "model")
        { EmitError("Wait for the current turn to finish, or interrupt it first."); return; }

        switch (command.Name)
        {
            case "exit":
                _exiting = true;
                _screen.ClearLive();
                return;

            case "help":
                EmitHelp();
                return;

            case "version":
                Emit($"Jarvis Code {StreamJson.Version}");
                return;

            case "status":
                EmitStatus();
                return;

            case "context":
                EmitContext();
                return;

            case "usage":
                EmitUsage();
                return;

            case "doctor":
                await Subcommands.DoctorCommandAsync(cancellationToken);
                return;

            case "clear":
                await ClearAsync(cancellationToken);
                return;

            case "compact":
                await CompactAsync(argument, cancellationToken);
                return;

            case "model":
                await SwitchModelCommandAsync(argument, cancellationToken);
                return;

            case "ide":
                Emit(await IdeServices.CommandAsync(_session.WorkingDirectory, argument, cancellationToken));
                return;
            case "chrome":
                if (!services.ChromeEnabled) { EmitNotice("Browser integration is disabled. Start with --chrome to enable it."); return; }
                await services.App.Browser.RefreshDesktopConnectionAsync(cancellationToken);
                if (argument.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
                {
                    var error = services.App.Browser.SelectBrowser(argument[4..].Trim());
                    if (error is not null) { EmitError(error); return; }
                }
                var browsers = services.App.Browser.Connections;
                Emit(browsers.Count == 0 ? "No browser extension is connected." : string.Join('\n', browsers.Select(browser =>
                    (browser.Active ? "* " : "  ") + browser.Id + " · " + browser.Name + (browser.Ready ? " · ready" : " · connecting"))));
                return;

            case "effort":
                EffortCommand(argument);
                return;

            case "autocompact":
                AutocompactCommand(argument);
                return;

            case "brief":
                _brief = !_brief;
                EmitNotice(_brief ? "Brief mode on" : "Brief mode off");
                return;

            case "goal":
                GoalCommand(argument);
                return;

            case "pause-memory":
                _memoryPaused = !_memoryPaused;
                _runner.MemoryPaused = _memoryPaused;
                EmitNotice(_memoryPaused
                    ? "Memory recall paused for this session."
                    : "Memory recall resumed.");
                return;

            case "memory":
                EmitMemoryFiles();
                return;

            case "hooks":
                EmitHooks();
                return;

            case "insights":
                EmitInsights();
                return;

            case "skill-doctor":
                EmitSkillDoctor();
                return;

            case "skills":
                EmitSkills();
                return;

            case "keybindings":
                await OpenKeybindingsAsync(cancellationToken);
                return;

            case "statusline":
                StatuslineCommand(argument);
                return;

            case "output-style":
                OutputStyleCommand(argument);
                return;

            case "terminal-setup":
                Emit(TerminalSetup.Run(services.App.Paths.Root, services.App.Paths.ProfileName));
                return;

            case "heapdump":
                var dump = HeapDump.Write(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    $"jarvis-{DateTime.Now:yyyyMMdd-HHmmss}.dmp"));
                Emit(dump is null ? "Heap dump failed." : $"Heap dump written to {dump}");
                return;

            case "powerup":
                PowerupCommand(argument);
                return;

            case "tui":
                _fullscreen = !_fullscreen;
                _screen.SetAlternateScreen(_fullscreen);
                return;

            case "add-dir":
                AddDirectory(argument);
                return;

            case "cd":
                ChangeDirectory(argument);
                return;

            case "rename":
                await RenameAsync(argument, cancellationToken);
                return;

            case "export":
                ExportSession(argument);
                return;

            case "fork":
                await ForkAsync(cancellationToken);
                return;

            case "branch":
                await BranchAsync(cancellationToken);
                return;

            case "resume":
                await OpenResumePickerAsync(argument, cancellationToken);
                return;

            case "rewind":
                await RewindAsync(cancellationToken);
                return;
            case "diff":
                await OpenDiffAsync(argument, cancellationToken);
                return;
            case "babysit-pr":
                await BabysitPrAsync(argument, cancellationToken);
                return;
            case "background":
                if (!options.NoSessionPersistence) await services.App.Sessions.SaveAsync(_session, cancellationToken);
                await CliBackground.StartAsync(options with { Prompt = "", Resume = true, ResumeValue = _session.Id,
                    Continue = false, SessionId = null, Background = true, Worktree = false }, cancellationToken);
                _exiting = true;
                return;

            case "subtask":
                await SubtaskAsync(argument, cancellationToken);
                return;

            case "plan":
                PlanCommand();
                return;

            case "permissions":
                EmitPermissions();
                return;

            case "tasks":
                EmitTasks();
                return;

            case "teammates":
                EmitTeammates();
                return;

            case "list-agents":
                EmitAgents();
                return;

            case "workflows":
                EmitWorkflows();
                return;

            case "loop":
                LoopCommand(argument);
                return;

            case "loops":
                LoopsCommand(argument);
                return;

            case "mcp":
                await McpCommandAsync(argument, cancellationToken);
                return;

            case "mcp-auth":
                await McpAuthAsync(argument, cancellationToken);
                return;

            case "config":
                ConfigCommand(argument);
                return;

            case "theme":
                ThemeCommand(argument);
                return;

            case "color":
                ColorCommand(argument);
                return;

            case "copy":
                CopyCommand(argument);
                return;

            case "reload-skills":
                SkillCatalog.InvalidateCache();
                EmitNotice("Skills reloaded.");
                return;

            case "reload-plugins":
                SkillCatalog.InvalidateCache();
                EmitNotice("Plugins reloaded.");
                return;

            case "plugin":
                await PluginCommandAsync(argument, cancellationToken);
                return;

            case "plugin-types":
                EmitNotice(ReplCommandTable.NotAvailable(
                    "plugin-types", "the typings file is written from the desktop app's Connectors page"));
                return;

            case "daemon":
                EmitNotice("Scheduled routines run from the desktop app's Scheduled page; " +
                           "the CLI's own schedules are managed with CronCreate/CronList/CronDelete.");
                return;

            case "wellbeing":
                EmitNotice(ReplCommandTable.NotAvailable(
                    "wellbeing", "break reminders are a desktop notification feature"));
                return;

            case "setup-bedrock":
            case "setup-vertex":
                EmitNotice($"Open Settings › Providers in the desktop app to configure " +
                           $"{(command.Name == "setup-bedrock" ? "Amazon Bedrock" : "Google Vertex AI")}: " +
                           "its card holds the credential, region and model ids.");
                return;

            case "batch":
                BatchCommandRun(argument);
                return;

            case "debug":
                DebugCommandRun(argument);
                return;

            case "release-notes":
                await ReleaseNotesAsync(cancellationToken);
                return;

            case "code-review":
            case "security-review":
            case "init":
            case "recap":
                await RunPromptCommandAsync(command.Name, argument, cancellationToken);
                return;
        }

        EmitError(ReplCommandTable.Unknown(name));
    }

    /// <summary>A typed <c>/name</c> that is not a built-in: a skill, or the reference's unknown line.</summary>
    private async Task RunSkillOrUnknownAsync(string name, string argument, CancellationToken cancellationToken)
    {
        var skills = LoadSkills();
        var skill = skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            EmitError(ReplCommandTable.Unknown(name, skills.Select(s => s.Name)));
            return;
        }

        if (_turn is { IsCompleted: false })
        {
            _queued.Add("/" + name + (argument.Length > 0 ? " " + argument : ""));
            return;
        }

        Emit(_ansi.Dim($"> /{name}{(argument.Length > 0 ? " " + argument : "")}"));
        StartTurn("/" + name + (argument.Length > 0 ? " " + argument : ""));
        await Task.CompletedTask;
    }

    /// <summary>The commands that are a prompt: they run a turn like a typed message.</summary>
    private Task RunPromptCommandAsync(string name, string argument, CancellationToken cancellationToken)
    {
        var text = "/" + name + (argument.Length > 0 ? " " + argument : "");
        if (_turn is { IsCompleted: false })
        {
            _queued.Add(text);
            return Task.CompletedTask;
        }

        Emit(_ansi.Dim("> " + text));
        StartTurn(text);
        return Task.CompletedTask;
    }

    /// <summary>
    /// /batch: the reference refuses an empty instruction and a working
    /// directory that is not a repository, and otherwise sends the
    /// orchestration prompt as a turn.
    /// </summary>
    private void BatchCommandRun(string argument)
    {
        var composed = BatchCommand.Compose(
            argument, BatchCommand.InsideGitRepository(_session.WorkingDirectory));
        if (!BatchCommand.IsPrompt(composed))
        {
            EmitError(composed);
            return;
        }

        SendComposedPrompt("/batch" + (argument.Trim().Length > 0 ? " " + argument.Trim() : ""), composed);
    }

    /// <summary>/debug: arm the session's debug log, then hand the model its tail.</summary>
    private void DebugCommandRun(string argument)
    {
        var wasAlreadyOn = SessionDebugLog.Enable();
        var path = SessionDebugLog.Path ?? "(no debug log)";
        var settings = new DebugCommand.SettingsPaths(
            services.App.Paths.SettingsFile,
            Path.Combine(_session.WorkingDirectory, ".jarvis", "settings.json"),
            Path.Combine(_session.WorkingDirectory, ".jarvis", "settings.local.json"));
        SendComposedPrompt(
            "/debug" + (argument.Trim().Length > 0 ? " " + argument.Trim() : ""),
            DebugCommand.Prompt(
                argument, path, DebugCommand.ReadTail(SessionDebugLog.Path), settings, wasAlreadyOn));
    }

    /// <summary>Echoes what was typed, then runs the composed text as the turn's prompt.</summary>
    private void SendComposedPrompt(string typed, string prompt)
    {
        if (_turn is { IsCompleted: false })
        {
            _queued.Add(typed);
            return;
        }

        Emit(_ansi.Dim("> " + typed));
        StartTurn(prompt);
    }

    /// <summary>
    /// /release-notes: the reference's picker over this product's own release
    /// notes, or its one-line notice when there are none to show.
    /// </summary>
    private async Task ReleaseNotesAsync(CancellationToken cancellationToken)
    {
        var notes = await ReleaseNotes.FetchAsync(services.App.Http, cancellationToken);
        if (notes.Count == 0 || !_console.IsInteractive)
        {
            EmitNotice(notes.Count == 0 ? ReleaseNotes.NothingToShow() : ReleaseNotes.FormatAll(notes));
            return;
        }

        _releaseNotes = new ReleaseNotesPicker(notes, _ansi);
        try
        {
            while (true)
            {
                Redraw();
                var press = await ReadNextAsync(cancellationToken);
                if (press is null)
                {
                    return;
                }

                if (press.Key == ReplRedrawKey)
                {
                    continue;
                }

                var route = _router.Route(_releaseNotes.Context, press);
                var result = _releaseNotes.Handle(route.Action, press);
                if (result.Outcome == DialogOutcome.Cancelled)
                {
                    return;
                }

                if (result.Outcome == DialogOutcome.Accepted && result.Value is { } value)
                {
                    var chosen = _releaseNotes.Chosen(value);
                    if (chosen is { Length: > 0 })
                    {
                        EmitNotice(chosen);
                    }

                    return;
                }
            }
        }
        finally
        {
            _releaseNotes = null;
            _screen.ClearLive();
        }
    }

    private void EmitHelp()
    {
        var text = new StringBuilder();
        text.AppendLine("Commands:");
        foreach (var command in ReplCommandTable.Commands
                     .Where(c => c.Kind != ReplCommandKind.Unavailable)
                     .OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var hint = command.ArgumentHint is { Length: > 0 } h ? " " + h : "";
            text.AppendLine($"  /{command.Name}{hint}".PadRight(34) + command.Description);
        }

        text.AppendLine();
        text.AppendLine(ShortcutsOverlay.Render(_keys, _console.Width) is { Count: > 0 } lines
            ? string.Join('\n', lines)
            : "");
        Emit(_ansi.Dim(text.ToString().TrimEnd()));
    }

    private void EmitStatus() =>
        Emit(SessionInfo.BuildStatus(new SessionInfo.StatusData(
            StreamJson.Version,
            _model.DisplayName,
            _model.ProviderId,
            EffortName(),
            _gate.Mode.ToString(),
            _session.WorkingDirectory,
            _session.Id,
            _runner.LastContextTokens,
            _model.MaxContextTokens,
            0,
            services.App.Mcp.ConnectedToolCounts)));

    private string EffortName() =>
        EffortLevels.Resolve(_runner.SessionEffortName ?? services.App.Settings.Current.EffortByModel.GetValueOrDefault(
            _model.ModelId, services.App.Settings.Current.ThinkingEffortName));

    /// <summary>
    /// /context: the reference's thin-client report, over a snapshot built from
    /// what this front-end knows — the session's own token figures rather than
    /// the desktop's per-category accounting.
    /// </summary>
    private void EmitContext()
    {
        long used = _runner.LastContextTokens;
        long cap = _model.MaxContextTokens;
        var window = ContextWindows.ResolveWindow(
            _model.MaxContextTokens,
            services.App.Settings.Current.AutoCompactWindow,
            Environment.GetEnvironmentVariable("CLAUDE_CODE_AUTO_COMPACT_WINDOW"),
            _model.ModelId);
        long messages = _session.Messages.Sum(m => (long)SystemReminders.VisibleText(m).Length / 4);
        var categories = new List<ContextCategory>
        {
            new("Messages", messages),
            new("Free space", Math.Max(0, cap - used)),
        };
        var snapshot = new ContextSnapshot(
            cap, window.Window, window.Source, used, Math.Max(0, cap - used), categories)
        {
            ModelName = _model.DisplayName,
            IsEstimated = !Core.Providers.ProviderCapabilities.For(services.App.Providers.Get(_model.ProviderId)).ReportsExactUsage,
        };
        Emit(ContextReport.Render(snapshot));
    }

    /// <summary>
    /// /usage (and its /cost and /stats aliases): the reference's totals. The
    /// cost figures are the engine's — this build does not price turns — so
    /// they read as $0.00 rather than being dropped.
    /// </summary>
    private void EmitUsage()
    {
        var text = new StringBuilder();
        var estimated = _sessionUsageIsEstimated || !Core.Providers.ProviderCapabilities.For(services.App.Providers.Get(_model.ProviderId)).ReportsExactUsage;
        text.AppendLine($"Total cost:            {(estimated ? "Unavailable (browser session)" : Format.Cost(0))}");
        text.AppendLine($"Total duration (API):  {Format.Duration(_apiDuration)}");
        text.AppendLine($"Total duration (wall): {Format.Duration(_wallClock.Elapsed)}");
        text.AppendLine($"Total code changes:    {_linesAdded} {Format.Plural(_linesAdded, "line")} added, " +
                        $"{_linesRemoved} {Format.Plural(_linesRemoved, "line")} removed");
        text.AppendLine(estimated ? "Estimated session tokens:" : "Usage by model:");
        if (estimated) text.AppendLine("Token counts are estimates, not ChatGPT account usage or billing.");
        text.AppendLine($"    {(estimated ? "session" : _model.ModelId)}:  {Format.Tokens(_sessionInputTokens)} input, " +
                        $"{Format.Tokens(_sessionOutputTokens)} output, {Format.Tokens(_sessionCacheReadInputTokens)} cache read, " +
                        $"{Format.Tokens(_sessionCacheCreationInputTokens)} cache write");
        Emit(text.ToString().TrimEnd());
    }

    private void EmitMemoryFiles()
    {
        var rows = SessionInfo.BuildMemoryFiles(_session.WorkingDirectory, services.App.Paths.Root);
        var text = new StringBuilder();
        text.AppendLine("Memory files:");
        foreach (var row in rows)
        {
            text.AppendLine($"  {row.Label}".PadRight(30) + row.FilePath + (row.Exists ? "" : "  (new)"));
        }

        Emit(text.ToString().TrimEnd());
    }

    private void EmitHooks()
    {
        var hooks = Core.Hooks.HookRunner.Load(_session.WorkingDirectory, services.App.Paths.UserHooksFile);
        Emit(SessionInfo.BuildHooksSummary(
            hooks,
            Path.Combine(_session.WorkingDirectory, ".jarvis", "hooks.json"),
            services.App.Paths.UserHooksFile));
    }

    private void EmitInsights()
    {
        try
        {
            var sessions = services.App.Sessions.ListAsync(CancellationToken.None).GetAwaiter().GetResult();
            Emit(SessionInfo.BuildInsights(services.App.UsageStats.Load(), sessions));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            EmitError($"Could not build the report: {ex.Message}");
        }
    }

    private void EmitSkillDoctor()
    {
        var skills = LoadSkills();
        Emit(SessionInfo.BuildSkillDoctor(
            skills, skills, services.App.UiSettings.Current, services.App.Paths.SessionsDirectory));
    }

    private void EmitSkills()
    {
        var skills = LoadSkills();
        if (skills.Count == 0)
        {
            EmitNotice("No skills are available in this session.");
            return;
        }

        var text = new StringBuilder();
        text.AppendLine($"{skills.Count} {Format.Plural(skills.Count, "skill")}:");
        foreach (var skill in skills.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            text.AppendLine($"  /{skill.Name}".PadRight(34) + skill.Description);
        }

        Emit(_ansi.Dim(text.ToString().TrimEnd()));
    }

    private async Task OpenKeybindingsAsync(CancellationToken cancellationToken)
    {
        var path = services.App.Paths.KeybindingsFile;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, KeybindingsFile.Template, cancellationToken);
        }

        if (await EditInEditorAsync(await File.ReadAllTextAsync(path, cancellationToken), ".json",
                cancellationToken) is { } edited)
        {
            await File.WriteAllTextAsync(path, edited, cancellationToken);
        }

        LoadKeybindings();
        _router = new KeyRouter(_keys);
        EmitNotice($"Keybindings reloaded from {path}");
    }

    private void StatuslineCommand(string argument)
    {
        var ui = services.App.UiSettings;
        if (argument.Length == 0)
        {
            EmitNotice(ui.Current.StatuslineCommand is { Length: > 0 } current
                ? $"Status line: {current}"
                : "No status line is set. /statusline <command> sets one.");
            return;
        }

        ui.Current.StatuslineCommand = argument == "off" ? "" : argument;
        ui.Save();
        EmitNotice(argument == "off" ? "Status line cleared." : $"Status line set to: {argument}");
    }

    private void OutputStyleCommand(string argument)
    {
        if (options.SafeMode) { EmitNotice("Output styles are disabled in safe mode."); return; }
        var ui = services.App.UiSettings;
        if (argument.Length == 0)
        {
            var text = new StringBuilder();
            text.AppendLine("Output styles:");
            foreach (var style in OutputStyles.Available(_session.WorkingDirectory, services.App.Paths.Root, services.Customizations.PluginOutputStylePaths))
            {
                var mark = string.Equals(style.Name, ui.Current.OutputStyle, StringComparison.OrdinalIgnoreCase)
                    ? "* "
                    : "  ";
                text.AppendLine($"{mark}{style.Name}".PadRight(20) + style.Description);
            }

            Emit(_ansi.Dim(text.ToString().TrimEnd()));
            return;
        }

        if (string.Equals(argument, OutputStyles.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            ui.Current.OutputStyle = OutputStyles.DefaultName;
            ui.Save();
            EmitNotice("Output style cleared.");
            return;
        }

        if (OutputStyles.Find(argument, _session.WorkingDirectory, services.App.Paths.Root, services.Customizations.PluginOutputStylePaths) is not { } chosen)
        {
            EmitError($"Unknown output style: {argument}");
            return;
        }

        ui.Current.OutputStyle = chosen.Name;
        ui.Save();
        EmitNotice($"Output style set to {chosen.Name}.");
    }

    private void PowerupCommand(string argument)
    {
        var ui = services.App.UiSettings;
        if (argument.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            ui.Current.PowerupIndex = 0;
            ui.Save();
            EmitNotice("Powerup lessons reset.");
            return;
        }

        Emit(Powerup.Render(ui.Current.PowerupIndex));
        ui.Current.PowerupIndex++;
        ui.Save();
    }

    private void AutocompactCommand(string argument)
    {
        var settings = services.App.Settings;
        var window = ContextWindows.ResolveWindow(
            _model.MaxContextTokens, settings.Current.AutoCompactWindow,
            Environment.GetEnvironmentVariable("CLAUDE_CODE_AUTO_COMPACT_WINDOW"), _model.ModelId);
        if (argument.Length == 0)
        {
            Emit(AutoCompactCommand.Status(window, settings.Current.AutoCompactEnabled));
            return;
        }

        if (!AutoCompactCommand.TryReadArgument(argument, out int? chosen))
        {
            EmitError(AutoCompactCommand.Status(window, settings.Current.AutoCompactEnabled));
            return;
        }

        settings.Current.AutoCompactWindow = chosen;
        settings.Save();
        var applied = ContextWindows.ResolveWindow(
            _model.MaxContextTokens, chosen,
            Environment.GetEnvironmentVariable("CLAUDE_CODE_AUTO_COMPACT_WINDOW"), _model.ModelId);
        Emit(AutoCompactCommand.Applied(chosen, applied, applied.IsOverridden));
    }

    private void GoalCommand(string argument)
    {
        if (argument.Length == 0)
        {
            EmitNotice(_sessionGoal is { Length: > 0 } goal
                ? $"Goal: {goal}"
                : "No goal is set. /goal <goal> sets one.");
            return;
        }

        if (argument.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _sessionGoal = null;
            _runner.SessionGoal = null;
            EmitNotice("Goal cleared.");
            return;
        }

        _sessionGoal = argument.Trim();
        _runner.SessionGoal = _sessionGoal;
        EmitNotice($"Goal set: {_sessionGoal}");
    }

    private void PlanCommand()
    {
        var path = PlanModeTools.PlanFilePath(_session.WorkingDirectory, _session.Id);
        if (_gate.Mode == PermissionMode.Plan)
        {
            Emit(File.Exists(path)
                ? File.ReadAllText(path)
                : $"Plan mode is on. The plan file is {path} (nothing written yet).");
            return;
        }

        _gate.EnterPlanMode([path]);
        EmitNotice("Plan mode on.");
    }

    private void EmitPermissions()
    {
        var rules = services.App.Settings.Current.PermissionRuleLines;
        if (rules.Count == 0)
        {
            EmitNotice("No permission rules are configured.");
            return;
        }

        Emit(_ansi.Dim("Permission rules:\n" + string.Join('\n', rules.Select(rule => "  " + rule))));
    }

    private void EmitTasks()
    {
        var board = new TaskBoard(Path.Combine(services.App.Paths.TasksDirectory, _session.Id + ".json"));
        var tasks = board.Visible();
        if (tasks.Count == 0)
        {
            Emit("No tasks found");
            return;
        }

        Emit(string.Join('\n', tasks.Select(task =>
        {
            var owner = task.Owner is { Length: > 0 } who ? $" ({who})" : "";
            var blocked = task.BlockedBy.Count > 0
                ? $" [blocked by {string.Join(", ", task.BlockedBy.Select(id => "#" + id))}]"
                : "";
            return $"#{task.Id} [{task.Status.ToString().ToLowerInvariant()}] {task.Subject}{owner}{blocked}";
        })));
    }

    private void EmitTeammates()
    {
        var teams = new TeamStore(services.App.Paths.TeamsDirectory);
        var members = teams.Read(_session.Id)?.Members ?? [];
        if (members.Count == 0)
        {
            EmitNotice("This session has no teammates.");
            return;
        }

        Emit(string.Join('\n', members.Select(m => $"  {m.Name}  {(m.IsActive ? "working" : "idle")}")));
    }

    private void EmitAgents()
    {
        var running = _workers.List();
        if (running.Count == 0)
        {
            EmitNotice("No agents are running in this session.");
            return;
        }

        Emit(string.Join('\n', running.Select(w => $"  {w.Id}  {w.AgentType}  {w.Status}")));
    }

    private void EmitWorkflows()
    {
        var directory = services.App.Paths.WorkflowRunsDirectory;
        if (!Directory.Exists(directory))
        {
            EmitNotice("No workflows have run in this profile.");
            return;
        }

        var runs = Directory.GetDirectories(directory)
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .Take(10)
            .Select(Path.GetFileName);
        Emit(_ansi.Dim("Recent workflow runs:\n" + string.Join('\n', runs.Select(r => "  " + r))));
    }

    private void LoopCommand(string argument)
    {
        var parsed = LoopPrompts.Parse(argument);
        _loop = parsed;
        EmitNotice(parsed.Interval is { } interval
            ? $"Loop armed every {parsed.IntervalText ?? interval.ToString()}."
            : "Loop armed; Jarvis paces it with ScheduleWakeup.");
        if (parsed.Prompt.Length > 0)
        {
            RunPromptCommandAsync("loop", parsed.Prompt, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private LoopPrompts.ParsedLoop? _loop;

    private void LoopsCommand(string argument)
    {
        if (argument.StartsWith("stop", StringComparison.OrdinalIgnoreCase))
        {
            _loop = null;
            EmitNotice("Loop stopped.");
            return;
        }

        EmitNotice(_loop is null ? "No loops are running." : $"1 loop: {_loop.Prompt}");
    }

    private async Task McpCommandAsync(string argument, CancellationToken cancellationToken)
    {
        if (argument.StartsWith("reconnect", StringComparison.OrdinalIgnoreCase))
        {
            await services.ConnectMcpAsync(options, _session.WorkingDirectory, cancellationToken);
            EmitNotice("MCP servers reconnected.");
            return;
        }

        var counts = services.App.Mcp.ConnectedToolCounts;
        if (counts.Count == 0)
        {
            EmitNotice("No MCP servers are connected.");
            return;
        }

        Emit(_ansi.Dim("MCP servers:\n" +
                       string.Join('\n', counts.Select(kv => $"  {kv.Key}".PadRight(28) + $"{kv.Value} tools"))));
    }

    private async Task McpAuthAsync(string argument, CancellationToken cancellationToken)
    {
        if (argument.Length == 0)
        {
            EmitError("Usage: /mcp-auth <server>");
            return;
        }

        try
        {
            await services.App.Mcp.AuthorizeAsync(
                argument.Trim(), _session.WorkingDirectory, services.App.Paths.UserMcpFile, cancellationToken);
            EmitNotice($"Signed in to {argument.Trim()}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            EmitError(ex.Message);
        }
    }

    private void ConfigCommand(string argument)
    {
        var parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var tool = new ConfigTool(
            () => services.App.Settings.Current,
            services.App.Settings.Save,
            uiCurrent: () => services.App.UiSettings.Current,
            uiSave: services.App.UiSettings.Save);
        var arguments = new System.Text.Json.Nodes.JsonObject
        {
            ["action"] = parts.Length == 0 ? "list" : parts.Length == 1 ? "get" : "set",
        };
        if (parts.Length >= 1)
        {
            arguments["key"] = parts[0];
        }

        if (parts.Length >= 2)
        {
            arguments["value"] = parts[1];
        }

        var result = tool.ExecuteAsync(arguments, new Core.Tools.ToolExecutionContext
        {
            WorkingDirectory = _session.WorkingDirectory,
        }, CancellationToken.None).GetAwaiter().GetResult();
        Emit(result.Content);
    }

    private void ThemeCommand(string argument)
    {
        var ui = services.App.UiSettings;
        if (argument.Length == 0)
        {
            EmitNotice($"Theme: {(ui.Current.ActiveTheme is { Length: > 0 } t ? t : "default")}");
            return;
        }

        ui.Current.ActiveTheme = argument.Trim();
        ui.Save();
        EmitNotice($"Theme set to {argument.Trim()}.");
    }

    private void ColorCommand(string argument)
    {
        EmitNotice(argument.Trim() is "off" or ""
            ? "Session accent cleared."
            : $"Session accent set to {argument.Trim()}.");
    }

    private void CopyCommand(string argument)
    {
        int back = int.TryParse(argument, out int n) && n > 0 ? n : 1;
        var answers = _session.Messages
            .Where(m => m.Role == Role.Assistant)
            .Select(SystemReminders.VisibleText)
            .Where(text => text.Trim().Length > 0)
            .ToList();
        if (answers.Count < back)
        {
            EmitError("Nothing to copy yet.");
            return;
        }

        var chosen = answers[^back];
        try
        {
            System.Windows.Clipboard.SetText(chosen);
            EmitNotice($"Copied {chosen.Length} characters.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            EmitError("The clipboard is not available from this session.");
        }
    }

    private async Task PluginCommandAsync(string argument, CancellationToken cancellationToken)
    {
        await Subcommands.DispatchAsync(
            "plugin", [.. argument.Split(' ', StringSplitOptions.RemoveEmptyEntries)], cancellationToken);
    }

    private void AddDirectory(string argument)
    {
        var path = argument.Trim();
        if (path.Length == 0)
        {
            EmitError("Usage: /add-dir <path>");
            return;
        }

        if (Core.Utilities.NetworkPaths.IsNetworkPath(path))
        {
            EmitError(Core.Utilities.NetworkPaths.AddDirectoryRefusal(path));
            return;
        }

        var full = Path.GetFullPath(path, _session.WorkingDirectory);
        if (!Directory.Exists(full))
        {
            EmitError($"Not a directory: {path}");
            return;
        }

        _session.AdditionalDirectories.Add(full);
        EmitNotice($"Added {full} to this session.");
    }

    private void ChangeDirectory(string argument)
    {
        var path = argument.Trim();
        if (path.Length == 0)
        {
            EmitNotice($"Working directory: {_session.WorkingDirectory}");
            return;
        }

        var full = Path.GetFullPath(path, _session.WorkingDirectory);
        if (!Directory.Exists(full))
        {
            EmitError($"Not a directory: {path}");
            return;
        }

        _session.WorkingDirectory = full;
        _gate.WorkingDirectory = full;
        EmitNotice($"Working directory is now {full}");
    }
}
