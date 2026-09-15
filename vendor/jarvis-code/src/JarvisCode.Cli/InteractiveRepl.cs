using System.Diagnostics;
using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Input;
using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli;

/// <summary>
/// The default (no --print) mode: the reference CLI's interactive session on a
/// hand-rolled console renderer. The transcript is printed into the scrollback
/// and everything under it — the spinner row, the prompt box, the completion
/// popup, the footer, a dialog — is the live region, erased and redrawn on
/// every change, which is the model the reference's Ink app draws with.
/// </summary>
internal sealed partial class InteractiveRepl : IDisposable
{
    private readonly CliServices services;
    private readonly CliOptions options;
    private readonly IConsole _console;
    private readonly bool _ownsConsole;

    /// <summary>
    /// The terminal is injected so the whole loop — key dispatch, rendering,
    /// the dialogs and the commands — runs against a scripted console in a
    /// test with no terminal attached.
    /// </summary>
    public InteractiveRepl(CliServices services, CliOptions options, IConsole? console = null)
    {
        this.services = services;
        this.options = options;
        _console = console ?? new SystemConsole();
        _ownsConsole = console is null;
    }
    private Screen _screen = null!;
    private Ansi _ansi = null!;
    private KeyMap _keys = KeyMap.Default;
    private KeyRouter _router = null!;
    private PromptHistory _history = null!;
    private readonly DoublePress _interrupt = new();
    private readonly DoublePress _exit = new();
    private readonly DoublePress _escape = new();

    private bool _overlay;
    private bool _brief;
    private bool _verbose;
    private bool _fullscreen;
    private SessionState? _browserOwner;

    private TrustDialog? _trust;
    private ReleaseNotesPicker? _releaseNotes;

    private readonly CancellationTokenSource _hostLifetime = new();
    private readonly SemaphoreSlim _notificationReady = new(0);
    private Task? _pendingNotificationRead;

    public void Dispose()
    {
        services.App.Browser.StopRequested -= StopBrowserTurn;
        _hostLifetime.Cancel();
        try { Task.WhenAll(_sessionTabs.Values.Select(state => state._turn).OfType<Task>()).Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        foreach (var state in _sessionTabs.Values) InSession(state, DisposeSessionRuntime);
        if (services.ChromeEnabled) services.App.Browser.EndSession();
        _hostLifetime.Dispose();
        if (_ownsConsole && _console is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var hostCancellation = cancellationToken.Register(_hostLifetime.Cancel);
        var cwd = Directory.GetCurrentDirectory();
        _ansi = new Ansi(_console.SupportsAnsi, TrueColor());
        _screen = new Screen(_console);
        LoadKeybindings();
        _router = new KeyRouter(_keys);
        _history = new PromptHistory(PromptHistory.DefaultPath(services.App.Paths.Root));

        var resumeId = options.Resume && options.ResumeValue is { Length: > 0 } wanted
            ? (await services.App.Sessions.ListAsync(cancellationToken)).FirstOrDefault(summary =>
                summary.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase) || summary.Id.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))?.Id : null;
        _session = await services.ResolveSessionAsync(options with
        {
            Resume = options.Resume && resumeId is not null,
            ResumeValue = resumeId,
            FromPr = options.FromPr && options.FromPrValue is { } pr && CliServices.IsPrIdentity(pr),
        }, cwd, cancellationToken);
        if (options.Model is { Length: > 0 } modelName)
        {
            _session.ModelId = services.ResolveModel(modelName).ModelId;
        }

        _model = services.Factory.ResolveModel(_session)
            ?? throw new CliError("Error: No model is configured. Add an API key and pick a default model " +
                                  "in Settings, or pass --model/--settings.");
        _navigator = new HistoryNavigator(_history, cwd, _session.Id);

        _gate = BuildGate(cwd);

        PrintOpening(cwd);
        if (!await ConfirmTrustAsync(cwd, cancellationToken))
        {
            return 1;
        }

        // Until trust succeeds the welcome/dialog uses metadata only. Plugin
        // setup, helpers, MCP, editor startup and session timers can then run.
        await services.InitializeAsync(options, cwd, cancellationToken);
        services.App.Browser.StopRequested += StopBrowserTurn;
        await services.ConnectMcpAsync(options, cwd, cancellationToken);
        if (options.AutoConnectIde && !services.Customizations.DisableLsp)
            Console.Error.WriteLine(await IdeServices.AutoConnectAsync(cwd, cancellationToken));
        _runner = NewRunner();

        ReplayTranscript();
        if (options.Resume && resumeId is null)
            await OpenResumePickerAsync(options.ResumeValue ?? "", cancellationToken);
        if (options.FromPr && (options.FromPrValue is null || !CliServices.IsPrIdentity(options.FromPrValue)))
        {
            var linked = await services.FindPrSessionsAsync(cwd, options.FromPrValue, cancellationToken);
            _resume = new ResumePicker([.. linked.Select(summary => new ResumeRow(summary, summary.Title,
                Format.Relative(summary.UpdatedAt, DateTimeOffset.Now), ""))], cwd, _ansi);
        }
        if (options.Prompt.Length > 0) StartTurn(options.Prompt);

        while (!cancellationToken.IsCancellationRequested && !_exiting)
        {
            Redraw();
            var press = await ReadNextAsync(cancellationToken);
            if (press is null)
            {
                break;
            }

            if (press.Key == ReplRedrawKey)
            {
                DeliverNotifications();
                continue;
            }

            var outcome = await HandleKeyAsync(press, cancellationToken);
            if (outcome is not null || _exiting)
            {
                _screen.ClearLive();
                return outcome ?? 0;
            }
        }

        _screen.ClearLive();
        return 0;
    }

    /// <summary>The key name of the synthetic press that means "repaint", not a key at all.</summary>
    private const string ReplRedrawKey = "repaint";

    private Task<KeyPress?>? _pendingRead;

    /// <summary>
    /// The next key, or a redraw tick when the spinner needs repainting. The
    /// pending read is kept across ticks — starting a second one would drop the
    /// key the first is holding — which is what lets a running turn animate
    /// while the user keeps typing into the box.
    /// </summary>
    private async Task<KeyPress?> ReadNextAsync(CancellationToken cancellationToken)
    {
        _pendingRead ??= _console.ReadKeyAsync(cancellationToken).AsTask();
        _pendingNotificationRead ??= _notificationReady.WaitAsync(cancellationToken);
        if (_turn is null || _turn.IsCompleted)
        {
            if (await Task.WhenAny(_pendingRead, _pendingNotificationRead) == _pendingNotificationRead)
            { _pendingNotificationRead = null; return new KeyPress(ReplRedrawKey); }
            var only = await _pendingRead;
            _pendingRead = null;
            return only;
        }

        var tick = Task.Delay(StatusRow.FramePeriod, cancellationToken);
        var completed = await Task.WhenAny(_pendingRead, _pendingNotificationRead, tick);
        if (completed != _pendingRead)
        {
            if (completed == _pendingNotificationRead) _pendingNotificationRead = null;
            return new KeyPress(ReplRedrawKey);
        }

        var press = await _pendingRead;
        _pendingRead = null;
        return press;
    }

    private bool TrueColor() =>
        Environment.GetEnvironmentVariable("COLORTERM") is "truecolor" or "24bit" ||
        Environment.GetEnvironmentVariable("WT_SESSION") is { Length: > 0 };

    private void LoadKeybindings()
    {
        if (options.SafeMode) { _keys = KeyMap.Default; return; }
        var load = KeybindingsFile.Load(services.App.Paths.KeybindingsFile);
        _keys = load.Map;
        foreach (var warning in load.Warnings)
        {
            Console.Error.WriteLine(warning.LogLine);
        }
    }

    private UiPermissionGate BuildGate(string cwd)
    {
        var configuredMode = options.PermissionModeName ?? services.App.Settings.Current.PermissionModeName;
        var (mode, dontAsk) = (options with { PermissionModeName = configuredMode }).ResolvePermissionMode();
        if (configuredMode is null && !options.Bare && !options.SafeMode &&
            services.CliSettings.Sources.Contains("project") && !options.DangerouslySkipPermissions &&
            ProjectPermissions.LoadDefaultMode(cwd) is { } projectMode)
        {
            mode = projectMode;
        }

        if (options.Restricted && mode == PermissionMode.Bypass)
        {
            throw new CliError(RestrictedMode.BypassRefused);
        }

        var gate = new UiPermissionGate
        {
            Mode = mode,
            WorkingDirectory = cwd,
            AdditionalDirectories = _session.AdditionalDirectories,
            SuppliedRuleLines = new CliSettingsStore(new Core.Settings.JsonSettingsStore(services.App.Paths.SettingsFile,
                new JarvisCode.Host.DpapiSecretProtector()), options, cwd, userSettingsPath: services.App.Paths.SettingsFile).Load().PermissionRuleLines,
            RestrictToWorkspace = options.Restricted,
            IgnoreSettingsFiles = true,
            BlockReadsOutsideWorkingDirectories = services.App.Settings.Current.BlockReadsOutsideWorkingDirectories,
            PersistBlockReadsAsync = () =>
            {
                services.App.Settings.Current.BlockReadsOutsideWorkingDirectories = true;
                services.App.Settings.Save();
                return Task.CompletedTask;
            },
        };
        _dontAsk = dontAsk;
        if (!dontAsk && mode != PermissionMode.Bypass)
        {
            gate.PromptAsync = PromptForPermissionAsync;
        }

        return gate;
    }


    private void PrintOpening(string cwd)
    {
        var lines = new List<string> { _ansi.Color("claude", Welcome.Line(StreamJson.Version)) };
        lines.Add(_ansi.Dim(Welcome.Where(_model.ModelId, cwd)));
        if (_session.Messages.Count > 0)
        {
            lines.Add(_ansi.Dim($"  resumed: {_session.Title} ({_session.Messages.Count} messages)"));
        }

        if (_gate.Mode == PermissionMode.Bypass)
        {
            lines.Add(_ansi.Color("error", "  Bypassing all permission checks."));
        }

        if (PickTip() is { } tip)
        {
            lines.Add("");
            lines.Add(_ansi.Dim(StartupTips.Render(tip)));
        }

        _screen.AppendStatic(string.Join('\n', lines) + "\n");
    }

    private StartupTip? PickTip()
    {
        try
        {
            var ui = services.App.UiSettings;
            int startups = ui.Current.StartupCount + 1;
            ui.Current.StartupCount = startups;
            var tips = StartupTips.All(_keys);
            var context = new TipContext(
                startups,
                HasCustomSkills: false,
                WorkflowsEnabled: ui.Current.DynamicWorkflowsEnabled,
                InGitRepository: Directory.Exists(Path.Combine(_session.WorkingDirectory, ".git")));
            var tip = StartupTips.Pick(tips, context, ui.Current.TipLastShown, ui.Current.TipShownCount);
            if (tip is not null)
            {
                ui.Current.TipLastShown[tip.Id] = startups;
                ui.Current.TipShownCount[tip.Id] = ui.Current.TipShownCount.GetValueOrDefault(tip.Id) + 1;
            }

            ui.Save();
            return tip;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The reference's trust dialog, once per workspace. A session that cannot
    /// ask (no terminal) is treated as already trusted, which is what the print
    /// path does.
    /// </summary>
    private async Task<bool> ConfirmTrustAsync(string cwd, CancellationToken cancellationToken)
    {
        var ui = services.App.UiSettings;
        if (!_console.IsInteractive || options.DangerouslySkipPermissions ||
            ui.Current.TrustedWorkspaces.Contains(cwd))
        {
            return true;
        }

        _trust = new TrustDialog(cwd, _ansi);
        try
        {
            while (true)
            {
                Redraw();
                var press = await ReadNextAsync(cancellationToken);
                if (press is null)
                {
                    return false;
                }

                if (press.Key == ReplRedrawKey)
                {
                    continue;
                }

                var route = _router.Route(_trust.Context, press);
                var result = _trust.Handle(route.Action, press);
                if (result.Outcome == DialogOutcome.Cancelled ||
                    (result.Outcome == DialogOutcome.Accepted && result.Value == "no"))
                {
                    return false;
                }

                if (result.Outcome == DialogOutcome.Accepted)
                {
                    ui.Current.TrustedWorkspaces.Add(cwd);
                    ui.Save();
                    return true;
                }
            }
        }
        finally
        {
            _trust = null;
            _screen.ClearLive();
        }
    }

    /// <summary>Prints a resumed conversation back into the scrollback, as --continue does in the reference.</summary>
    private void ReplayTranscript()
    {
        if (_session.Messages.Count == 0)
        {
            return;
        }

        var markdown = new MarkdownTerminal(_ansi, _console.Width);
        var lines = new List<string>();
        foreach (var message in _session.Messages)
        {
            var text = SystemReminders.VisibleText(message);
            if (text.Trim().Length == 0)
            {
                continue;
            }

            lines.Add(message.Role == Role.User
                ? _ansi.Dim("> " + text.ReplaceLineEndings(" "))
                : markdown.Render(text));
            lines.Add("");
        }

        if (lines.Count > 0)
        {
            _screen.AppendStatic(string.Join('\n', lines));
        }
    }

    private void Redraw()
    {
        if (!_console.IsInteractive)
        {
            return;
        }

        var state = BuildViewState();
        var (lines, caret) = ReplView.Render(state, _ansi, _console.Width);
        _screen.SetLive(lines, caret);
    }

    private ReplViewState BuildViewState()
    {
        var dialog = CurrentDialogLines();
        return new ReplViewState
        {
            Text = _composer.Text,
            SessionStrip = SessionStrip(),
            SuggestedPrompt = _composer.IsEmpty ? _suggestedPrompt : null,
            Offset = _composer.Offset,
            Mode = _composer.Mode,
            VimIndicator = _composer.VimIndicator,
            Completion = _completion,
            Queued = _queued,
            StatusRow = StatusLine(),
            AnswerPreview = _answerPreview,
            PreviewMaxRows = Math.Clamp((_console.Height - 8) / 3, 1, 6),
            Footer = Footer.Render(
                new Footer.State(
                    _gate.Mode,
                    _dontAsk,
                    IsRunning: _turn is { IsCompleted: false },
                    ShowHint: true,
                    RunningTasks: _workers.RunningCount,
                    ContextIndicator: ContextIndicatorText(),
                    Statusline: StatuslineText()),
                _keys),
            Overlay = _overlay ? ShortcutsOverlay.Render(_keys, _console.Width) : null,
            Dialog = dialog,
            PendingNotice = _pendingNotice,
            Search = _search,
            ShowExpandPasteHint = _composer.CanExpandPaste,
        };
    }

    private IReadOnlyList<string>? CurrentDialogLines()
    {
        if (State.Diff is { } diff) return diff.Render(_ansi, _console.Width, _console.Height - 2);
        if (State.Document is { } document) return document.Render(_ansi, _console.Width, _console.Height - 2);
        if (_trust is { } trust)
        {
            return trust.Render(_console.Width);
        }

        if (_permission is { } permission)
        {
            return permission.Render(_console.Width);
        }

        if (_plan is { } plan)
        {
            return plan.Render(_console.Width, Environment.GetEnvironmentVariable("EDITOR"));
        }

        if (_question is { } question)
        {
            return question.Render(_console.Width);
        }

        if (_resume is { } resume)
        {
            return resume.Render(_console.Width);
        }
        if (_choice is { } choice) return choice.Render(_ansi, _console.Width);

        if (_releaseNotes is { } releaseNotes)
        {
            return releaseNotes.Render(_console.Width);
        }

        return null;
    }

    private string? StatusLine()
    {
        if (_turn is not { IsCompleted: false })
        {
            return null;
        }

        var verb = _compacting ? StatusRow.CompactingMessage : _verb;
        return StatusRow.Render(
            verb, _phase, _turnClock.Elapsed, _turnTokens, _thinking, _verbose);
    }

    private string? ContextIndicatorText()
    {
        long used = _runner?.LastContextTokens ?? 0;
        if (used <= 0 || _model.MaxContextTokens <= 0)
        {
            return null;
        }

        bool autoCompact = services.App.Settings.Current.AutoCompactEnabled;
        var window = ContextWindows.ResolveWindow(
            _model.MaxContextTokens,
            services.App.Settings.Current.AutoCompactWindow,
            Environment.GetEnvironmentVariable("CLAUDE_CODE_AUTO_COMPACT_WINDOW"),
            _model.ModelId);
        // The reference reserves the model's output budget out of the window;
        // this engine does not carry a per-model output cap, so its own cap is
        // what the arithmetic holds back.
        long effective = ContextWindows.EffectiveWindow(window.Window, ContextWindows.OutputReserveCap);
        var (level, percentLeft) = ContextWindows.Classify(used, effective, effective, autoCompact);
        return ContextIndicator.Render(
            level.ToString().ToLowerInvariant(), percentLeft, autoCompact,
            // The reference asks whether the window in force is the model's own;
            // anything else is reported as context used instead.
            enforced: window.Source != AutoCompactWindowSource.Auto,
            effectiveWindow: effective,
            usedTokens: used);
    }

    private string? StatuslineText()
    {
        if (_runner is null || options.Bare || options.SafeMode) return null;
        var command = services.App.UiSettings.Current.StatuslineCommand;
        if (command is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var json = Statusline.BuildContextJson(
                _model.ModelId, _model.DisplayName, _session.WorkingDirectory, _session.Id,
                _gate.Mode.ToString(), EffortName());
            return Statusline.Run(command, json);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Emit(string text)
    {
        if (_executingSession.Value is { } owner && !ReferenceEquals(owner, _selectedSession)) return;
        _screen.AppendStatic(text.TrimEnd('\n') + "\n");
    }

    private void EmitNotice(string text) => Emit(_ansi.Dim(text));

    private void EmitError(string text) => Emit(_ansi.Color("error", text));
}
