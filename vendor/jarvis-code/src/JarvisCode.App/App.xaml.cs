using System.Windows;
using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;
using JarvisCode.App.Views;

namespace JarvisCode.App;

public partial class App : Application
{
    /// <summary>
    /// How long the engine's browser processes get to exit on their own after its
    /// windows were released, before it is killed. Enough for a released Chromium to
    /// flush the profile it was given; the rest are only waiting on this process, so
    /// waiting longer would
    /// just be a slower quit for the same result.
    /// </summary>
    /// <summary>
    /// How long the app waits for the engine to go. The engine's browser
    /// processes only start leaving once it has, and take about a second over
    /// it — measured — so ending it here means the app's memory is actually
    /// back when the window closes. Losing the pipe is what ends it, and that
    /// happens when this process exits whatever the wait, so this buys a clean
    /// exit rather than guaranteeing one.
    /// </summary>
    private static readonly TimeSpan EngineShutdownGrace = TimeSpan.FromSeconds(3);

    private AppServices? _services;
    private SingleInstance? _instance;
    private MainWindow? _mainWindow;

    public static new App Current => (App)Application.Current;

    public AppServices Services => _services
        ?? throw new InvalidOperationException("Application services are not initialized yet.");

    public bool TryGetServices(out AppServices services)
    {
        services = _services!;
        return _services is not null;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Chrome launches this exe for native messaging with the extension
        // origin as the first argument — run as a pure stdio relay, no UI.
        if (e.Args.Any(static a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) ||
            e.Args.Contains("--browser-host"))
        {
            _ = Task.Run(() =>
            {
                JarvisCode.App.Services.BrowserHostRelay.Run();
                Dispatcher.Invoke(() => Shutdown(0));
            });
            return;
        }

        var args = ParseArgs(e.Args);

        ProfilePaths paths;
        try
        {
            paths = ProfilePaths.Create(args.Profile);
        }
        catch (ArgumentException ex)
        {
            Views.MessageDialog.Show(
                null,
                "Could not load app settings",
                ex.Message,
                type: Views.MessageDialogType.Error);
            Shutdown(1);
            return;
        }

        // A --screenshot run renders and exits; it is not a request to show a
        // window that already exists. Letting it forward to a primary instance
        // would end it with exit 0 and no image, which is what the golden-image
        // check sees when two renders of the same profile overlap.
        PendingAppDataReset.RunIfArmed(paths.Root);

        _instance = new SingleInstance(paths.InstanceKey);
        if (!_instance.IsPrimary && args.ScreenshotPath is null)
        {
            _instance.SendToPrimary(args switch
            {
                { ToggleQuickEntry: true } => SingleInstance.ToggleQuickEntryCommand,
                { CodeDirectory: { } dir } => SingleInstance.CodeDirCommandPrefix + dir,
                { DeepLink: { } deepLink } => SingleInstance.DeepLinkCommandPrefix + deepLink,
                _ => SingleInstance.ShowCommand,
            });
            Shutdown(0);
            return;
        }

        // request_access names the installed applications inside its own schema,
        // and the reference pre-warms that enumeration at start-up so the first
        // turn's tool doc already carries the list rather than omitting it.
        JarvisCode.App.Services.InstalledApplications.Prewarm();

        // The rim of light that says a session is driving this desktop. The
        // reference arms the same listener when its main process starts.
        JarvisCode.App.Services.ComputerUseGlow.Install(Dispatcher);

        // "Copy session link" is only worth offering if the link opens something.

        if (args.ToggleQuickEntry)
        {
            // "--toggle" with no running instance: start hidden, then open Quick Entry.
            args = args with { StartHidden = true, OpenQuickEntry = true };
        }

        DispatcherUnhandledException += OnUnhandledException;
        // A failure on a worker thread or in a dropped Task would otherwise take the
        // process down with no window and no explanation.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportCrash(e.ExceptionObject as Exception, terminating: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            ReportCrash(e.Exception, terminating: false);
        };

        _services = new AppServices(paths);
        // The transcript's clocks follow the reference's timeFormat / timeZone settings, read live.
        ViewModels.TranscriptTime.Settings = () =>
            (_services.Settings.Current.TimeFormat, _services.Settings.Current.TimeZone);
        var ui = _services.UiSettings.Current;
        if (ui.HardwareAccelerationDisabled)
        {
            // The reference restarts into a software-rendered process; WPF reads its
            // own switch once, here, which is why the menu row asks for a restart.
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        SyncProtocolRegistration(ui, args.Profile);
        _services.Theme.Apply(ui.ActiveTheme, ui.ThemeMode);
        ApplyChatFont(ui.ChatFont);
        ApplyInterfaceFont(ui.InterfaceFont);
        ApplyCodeFont(ui.CodeFont);
        ApplyTranscriptWidth(ui.TranscriptWidth);
        JarvisCode.App.Services.CodeThemes.Apply(ui, _services.Theme.IsDark);
        _services.Theme.ThemeChanged += (_, _) => JarvisCode.App.Services.CodeThemes.Apply(_services.UiSettings.Current, _services.Theme.IsDark);
        Controls.MarkdownView.TranscriptTextSize =
            Controls.TranscriptTextSizes.FromName(ui.TranscriptTextSize);

        // Cookie mode runs its ChatGPT calls inside an embedded browser, which only the App can
        // supply; the provider asks the registry for it.
        ChatGptWebViewTransport.Register(Dispatcher, paths.Root);

        CleanUpPastedAttachments(paths.Root);

        _instance.CommandReceived += OnInstanceCommand;

        Views.MainWindow.AutomationMode =
            args is { ScreenshotPath: not null } or { OpenTarget: not null } or
                { E2EPrompt: not null } or { E2ESwitchPrompt: not null } or
                { PaneSelfTest: true } or { UiSelfTest: true } or { InputSelfTest: true } or
                { SubagentSelfTest: true } or { SlashSelfTest: true } or
                { SidebarSelfTest: true } or { TitleBarSelfTest: true } or
                { TranscriptSelfTest: true };

        _mainWindow = new MainWindow(_services);
        MainWindow = _mainWindow;

        if (args.WindowSize is { } size)
        {
            _mainWindow.Width = size.Width;
            _mainWindow.Height = size.Height;
        }

        if (!(args.StartHidden || ui.StartInTray))
        {
            _mainWindow.Show();
        }
        else
        {
            // Starting in the tray still needs the window's handle: the WndProc hook and
            // the Quick Entry hotkey are registered on it, and without a Show() nothing
            // would ever create it.
            new System.Windows.Interop.WindowInteropHelper(_mainWindow).EnsureHandle();
        }

        if (args.OpenQuickEntry)
        {
            Dispatcher.BeginInvoke(() => _mainWindow.ToggleQuickEntry(), DispatcherPriority.ApplicationIdle);
        }

        if (args.CodeDirectory is { } codeDirectory && args.ScreenshotPath is null)
        {
            Dispatcher.BeginInvoke(() => _mainWindow.StartCodeSessionIn(codeDirectory), DispatcherPriority.ApplicationIdle);
        }

        if (args.ScreenshotPath is { } screenshotPath)
        {
            _mainWindow.ContentRendered += async (_, _) =>
            {
                var exitCode = 0;
                await ApplyOpenTargetAsync(args.OpenTarget);

                // Under --screenshot the switch must land before the E2E prompt is
                // submitted, so it runs here instead of racing on the dispatcher.
                if (args.CodeDirectory is { } dir)
                {
                    _mainWindow.StartCodeSessionIn(dir);
                }

                if (args.E2EPrompt is { } prompt)
                {
                    exitCode = await _mainWindow.RunE2ETurnAsync(prompt);
                }
                else if (args.E2ESwitchPrompt is { } switchPrompt)
                {
                    exitCode = await _mainWindow.RunE2ESessionSwitchAsync(switchPrompt);
                }
                else if (args.PaneSelfTest)
                {
                    exitCode = await _mainWindow.RunPaneSelfTestAsync();
                }
                else if (args.SlashSelfTest)
                {
                    exitCode = await _mainWindow.RunSlashSelfTestAsync();
                }
                else if (args.SidebarSelfTest)
                {
                    exitCode = await _mainWindow.RunSidebarSelfTestAsync();
                }
                else if (args.TitleBarSelfTest)
                {
                    exitCode = await _mainWindow.RunTitleBarSelfTestAsync();
                }
                else if (args.TranscriptSelfTest)
                {
                    exitCode = await _mainWindow.RunTranscriptSelfTestAsync();
                }
                else if (args.SubagentSelfTest)
                {
                    exitCode = await _mainWindow.RunSubagentSelfTestAsync();
                }
                else if (args.UiSelfTest)
                {
                    exitCode = await _mainWindow.RunUiSelfTestAsync();
                }
                else if (args.InputSelfTest)
                {
                    exitCode = await _mainWindow.RunInputSelfTestAsync();
                }

                await Task.Delay(700);
                try
                {
                    Window captureTarget = _mainWindow;
                    foreach (Window window in Windows)
                    {
                        if ((args.OpenTarget == "themes" && window is ThemePickerWindow) ||
                            (args.OpenTarget == "about" && window is AboutWindow) ||
                            (args.OpenTarget == "quickentry" && window is QuickEntryWindow) ||
                            (args.OpenTarget == "diagnostics" && window is DiagnosticReportDialog))
                        {
                            captureTarget = window;
                        }
                    }

                    CaptureWindow(captureTarget, screenshotPath);
                }
                finally
                {
                    Shutdown(exitCode);
                }
            };
        }
        else if (args.OpenTarget is not null)
        {
            // Without --screenshot the target opens and the app stays up, so
            // engine surfaces (artifact, question previews) can be inspected.
            _mainWindow.ContentRendered += async (_, _) => await ApplyOpenTargetAsync(args.OpenTarget);
        }
    }

    /// <summary>
    /// Fills Settings › Debug with one call of each state for --open=debug. It drives the
    /// real capture API rather than fabricating snapshots, so the pose renders whatever
    /// the live path would.
    /// </summary>
    /// <param name="newest">
    /// Which call to put last, since the pane opens on the newest: "inflight", "failed", or
    /// null for the finished one, which is what it usually opens on.
    /// </param>
    private static void SeedSampleTraffic(ModelTrafficLog log, string? newest)
    {
        var url = new Uri("https://api.anthropic.com/v1/messages?beta=true");
        var headers = new KeyValuePair<string, string>[]
        {
            new("anthropic-beta", "effort-2025-11-24,context-management-2025-06-27"),
            new("anthropic-version", "2023-06-01"),
            new("content-type", "application/json"),
            new("x-api-key", JarvisCode.Core.Providers.RequestBodyOverride.RedactedValue),
        };
        var body = System.Text.Encoding.UTF8.GetBytes(
            """{"model":"claude-opus-5","max_tokens":64000,"stream":true,"system":[{"type":"text","text":"You are Jarvis Code."}],"messages":[{"role":"user","content":"Where does the tee stream sit?"}],"thinking":{"type":"adaptive"},"output_config":{"effort":"high"}}""");

        void Failed() => log.Start("POST", url, headers, body).Fail(
            new System.Net.Http.HttpRequestException("The SSL connection could not be established."));

        void Running() => log.Start("POST", url, headers, body);

        void Done()
        {
            var done = log.Start("POST", url, headers, body);
            using (var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK))
            {
                response.Headers.TryAddWithoutValidation("request-id", "req_011CX9sample");
                response.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-remaining", "49");
                response.Content = new System.Net.Http.StringContent("");
                response.Content.Headers.ContentType = new("text/event-stream");
                done.RecordResponse(response);
            }

            done.AppendResponse(System.Text.Encoding.UTF8.GetBytes(
                """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_01Sample","model":"claude-opus-5"}}

                event: content_block_delta
                data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"On the "}}

                event: message_delta
                data: {"type":"message_delta","usage":{"input_tokens":18422,"output_tokens":96}}

                """));
            done.CompleteBody(reachedEnd: true);
        }

        // The pane opens on the newest call, so the state being posed is seeded last.
        Action[] order = newest switch
        {
            "inflight" => [Failed, Done, Running],
            "failed" => [Running, Done, Failed],
            _ => [Failed, Running, Done],
        };
        foreach (var seed in order)
        {
            seed();
        }
    }

    /// <summary>Holds the desktop for --open=glow, so the indicator stays up.</summary>
    private JarvisCode.App.Services.DesktopLock.Lease _glowPose;

    /// <summary>Poses the surface named by --open= (dev/verification flag).</summary>
    private async Task ApplyOpenTargetAsync(string? openTarget)
    {
        if (openTarget is null || _mainWindow is null)
        {
            return;
        }

        if (openTarget.StartsWith("settings"))
        {
            var parts = openTarget.Split(':');
            if (parts.Length == 3)
            {
                _mainWindow.OpenSettings(parts[1], parts[2]);
            }
            else
            {
                _mainWindow.OpenSettings();
            }
        }
        else if (openTarget.StartsWith("debug", StringComparison.Ordinal))
        {
            if (!openTarget.EndsWith(":empty", StringComparison.Ordinal) && _services is { } services)
            {
                SeedSampleTraffic(services.ModelTraffic, openTarget.Split(':').ElementAtOrDefault(1));
            }

            _mainWindow.OpenSettings("Desktop app", "Debug");
        }
        else if (openTarget == "about")
        {
            AboutWindow.ShowFor(_mainWindow);
            await Task.Delay(300);
        }
        else if (openTarget == "diagnostics")
        {
            // --open=diagnostics poses Help ▸ Troubleshooting ▸ Generate Diagnostic
            // Report; the packaging pass is real, so the wait is what it takes.
            _mainWindow.PoseDiagnosticReport();
            await Task.Delay(4000);
        }
        else if (openTarget == "appmenu")
        {
            // The application menu is a ContextMenu in a window of its own, so this
            // one needs a real screen grab, as the model and mode menus do.
            _mainWindow.OpenAppMenu();
            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("update", StringComparison.Ordinal))
        {
            // --open=update[:checking|:downloading|:failed] poses the sidebar's
            // auto-updater banner in one of its four states.
            _mainWindow.ShowSampleUpdateCard(openTarget.Split(':').ElementAtOrDefault(1));
            await Task.Delay(300);
        }
        else if (openTarget == "themes")
        {
            _mainWindow.OpenThemePicker();
        }
        else if (openTarget == "palette")
        {
            _mainWindow.OpenCommandPalette();
        }
        else if (openTarget == "shortcuts")
        {
            _mainWindow.OpenShortcuts();
        }
        else if (openTarget == "grant")
        {
            // --open=grant poses the computer-use grant card with one row of each
            // kind the reference draws: a plain app, a restricted one, one already
            // held, one nothing resolved, and a grant flag.
            // Modal, so it is posed after the window has rendered and needs a real
            // screen grab.
            var grantRequest = new JarvisCode.App.Services.GrantRequest(
                    "Read the error dialog Notepad is showing and retype the line it rejected.",
                    [
                        new JarvisCode.App.Services.GrantRow("notepad", true, false, JarvisCode.App.Services.AppTier.Full),
                        new JarvisCode.App.Services.GrantRow("powershell", true, false, JarvisCode.App.Services.AppTier.Click),
                        new JarvisCode.App.Services.GrantRow("chrome", true, true, JarvisCode.App.Services.AppTier.Read),
                        new JarvisCode.App.Services.GrantRow("acmewriter", false, false, JarvisCode.App.Services.AppTier.Full),
                    ],
                    ClipboardRead: true,
                    ClipboardWrite: false,
                    SystemKeyCombos: true,
                    ["slack", "spotify"],
                    AutoUnhide: true);
            _ = _mainWindow.Dispatcher.BeginInvoke(() =>
                JarvisCode.App.Views.ComputerUseGrantDialog.AskAsync(
                    grantRequest, System.Threading.CancellationToken.None));
            await Task.Delay(800);
        }
        else if (openTarget == "glow")
        {
            // --open=glow poses the on-screen indicator by taking the desktop
            // lock the way a computer-use batch does. It is a click-through
            // window of its own and is excluded from capture, so it is checked
            // with a camera rather than with RenderTargetBitmap.
            JarvisCode.App.Services.ComputerUseGlow.Capturable = true;
            _glowPose = JarvisCode.App.Services.DesktopLock.TryAcquire("open-glow");
        }
        else if (openTarget.StartsWith("toast", StringComparison.Ordinal))
        {
            _mainWindow.ShowSampleToasts();
        }
        else if (openTarget.StartsWith("errorcard", StringComparison.Ordinal))
        {
            _mainWindow.ShowSampleErrorCard(openTarget.Split(':').ElementAtOrDefault(1));
        }
        else if (openTarget == "quickentry")
        {
            _mainWindow.ToggleQuickEntry();
        }
        else if (openTarget.StartsWith("customize"))
        {
            var pieces = openTarget.Split(':');
            _mainWindow.EnterCustomize(pieces.Length > 1 ? pieces[1] : null);
        }
        else if (openTarget == "artifact")
        {
            _mainWindow.PublishSampleArtifact();
            await Task.Delay(1500);
        }
        else if (openTarget.StartsWith("scheduled", StringComparison.Ordinal))
        {
            // "--open=scheduled:editor" and ":detail" pose the two views the
            // list opens into, which a screenshot run cannot click its way to.
            var pose = openTarget.Length > "scheduled:".Length && openTarget.StartsWith("scheduled:", StringComparison.Ordinal)
                ? openTarget["scheduled:".Length..]
                : null;
            _mainWindow.OpenRoutines(pose);
        }
        else if (openTarget.StartsWith("home", StringComparison.Ordinal))
        {
            // The Code home view. "--open=home" poses the action center with one row of
            // every kind; "--open=home:clear" its landing-clear state, which is the only
            // one the reference shows its usage stats card in.
            _mainWindow.PoseHomeView(!openTarget.EndsWith(":clear", StringComparison.Ordinal));
            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("sidebar", StringComparison.Ordinal))
        {
            // The Code sidebar's list: "--open=sidebar" seeds a row of every kind,
            // ":showmore" pushes a bucket past its twenty, ":hints" raises the jump
            // keycaps and ":state" poses the State grouping's buckets.
            _mainWindow.PoseSidebar(openTarget.Split(':').ElementAtOrDefault(1));
            await Task.Delay(500);
        }
        else if (openTarget.StartsWith("gitbar", StringComparison.Ordinal))
        {
            // The PR bar in one of its modes: "--open=gitbar[:create|:draft|:commit|
            // :view|:merged|:queued|:failing]".
            _mainWindow.PoseGitBar(openTarget.Split(':').ElementAtOrDefault(1) ?? "create");
            await Task.Delay(400);
        }
        else if (openTarget == "monitors")
        {
            // A sample plugin with monitors, on its detail page: the Contents
            // section's Monitors rows are what this poses.
            _mainWindow.PosePluginMonitors();
            await Task.Delay(500);
        }
        else if (openTarget == "issuepicker")
        {
            // The Import GitHub issue picker. It is modal and asks `gh`, so it needs
            // a real screen grab and a repository this machine can reach.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            _ = _mainWindow.Dispatcher.BeginInvoke(_mainWindow.PoseIssuePicker);
            await Task.Delay(1500);
        }
        else if (openTarget == "sessionnotfound")
        {
            // The card the reference shows for a session whose transcript is gone.
            _mainWindow.PoseSessionNotFound();
            await Task.Delay(400);
        }
        else if (openTarget == "branchswitch")
        {
            // The dirty-tree dialog a branch pick raises. It is modal, so it is posed
            // after the window has rendered and needs a real screen grab.
            _ = _mainWindow.Dispatcher.BeginInvoke(_mainWindow.PoseBranchSwitch);
            await Task.Delay(800);
        }
        else if (openTarget.StartsWith("slash", StringComparison.Ordinal))
        {
            // A Code session first: the command menu lists skills, and Chat has none.
            // "--open=slash:query" poses the menu mid-filter.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            var pieces = openTarget.Split(':', 2);
            _mainWindow.PrefillComposer("/" + (pieces.Length > 1 ? pieces[1] : ""));
            await Task.Delay(800);
        }
        else if (openTarget == "mention")
        {
            // A Code session in the current directory, with the @-completion open.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            _mainWindow.PrefillComposer("@");
            await Task.Delay(800);
        }
        else if (openTarget.StartsWith("markdown", StringComparison.Ordinal))
        {
            if (openTarget.Contains(":code", StringComparison.Ordinal))
            {
                _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
                await Task.Delay(500);
            }

            _mainWindow.ShowSampleMarkdown(
                openTarget.EndsWith(":top", StringComparison.Ordinal) ? "top"
                : openTarget.EndsWith(":mid", StringComparison.Ordinal) ? "mid"
                : openTarget.EndsWith(":mermaid", StringComparison.Ordinal) ? "mermaid"
                : "blocks");
            // A diagram is rendered by a real browser, so the pose waits for it
            // rather than grabbing the loading placeholder.
            await Task.Delay(openTarget.EndsWith(":mermaid", StringComparison.Ordinal) ? 6000 : 400);
        }
        else if (openTarget == "widget")
        {
            // One rendered visualize widget in the transcript. The page is a real
            // browser, so the pose waits for it rather than grabbing the row
            // before it has answered the host handshake.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(500);
            _mainWindow.ShowSampleWidget();
            await Task.Delay(6000);
        }
        else if (openTarget == "transcript")
        {
            _mainWindow.ShowSampleTranscript();
        }
        else if (openTarget.StartsWith("titlebar", StringComparison.Ordinal))
        {
            // The Code session's titlebar: "--open=titlebar" for the ordinary bar, and
            // ":loading" / ":agent" / ":panes" for the three states a posed profile would
            // otherwise never reach. Narrow the window to watch the pill and the rail fold.
            _mainWindow.PoseTitleBar(openTarget.Split(':').ElementAtOrDefault(1) ?? "");
            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("comparison", StringComparison.Ordinal))
        {
            // The side-by-side view: empty, at one of its three vote states, or
            // ":send", which runs both arms for real and waits for them.
            var state = openTarget.Split(':', 2).ElementAtOrDefault(1) ?? "";
            _mainWindow.PoseComparison(state);
            await Task.Delay(state == "send" ? 12000 : 400);
        }
        else if (openTarget.StartsWith("chat", StringComparison.Ordinal))
        {
            // The Chat surface's own screens: the empty state, the first-chat
            // onboarding, an incognito chat, the waiting line, the compaction
            // indicator, the unfinished-turn card, the queue and the drawer.
            var pieces = openTarget.Split(':', 2);
            _mainWindow.ShowSampleChat(pieces.Length > 1 ? pieces[1] : "");
            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("thinking", StringComparison.Ordinal))
        {
            _mainWindow.ShowSampleThinking(openTarget.EndsWith(":code", StringComparison.Ordinal));
            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("backgroundtasks", StringComparison.Ordinal))
        {
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            if (openTarget.Contains(":subagent", StringComparison.Ordinal))
            {
                _mainWindow.ShowSampleSubagentView(
                    openTarget.EndsWith(":failed", StringComparison.Ordinal));
            }
            else
            {
                _mainWindow.ShowSampleBackgroundTasks(openTarget.EndsWith(":empty", StringComparison.Ordinal));
            }

            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("taskboard", StringComparison.Ordinal))
        {
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            _mainWindow.ShowSampleTaskBoard();
            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("panes", StringComparison.Ordinal))
        {
            // The tile mosaic: a Code session with the named pane open beside the
            // conversation, or the terminal and the diff tiled when none is named.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            var pane = openTarget.Contains(':', StringComparison.Ordinal)
                ? openTarget[(openTarget.IndexOf(':', StringComparison.Ordinal) + 1)..]
                : "";
            _mainWindow.ShowSamplePanes(pane);
            await Task.Delay(600);
        }
        else if (openTarget == "emulator")
        {
            // The Android Emulator pane, attached to a booted emulator when this
            // machine has one; the frame pump needs a moment to draw its first.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            await _mainWindow.ShowSampleEmulatorAsync();
            await Task.Delay(1500);
        }
        else if (openTarget == "browser")
        {
            // A Code session in the current directory with the Browser panel open.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            _mainWindow.ShowBrowserPanel();
            await Task.Delay(400);
        }
        else if (openTarget.StartsWith("permission"))
        {
            var pieces = openTarget.Split(':');
            _mainWindow.ShowSamplePermission(pieces.Length > 1 ? pieces[1] : "");
        }
        else if (openTarget == "question")
        {
            _mainWindow.ShowSampleQuestion();
        }
        else if (openTarget == "goalcheckin")
        {
            _mainWindow.ShowSampleGoalCheckin();
        }
        else if (openTarget is "plan" or "plan:edit")
        {
            // ":edit" poses the card in the state the reference's edit affordance
            // leaves it in, so a screenshot can show the editable plan.
            _mainWindow.ShowSamplePlanApproval(editing: openTarget == "plan:edit");
        }
        else if (openTarget == "context")
        {
            _mainWindow.ShowSampleTranscript();
            _mainWindow.ShowContextPopup();
        }
        else if (openTarget == "effort")
        {
            _mainWindow.OpenEffortSelector();
        }
        else if (openTarget == "model")
        {
            // The model menu is a ContextMenu in a window of its own, so this one
            // needs a real screen grab too.
            _mainWindow.OpenModelSelector();
            await Task.Delay(400);
        }
        else if (openTarget == "mode")
        {
            // The permission-mode menu is a ContextMenu too, so a real screen grab is
            // the only way to see its rows, its digits and the Default badge.
            _mainWindow.OpenPermissionModeSelector();
            await Task.Delay(400);
        }
        else if (openTarget == "teach")
        {
            // The teach overlay is its own layered window, so it needs a real
            // screen grab too — RenderTargetBitmap only sees the main window.
            _mainWindow.ShowSampleTeachStep();
            await Task.Delay(600);
        }
        else if (openTarget == "request")
        {
            // Needs a real screen grab like the other popups — RenderTargetBitmap
            // never sees a Popup's own window.
            _mainWindow.StartCodeSessionIn(Environment.CurrentDirectory);
            await Task.Delay(400);
            _mainWindow.OpenRequestInspector();
            await Task.Delay(800);
        }
    }

    /// <summary>
    /// The reference's "Interface font": its own sans face, or the system's. This app
    /// ships no Anthropic Sans, so "anthropic" is the app's own UI face (Segoe UI
    /// Variable) and "system" the shell's message font.
    /// </summary>
    public static void ApplyInterfaceFont(string interfaceFont)
    {
        Current.Resources["UiFontFamily"] = interfaceFont == "system"
            ? SystemFonts.MessageFontFamily
            : new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Arial");
    }

    /// <summary>The reference's "Code font": a custom monospace family for code and the terminal; empty keeps the app's own.</summary>
    public static void ApplyCodeFont(string codeFont)
    {
        Current.Resources["MonoFontFamily"] = string.IsNullOrWhiteSpace(codeFont)
            ? new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New")
            : new System.Windows.Media.FontFamily(codeFont.Trim() + ", Cascadia Mono, Consolas, Courier New");
    }

    /// <summary>The reference's "Transcript width" (s | m | l), applied to the transcript and composer columns.</summary>
    public static void ApplyTranscriptWidth(string choice)
    {
        Current.Resources["TranscriptMaxWidth"] = JarvisCode.App.Services.TranscriptWidths.ContentWidth(choice);
    }

    /// <summary>Swaps the conversation font family (default/sans/system/dyslexia).</summary>
    public static void ApplyChatFont(string chatFont)
    {
        var family = chatFont switch
        {
            "dyslexia" => new System.Windows.Media.FontFamily("OpenDyslexic, Comic Sans MS, Segoe UI"),
            "system" => new System.Windows.Media.FontFamily("Segoe UI, Arial"),
            _ => new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Arial"),
        };
        Current.Resources["ChatFontFamily"] = family;
    }

    private static void CaptureWindow(Window window, string path)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    private void OnInstanceCommand(string command)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (command)
            {
                case SingleInstance.ToggleQuickEntryCommand:
                    _mainWindow?.ToggleQuickEntry();
                    break;
                case SingleInstance.NewChatCommand:
                    _mainWindow?.RestoreFromTray();
                    _mainWindow?.StartNewSession();
                    break;
                case var codeDir when codeDir.StartsWith(SingleInstance.CodeDirCommandPrefix, StringComparison.Ordinal):
                    _mainWindow?.RestoreFromTray();
                    _mainWindow?.StartCodeSessionIn(codeDir[SingleInstance.CodeDirCommandPrefix.Length..]);
                    break;
                case var link when link.StartsWith(SingleInstance.DeepLinkCommandPrefix, StringComparison.Ordinal):
                    _mainWindow?.HandleDeepLink(link[SingleInstance.DeepLinkCommandPrefix.Length..]);
                    break;
                default:
                    _mainWindow?.RestoreFromTray();
                    break;
            }
        });
    }

    /// <summary>
    /// Pasted images are copied into attachments\ so the chip has a file to point
    /// at; the sent message carries its own base64 copy, so anything older than
    /// 30 days is just disk growth.
    /// </summary>
    private static void CleanUpPastedAttachments(string root)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var directory = System.IO.Path.Combine(root, "attachments");
                if (!System.IO.Directory.Exists(directory))
                {
                    return;
                }

                var cutoff = DateTime.UtcNow.AddDays(-30);
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, "pasted-*.png"))
                {
                    if (System.IO.File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        System.IO.File.Delete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Best-effort hygiene; a locked file just waits for the next launch.
            }
        });
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportCrash(e.Exception, terminating: false);
        e.Handled = true;
    }

    /// <summary>
    /// One dialog for every unhandled failure, in the shape the reference's own
    /// error dialogs take: what happened on the first line, the detail under it,
    /// and a button that copies the whole thing so it can be pasted into an issue.
    /// </summary>
    private void ReportCrash(Exception? exception, bool terminating)
    {
        if (exception is null)
        {
            return;
        }

        void Show()
        {
            var detail = exception.ToString();
            var response = Views.MessageDialog.Show(
                _mainWindow,
                "Something went wrong",
                detail.Length > 1200 ? detail[..1200] + "\u2026" : detail,
                ["Copy details", Views.MessageDialog.OkLabel],
                defaultId: 1,
                cancelId: 1,
                Views.MessageDialogType.Error);
            if (response == 0)
            {
                try
                {
                    Clipboard.SetText(detail);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Another process owns the clipboard.
                }
            }
        }

        if (Dispatcher.CheckAccess())
        {
            Show();
        }
        else if (terminating)
        {
            // A terminating failure gives the dispatcher no chance to run later,
            // so the dialog is raised synchronously on it.
            Dispatcher.Invoke(Show);
        }
        else
        {
            Dispatcher.BeginInvoke(Show);
        }
    }

    /// <summary>
    /// Keeps the jarvis-code:// registration matching the setting. It names this
    /// executable, so it is rewritten on every launch that owns it — a moved or
    /// updated install would otherwise leave the shell pointing at a path that is
    /// no longer there.
    /// </summary>
    private static void SyncProtocolRegistration(UiSettings ui, string? profile)
    {
        try
        {
            if (ui.DeepLinksRegistered)
            {
                ProtocolRegistration.SetRegistered(true, profile);
            }
            else if (ProtocolRegistration.IsRegistered())
            {
                ProtocolRegistration.SetRegistered(false, profile);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The registration is a convenience; the app runs without it.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainWindow?.ShutdownCleanup();
        _services?.Dispose();
        // The engine outlives any one surface on purpose - they share it - so
        // it is the app's to end. Closing its pipe is the signal it shuts down
        // on; the wait is bounded so a wedged engine cannot hold the app open.
        Task.WhenAll(
            JarvisCode.App.Services.ElectronEngine.ShutdownAsync(),
            ChatGptWebViewTransport.ShutdownAsync()).Wait(EngineShutdownGrace);
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static LaunchArgs ParseArgs(string[] args)
    {
        var result = new LaunchArgs(
            Profile: Environment.GetEnvironmentVariable("JARVIS_PROFILE"),
            ToggleQuickEntry: false,
            OpenQuickEntry: false,
            StartHidden: false,
            ScreenshotPath: null);

        foreach (var arg in args)
        {
            if (arg.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                result = result with { Profile = arg["--profile=".Length..] };
            }
            else if (arg is "--toggle" or "--toggle-quick-entry")
            {
                result = result with { ToggleQuickEntry = true };
            }
            else if (arg is "--hidden" or "--tray")
            {
                result = result with { StartHidden = true };
            }
            else if (arg.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase))
            {
                result = result with { ScreenshotPath = arg["--screenshot=".Length..] };
            }
            else if (arg.StartsWith("--open=", StringComparison.OrdinalIgnoreCase))
            {
                result = result with { OpenTarget = arg["--open=".Length..].ToLowerInvariant() };
            }
            else if (arg.StartsWith("--e2e=", StringComparison.OrdinalIgnoreCase))
            {
                result = result with { E2EPrompt = arg["--e2e=".Length..] };
            }
            else if (arg is "--pane-selftest")
            {
                // Dev/verification: drives the Browser pane's own toolset (navigate,
                // read_page, click, console, JS, screenshot, resize) against a local
                // page and exits 0/2. Like --e2e it needs --screenshot to arm.
                result = result with { PaneSelfTest = true };
            }
            else if (arg is "--subagent-selftest")
            {
                // Drives the pane's subagent view on the real controls and
                // exits 0/2. Like the other self-tests it needs --screenshot.
                result = result with { SubagentSelfTest = true };
            }
            else if (arg is "--ui-selftest")
            {
                // Checks the reference-measured geometry on the laid-out tree
                // and exits 0/2. Like --pane-selftest it needs --screenshot.
                result = result with { UiSelfTest = true };
            }
            else if (arg is "--input-selftest")
            {
                // Walks every field on every surface and checks the three things a
                // screenshot cannot settle - where the caret is, what colour it is,
                // and whether the placeholder starts where the text will - exiting
                // 0/2. Like the other self-tests it needs --screenshot.
                result = result with { InputSelfTest = true };
            }
            else if (arg is "--titlebar-selftest")
            {
                // Drives the Code session's titlebar on the real controls - its drag
                // region, its rename editor, the right-click that opens the session
                // menu, and the pill compacting as the window narrows - and exits 0/2.
                // Like the other self-tests it needs --screenshot.
                result = result with { TitleBarSelfTest = true };
            }
            else if (arg is "--transcript-selftest")
            {
                // Drives the transcript's virtualizer on the real controls - the
                // window stays inside the viewport and its overscan, the extent
                // covers every row, a row nobody built can still be scrolled to,
                // the anchor survives a round trip, and Select all reaches rows
                // that were never on screen - and exits 0/2. Like the other
                // self-tests it needs --screenshot.
                result = result with { TranscriptSelfTest = true };
            }
            else if (arg is "--sidebar-selftest")
            {
                // Drives the Code sidebar's list on the real controls - the section
                // header's filter and menu, the row menu, the collapse caret, the
                // show-more row and the in-place rename - and exits 0/2. Like the
                // other self-tests it needs --screenshot.
                result = result with { SidebarSelfTest = true };
            }
            else if (arg is "--slash-selftest")
            {
                // Drives the composer's command menu on the real controls and
                // exits 0/2. Like the other self-tests it needs --screenshot.
                result = result with { SlashSelfTest = true };
            }
            else if (arg.StartsWith("--e2e-switch=", StringComparison.OrdinalIgnoreCase))
            {
                result = result with { E2ESwitchPrompt = arg["--e2e-switch=".Length..] };
            }
            else if (arg.StartsWith("--code-dir=", StringComparison.OrdinalIgnoreCase))
            {
                result = result with { CodeDirectory = arg["--code-dir=".Length..].Trim('"') };
            }
            else if (JarvisCode.App.Services.DeepLinks.IsDeepLink(arg))
            {
                // Windows hands the whole link as one argument when the protocol
                // handler starts the app.
                result = result with { DeepLink = arg };
            }
            else if (arg.StartsWith("--window-size=", StringComparison.OrdinalIgnoreCase))
            {
                // Dev/verification flag: render at an explicit size (e.g. 800x600)
                // so --screenshot can exercise narrow layouts.
                var parts = arg["--window-size=".Length..].Split('x', 'X');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out var width) &&
                    double.TryParse(parts[1], out var height))
                {
                    result = result with { WindowSize = (width, height) };
                }
            }
        }

        return result;
    }

    private sealed record LaunchArgs(
        string? Profile, bool ToggleQuickEntry, bool OpenQuickEntry, bool StartHidden,
        string? ScreenshotPath = null, string? OpenTarget = null, string? E2EPrompt = null,
        string? E2ESwitchPrompt = null, string? CodeDirectory = null,
        (double Width, double Height)? WindowSize = null, bool PaneSelfTest = false,
        bool UiSelfTest = false, bool InputSelfTest = false,
        bool SubagentSelfTest = false, bool SlashSelfTest = false,
        bool SidebarSelfTest = false,
        bool TitleBarSelfTest = false,
        bool TranscriptSelfTest = false,
        string? DeepLink = null);
}
