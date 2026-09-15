using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.App.Controls;
using JarvisCode.App.Infrastructure;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Utilities;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.App.Views;

public partial class ChatSurface : UserControl
{
    /// <summary>
    /// One entry of the composer's single command namespace. <paramref name="Aliases"/>
    /// are alternate names for the same entry rather than entries of their own,
    /// which is how the reference models them: the menu lists a command once and
    /// shows the matching alias in parentheses.
    /// </summary>
    private sealed record SlashCommand(
        string Name,
        string Description,
        Func<string, Task> Execute,
        string? Hint = null,
        string[]? Aliases = null)
    {
        /// <summary>This entry's own name plus its aliases, which is what a typed command resolves against.</summary>
        public IEnumerable<string> Names => Aliases is null ? [Name] : [Name, .. Aliases];

        public bool Matches(string typed) =>
            Names.Any(name => string.Equals(name, typed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// /output-style: with no argument, list the styles and mark the active
    /// one; with one, switch to it. The style is global rather than
    /// per-session, which is where the reference keeps it too.
    /// </summary>
    private string SetOutputStyle(string argument)
    {
        var settings = _services!.UiSettings;
        var active = settings.Current.OutputStyle;
        var cwd = _vm?.Session.WorkingDirectory ?? Environment.CurrentDirectory;
        var available = Services.OutputStyles.Available(cwd, _services.Paths.Root);
        if (argument.Equals("new", StringComparison.OrdinalIgnoreCase) || argument.Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            var editor = new OutputStyleEditor(_services, cwd, argument.Equals("edit", StringComparison.OrdinalIgnoreCase) ? active : null)
                { Owner = Window.GetWindow(this) };
            if (editor.ShowDialog() != true) return "Output style unchanged.";
            settings.Current.OutputStyle = editor.SavedName!;
            if (_vm is not null) settings.Current.SessionOutputStyles.Remove(_vm.Session.Id);
            settings.Save();
            return $"Output style set to {editor.SavedName}. It applies from your next message.";
        }
        if (argument.Length == 0)
        {
            var lines = available.Select(style =>
                (string.Equals(style.Name, active, StringComparison.OrdinalIgnoreCase) ? "• " : "  ")
                + $"{style.Name} — {style.Description}");
            var none = string.Equals(active, Services.OutputStyles.DefaultName, StringComparison.OrdinalIgnoreCase)
                ? "• default — no style; the harness prompt alone"
                : "  default — no style; the harness prompt alone";
            return "Output style:\n" + none + "\n" + string.Join("\n", lines)
                + "\n\nSwitch with /output-style <name>. Create or edit with /output-style new or /output-style edit.";
        }

        if (string.Equals(argument, Services.OutputStyles.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            settings.Current.OutputStyle = Services.OutputStyles.DefaultName;
            if (_vm is not null) settings.Current.SessionOutputStyles.Remove(_vm.Session.Id);
            settings.Save();
            return "Output style set to default. It applies from your next message.";
        }

        if (Services.OutputStyles.Find(argument, cwd, _services.Paths.Root) is not { } chosen)
        {
            return $"Unknown output style: {argument}. Available: default, "
                + string.Join(", ", available.Select(static s => s.Name)) + ".";
        }

        settings.Current.OutputStyle = chosen.Name;
        if (_vm is not null) settings.Current.SessionOutputStyles.Remove(_vm.Session.Id);
        settings.Save();
        return $"Output style set to {chosen.Name}. It applies from your next message.";
    }

    /// <summary>/autocompact with no argument: the window in force and why.</summary>
    private string AutoCompactStatus() =>
        Services.AutoCompactCommand.Status(
            ResolveAutoCompactWindow(_services!.Settings.Current.AutoCompactWindow),
            _services.Settings.Current.AutoCompactEnabled);

    /// <summary>/autocompact with an argument: read it, save it, report it.</summary>
    private string SetAutoCompactWindow(string argument)
    {
        if (ResolveAutoCompactWindow(null).Source == AutoCompactWindowSource.Env)
        {
            return Services.AutoCompactCommand.EnvironmentTakesPrecedence;
        }

        if (!Services.AutoCompactCommand.TryReadArgument(argument, out int? chosen))
        {
            return Services.AutoCompactCommand.ParseFailure(argument);
        }

        var settings = _services!.Settings.Current;
        settings.AutoCompactWindow = chosen;
        _services.Settings.Save();

        // The reference reads the setting back and reports whatever a
        // higher-priority source overrode on the way.
        var inForce = ResolveAutoCompactWindow(settings.AutoCompactWindow);
        return Services.AutoCompactCommand.Applied(
            chosen,
            inForce,
            inForce.Source == AutoCompactWindowSource.Env || settings.AutoCompactWindow != chosen);
    }

    private AutoCompactWindow ResolveAutoCompactWindow(int? configured)
    {
        var model = _vm?.CurrentModel;
        return ContextWindows.ResolveWindow(
            model?.MaxContextTokens ?? ContextWindows.ModelDefaultWindow,
            configured,
            Environment.GetEnvironmentVariable(Services.TurnContextFactory.AutoCompactWindowVariable),
            model?.ModelId);
    }

    private AppServices? _services;
    private SessionViewModels? _sessions;
    private bool _isCodeSurface;

    /// <summary>
    /// The per-session view models this surface keeps, so the workspace can ask
    /// which sessions currently have a turn in flight.
    /// </summary>
    public SessionViewModels? SessionViewModels => _sessions;
    private ChatViewModel? _vm;
    private bool _autoScroll = true;

    // Scroll anchoring: the element a click is about to resize around, and where
    // it sat in the viewport at the moment of the press.
    private FrameworkElement? _scrollAnchor;
    private double _scrollAnchorY;
    private bool _anchoringScroll;
    private bool _loadingOlder;
    private bool _olderQueued;
    private List<SlashCommand> _slashCommands = [];
    private int _commandSelection;
    private List<int> _findMatches = [];
    private int _findIndex = -1;
    private bool _gitBannerDismissed;
    private Services.VoiceInput? _voice;
    private DispatcherTimer? _relativeTimeTimer;
    private bool _rewindInFlight;
    private Panels.TranscriptViewMode _transcriptView = Panels.TranscriptViewMode.Normal;

    public event EventHandler? SessionPersisted;

    /// <summary>A turn started or ended on any of this surface's sessions, shown or not.</summary>
    public event EventHandler? RunningStateChanged;

    /// <summary>A turn finished on any of this surface's sessions.</summary>
    public event Action<ChatViewModel>? TurnFinished;

    /// <summary>Any of this surface's sessions is waiting on the user.</summary>
    public event Action<ChatViewModel>? AttentionRequested;

    public event EventHandler? SettingsRequested;
    public event EventHandler? ThemePickerRequested;

    /// <summary>Raised with "skills" | "connectors" | "plugins" to open the Customize page.</summary>
    public event EventHandler<string>? CustomizeNavigateRequested;

    /// <summary>
    /// Asks the host to pin the assistant turn at this transcript index as a
    /// chapter, or to unpin the chapters already there. The workspace owns the
    /// chapter list, so the surface only names the anchor.
    /// </summary>
    public event Action<ChatViewModel, int>? ChapterPinToggleRequested;

    /// <summary>Whether that index already carries a chapter, asked of the host.</summary>
    public Func<ChatViewModel, int, bool>? IsChapterPinned { get; set; }

    public ChatSurface()
    {
        CopyMessageCommand = new RelayCommand<UserMessageItem>(CopyMessage);
        RewindMessageCommand = new RelayCommand<UserMessageItem>(item => _ = RewindMessageAsync(item));
        BranchMessageCommand = new RelayCommand<UserMessageItem>(item => _ = BranchMessageAsync(item));
        EditMessageCommand = new RelayCommand<UserMessageItem>(item => _ = EditMessageAsync(item));
        CopyResponseCommand = new RelayCommand<AssistantTextItem>(CopyResponse);
        ReadAloudCommand = new RelayCommand<AssistantTextItem>(ReadAloudResponse);
        // Reaching the end of the answer puts the button back to "Read aloud".
        _readAloud.StateChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_readAloud.IsSpeaking && _reading is { } spoken)
            {
                spoken.IsReading = false;
                _reading = null;
            }
        });
        RetryResponseCommand = new RelayCommand<AssistantTextItem>(item =>
        {
            if (item is not null && _vm is { IsRunning: false })
            {
                _ = _vm.RetryAssistantAsync(item);
            }
        });
        GoodResponseCommand = new RelayCommand<AssistantTextItem>(item =>
        {
            if (item is not null)
            {
                item.Feedback = item.Feedback > 0 ? 0 : 1;
            }
        });
        BadResponseCommand = new RelayCommand<AssistantTextItem>(item =>
        {
            if (item is not null)
            {
                item.Feedback = item.Feedback < 0 ? 0 : -1;
            }
        });
        CopyTurnCommand = new RelayCommand<ViewModels.AssistantFooterItem>(CopyTurn);
        ForkTurnCommand = new RelayCommand<ViewModels.AssistantFooterItem>(item => _ = ForkTurnAsync(item));
        RunTurnInBackgroundCommand = new RelayCommand<ViewModels.AssistantFooterItem>(RunTurnInBackground);
        GoodTurnCommand = new RelayCommand<ViewModels.AssistantFooterItem>(item =>
        {
            if (item is not null)
            {
                item.Feedback = item.Feedback > 0 ? 0 : 1;
            }
        });
        BadTurnCommand = new RelayCommand<ViewModels.AssistantFooterItem>(item =>
        {
            if (item is not null)
            {
                item.Feedback = item.Feedback < 0 ? 0 : -1;
            }
        });
        InitializeComponent();
        IsVisibleChanged += (_, _) => _vm?.SetLiveSummaryVisible(IsVisible && _transcriptView == Panels.TranscriptViewMode.Summary);
        Rail.MenuRequested += (_, _) => ShowSessionMenu();
        // The bar folds the rail's toggles away when the title needs the room, so the count
        // belongs to the bar's own fitting pass rather than to the rail measuring leftovers.
        SessionHeader.Rail = Rail;
        // The rail toggles a pane; every other caller opens one, so the two travel
        // on separate events and the workspace can tell them apart.
        Rail.PanelRequested += (_, key) => PanelToggleRequested?.Invoke(this, key ?? "");

        // The command menu hangs off the typed slash rather than off the whole box.
        CommandPopup.CustomPopupPlacementCallback = PlaceCommandPopup;
        CommandScroller.ScrollChanged += (_, _) => UpdateCommandDescriptionCard();

        // Pasted images and very long pastes become composer chips, like the reference.
        DataObject.AddPastingHandler(InputBox, OnComposerPaste);
        InputBox.SizeChanged += (_, _) => UpdateSkillArgumentHint();
        InputBox.Loaded += (_, _) => Dispatcher.BeginInvoke(UpdateSkillArgumentHint, DispatcherPriority.Loaded);

        // Wheel over a code fence or tool result moves the transcript unless that
        // box itself still has somewhere to scroll — like the reference's page scroll.
        TranscriptScroll.PreviewMouseWheel += OnTranscriptWheel;
        // A disclosure the reader opens must not walk out from under the cursor.
        // The transcript sticks to the tail while an answer streams in, and that
        // same rule used to re-pin on the height change a click caused, moving the
        // row 77px before the second click landed. Browsers anchor instead, so the
        // row a click resizes keeps its place on screen and closes on the next one.
        TranscriptItems.AddHandler(
            System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler(OnTranscriptRowClicked),
            handledEventsToo: true);
        // "Scroll the transcript with arrow and page keys." The composer keeps
        // its own keys, so this only runs when focus is not in a text box.
        TranscriptScroll.PreviewKeyDown += OnTranscriptKey;

        // "Right-click a message or select text → Attach as context".
        // The virtualizer only builds the rows on screen, so a selection has to say
        // when it needs more than that: a live drag stops eviction, and Select all
        // asks for every row before it reads them.
        TranscriptSelection.HoldRows = () => TranscriptPanel?.HoldRealized();
        TranscriptSelection.ReleaseRows = () => TranscriptPanel?.ReleaseHold();
        TranscriptSelection.RealizeAllRows = () =>
        {
            _vm?.LoadEntireTranscript();
            TranscriptPanel?.RealizeAll();
        };
        TranscriptSelection.ResolveContextSource = ResolveTranscriptContext;
        TranscriptSelection.AttachAsContextRequested += (_, text) => AttachContext(text);
        // The reference's message entries: what it says, what it said in
        // markdown, and the two things the menu offers to do with the turn.
        TranscriptSelection.ResolveMessage = ResolveTranscriptMessage;
        TranscriptSelection.ResolveImage = ResolveTranscriptImage;
    }

    /// <summary>
    /// Arrow and page keys move the transcript. A key pressed inside an editable
    /// box or a control that scrolls itself is left alone.
    /// </summary>
    private void OnTranscriptKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None ||
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                TranscriptScroll.LineUp();
                break;
            case Key.Down:
                TranscriptScroll.LineDown();
                break;
            case Key.PageUp:
                TranscriptScroll.PageUp();
                break;
            case Key.PageDown:
                TranscriptScroll.PageDown();
                break;
            case Key.Home:
                TranscriptScroll.ScrollToTop();
                break;
            case Key.End:
                TranscriptScroll.ScrollToEnd();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Hover action on a user message: put the prompt on the clipboard.</summary>
    public ICommand CopyMessageCommand { get; }

    /// <summary>Hover action: restore the files this turn changed and cut the history back to it.</summary>
    public ICommand RewindMessageCommand { get; }

    /// <summary>Hover action: copy the conversation up to this prompt into a new session.</summary>
    public ICommand BranchMessageCommand { get; }

    public ICommand EditMessageCommand { get; }

    public ICommand CopyResponseCommand { get; }

    /// <summary>The reference's "Read aloud", which becomes "Pause" while it speaks.</summary>
    public ICommand ReadAloudCommand { get; }

    public ICommand RetryResponseCommand { get; }

    public ICommand GoodResponseCommand { get; }

    public ICommand BadResponseCommand { get; }

    /// <summary>The Code surface's message footer, in the reference's action order.</summary>
    public ICommand CopyTurnCommand { get; }

    public ICommand ForkTurnCommand { get; }

    public ICommand GoodTurnCommand { get; }

    public ICommand BadTurnCommand { get; }

    public ICommand RunTurnInBackgroundCommand { get; }

    /// <summary>Chat's Edit message: cuts back to the prompt and refills the composer.</summary>
    private async Task EditMessageAsync(UserMessageItem? item)
    {
        if (item is null || _vm is null)
        {
            return;
        }

        var text = await _vm.EditUserMessageAsync(item);
        PrefillInput(text);
    }

    /// <summary>Hover action on a response: put the markdown on the clipboard.</summary>
    private void CopyResponse(AssistantTextItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.Markdown);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        item.IsCopied = true;
        var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            item.IsCopied = false;
        };
        revert.Start();
    }

    public ChatViewModel ViewModel => _vm ?? throw new InvalidOperationException("ChatSurface is not initialized.");

    /// <summary>The session header's panel rail (toggles hidden outside the Code surface).</summary>
    public Panels.PanelRail HeaderRail => Rail;

    /// <summary>The Code session's titlebar, which the geometry self-test measures.</summary>
    internal Controls.SessionTitleBarPanel SessionTitleBar => SessionHeader;

    /// <summary>A panel the session menu or the rail asked for; null closes the panel.</summary>
    public event EventHandler<string?>? PanelRequested;

    /// <summary>The pane rail asked to toggle a pane by key.</summary>
    public event EventHandler<string>? PanelToggleRequested;

    /// <summary>The titlebar's × was pressed: the host should remove this pane.</summary>
    public event EventHandler? ClosePaneRequested;

    /// <summary>The titlebar's artifact expander was pressed.</summary>
    public event EventHandler? ArtifactExpandRequested;

    /// <summary>The workspace answers whether a pane is open, for the menu's tick marks.</summary>
    public Func<string, bool>? IsPaneOpen { get; set; }

    public void Initialize(AppServices services, bool isCodeSurface)
    {
        _services = services;
        InputBox.ResolveSkill = ResolveComposerSkill;
        _isCodeSurface = isCodeSurface;
        _sessions = new SessionViewModels(services, isCodeSurface);
        _sessions.SessionPersisted += (_, _) => SessionPersisted?.Invoke(this, EventArgs.Empty);
        _sessions.RunningChanged += (_, _) => RunningStateChanged?.Invoke(this, EventArgs.Empty);
        _sessions.TurnFinished += vm => TurnFinished?.Invoke(vm);
        _sessions.AttentionRequested += vm => AttentionRequested?.Invoke(vm);
        InitializeSpelling(services.Paths.Root);

        if (!isCodeSurface)
        {
            ModeChip.Visibility = Visibility.Collapsed;
        }
        else
        {
            // "Send to side chat" only makes sense from a code session's transcript.
            TranscriptSelection.SendToSideChatRequested += (_, text) => SideChatSendRequested?.Invoke(this, text);
        }

        TranscriptScroll.ScrollChanged += OnScrollChanged;
        Bind(_sessions.ForNewSession(null));
        _ = ResumePrAutomationAsync();

        // "11 minutes ago" under each prompt has to age while the session stays open.
        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _relativeTimeTimer.Tick += (_, _) =>
        {
            foreach (var item in ViewModel.Transcript.OfType<UserMessageItem>())
            {
                item.RefreshRelativeTime();
            }

            foreach (var footer in ViewModel.Transcript.OfType<AssistantFooterItem>())
            {
                footer.RefreshRelativeTime();
            }
        };
        _relativeTimeTimer.Start();
    }

    /// <summary>The sessions on this surface with a turn in flight, shown or not.</summary>
    public IReadOnlyCollection<string> RunningSessionIds => _sessions?.RunningSessionIds ?? [];

    /// <summary>Stops and drops a deleted session's turn so it cannot keep writing to disk.</summary>
    public void ForgetSession(string sessionId) => _sessions?.Forget(sessionId);

    /// <summary>
    /// Points the view at another session's view model. Only the view moves: the model
    /// left behind keeps whatever turn it is running, which is what lets an answer
    /// survive a trip to another session and back.
    /// </summary>
    private void Bind(ChatViewModel viewModel)
    {
        if (ReferenceEquals(_vm, viewModel))
        {
            return;
        }

        // A live Power read belongs to the session/model that opened it. Switching sessions
        // retires that read so its result or error cannot repaint the new session's composer.
        CancelChatGptEffortRead();

        if (_vm is { } previous)
        {
            SaveComposerDraft();
            // The view is about to leave this transcript; what it measured and where
            // it was reading are worth keeping for the trip back.
            SaveTranscriptViewport();
            previous.SetLiveSummaryVisible(false);
            previous.PropertyChanged -= OnViewModelPropertyChanged;
            previous.PermissionModeChanged -= OnPermissionModeChanged;
            previous.TurnFinished -= OnTurnFinishedRefreshStatusline;
            previous.ComposerAttachments.CollectionChanged -= OnComposerDraftChanged;
        }

        _vm = viewModel;
        RestoreComposerDraft();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.PermissionModeChanged += OnPermissionModeChanged;
        viewModel.TurnFinished += OnTurnFinishedRefreshStatusline;
        // An attachment is draft content too, and mid-turn that decides whether the
        // primary action is Send or Stop.
        viewModel.ComposerAttachments.CollectionChanged += OnComposerDraftChanged;
        DataContext = viewModel;
        _gitBannerDismissed = false;

        // The transcript view is sticky per session (the reference's
        // transcriptModeBySession): the incoming session's stored mode — or
        // Normal — replaces whatever the outgoing session was showing.
        _transcriptView = Services.TranscriptViewModes.Parse(
            _services?.UiSettings.Current.TranscriptViewBySession.GetValueOrDefault(viewModel.Session.Id));

        // The transcript is re-materialised for the new session, so pin it to the newest
        // message: coming back to a session that is still streaming should follow it again.
        _autoScroll = true;
        TranscriptSelection.ClearSelection();
        _sessions?.EvictIdle(viewModel);

        // Each surface narrates a turn with its own component and the other stays
        // dark: the reference runs the epitaxy status line on Code and the chat
        // page's one-sentence waiting line on Chat, never both.
        StatusLine.Status = viewModel.IsCodeSurface ? viewModel.TurnStatus : null;
        ActivityPanel.Visibility = Visibility.Collapsed;
        viewModel.Searches.CollectionChanged += (_, _) => UpdateActivityChips();
        ChatStatus.Status = viewModel.IsCodeSurface ? null : viewModel.ChatStatus;

        UpdateLayoutState();
        UpdateModelChip();
        UpdateModeChip();
        UpdateEffortChip();
        if (_services is not null && viewModel.IsCodeSurface)
        {
            viewModel.CoordinatorMode = _services.SessionGroups.IsCoordinator(viewModel.Session.Id);
        }

        UpdateCoordinatorButton();
        UpdateActivityChips();
        UpdateGreeting();
        ApplyPromptColor();
        // The reference rotates the composer placeholder; on Code the skills
        // variant alternates in ("Type / for skills").
        if (viewModel.IsCodeSurface)
        {
            Placeholder.Text = ++_placeholderRotation % 2 == 0
                ? "Type / for skills"
                : "Type / for commands";
        }
        BindCrossSessionSend(viewModel);
        BuildSlashCommands();
        ApplyTranscriptView();
        RefreshStatusline();
        // The bar's fitting hysteresis is about one title narrowing and widening again;
        // carried across a switch it would let the last session's name decide this one's
        // first layout.
        SessionHeader.ResetFit();
        RestoreTranscriptViewport(viewModel);
        ViewModelBound?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs the /statusline command off the UI thread and renders its first
    /// output line under the composer. A missing command hides the row; a
    /// failing one leaves the previous line rather than flickering.
    /// </summary>
    public void RefreshStatusline()
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        var command = _services.UiSettings.Current.StatuslineCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            StatuslineText.Visibility = Visibility.Collapsed;
            StatuslineText.Text = "";
            return;
        }

        var model = _vm.CurrentModel;
        var context = Services.Statusline.BuildContextJson(
            model?.ModelId,
            model?.DisplayName,
            _vm.Session.WorkingDirectory,
            _vm.Session.Id,
            _vm.Gate.Mode.ToString(),
            CurrentEffortName());

        var token = ++_statuslineToken;
        _ = Task.Run(() => Services.Statusline.Run(command, context)).ContinueWith(
            task =>
            {
                // A session switch (or another refresh) already superseded this run.
                if (token != _statuslineToken || task.Result is not { Length: > 0 } line)
                {
                    return;
                }

                StatuslineText.Text = line;
                StatuslineText.Visibility = Visibility.Visible;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private int _statuslineToken;

    private void OnTurnFinishedRefreshStatusline(object? sender, EventArgs e) => RefreshStatusline();

    /// <summary>Raised after a session switch rebinds the surface to another view model.</summary>
    public event EventHandler? ViewModelBound;

    /// <summary>Transcript selection → "Send to side chat" (Code surface only).</summary>
    public event EventHandler<string>? SideChatSendRequested;

    /// <summary>/btw — ask in Side Chat right away (staged and sent, not just prefetched).</summary>
    public event EventHandler<string>? SideChatAskRequested;

    /// <summary>/resume — the window opens the session picker (sidebar search).</summary>
    public event EventHandler? ResumePickerRequested;

    private void OnQuestionOptionHover(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuestionOptionViewModel option })
        {
            option.NotifyHovered();
        }
    }

    private void OnPermissionModeChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _vm))
        {
            UpdateModeChip();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatViewModel.IsEmpty) or nameof(ChatViewModel.IsRunning))
        {
            UpdateLayoutState();
        }

        if (e.PropertyName == nameof(ChatViewModel.CurrentModel))
        {
            CancelChatGptEffortRead();
            UpdateModelChip();
        }

        if (e.PropertyName == nameof(ChatViewModel.ToolCallCount))
        {
            UpdateActivityChips();
        }

        if (e.PropertyName == nameof(ChatViewModel.ContextUsageText))
        {
            UpdateContextRing();
        }
    }

    // ---- user-message hover actions ----

    private void CopyMessage(UserMessageItem? item)
    {
        if (item is null)
        {
            return;
        }

        MarkdownView.TrySetClipboard(item.Text);
        item.IsCopied = true;
        var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            item.IsCopied = false;
        };
        revert.Start();
    }

    /// <summary>The reference CLI's /init prompt, verbatim (extracted from the installed bundle).</summary>
    private const string InitPrompt =
        "Please analyze this codebase and create a CLAUDE.md file, which will be given to future instances of " +
        "Claude Code to operate in this repository.\n\nWhat to add:\n" +
        "1. Commands that will be commonly used, such as how to build, lint, and run tests. Include the necessary " +
        "commands to develop in this codebase, such as how to run a single test.\n" +
        "2. High-level code architecture and structure so that future instances can be productive more quickly. " +
        "Focus on the \"big picture\" architecture that requires reading multiple files to understand.\n\n" +
        "Usage notes:\n" +
        "- If there's already a CLAUDE.md, suggest improvements to it.\n" +
        "- When you make the initial CLAUDE.md, do not repeat yourself and do not include obvious instructions " +
        "like \"Provide helpful error messages to users\", \"Write unit tests for all new utilities\", \"Never " +
        "include sensitive information (API keys, tokens) in code or commits\".\n" +
        "- Avoid listing every component or file structure that can be easily discovered.\n" +
        "- Don't include generic development practices.\n" +
        "- If there are Cursor rules (in .cursor/rules/ or .cursorrules) or Copilot rules (in " +
        ".github/copilot-instructions.md), make sure to include the important parts.\n" +
        "- If there is a README.md, make sure to include the important parts.";

    /// <summary>/rewind — the reference steps back to before the last user message.</summary>
    private async Task RewindLastPromptAsync()
    {
        var last = _vm?.Transcript.OfType<UserMessageItem>().LastOrDefault();
        if (last is null)
        {
            _vm?.Transcript.Add(new NoticeItem { Text = "Nothing to rewind — this session has no user message yet." });
            return;
        }

        await RewindMessageAsync(last);
    }

    private async Task RewindMessageAsync(UserMessageItem? item)
    {
        if (item is null || _vm is null || _rewindInFlight)
        {
            return;
        }

        // The rewind runs straight off the click, so the flag is what keeps a second click
        // from starting another one while the first is still restoring files.
        _rewindInFlight = true;
        try
        {
            var prompt = await _vm.RewindToAsync(item);
            UpdateLayoutState();
            if (prompt.Length > 0)
            {
                PrefillInput(prompt);
            }
        }
        finally
        {
            _rewindInFlight = false;
        }
    }

    private async Task BranchMessageAsync(UserMessageItem? item)
    {
        if (item is null || _vm is null)
        {
            return;
        }

        var (branch, prompt) = await _vm.BranchFromAsync(item);
        if (branch is not null)
        {
            LoadSession(branch);
            ViewModel.Transcript.Add(new NoticeItem
            {
                Text = "Branched into a new session — the original is untouched.",
            });
        }

        UpdateLayoutState();
        if (prompt.Length > 0)
        {
            PrefillInput(prompt);
        }
    }

    private void BuildSlashCommands()
    {
        _slashCommands =
        [
            new SlashCommand("background", "Send the app to the tray; everything keeps running", _ =>
            {
                (Window.GetWindow(this) as MainWindow)?.SendToBackground();
                return Task.CompletedTask;
            }, Aliases: ["bg"]),
            new SlashCommand("exit", "Quit Jarvis Code", _ =>
            {
                (Window.GetWindow(this) as MainWindow)?.ExitApplication();
                return Task.CompletedTask;
            }, Aliases: ["quit"]),
            new SlashCommand("tui", "Toggle fullscreen (chromeless) presentation", _ =>
            {
                if (Window.GetWindow(this) is MainWindow window && _services is not null)
                {
                    var fullscreen = !_services.UiSettings.Current.FullscreenMode;
                    window.SetFullscreen(fullscreen);
                    _vm?.Transcript.Add(new NoticeItem
                    {
                        Text = fullscreen ? "Fullscreen on — /tui again restores the window frame." : "Fullscreen off.",
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("heapdump", "Dump this process's memory to the Desktop for offline analysis", async _ =>
            {
                if (_vm is null)
                {
                    return;
                }

                var vm = _vm;
                var path = Services.HeapDump.DefaultPath();
                var error = await Task.Run(() => Services.HeapDump.Write(path));
                if (error is null)
                {
                    var size = new System.IO.FileInfo(path).Length / (1024.0 * 1024.0);
                    vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Heap dump written: {path} ({size:0.#} MB). Open it in Visual Studio or WinDbg.",
                    });
                }
                else
                {
                    vm.Transcript.Add(new NoticeItem { Text = $"Heap dump failed: {error}", IsError = true });
                }
            }),
            new SlashCommand("terminal-setup", "Make `jarvis-code` launchable from any terminal", _ =>
            {
                if (_vm is not null && _services is not null)
                {
                    _vm.FireHook(JarvisCode.Core.Hooks.HookEvent.Setup,
                        new System.Text.Json.Nodes.JsonObject { ["action"] = "terminal-setup" });
                    string report;
                    try
                    {
                        report = Services.TerminalSetup.Run(_services.Paths.Root, _services.Paths.ProfileName);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        report = $"Terminal setup failed: {ex.Message}";
                    }

                    _vm.Transcript.Add(new NoticeItem { Text = report, IsError = report.Contains("failed") });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("keybindings", "Open your keyboard shortcuts file", _ =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var path = _services.Paths.KeybindingsFile;
                try
                {
                    if (!File.Exists(path))
                    {
                        File.WriteAllText(path, Services.UserKeybindings.Template);
                    }

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Opened {path}. Edits apply after /keybindings reload (or a restart); " +
                               "user chords run in addition to the built-in ones.",
                    });
                    (Window.GetWindow(this) as MainWindow)?.ReloadUserKeybindings();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = $"Could not open the keybindings file: {ex.Message}", IsError = true });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("scroll-speed", "Adjust mouse wheel scroll speed (0.25–5)", arg =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim();
                if (trimmed.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Scroll speed is {_services.UiSettings.Current.ScrollSpeed:0.##}×. /scroll-speed <0.25-5> changes it.",
                    });
                }
                else if (double.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var speed) &&
                         speed is >= MinScrollSpeed and <= MaxScrollSpeed)
                {
                    _services.UiSettings.Current.ScrollSpeed = speed;
                    _services.UiSettings.Save();
                    _vm.Transcript.Add(new NoticeItem { Text = $"Scroll speed set to {speed:0.##}×." });
                }
                else
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = "Scroll speed must be a number between 0.25 and 5.",
                        IsError = true,
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("statusline", "Set a script whose output shows under the composer", arg =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim();
                var ui = _services.UiSettings.Current;
                if (trimmed.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = ui.StatuslineCommand.Length == 0
                            ? "No status line is configured. /statusline <shell command> sets one — it receives " +
                              "session context as JSON on stdin and its first output line shows under the composer."
                            : $"Status line command: {ui.StatuslineCommand}\n/statusline off removes it.",
                    });
                    return Task.CompletedTask;
                }

                ui.StatuslineCommand = trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
                _services.UiSettings.Save();
                RefreshStatusline();
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = ui.StatuslineCommand.Length == 0 ? "Status line removed." : "Status line set.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("autocompact", "Set how full the context gets before auto-summarizing", arg =>
            {
                if (_services is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim();
                _vm?.Transcript.Add(new NoticeItem
                {
                    Text = trimmed.Length == 0 ? AutoCompactStatus() : SetAutoCompactWindow(trimmed),
                });
                return Task.CompletedTask;
            }, "[auto|<tokens>]"),
            new SlashCommand("brief", "Toggle brief-only mode", _ =>
            {
                if (_vm is not null)
                {
                    _vm.BriefMode = !_vm.BriefMode;
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = _vm.BriefMode ? "Brief mode on — responses stay terse. /brief again turns it off." : "Brief mode off.",
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("output-style", "Choose how Jarvis writes: Proactive, Concise, Explanatory or Learning", arg =>
            {
                _vm?.Transcript.Add(new NoticeItem { Text = SetOutputStyle(arg.Trim()) });
                return Task.CompletedTask;
            }, "[<style>|default]"),
            new SlashCommand("batch", Services.BatchCommand.MenuDescription, arg =>
            {
                var composed = Services.BatchCommand.Compose(
                    arg, Services.BatchCommand.InsideGitRepository(_vm?.Session.WorkingDirectory));
                if (Services.BatchCommand.IsPrompt(composed))
                {
                    return SubmitTextAsync(composed);
                }

                _vm?.Transcript.Add(new NoticeItem { Text = composed, IsError = true });
                return Task.CompletedTask;
            }, Services.BatchCommand.ArgumentHint),
            new SlashCommand("debug", Services.DebugCommand.MenuDescription,
                arg => SubmitTextAsync(BuildDebugPrompt(arg)), Services.DebugCommand.ArgumentHint),
            new SlashCommand("release-notes", Services.ReleaseNotes.Description, async _ =>
            {
                if (_vm is null || _services is null)
                {
                    return;
                }

                var notes = await Services.ReleaseNotes.FetchAsync(_services.Http);
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = notes.Count == 0
                        ? Services.ReleaseNotes.NothingToShow()
                        : Services.ReleaseNotes.FormatAll(notes),
                });
            }),
            new SlashCommand("clear", "Start a new conversation", _ => { StartNew(); return Task.CompletedTask; }),
            new SlashCommand("compact", "Free up context by summarizing the conversation so far",
                arg => _vm!.CompactAsync(arg.Trim() is { Length: > 0 } instructions ? instructions : null),
                "<optional custom summarization instructions>"),
            new SlashCommand("context", "Show current context usage", _ =>
            {
                if (_services is not null && _vm is not null)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = Services.ContextReport.Render(
                            Services.ContextBreakdown.Compute(_services, _vm)),
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("copy", "Copy Jarvis's last response to clipboard (or /copy N for the Nth-latest)", arg =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                int nth = int.TryParse(arg.Trim(), out var parsed) && parsed > 0 ? parsed : 1;
                var responses = _vm.Transcript.OfType<AssistantTextItem>().ToList();
                if (responses.Count < nth)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = responses.Count == 0
                            ? "There is no response to copy yet."
                            : $"Only {responses.Count} response(s) in this session.",
                        IsError = true,
                    });
                    return Task.CompletedTask;
                }

                CopyResponse(responses[^nth]);
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = nth == 1 ? "Copied the last response." : $"Copied response #{nth} from the end.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("ide", "Connect an editor and inspect its current context", async argument =>
            {
                if (_vm is null) return;
                try
                {
                    _vm.Transcript.Add(new NoticeItem
                    { Text = await Services.IdeServices.CommandAsync(_vm.Session.WorkingDirectory, argument) });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                { _vm.Transcript.Add(new NoticeItem { Text = ex.Message, IsError = true }); }
            }),
            new SlashCommand("doctor", "Check this machine for problems that would break the app's features", async _ =>
            {
                if (_vm is null || _services is null)
                {
                    return;
                }

                var vm = _vm;
                var settings = _services.Settings.Current;
                var model = vm.CurrentModel;
                bool hasKey = model is not null &&
                    (!_services.Providers.Get(model.ProviderId).RequiresApiKey ||
                     settings.GetApiKey(model.ProviderId) is not null);
                var configured = JarvisCode.Core.Mcp.McpConfig.Load(
                    vm.Session.WorkingDirectory, _services.Paths.UserMcpFile).Count;
                var report = await Services.SessionInfo.RunDoctorAsync(
                    vm.Session.WorkingDirectory,
                    _services.Paths.Root,
                    settings.EnableWebSearch ? settings.SearxngBaseUrl : null,
                    hasKey,
                    model?.DisplayName,
                    configured,
                    _services.Mcp.ConnectedToolCounts.Count,
                    _services.Http,
                    model?.MaxContextTokens ?? 0);
                vm.Transcript.Add(new NoticeItem { Text = report });
            }),
            new SlashCommand("focus", "Toggle focus view: just your prompts and responses", _ =>
            {
                SetTranscriptView(_transcriptView == Panels.TranscriptViewMode.Summary
                    ? Panels.TranscriptViewMode.Normal
                    : Panels.TranscriptViewMode.Summary);
                _vm?.Transcript.Add(new NoticeItem
                {
                    Text = _transcriptView == Panels.TranscriptViewMode.Summary
                        ? "Focus view on — only prompts and responses are shown. /focus again shows everything."
                        : "Focus view off.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("goal", "Set a goal Jarvis checks before stopping (/goal clear removes it)", arg =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim();
                if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    _vm.SetSessionGoal(null);
                    _vm.Transcript.Add(new NoticeItem { Text = "Session goal cleared." });
                }
                else if (trimmed.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = _vm.SessionGoal is { Length: > 0 } current
                            ? $"Session goal: {current}\n/goal clear removes it."
                            : "No session goal is set. /goal <text> sets one.",
                    });
                }
                else
                {
                    _vm.SetSessionGoal(trimmed);
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Session goal set: {trimmed}\nJarvis checks it before ending each turn.",
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("hooks", "View the hook configuration for tool events", _ =>
            {
                if (_vm is not null && _services is not null)
                {
                    var cwd = _vm.Session.WorkingDirectory;
                    var runner = JarvisCode.Core.Hooks.HookRunner.Load(cwd, _services.Paths.UserHooksFile);
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = Services.SessionInfo.BuildHooksSummary(
                            runner,
                            System.IO.Path.Combine(cwd, JarvisCode.Core.Hooks.HookRunner.ProjectRelativePath),
                            _services.Paths.UserHooksFile),
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("insights", "A local report on your sessions and usage", async _ =>
            {
                if (_vm is null || _services is null)
                {
                    return;
                }

                var vm = _vm;
                var sessions = await _services.Sessions.ListAsync();
                vm.Transcript.Add(new NoticeItem
                {
                    Text = Services.SessionInfo.BuildInsights(_services.UsageStats.Load(), sessions),
                });
            }),
            new SlashCommand("memory", "Edit JARVIS.md files and memory settings", _ =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                return _vm.ShowMemoryFilesAsync(
                    JarvisCode.Core.Memory.ProjectMemory.DirectoryFor(
                        _services.Paths.MemoryRoot, _vm.Session.WorkingDirectory));
            }),
            new SlashCommand("export", "Export this conversation as markdown", _ => { ExportCurrent(); return Task.CompletedTask; }),
            new SlashCommand("code-review", "Find and verify bugs on the current branch or a pull request", arg => SubmitTextAsync(
                $"Review {(string.IsNullOrWhiteSpace(arg) ? "the changes on the current branch (or its pull request, if one exists)" : $"pull request #{arg.Trim()}")}. " +
                "Read the diff (gh pr view/diff when a PR exists, git diff against the main branch otherwise). Give a code review: " +
                "correctness bugs first, each with file and line references and the concrete failure scenario, then reuse/simplification " +
                "and efficiency notes. Verify each suspected bug against the surrounding code before reporting it, be direct about " +
                "severity, and end with a verdict on whether it is safe to merge.")),
            new SlashCommand("init", Services.ReferencePrompts.UseNewInit
                    ? "Initialize new CLAUDE.md file(s) and optional skills/hooks with codebase documentation"
                    : "Analyze the codebase and create or improve CLAUDE.md",
                _ =>
                {
                    _vm?.FireHook(JarvisCode.Core.Hooks.HookEvent.Setup,
                        new System.Text.Json.Nodes.JsonObject { ["action"] = "init" });
                    return SubmitTextAsync(Services.ReferencePrompts.UseNewInit ? Services.ReferencePrompts.NewInit : InitPrompt);
                }),
            new SlashCommand("model", "Open the model menu", _ => { OpenModelMenu(); return Task.CompletedTask; }),
            new SlashCommand("pause-memory", "Pause memory recall and the memory tool for this session", _ =>
            {
                if (_vm is not null)
                {
                    _vm.MemoryPaused = !_vm.MemoryPaused;
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = _vm.MemoryPaused
                            ? "Memory paused for this session — no recall, and the memory tool is withheld. /pause-memory again resumes it."
                            : "Memory resumed for this session.",
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("plan", "Enable plan mode or view the current session plan", _ =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                if (_vm.Gate.Mode != PermissionMode.Plan)
                {
                    // Remembered, so approving the plan returns to this mode.
                    _vm.Gate.EnterPlanMode();
                    UpdateModeChip();
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = "Plan mode on — Jarvis researches and presents a plan for approval before making changes.",
                    });
                }
                else
                {
                    var planPath = Services.PlanModeTools.PlanFilePath(_vm.Session.WorkingDirectory, _vm.Session.Id);
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = File.Exists(planPath)
                            ? $"Current plan ({planPath}):\n\n{File.ReadAllText(planPath).Trim()}"
                            : "Plan mode is already on; no plan has been written yet.",
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("resume", "Find a session to resume", _ =>
            {
                ResumePickerRequested?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }),
            new SlashCommand(
                "setup-bedrock", "Reconfigure Amazon Bedrock authentication, region, or model pins",
                _ => OpenProviderSettings("Bedrock", "its access key, secret, region and model ids")),
            new SlashCommand(
                "setup-vertex", "Reconfigure Google Vertex AI authentication, project, region, or model pins",
                _ => OpenProviderSettings("Vertex AI", "its credential, project, region and model ids")),
            new SlashCommand(
                "list-agents", "List subagents, teammates, and other Jarvis sessions you can message",
                _ => ShowAgentListingAsync(), Aliases: ["peers"]),
            new SlashCommand("voice", "Toggle voice mode", _ =>
            {
                OnMicClick(this, new RoutedEventArgs());
                return Task.CompletedTask;
            }),
            new SlashCommand("daemon", "Manage background services and routines", _ =>
            {
                (Window.GetWindow(this) as MainWindow ?? Application.Current.MainWindow as MainWindow)
                    ?.OpenRoutines();
                _vm?.Transcript.Add(new NoticeItem
                {
                    Text = "Scheduled routines are open. Work already running in this session — agents, " +
                           "shell tasks, loops and preview servers — lives in the Background tasks panel.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("recap", "Generate a one-line session recap now", _ => SubmitTextAsync(
                "Give a one-line recap of this session so far: what was asked, what was done, and the current " +
                "state — one sentence, nothing else.")),
            new SlashCommand("rewind", "Step back to before your last message", _ => RewindLastPromptAsync()),
            new SlashCommand("skill-doctor", "Show which loaded skills are unused and costing context", _ =>
            {
                if (_vm is not null && _services is not null)
                {
                    var ui = _services.UiSettings.Current;
                    var enabled = Services.SkillCatalog.Enabled(
                        Services.SkillCatalog.LoadAll(_vm.Session.WorkingDirectory, _services.Paths), ui);
                    var listed = Services.SkillCatalog.ForListing(enabled, ui, _vm.SkillState);
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = Services.SessionInfo.BuildSkillDoctor(
                            enabled, listed, ui, _services.Paths.SessionsDirectory),
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("status", "Show version, model, account and connection status", _ =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var settings = _services.Settings.Current;
                var model = _vm.CurrentModel;
                string? providerName = null;
                if (model is not null)
                {
                    try
                    {
                        providerName = _services.Providers.Get(model.ProviderId).DisplayName;
                    }
                    catch (JarvisCode.Core.Providers.ProviderException)
                    {
                    }
                }

                int cap = model is null ? 0
                    : settings.ModelContextOverrides.TryGetValue(model.ModelId, out var overridden)
                        ? overridden
                        : model.MaxContextTokens;
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = Services.SessionInfo.BuildStatus(new Services.SessionInfo.StatusData(
                        typeof(ChatSurface).Assembly.GetName().Version?.ToString(3) ?? "dev",
                        model?.DisplayName,
                        providerName,
                        CurrentEffortName(),
                        _vm.Gate.Mode.ToString(),
                        _vm.Session.WorkingDirectory,
                        _vm.Session.Id,
                        _vm.LastContextTokens,
                        cap,
                        _vm.ExtraTools.Count + _services.Mcp.Tools.Count,
                        _services.Mcp.ConnectedToolCounts)),
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("security-review", "Complete a security review of the pending changes on the current branch",
                _ => SubmitTextAsync(Services.ReferencePrompts.SecurityReview)),
            new SlashCommand("theme", "Open the theme gallery", _ => { ThemePickerRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }),
            new SlashCommand("config", "Open settings", _ => { SettingsRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }, Aliases: ["settings"]),
            new SlashCommand("help", "List the available commands", _ =>
            {
                var text = new System.Text.StringBuilder("Commands:\n");
                foreach (var command in _slashCommands.OrderBy(static c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var aliases = command.Aliases is { Length: > 0 }
                        ? " (" + string.Join(", ", command.Aliases.Select(static a => "/" + a)) + ")"
                        : "";
                    text.AppendLine($"  /{command.Name}{aliases} — {command.Description}");
                }

                text.Append("\nCtrl+K opens the command palette; Ctrl+/ lists the keyboard shortcuts.");
                _vm?.Transcript.Add(new NoticeItem { Text = text.ToString() });
                return Task.CompletedTask;
            }),
            new SlashCommand("version", "Show the app version", _ =>
            {
                _vm?.Transcript.Add(new NoticeItem
                {
                    Text = $"Jarvis Code {typeof(ChatSurface).Assembly.GetName().Version?.ToString(3) ?? "dev"} — " +
                           "feature surface audited against Claude Code CLI 2.1.247.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("context", "Show the context-window breakdown", _ => { ShowContextPopup(); return Task.CompletedTask; }),
            new SlashCommand("effort", "Set effort for the current model, or open its selector", SetEffortFromCommandAsync),
            new SlashCommand("rename", "Rename this session: /rename <new title>", async arg =>
            {
                if (_vm is null)
                {
                    return;
                }

                var title = arg.Trim();
                if (title.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"This session is “{_vm.Session.Title}”. /rename <new title> renames it.",
                    });
                    return;
                }

                await _vm.RenameAsync(title);
                SessionPersisted?.Invoke(this, EventArgs.Empty);
                _vm.Transcript.Add(new NoticeItem { Text = $"Renamed to “{title}”." });
            }),
            new SlashCommand("fork", "Copy this conversation into a new session and continue there", async _ =>
            {
                if (_vm is null || _services is null || !_vm.IsCodeSurface)
                {
                    _vm?.Transcript.Add(new NoticeItem { Text = "/fork works in Code sessions.", IsError = true });
                    return;
                }

                if (_vm.IsRunning)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "Wait for the current turn to finish before forking.", IsError = true });
                    return;
                }

                var (fork, error) = await Services.SessionActions.ForkAsync(_services, _vm.Session);
                if (fork is null)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = error ?? "Fork failed.", IsError = true });
                    return;
                }

                LoadSession(fork);
                SessionPersisted?.Invoke(this, EventArgs.Empty);
                ViewModel.Transcript.Add(new NoticeItem { Text = "Forked into a new session — the original is untouched." });
            }),
            new SlashCommand("branch", "Branch a new session from your latest message", async _ =>
            {
                if (_vm is null)
                {
                    return;
                }

                var last = _vm.Transcript.OfType<UserMessageItem>().LastOrDefault();
                if (last is null)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "Nothing to branch from yet.", IsError = true });
                    return;
                }

                await BranchMessageAsync(last);
            }),
            new SlashCommand("stop", "Stop the current turn", _ =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                if (_vm.IsRunning)
                {
                    _vm.CancelTurn();
                    _vm.Transcript.Add(new NoticeItem { Text = "Stopping." });
                }
                else
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "Nothing is running." });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("tasks", "Open the background runs panel", _ =>
            {
                if (_vm is { IsCodeSurface: true })
                {
                    PanelRequested?.Invoke(this, "runs");
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("diff", "Open the changes panel", _ =>
            {
                if (_vm is { IsCodeSurface: true })
                {
                    PanelRequested?.Invoke(this, "changes");
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("add-dir", "Add a working directory: /add-dir <path>, or no argument for the picker", arg =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim().Trim('"');
                if (trimmed.Length == 0)
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add a working directory" };
                    if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                    {
                        _vm.AddAdditionalDirectory(dialog.FolderName);
                        UpdateContextBar();
                    }

                    return Task.CompletedTask;
                }

                var full = System.IO.Path.GetFullPath(trimmed, _vm.Session.WorkingDirectory);
                if (System.IO.Directory.Exists(full))
                {
                    _vm.AddAdditionalDirectory(full);
                    UpdateContextBar();
                    _vm.Transcript.Add(new NoticeItem { Text = $"Added {full} as a working directory." });
                }
                else
                {
                    _vm.Transcript.Add(new NoticeItem { Text = $"Directory not found: {full}", IsError = true });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("cd", "Change this session's working directory", arg =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim().Trim('"');
                if (trimmed.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = $"Working directory: {_vm.Session.WorkingDirectory}" });
                    return Task.CompletedTask;
                }

                if (_vm.IsRunning)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "Wait for the current turn to finish before changing directory.", IsError = true });
                    return Task.CompletedTask;
                }

                var full = System.IO.Path.GetFullPath(trimmed, _vm.Session.WorkingDirectory);
                if (!System.IO.Directory.Exists(full))
                {
                    _vm.Transcript.Add(new NoticeItem { Text = $"Directory not found: {full}", IsError = true });
                    return Task.CompletedTask;
                }

                _vm.SetWorkingDirectory(full);
                UpdateContextBar();
                _ = RefreshGitBannerAsync();
                _vm.Transcript.Add(new NoticeItem { Text = $"Working directory is now {full}." });
                return Task.CompletedTask;
            }),
            new SlashCommand("usage", "Show session and recent token usage", _ =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var model = _vm.CurrentModel;
                int cap = model is null ? 0
                    : _services.Settings.Current.ModelContextOverrides.TryGetValue(model.ModelId, out var overridden)
                        ? overridden
                        : model.MaxContextTokens;
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = Services.SessionInfo.BuildUsage(_services.UsageStats.Load(), _vm.LastContextTokens, cap),
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("mcp", "Manage MCP servers (Customize › Connectors)", _ =>
            {
                CustomizeNavigateRequested?.Invoke(this, "connectors");
                return Task.CompletedTask;
            }),
            new SlashCommand("mcp-auth", "Sign in to a remote MCP server: /mcp-auth <server>", async arg =>
            {
                if (_vm is null || _services is null)
                {
                    return;
                }

                var server = arg.Trim();
                if (server.Length == 0)
                {
                    var remotes = JarvisCode.Core.Mcp.McpConfig
                        .Load(_vm.Session.WorkingDirectory, _services.Paths.UserMcpFile)
                        .Where(static c => c.IsRemote)
                        .Select(static c => c.Name)
                        .ToList();
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = remotes.Count == 0
                            ? "No remote MCP servers are configured. Add one with a URL in Customize › Connectors."
                            : $"Remote MCP servers: {string.Join(", ", remotes)}. /mcp-auth <server> signs in via the browser.",
                    });
                    return;
                }

                var vm = _vm;
                try
                {
                    vm.Transcript.Add(new NoticeItem { Text = $"Opening the browser to sign in to '{server}'…" });
                    await _services.Mcp.AuthorizeAsync(
                        server, vm.Session.WorkingDirectory, _services.Paths.UserMcpFile, CancellationToken.None);
                    vm.Transcript.Add(new NoticeItem { Text = $"Signed in to '{server}'. Reconnecting…" });
                    vm.RefreshMcp();
                }
                catch (JarvisCode.Core.Mcp.McpException ex)
                {
                    vm.Transcript.Add(new NoticeItem { Text = ex.Message, IsError = true });
                }
            }),
            new SlashCommand("skills", "Manage skills (Customize › Skills)", _ =>
            {
                CustomizeNavigateRequested?.Invoke(this, "skills");
                return Task.CompletedTask;
            }),
            new SlashCommand("plugin", "Manage plugins (Customize › Personal plugins)", _ =>
            {
                CustomizeNavigateRequested?.Invoke(this, "plugins");
                return Task.CompletedTask;
            }),
            new SlashCommand("permissions", "Manage allow and deny tool permission rules", _ =>
            {
                (Window.GetWindow(this) as MainWindow)?.OpenSettings("Settings", "Permissions");
                return Task.CompletedTask;
            }),
            new SlashCommand("reload-skills", "Pick up skills added or changed on disk", _ =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                BuildSlashCommands();
                int count = 0;
                try
                {
                    count = Services.SkillCatalog.Enabled(
                        Services.SkillCatalog.LoadAll(_vm.Session.WorkingDirectory, _services.Paths),
                        _services.UiSettings.Current).Count;
                }
                catch (System.IO.IOException)
                {
                }

                _vm.Transcript.Add(new NoticeItem
                {
                    Text = $"Skills reloaded — {count} available as slash commands. Turns always read skills fresh from disk.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("reload-plugins", "Activate plugin changes in this session", _ =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                _vm.RefreshMcp();
                BuildSlashCommands();
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = "Plugins reloaded — MCP connections refreshed; new turns load plugin skills, hooks and agents from disk.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("color", "Set the composer accent for this session: /color <name|#hex>, /color off resets", arg =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim();
                var colors = _services.UiSettings.Current.SessionPromptColors;
                if (trimmed.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = colors.TryGetValue(_vm.Session.Id, out var current)
                            ? $"This session's composer accent is {current}. /color off resets it."
                            : "This session uses the theme's composer accent. /color <name|#hex> overrides it.",
                    });
                }
                else if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    colors.Remove(_vm.Session.Id);
                    _services.UiSettings.Save();
                    ApplyPromptColor();
                    _vm.Transcript.Add(new NoticeItem { Text = "Composer accent reset to the theme." });
                }
                else if (TryParseColor(trimmed) is null)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"'{trimmed}' is not a color. Use a name (teal) or hex (#28a0a0).",
                        IsError = true,
                    });
                }
                else
                {
                    colors[_vm.Session.Id] = trimmed;
                    _services.UiSettings.Save();
                    ApplyPromptColor();
                    _vm.Transcript.Add(new NoticeItem { Text = $"Composer accent set to {trimmed} for this session." });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("wellbeing", "Break reminders: /wellbeing <minutes>, /wellbeing off", arg =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim();
                var settings = _services.UiSettings.Current;
                if (trimmed.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = settings.WellbeingMinutes > 0
                            ? $"Break reminders every {settings.WellbeingMinutes} minutes. /wellbeing off stops them."
                            : "Break reminders are off. /wellbeing <minutes> reminds you to step away (tray notification).",
                    });
                }
                else if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) || trimmed == "0")
                {
                    settings.WellbeingMinutes = 0;
                    _services.UiSettings.Save();
                    _vm.Transcript.Add(new NoticeItem { Text = "Break reminders off." });
                }
                else if (int.TryParse(trimmed, out var minutes) && minutes is >= 5 and <= 480)
                {
                    settings.WellbeingMinutes = minutes;
                    _services.UiSettings.Save();
                    _vm.Transcript.Add(new NoticeItem { Text = $"Break reminder every {minutes} minutes." });
                }
                else
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "Give minutes between 5 and 480, or off.", IsError = true });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("powerup", "Discover features through quick lessons (/powerup reset starts over)", arg =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var settings = _services.UiSettings.Current;
                if (arg.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    settings.PowerupLessonIndex = 0;
                }

                _vm.Transcript.Add(new NoticeItem { Text = Services.Powerup.Render(settings.PowerupLessonIndex) });
                settings.PowerupLessonIndex = (settings.PowerupLessonIndex + 1) % Services.Powerup.Lessons.Count;
                _services.UiSettings.Save();
                return Task.CompletedTask;
            }),
            new SlashCommand("advisor", "Let Jarvis consult a stronger model: /advisor <model id>, /advisor off", arg =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var trimmed = arg.Trim();
                var settings = _services.UiSettings.Current;
                if (trimmed.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = settings.AdvisorModelId is { Length: > 0 } current
                            ? $"Advisor: {current} — Code turns carry an advisor tool for second opinions. /advisor off removes it."
                            : "No advisor is configured. /advisor <model id> gives Code turns an advisor tool for key decisions.",
                    });
                }
                else if (trimmed.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    settings.AdvisorModelId = null;
                    _services.UiSettings.Save();
                    _vm.Transcript.Add(new NoticeItem { Text = "Advisor off." });
                }
                else if (JarvisCode.Core.Settings.ModelCatalog.Find(_services.Settings.Models, trimmed) is { } model)
                {
                    settings.AdvisorModelId = model.ModelId;
                    _services.UiSettings.Save();
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Advisor set to {model.DisplayName} — Jarvis can consult it at key moments through the advisor tool.",
                    });
                }
                else
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Unknown model id '{trimmed}'. Available: " +
                               string.Join(", ", _services.Settings.Models.Select(static m => m.ModelId)),
                        IsError = true,
                    });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("plugin-types", "Write jarvis-code-mcp.d.ts typing the connected MCP tools", _ =>
            {
                if (_vm is null || _services is null)
                {
                    return Task.CompletedTask;
                }

                var tools = _services.Mcp.Tools.Select(static t => (t.Name, t.InputSchema)).ToList();
                if (tools.Count == 0)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "No MCP tools are connected — nothing to type." });
                    return Task.CompletedTask;
                }

                var path = System.IO.Path.Combine(_vm.Session.WorkingDirectory, Services.McpTypings.FileName);
                try
                {
                    File.WriteAllText(path, Services.McpTypings.Generate(tools));
                    _vm.Transcript.Add(new NoticeItem { Text = $"Wrote {path} — input types for {tools.Count} MCP tool(s)." });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = $"Could not write the typings: {ex.Message}", IsError = true });
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("btw", "Ask a quick side question in Side Chat: /btw <question>", arg =>
            {
                if (_vm is not { IsCodeSurface: true })
                {
                    _vm?.Transcript.Add(new NoticeItem { Text = "/btw works in Code sessions (Side Chat).", IsError = true });
                    return Task.CompletedTask;
                }

                var question = arg.Trim();
                if (question.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "/btw <question> asks in Side Chat without touching this conversation." });
                }
                else
                {
                    SideChatAskRequested?.Invoke(this, question);
                }

                return Task.CompletedTask;
            }),
            new SlashCommand("subtask", "Run a task with this conversation's full context in a background session: /subtask <prompt>", async arg =>
            {
                if (_vm is null || _services is null || !_vm.IsCodeSurface || _sessions is null)
                {
                    _vm?.Transcript.Add(new NoticeItem { Text = "/subtask works in Code sessions.", IsError = true });
                    return;
                }

                var prompt = arg.Trim();
                if (prompt.Length == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = "/subtask <prompt> forks this conversation and runs the prompt there in the background, full context included.",
                    });
                    return;
                }

                var (fork, error) = await Services.SessionActions.ForkAsync(_services, _vm.Session);
                if (fork is null)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = error ?? "Could not start the subtask.", IsError = true });
                    return;
                }

                fork.Title = $"Subtask: {(prompt.Length > 40 ? prompt[..40] + "…" : prompt)}";
                await _services.Sessions.SaveAsync(fork);
                var worker = _sessions.ForSession(fork);
                _ = worker.SendAsync(prompt);
                SessionPersisted?.Invoke(this, EventArgs.Empty);
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = $"Subtask running in “{fork.Title}” with this conversation's full context — its unread dot appears in the sidebar when it finishes.",
                });
            }),
            new SlashCommand("loop",
                "Run a prompt or slash command on a recurring interval (e.g. /loop 5m /foo). Omit the interval to let the model self-pace.",
                async arg =>
            {
                if (_vm is null || !_vm.IsCodeSurface)
                {
                    _vm?.Transcript.Add(new NoticeItem { Text = "/loop works in Code sessions.", IsError = true });
                    return;
                }

                // The reference parsing rules: a leading `^\d+[smhd]$` token, a
                // trailing "every N unit" clause, or dynamic mode. An empty prompt
                // is the autonomous default (loop.md when the project has one).
                var parsed = Services.LoopPrompts.Parse(arg);
                var cwd = _vm.Session.WorkingDirectory;

                if (parsed.Prompt.Length == 0)
                {
                    var file = Services.LoopPrompts.ReadLoopFile(cwd);
                    if (parsed.Interval is null)
                    {
                        // Bare /loop: run the autonomous check now and self-pace
                        // via ScheduleWakeup — nothing is scheduled host-side.
                        await SubmitTextAsync(Services.LoopPrompts.BuildAutonomousDynamicExpansion(file));
                        return;
                    }

                    int autoId = _vm.StartLoop(parsed.Interval.Value,
                        file is null ? Services.LoopPrompts.AutonomousSentinel : Services.LoopPrompts.LoopFileSentinel);
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Loop {autoId} started — {(file is null ? "the autonomous check" : $"the tasks in {file.Path}")} " +
                               $"run every {parsed.IntervalText} while this session is open (ticks during a running " +
                               $"turn are skipped). /loops stop {autoId} ends it.",
                    });
                    await SubmitTextAsync(Services.LoopPrompts.BuildAutonomousFixedExpansion(file, parsed.IntervalText!));
                    return;
                }

                if (parsed.Interval is { } interval)
                {
                    int id = _vm.StartLoop(interval, parsed.Prompt);
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"Loop {id} started — the prompt repeats every {parsed.IntervalText} " +
                               "while this session is open (ticks during a running turn are skipped). /loops stop " +
                               $"{id} ends it.",
                    });
                    await SubmitTextAsync(parsed.Prompt);
                    return;
                }

                await SubmitTextAsync(Services.LoopPrompts.BuildDynamicExpansion(parsed.Prompt));
            }),
            new SlashCommand("tasks", "Show the session task board", _ =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                var tasks = _vm.Tasks.Visible();
                if (tasks.Count == 0)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "No tasks found" });
                }
                else
                {
                    var lines = tasks.Select(task =>
                    {
                        var owner = string.IsNullOrEmpty(task.Owner) ? "" : $" ({task.Owner})";
                        var blockers = _vm.Tasks.OpenBlockers(task);
                        var blocked = blockers.Count == 0
                            ? ""
                            : $" [blocked by {string.Join(", ", blockers.Select(b => "#" + b))}]";
                        return $"#{task.Id} [{JarvisCode.Core.Agent.TaskBoard.StatusName(task.Status)}] " +
                            $"{task.Subject}{owner}{blocked}";
                    });
                    _vm.Transcript.Add(new NoticeItem { Text = string.Join("\n", lines) });
                }

                PanelRequested?.Invoke(this, "tasks");
                return Task.CompletedTask;
            }),
            new SlashCommand("teammates", "List the session's named agents and their state", _ =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                var team = _vm.Teams.Read(_vm.Session.Id);
                var running = _vm.Workers.RunningNames();
                if (team is null || team.Members.Count == 0)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = "No teammates in this session. Spawn one with Agent " +
                            "(name: \"analyzer\", run_in_background: true).",
                    });
                    return Task.CompletedTask;
                }

                var rows = team.Members.Select(member =>
                {
                    var state = running.Contains(member.Name, StringComparer.OrdinalIgnoreCase)
                        ? "working"
                        : member.IsActive ? "idle" : "done";
                    return $"{member.Name} · {member.Mode} · {state}";
                });
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = $"Team \"{_vm.Session.Id}\" · lead is \"team-lead\"\n" + string.Join("\n", rows),
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("workflows", "List saved workflows and recent runs", _ =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                var store = new JarvisCode.Core.Agent.WorkflowStore(
                    _vm.Session.WorkingDirectory, _services!.Paths.WorkflowRunsDirectory);
                var names = store.Names();
                var runsRoot = System.IO.Path.Combine(_services!.Paths.WorkflowRunsDirectory, _vm.Session.Id);
                var runs = System.IO.Directory.Exists(runsRoot)
                    ? System.IO.Directory.EnumerateDirectories(runsRoot)
                        .OrderByDescending(System.IO.Directory.GetLastWriteTimeUtc)
                        .Take(5)
                        .Select(System.IO.Path.GetFileName)
                        .ToList()
                    : [];

                var report = new System.Text.StringBuilder();
                report.AppendLine(_services!.UiSettings.Current.DynamicWorkflowsEnabled
                    ? "Dynamic workflows: on"
                    : "Dynamic workflows: off (the Workflow tool is not offered this session)");
                report.AppendLine(names.Count == 0
                    ? "Saved workflows: none (.jarvis/workflows/*.js)"
                    : "Saved workflows: " + string.Join(", ", names));
                report.Append(runs.Count == 0
                    ? "Recent runs: none"
                    : "Recent runs: " + string.Join(", ", runs));
                _vm.Transcript.Add(new NoticeItem { Text = report.ToString() });
                return Task.CompletedTask;
            }),
            new SlashCommand("loops", "List running loops, or /loops stop <id|all>", arg =>
            {
                if (_vm is null)
                {
                    return Task.CompletedTask;
                }

                var parts = arg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 1 && parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length >= 2 && parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        int stopped = _vm.StopAllLoops();
                        _vm.Transcript.Add(new NoticeItem { Text = $"Stopped {stopped} loop(s)." });
                    }
                    else if (parts.Length >= 2 && int.TryParse(parts[1], out var id))
                    {
                        bool stoppedOne = _vm.StopLoop(id);
                        _vm.Transcript.Add(new NoticeItem
                        {
                            Text = stoppedOne ? $"Loop {id} stopped." : $"No loop {id} in this session.",
                            IsError = !stoppedOne,
                        });
                    }
                    else
                    {
                        _vm.Transcript.Add(new NoticeItem { Text = "/loops stop <id|all>", IsError = true });
                    }

                    return Task.CompletedTask;
                }

                var loops = _vm.Loops;
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = loops.Count == 0
                        ? "No loops are running in this session. /loop starts one."
                        : "Loops:\n" + string.Join('\n', loops.Select(static l =>
                            $"  {l.Id} · {(l.Dynamic ? "self-paced (ScheduleWakeup)" : $"every {l.Interval}")}" +
                            $" · {(l.Prompt.Length > 60 ? l.Prompt[..60] + "…" : l.Prompt)}")) +
                          "\n/loops stop <id|all> ends them.",
                });
                return Task.CompletedTask;
            }),
            new SlashCommand("import", "Import guidance from other AI coding agents' config files", _ => SubmitTextAsync(
                "Import configuration from other AI coding agents in this project. Look for: .cursorrules and " +
                ".cursor/rules/ (Cursor), .github/copilot-instructions.md (Copilot), AGENTS.md, .windsurfrules and " +
                ".windsurf/rules/ (Windsurf), .clinerules (Cline), and GEMINI.md. Summarize what you find, then merge " +
                "the durable, project-specific guidance into CLAUDE.md (create it if missing) — skip generic " +
                "boilerplate, skip anything CLAUDE.md already says, and note which file each imported rule came from. " +
                "If none of these files exist, say so and stop.")),
            new SlashCommand("auto-mode-setup", "Teach auto mode about this environment (propose permission rules)", _ =>
            {
                _vm?.FireHook(JarvisCode.Core.Hooks.HookEvent.Setup,
                    new System.Text.Json.Nodes.JsonObject { ["action"] = "auto-mode-setup" });
                return SubmitTextAsync(
                "Teach auto mode about this environment. Inspect the project — build system, test runner, package " +
                "manager, scripts — and propose permission allow-rules for the commands a session here runs " +
                "constantly (build, test, lint, status/list operations). Present the proposed rules with a short " +
                "rationale each, use AskUserQuestion to let me pick which to apply, then write the approved rules " +
                "into this project's .jarvis/settings.json permission rules (create the file if needed, preserving " +
                "anything already there) and report what changed. Only propose read-only or clearly-safe commands.");
            }),
        ];

        // Custom commands ride the skill namespace (registered below with the
        // skills, like the reference's one command list).

        // MCP prompts surface as /mcp__server__prompt on the Code surface, like the
        // reference. The typed argument text feeds the prompt's first declared argument.
        if (_vm is { IsCodeSurface: true } && _services is not null)
        {
            foreach (var (server, prompt) in _services.Mcp.Prompts)
            {
                var name = JarvisCode.Core.Mcp.McpToolAdapter.BuildToolName(server, prompt.Name);
                if (_slashCommands.Any(c => c.Matches(name)))
                {
                    continue;
                }

                var (capturedServer, capturedPrompt) = (server, prompt);
                _slashCommands.Add(new SlashCommand(
                    name,
                    string.IsNullOrWhiteSpace(prompt.Description)
                        ? $"Prompt from the {server} MCP server"
                        : prompt.Description!,
                    args => RunMcpPromptAsync(capturedServer, capturedPrompt, args)));
            }
        }

        // Skills (and legacy commands) are surface-filtered like the reference:
        // the Code composer lists the user-invocable ones; Chat keeps only the
        // names so a typed /skill gets the "isn't available in Chat" notice.
        _chatSkillNames.Clear();
        if (_vm is not null && _services is not null)
        {
            try
            {
                var ui = _services.UiSettings.Current;
                var enabled = Services.SkillCatalog.Enabled(
                    Services.SkillCatalog.LoadAll(_vm.Session.WorkingDirectory, _services.Paths), ui);
                if (_vm.IsCodeSurface)
                {
                    foreach (var skill in Services.SkillCatalog.ForComposer(enabled))
                    {
                        if (_slashCommands.Any(c => c.Matches(skill.Name)))
                        {
                            continue;
                        }

                        var captured = skill;
                        _slashCommands.Add(new SlashCommand(
                            skill.Name,
                            skill.Description,
                            args => RunSkillCommandAsync(captured, args),
                            captured.ArgumentHint));
                    }
                }
                else
                {
                    foreach (var skill in enabled)
                    {
                        _chatSkillNames.Add(skill.Name);
                    }
                }
            }
            catch (System.IO.IOException)
            {
                // Skills are optional.
            }
        }
    }

    /// <summary>Skill names known to the catalog while on the Chat surface (skills are Code-only, reference behavior).</summary>
    private readonly HashSet<string> _chatSkillNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alternates the composer placeholder between the reference's commands/skills variants.</summary>
    private static int _placeholderRotation;

    /// <summary>The stacked-command cap (reference De = 5).</summary>
    private const int StackedCommandLimit = 5;

    /// <summary>
    /// A typed /skill (or legacy command): peels stacked commands, runs the
    /// user_prompt_expansion hooks, renders each body (arguments, ${CLAUDE_*}
    /// variables, !`cmd` preprocessing through the permission gate), applies
    /// the skill's session effects, and sends envelope+body pairs.
    /// </summary>
    private async Task RunSkillCommandAsync(JarvisCode.Core.Customization.SkillDefinition skill, string args)
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        var vm = _vm;
        var services = _services;
        var cwd = vm.Session.WorkingDirectory;
        var typed = args.Length > 0 ? $"/{skill.Name} {args}" : $"/{skill.Name}";

        // Stacking: peel further leading /commands out of the arguments, up to
        // the reference cap; the remaining text is passed as arguments.
        var stack = new List<JarvisCode.Core.Customization.SkillDefinition> { skill };
        var rest = args;
        bool stackCapped = false;
        try
        {
            var ui = services.UiSettings.Current;
            var enabled = Services.SkillCatalog.Enabled(
                Services.SkillCatalog.LoadAll(cwd, services.Paths), ui);
            while (rest.TrimStart().StartsWith('/'))
            {
                if (stack.Count >= StackedCommandLimit)
                {
                    stackCapped = true;
                    break;
                }
                var token = rest.TrimStart();
                var split = token.Split(' ', 2);
                var candidate = enabled.FirstOrDefault(s =>
                    s.UserInvocable && !s.Fork &&
                    s.Name.Equals(split[0].TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                if (candidate is null)
                {
                    break;
                }
                stack.Add(candidate);
                rest = split.Length > 1 ? split[1] : "";
            }
        }
        catch (System.IO.IOException)
        {
        }
        var stackArgs = stack.Count > 1 ? rest : args;

        // The user_prompt_expansion hooks run per command; the first block
        // stops the send, a stacked block skips just that command.
        var hooks = JarvisCode.Core.Hooks.HookRunner.Load(
            cwd, services.Paths.UserHooksFile,
            Services.PluginLibrary.LoadUser(services.Paths).HookFiles);
        if (vm.SkillState.ExtraHooks.Count > 0)
        {
            hooks = hooks.WithExtra(vm.SkillState.ExtraHooks);
        }

        var parts = new List<SkillExpansionPart>();
        var notices = new List<string>();
        var hookContexts = new List<string>();
        for (int i = 0; i < stack.Count; i++)
        {
            var command = stack[i];
            var commandArgs = stackArgs;
            if (hooks.Has(JarvisCode.Core.Hooks.HookEvent.UserPromptExpansion))
            {
                var verdict = await hooks.RunUserPromptExpansionAsync(
                    "slash_command", command.Name, commandArgs, command.Source, typed, CancellationToken.None);
                if (verdict.AdditionalContext is { Length: > 0 } extraContext)
                {
                    hookContexts.Add(extraContext);
                }
                if (!verdict.Allowed)
                {
                    if (i == 0)
                    {
                        vm.Transcript.Add(new NoticeItem
                        {
                            Text = $"UserPromptExpansion operation blocked by hook:\n{verdict.BlockReason}" +
                                   $"\n\nOriginal prompt: {typed}",
                            IsError = true,
                        });
                        return;
                    }
                    notices.Add($"Stacked skill /{command.Name} blocked by UserPromptExpansion hook");
                    continue;
                }
            }

            string body;
            try
            {
                var gate = vm.Gate;
                var timeout = TimeSpan.FromSeconds(Math.Max(5, services.Settings.Current.ShellTimeoutSeconds));
                body = await Task.Run(() => JarvisCode.Core.Customization.SkillInvocation.RenderBody(
                    command, commandArgs,
                    new JarvisCode.Core.Customization.SkillInvocation.RenderOptions
                    {
                        ProjectDirectory = cwd,
                        SessionId = vm.Session.Id,
                        Effort = Services.EffortLevels.ResolveEffort(
                            services.Settings.Current.ThinkingEffortName).ToString().ToLowerInvariant(),
                        RunShellCommand = shellCommand => Services.SkillShellRunner.Run(
                            shellCommand, command.Shell, cwd, gate, timeout),
                    }));
            }
            catch (InvalidOperationException ex)
            {
                if (i == 0)
                {
                    vm.Transcript.Add(new NoticeItem { Text = ex.Message, IsError = true });
                    return;
                }
                notices.Add($"Stacked skill /{command.Name} failed to load: {ex.Message}");
                continue;
            }

            Services.SkillCatalog.ApplyInvocationEffects(
                services.UiSettings, vm.Gate, vm.SkillState, command, cwd);
            vm.SkillState.Tracker.Record(command.Name, command.FilePath, body);
            parts.Add(new SkillExpansionPart(
                JarvisCode.Core.Customization.SkillInvocation.Envelope(command, commandArgs), body));
        }

        if (stackCapped)
        {
            notices.Add($"Stacked command limit ({StackedCommandLimit}) reached — remaining input passed as arguments");
        }

        foreach (var notice in notices)
        {
            vm.Transcript.Add(new NoticeItem { Text = notice });
        }

        if (parts.Count > 0)
        {
            await vm.SendSkillExpansionAsync(
                typed, parts, hookContexts.Count > 0 ? string.Join("\n", hookContexts) : null);
        }
    }

    /// <summary>Expands an MCP server prompt and sends the result as the user message.</summary>
    private async Task RunMcpPromptAsync(
        string server, JarvisCode.Core.Mcp.McpPromptDescriptor prompt, string args)
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        var arguments = new System.Text.Json.Nodes.JsonObject();
        if (!string.IsNullOrWhiteSpace(args) && prompt.Arguments.Count > 0)
        {
            arguments[prompt.Arguments[0].Name] = args.Trim();
        }

        try
        {
            var expanded = await _services.Mcp.GetPromptAsync(server, prompt.Name, arguments, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = $"The {server} prompt '{prompt.Name}' produced no content.",
                    IsError = true,
                });
                return;
            }

            var typed = args.Length > 0 ? $"/{prompt.Name} {args}" : $"/{prompt.Name}";
            var hooks = JarvisCode.Core.Hooks.HookRunner.Load(
                _vm.Session.WorkingDirectory, _services.Paths.UserHooksFile,
                Services.PluginLibrary.LoadUser(_services.Paths).HookFiles);
            if (hooks.Has(JarvisCode.Core.Hooks.HookEvent.UserPromptExpansion))
            {
                var verdict = await hooks.RunUserPromptExpansionAsync(
                    "mcp_prompt", prompt.Name, args, "mcp", typed, CancellationToken.None);
                if (!verdict.Allowed)
                {
                    _vm.Transcript.Add(new NoticeItem
                    {
                        Text = $"UserPromptExpansion operation blocked by hook:\n{verdict.BlockReason}" +
                               $"\n\nOriginal prompt: {typed}",
                        IsError = true,
                    });
                    return;
                }
            }

            await _vm.SendAsync(expanded);
        }
        catch (JarvisCode.Core.Mcp.McpException ex)
        {
            _vm.Transcript.Add(new NoticeItem { Text = ex.Message, IsError = true });
        }
    }

    /// <summary>
    /// SendMessage's cross-session half: resolves another local Code session by
    /// title or id prefix and delivers the message as a task notification there.
    /// </summary>
    private void BindCrossSessionSend(ChatViewModel viewModel)
    {
        if (_services is null || _sessions is null || !viewModel.IsCodeSurface)
        {
            return;
        }

        var services = _services;
        var sessions = _sessions;
        viewModel.CrossSessionSend = async (target, message) =>
        {
            try
            {
                var mailbox = viewModel.EnsureMailbox();
                if (mailbox.ListPeers().Any(p => p.SessionId.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
                    p.Title.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    return await mailbox.SendAsync(target, message);
                var summaries = await services.Sessions.ListAsync();
                var matches = summaries
                    .Where(s => s.Id != viewModel.Session.Id &&
                        (s.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
                         s.Title.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matches.Count == 0)
                {
                    return $"No other local session matches '{target}' (ListAgents shows them).";
                }

                if (matches.Count > 1)
                {
                    return $"'{target}' matches {matches.Count} sessions " +
                           $"({string.Join(", ", matches.Select(static m => $"\"{m.Title}\""))}) — use an id prefix.";
                }

                var session = await services.Sessions.LoadAsync(matches[0].Id);
                if (session is null)
                {
                    return $"Session \"{matches[0].Title}\" could not be loaded.";
                }

                var source = viewModel.Session.Title;
                await Dispatcher.InvokeAsync(() =>
                {
                    var targetVm = sessions.ForSession(session);
                    targetVm.DeliverTaskNotification(
                        "session-message", "message", $"Message from session \"{source}\"", message);
                });
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Could not deliver the message: {ex.Message}";
            }
        };

        // notify_when_idle: the same address resolution, ending in a subscription
        // on the other session's view model rather than a message.
        viewModel.CrossSessionIdleSubscribe = async target =>
        {
            try
            {
                var mailbox = viewModel.EnsureMailbox();
                if (mailbox.ListPeers().Any(p => p.SessionId.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
                    p.Title.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    return await mailbox.SubscribeAsync(target);
                var summaries = await services.Sessions.ListAsync();
                if (viewModel.Session.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
                    viewModel.Session.Title.Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    return $"notify_when_idle: \"{target}\" is THIS session — nothing was subscribed; you already know when your own turn ends.";
                }

                var matches = summaries
                    .Where(s => s.Id != viewModel.Session.Id &&
                        (s.Id.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
                         s.Title.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matches.Count == 0)
                {
                    return "notify_when_idle: no session is listening at that address any more; nothing was " +
                           "subscribed, and any earlier idle subscription to it is void";
                }

                if (matches.Count > 1)
                {
                    return $"'{target}' matches {matches.Count} sessions " +
                           $"({string.Join(", ", matches.Select(static m => $"\"{m.Title}\""))}) — use an id prefix.";
                }

                var session = await services.Sessions.LoadAsync(matches[0].Id);
                if (session is null)
                {
                    return $"Session \"{matches[0].Title}\" could not be loaded.";
                }

                return await Dispatcher.InvokeAsync(() =>
                    sessions.ForSession(session).AddIdleSubscriber(viewModel, session.Title));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Could not subscribe: {ex.Message}";
            }
        };
    }

    private void ExportCurrent()
    {
        if (_vm is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export transcript",
            FileName = $"{_vm.Session.Title}.md",
            Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            File.WriteAllText(dialog.FileName, JarvisCode.Core.Utilities.TranscriptExporter.Render(_vm.Session));
        }
    }

    public void FocusInput() => InputBox.Focus();

    /// <summary>Dev/verification hook: pre-fills the composer (e.g. "/" to show the command popup).</summary>
    public void PrefillInput(string text)
    {
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        FocusInput();
    }

    public void StartNew(string? workingDirectory = null)
    {
        // The reference opens a new chat in the side-by-side comparison view when
        // the account asks for it, and only there — Code sessions and an existing
        // conversation are the ordinary surface.
        // With no model there is nothing to compare: the reference's own route
        // draws a bare 52px strip in that case, which here would be a new chat
        // with nowhere to go, so a fresh installation gets the ordinary greeting.
        if (!_isCodeSurface && _services is not null && _sessions is not null
            && _services.UiSettings.Current.NewChatComparisonView
            && _services.Settings.Models.Count > 0)
        {
            ShowComparison();
            return;
        }

        StartNewConversation(workingDirectory);
    }

    /// <summary>A new ordinary conversation, whatever the comparison setting says.</summary>
    private void StartNewConversation(string? workingDirectory = null)
    {
        HideComparison();
        if (_sessions is not null)
        {
            Bind(_sessions.ForNewSession(workingDirectory));
        }

        FocusInput();
    }

    /// <summary>The comparison view, built the first time a new chat opens in it.</summary>
    private Chat.ComparisonView? _comparison;

    private void ShowComparison()
    {
        if (_services is null || _sessions is null)
        {
            return;
        }

        if (_comparison is null)
        {
            _comparison = new Chat.ComparisonView();
            _comparison.CustomizeRequested += (_, page) => CustomizeNavigateRequested?.Invoke(this, page);
            _comparison.ProjectChanged += (_, _) => ProjectChanged?.Invoke(this, EventArgs.Empty);
            ComparisonHost.Content = _comparison;
            _comparison.Initialize(_services, _sessions);
        }
        else
        {
            _comparison.Reset();
        }

        ComparisonHost.Visibility = Visibility.Visible;
        RootGrid.Visibility = Visibility.Collapsed;
        ActivityPanel.Visibility = Visibility.Collapsed;
        _comparison.Focus();
    }

    /// <summary>Leaves the comparison view — an existing conversation is an ordinary one.</summary>
    private void HideComparison()
    {
        if (ComparisonHost.Visibility != Visibility.Visible)
        {
            return;
        }

        ComparisonHost.Visibility = Visibility.Collapsed;
        RootGrid.Visibility = Visibility.Visible;
    }

    /// <summary>The comparison view's own two arrow-key votes, while it is up.</summary>
    public bool HandleComparisonKey(System.Windows.Input.Key key) =>
        ComparisonHost.Visibility == Visibility.Visible
        && _comparison?.HandleVoteKey(key) == true;

    /// <summary>`--open=comparison[:vote|:reason|:saved]` poses the comparison view.</summary>
    public void PoseComparison(string state)
    {
        ShowComparison();
        _comparison?.Pose(state);
    }

    /// <summary>
    /// The reference's incognito chat: a new conversation that is never written to
    /// the store, so it leaves nothing in the list when it is closed.
    /// </summary>
    public void StartIncognito()
    {
        // An incognito chat is an ordinary conversation that is never written
        // down, so it opens on the conversation column even when new chats open
        // in the comparison view.
        StartNewConversation();
        if (_vm is not null)
        {
            _vm.Incognito = true;
            UpdateGreeting();
        }
    }

    public void LoadSession(JarvisCode.Core.Sessions.Session session)
    {
        HideComparison();
        if (_sessions is not null)
        {
            Bind(_sessions.ForSession(session));
        }
    }

    public void SubmitExternal(string text)
    {
        if (_vm is { IsRunning: false })
        {
            _ = SubmitTextAsync(text);
        }
    }

    /// <summary>Adds a context chip from panel content (picked element, terminal selection).</summary>
    public void AttachContextText(string text) => AttachContext(text);

    /// <summary>An image the workspace produced (an annotated preview) joins the composer.</summary>
    public void AttachImageFile(string path) => AddImageAttachment(path);

    /// <summary>A widget's user-selected files join this conversation's draft, never another active surface.</summary>
    public void AttachWidgetFiles(System.Text.Json.Nodes.JsonArray files, string? text)
    {
        if (_services is null || _vm is null) throw new InvalidOperationException("The conversation is no longer available.");
        var paths = Services.WidgetAttachments.Save(files, System.IO.Path.Combine(_services.Paths.Root, "attachments"));
        AttachFiles(paths);
        if (!string.IsNullOrWhiteSpace(text)) PrefillInput(text);
    }

    /// <summary>Appends an @-mention of a file to the composer, quoted when the path has spaces.</summary>
    public void AppendFileMention(string path)
    {
        var token = path.Contains(' ') ? $"@\"{path}\"" : "@" + path;
        var text = InputBox.Text;
        InputBox.Text = text.Length == 0 || char.IsWhiteSpace(text[^1])
            ? text + token + " "
            : text + " " + token + " ";
        InputBox.CaretIndex = InputBox.Text.Length;
        FocusInput();
    }

    /// <summary>The "+" menu's file picker — also Ctrl+Shift+L's fallback with no terminal selection.</summary>
    public void OpenAddFilesDialog() => AddFiles();

    // ---- layout state ----

    private void UpdateLayoutState()
    {
        var isEmpty = _vm?.IsEmpty ?? true;
        var isCode = _vm?.IsCodeSurface ?? false;

        if (isCode)
        {
            // Code surface: composer pinned to the bottom in both states;
            // the empty state shows the home view (greeting + usage stats).
            TopSpacerRow.Height = new GridLength(0);
            TranscriptRow.Height = new GridLength(1, GridUnitType.Star);
            BottomSpacerRow.Height = new GridLength(0);
            GreetingPanel.Visibility = Visibility.Collapsed;
            ChipsRow.Visibility = Visibility.Collapsed;
            ChatDisclaimer.Visibility = Visibility.Collapsed;
            HomeScroll.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            TranscriptScroll.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            if (isEmpty)
            {
                ScrollToBottomButton.Visibility = Visibility.Collapsed;
            }

            ContextBar.Visibility = Visibility.Visible;
            SessionHeader.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            Placeholder.Text = isEmpty ? "Describe a task or ask a question" : "Type / for commands";
            if (isEmpty)
            {
                BuildHomeView();
            }
            else
            {
                UpdateSessionHeader();
            }

            UpdateContextBar();
            _ = RefreshGitBannerAsync();
        }
        else
        {
            // Which of the two empty screens this is decides where the composer sits,
            // so the greeting is resolved before the rows are sized rather than after.
            UpdateGreeting();

            var appearing = isEmpty && GreetingPanel.Visibility != Visibility.Visible;
            GreetingPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            if (appearing)
            {
                PlayEmptyChatFade();
            }
            HomeScroll.Visibility = Visibility.Collapsed;
            TranscriptScroll.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            if (isEmpty)
            {
                ScrollToBottomButton.Visibility = Visibility.Collapsed;
            }

            // The reference's empty chat screen carries the greeting alone; its
            // Write/Learn/Code chips are this port's own and are gone with it.
            ChipsRow.Visibility = Visibility.Collapsed;
            ChatDisclaimer.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
            ContextBar.Visibility = Visibility.Collapsed;
            SessionHeader.Visibility = Visibility.Collapsed;
            GitBarHost.Content = null;
            Placeholder.Text = Services.ChatPlaceholders.Resolve(new Services.ChatPlaceholderState(
                Dictation: _dictation,
                NewConversation: isEmpty));

            if (isEmpty && _onboarding)
            {
                // The onboarding block sits at the top of the column, where the
                // reference's own pt-4 puts it, not floating above the composer.
                TopSpacerRow.Height = new GridLength(1, GridUnitType.Star);
                TranscriptRow.Height = new GridLength(0);
                BottomSpacerRow.Height = new GridLength(0);
            }
            else if (isEmpty)
            {
                TopSpacerRow.Height = new GridLength(42, GridUnitType.Star);
                TranscriptRow.Height = new GridLength(0);
                BottomSpacerRow.Height = new GridLength(58, GridUnitType.Star);
            }
            else
            {
                TopSpacerRow.Height = new GridLength(0);
                TranscriptRow.Height = new GridLength(1, GridUnitType.Star);
                BottomSpacerRow.Height = new GridLength(0);
            }
        }

        UpdateSendButton();
    }

    // ---- code home view (greeting + usage stats card) ----

    private void BuildHomeView()
    {
        if (_services is null)
        {
            return;
        }

        _ = RefreshHomeActionCenterAsync();

        StatsCardHost.Content ??= new UsageStatsCard(
            () => _services.UsageStats.Load(),
            () =>
            {
                try
                {
                    var dir = System.IO.Path.Combine(_services.Paths.SessionsDirectory, "code");
                    return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Length : 0;
                }
                catch (IOException)
                {
                    return 0;
                }
            });
    }

    // ---- session header ----

    private void UpdateSessionHeader()
    {
        if (_vm is null)
        {
            return;
        }

        SessionTitleText.Text = string.IsNullOrWhiteSpace(_vm.Session.Title) ? "New session" : _vm.Session.Title;
        // The reference's clickable title names itself "{name}, rename session".
        SessionTitleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            Controls.InlineRenameBox.RenameLabel(_vm.Session.Title));
        SessionTitleButton.ToolTip = "Rename";

        // The reference's origin slot: the glyph its `pp` picks for the kind, tinted by what
        // its `mp` says about the connection. Every session here is local.
        OriginGlyph.SetResourceReference(
            System.Windows.Shapes.Path.DataProperty,
            Services.SessionOriginIcon.Glyph(OriginKind, OriginConnection));
        OriginGlyph.SetResourceReference(
            System.Windows.Shapes.Shape.StrokeProperty,
            Services.SessionOriginIcon.BrushKey(OriginConnection) ?? "Text100Brush");
        OriginGlyph.BeginAnimation(OpacityProperty, null);
        if (Services.SessionOriginIcon.Pulses(OriginConnection))
        {
            OriginGlyph.BeginAnimation(OpacityProperty, PulseAnimation());
        }

        ProjectBadge.Label = Services.SidebarPresentation.FolderName(_vm.Session.WorkingDirectory);
    }

    /// <summary>The reference's origin kind. Nothing here produces anything but a local session.</summary>
    private const Services.SessionOriginKind OriginKind = Services.SessionOriginKind.Local;

    /// <summary>Its connection state, which a local session never has.</summary>
    private const Services.SessionOriginConnection OriginConnection = Services.SessionOriginConnection.None;

    /// <summary>The reference's `animate-pulse`, which its skeletons and a coming-up transport share.</summary>
    private static System.Windows.Media.Animation.DoubleAnimation PulseAnimation() =>
        new(1, 0.4, new Duration(TimeSpan.FromSeconds(1)))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };

    /// <summary>
    /// The reference's `isTitleLoading`: while the session is still being read, the title and
    /// the origin glyph are replaced by their skeletons, so the bar keeps its shape instead of
    /// showing the previous session's name until the new one arrives.
    /// </summary>
    public bool TitleLoading
    {
        get => SessionTitleSkeleton.Visibility == Visibility.Visible;
        set
        {
            if (TitleLoading == value)
            {
                return;
            }

            if (value)
            {
                // Its `Tb`: `h-[1lh] w-[24ch]`, sized off the face the title itself uses, so
                // the placeholder is the width of the text it stands in for.
                SessionTitleSkeleton.Width = 24 * ZeroWidth(SessionTitleText);
                SessionTitleSkeleton.BeginAnimation(OpacityProperty, PulseAnimation());
                OriginSkeleton.BeginAnimation(OpacityProperty, PulseAnimation());
            }
            else
            {
                SessionTitleSkeleton.BeginAnimation(OpacityProperty, null);
                OriginSkeleton.BeginAnimation(OpacityProperty, null);
            }

            SessionTitleSkeleton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            OriginSkeleton.Visibility = SessionTitleSkeleton.Visibility;
            OriginGlyph.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            SessionTitleButton.Visibility = value || _headerRenaming
                ? Visibility.Collapsed
                : Visibility.Visible;
            // A child's Visibility invalidates the child, not its host, and the bar measures
            // the host: without this the skeleton is laid out at the title's old width.
            SessionTitleHost.InvalidateMeasure();
        }
    }

    /// <summary>One "0" wide at a block's own face — the CSS <c>ch</c> unit.</summary>
    private static double ZeroWidth(TextBlock block) =>
        new System.Windows.Media.FormattedText(
            "0",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(block).PixelsPerDip).Width;

    private string? _runningAsAgent;

    /// <summary>
    /// The reference's `Lh`: a session running as a named agent says so, with the agent in the
    /// tooltip. Nothing here sets it — see the surface manifest.
    /// </summary>
    public string? RunningAsAgent
    {
        get => _runningAsAgent;
        set
        {
            _runningAsAgent = value;
            var named = value is { Length: > 0 };
            AgentBadge.Visibility = named ? Visibility.Visible : Visibility.Collapsed;
            var label = named ? $"Running as agent: {value}" : null;
            AgentBadge.ToolTip = label;
            AgentBadge.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, label ?? "");
            SessionTitleTrailing.InvalidateMeasure();
        }
    }

    /// <summary>
    /// The two pane controls the reference gates on its host rather than on the session: the
    /// × only where the pane can be removed, and the artifact expander only while that pane is
    /// in the layout without already filling the host.
    /// </summary>
    public void SetPaneControls(bool canClose, bool canExpandArtifact)
    {
        ClosePaneButton.Visibility = canClose ? Visibility.Visible : Visibility.Collapsed;
        ArtifactExpandButton.Visibility = canExpandArtifact ? Visibility.Visible : Visibility.Collapsed;
        SessionTitleTrailing.InvalidateMeasure();
    }

    /// <summary>
    /// The reference app's session menu. It opens from the ⋮ in the header rail, and from a
    /// right-click on the title, which the reference wires as the menu's own
    /// <c>contextMenuTrigger</c> — hence the placement target.
    /// </summary>
    private void ShowSessionMenu(UIElement? target = null)
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        Panels.SessionMenu.Show(target ?? Rail.MenuPlacementTarget, new Panels.SessionMenuHost
        {
            Services = _services,
            Chat = this,
            ShowPanel = key => PanelToggleRequested?.Invoke(this, key),
            IsPaneOpen = key => IsPaneOpen?.Invoke(key) ?? false,
            GetTranscriptView = () => _transcriptView,
            SetTranscriptView = SetTranscriptView,
            SessionHasThinking = TranscriptHasThinking,
            StartRename = BeginHeaderRename,
            SessionsChanged = () =>
            {
                UpdateSessionHeader();
                SessionPersisted?.Invoke(this, EventArgs.Empty);
            },
        });
    }

    /// <summary>
    /// Transcript view: Summary keeps only the conversation, Normal hides reasoning, and
    /// Thinking and Verbose show everything - Verbose with the tool groups held open.
    /// </summary>
    private void ApplyTranscriptView()
    {
        if (_vm is null)
        {
            return;
        }

        // Normal hides reasoning on the Code surface, matching the reference's
        // transcript modes. The Chat surface has no such mode: its thinking is part
        // of the message there, behind its own "Thought for …" disclosure.
        var hidesThinking = _vm.IsCodeSurface;
        _vm.SetLiveSummaryVisible(IsVisible && _transcriptView == Panels.TranscriptViewMode.Summary);
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_vm.Transcript);
        view.Filter = item => _transcriptView switch
        {
            Panels.TranscriptViewMode.Summary => item is ViewModels.SessionSummaryItem,
            Panels.TranscriptViewMode.Normal => item is not ViewModels.SessionSummaryItem && (!hidesThinking || item is not ViewModels.ThinkingItem),
            _ => item is not ViewModels.SessionSummaryItem,
        };

        // Thinking and Verbose carry the reference's one-line recap above each
        // tool group; Verbose additionally holds every group open, without
        // touching the rows' own expanded state.
        var showRecaps = Services.TranscriptViewModes.ShowsThinking(_transcriptView);
        var forceExpand = _transcriptView == Panels.TranscriptViewMode.Verbose;
        _vm.ShowThinkingRecaps = showRecaps;
        _vm.ForceExpandGroups = forceExpand;
        foreach (var group in _vm.Transcript.OfType<ViewModels.ToolGroupItem>())
        {
            group.ShowRecap = showRecaps;
            group.ForceExpanded = forceExpand;
        }

        // The status label's toggle is withheld in Verbose, which already shows
        // thinking — the reference's transcriptModeShowsThinking gate.
        StatusLine.ThinkingToggleEnabled = _transcriptView != Panels.TranscriptViewMode.Verbose;
    }

    /// <summary>Whether the transcript holds any thinking yet — the reference's Thinking-mode gate.</summary>
    private bool TranscriptHasThinking() =>
        _vm is not null && _vm.Transcript.OfType<ViewModels.ThinkingItem>().Any();

    /// <summary>Commits a transcript view: sticky per session, then re-filtered in place.</summary>
    private void SetTranscriptView(Panels.TranscriptViewMode mode)
    {
        _transcriptView = mode;
        if (_services is not null && _vm is not null)
        {
            Services.TranscriptViewModes.Stamp(
                _services.UiSettings.Current.TranscriptViewBySession, _vm.Session.Id, mode);
            _services.UiSettings.Save();
        }

        ApplyTranscriptView();
    }

    /// <summary>Ctrl+O — the reference's cycleTranscriptMode, silent like it.</summary>
    public void CycleTranscriptView()
    {
        if (_vm is null)
        {
            return;
        }

        SetTranscriptView(Services.TranscriptViewModes.Next(_transcriptView, TranscriptHasThinking()));
    }

    private Controls.ContextWindowPopup? _contextPopup;
    private Controls.EffortSliderPopup? _effortPopup;
    private Controls.RequestInspectorPopup? _requestPopup;
    private readonly Dictionary<string, ChatGptComposerControls> _chatGptControlsByContext =
        new(StringComparer.Ordinal);
    private CancellationTokenSource? _chatGptEffortRead;
    private bool _chatGptEffortLoading;
    private bool _chatGptModelsLoading;
    private DateTimeOffset _chatGptModelsReadAt = DateTimeOffset.MinValue;

    private void CancelChatGptEffortRead()
    {
        var stale = _chatGptEffortRead;
        _chatGptEffortRead = null;
        stale?.Cancel();
        _chatGptEffortLoading = false;
        _effortPopup?.Close();
    }

    /// <summary>
    /// The composer's status dot doubles as the request inspector: the exact call the
    /// next model turn would make, with its payload editable.
    /// </summary>
    private void OnRequestInspectorClick(object sender, RoutedEventArgs e) => ToggleRequestInspector();

    public void ToggleRequestInspector()
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        _requestPopup ??= new Controls.RequestInspectorPopup(
            RequestInspectorButton,
            () => _vm?.Session.Id,
            () => Services.RequestPreviewBuilder.Build(_services!, _vm!),
            UpdateRequestOverrideIndicator);
        _requestPopup.Toggle();
    }

    /// <summary>
    /// A filled dot means this session's requests are being rewritten by a hand edit —
    /// the only sign an override exists once the popup is closed.
    /// </summary>
    private void UpdateRequestOverrideIndicator()
    {
        bool overridden = _vm is not null && Services.RequestOverrides.Has(_vm.Session.Id);
        if (overridden)
        {
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrandBrush");
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrandBrush");
        }
        else
        {
            StatusDot.Fill = null;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text500Brush");
        }

        RequestInspectorButton.ToolTip = overridden
            ? "Request payload edited for this session — click to review"
            : "Inspect and edit the request sent to the provider";
    }

    /// <summary>The composer pill opens the reference context-window popup.</summary>
    private void OnContextPillClick(object sender, RoutedEventArgs e) => ShowContextPopup();

    public void ShowContextPopup()
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        _contextPopup ??= new Controls.ContextWindowPopup(ContextPill);
        _contextPopup.Show(Services.ContextBreakdown.Compute(_services, _vm));
    }

    private void OnTasksChipClicked(object? sender, EventArgs e) =>
        PanelRequested?.Invoke(this, StatusLine.TasksPaneOpen ? null : "runs");

    /// <summary>The workspace mirrors panel state and task counts into the status line.</summary>
    public void SetTasksPaneOpen(bool open) => StatusLine.TasksPaneOpen = open;

    public void SetRunningTaskCount(int count) => StatusLine.RunningTaskCount = count;

    /// <summary>The status-line label click: Normal ⇄ Thinking, like the reference.</summary>
    private void OnStatusLabelClicked(object? sender, EventArgs e)
    {
        if (_vm is null || _transcriptView == Panels.TranscriptViewMode.Verbose)
        {
            return;
        }

        var next = _transcriptView == Panels.TranscriptViewMode.Thinking
            ? Panels.TranscriptViewMode.Normal
            : Panels.TranscriptViewMode.Thinking;
        SetTranscriptView(next);
        _vm.Transcript.Add(new ViewModels.NoticeItem
        {
            Text = next == Panels.TranscriptViewMode.Thinking
                ? "Switched transcript view to Thinking"
                : "Switched transcript view to Normal",
        });
    }

    // ---- context bar (Local · project · branch/worktree · dirs · +) ----

    private void UpdateContextBar()
    {
        BuildContextRow();
    }

    private Button Chip(string glyph, string label, string? tooltip, bool removable = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
        content.Children.Add(icon);
        if (label.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (removable)
        {
            var close = new TextBlock
            {
                Text = "",
                FontSize = 8,
                Margin = new Thickness(7, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            close.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
            close.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            content.Children.Add(close);
        }

        var chip = new Button { Content = content, ToolTip = tooltip, Margin = new Thickness(0, 2, 6, 2) };
        chip.SetResourceReference(StyleProperty, "ContextChip");
        chip.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            label.Length > 0 ? label : tooltip ?? "chip");
        return chip;
    }

    private Button StaticChip(string glyph, string label, string tooltip)
    {
        var chip = Chip(glyph, label, tooltip);
        chip.IsHitTestVisible = false;
        return chip;
    }

    private void OnProjectChipClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the working directory" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && _vm is not null)
        {
            _vm.SetWorkingDirectory(dialog.FolderName);
            if (_services is not null)
            {
                _services.Settings.Current.LastWorkingDirectory = dialog.FolderName;
                _services.Settings.Save();
            }

            _gitBannerDismissed = false;
            UpdateContextBar();
            _ = RefreshGitBannerAsync();
        }
    }

    // ---- git worktree toggle ----

    private void ToggleWorktree(bool enable)
    {
        if (_vm is null || _vm.Session.Messages.Count > 0)
        {
            return;
        }

        var cwd = _vm.Session.WorkingDirectory;
        if (enable)
        {
            var (rootExit, rootOut) = RunGitCapture(cwd, "rev-parse --show-toplevel");
            if (rootExit != 0)
            {
                return;
            }

            var repoRoot = rootOut.Trim().Replace('/', '\\');
            var repoName = Path.GetFileName(repoRoot);
            var shortId = _vm.Session.Id.Length > 8 ? _vm.Session.Id[^8..] : _vm.Session.Id;
            var worktreePath = Path.Combine(
                Path.GetDirectoryName(repoRoot) ?? repoRoot, ".jarvis-worktrees", $"{repoName}-{shortId}");
            var (exit, output) = RunGitCapture(repoRoot, $"worktree add \"{worktreePath}\" -b jarvis/{shortId}");
            if (exit == 0)
            {
                _vm.SetWorkingDirectory(worktreePath);
            }
            else
            {
                ReportWorktreeFailure(output);
            }
        }
        else
        {
            var (exit, commonDir) = RunGitCapture(cwd, "rev-parse --git-common-dir");
            var origin = exit == 0
                ? Path.GetDirectoryName(Path.GetFullPath(commonDir.Trim().Replace('/', '\\'), cwd))
                : null;
            if (origin is null)
            {
                return;
            }

            _vm.SetWorkingDirectory(origin);
            RunGitCapture(origin, $"worktree remove \"{cwd}\" --force");
        }

        UpdateContextBar();
        _ = RefreshGitBannerAsync();
    }

    private static (int ExitCode, string Output) RunGitCapture(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return (-1, "git is not available");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return (process.ExitCode, stdout.Length > 0 ? stdout : stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }
    }

    // ---- git banner ----

    private Task RefreshGitBannerAsync() => RefreshGitBarAsync();

    /// <summary>
    /// The empty chat screen. The reference shows the mascot beside one greeting —
    /// not a time-of-day salutation — and the first chat of all gets its onboarding
    /// paragraphs instead.
    /// </summary>
    private void UpdateGreeting()
    {
        if (_vm is { IsCodeSurface: true })
        {
            return;
        }

        var view = Services.ChatWelcome.Describe(
            firstChatEver: _chatCountForWelcome == 0,
            _services?.UiSettings.Current.UserDisplayName,
            incognito: _vm?.Incognito == true);

        _onboarding = view.IsOnboarding;

        GreetingFigure.Visibility = view.IsOnboarding ? Visibility.Collapsed : Visibility.Visible;
        IncognitoNotice.Visibility = _vm?.Incognito == true ? Visibility.Visible : Visibility.Collapsed;
        OnboardingPanel.Visibility = view.IsOnboarding ? Visibility.Visible : Visibility.Collapsed;
        GreetingText.Text = view.Greeting ?? Services.ChatWelcome.Greeting;
        GreetingSpark.State = Services.SparkState.Waiting;

        if (view.IsOnboarding)
        {
            StartOnboardingReveal(view);
        }
        else
        {
            _revealStep = RevealDone;
            _revealedFor = null;
        }
    }

    /// <summary>Whether the empty screen is showing the first chat's onboarding.</summary>
    private bool _onboarding;

    /// <summary>The voice session's state, which outranks every placeholder rung.</summary>
    private Services.ChatDictation _dictation = Services.ChatDictation.Off;

    /// <summary>Nothing is left to reveal.</summary>
    private const int RevealDone = 3;

    /// <summary>
    /// Which paragraph the reveal is on. The chain is driven by each paragraph
    /// reporting completion, so a stale report — a session switched mid-reveal, or a
    /// rebuild of a paragraph that is off screen — must not advance the next one.
    /// </summary>
    private int _revealStep = RevealDone;

    private string _onboardingIntroText = "";
    private string _onboardingStartText = "";

    /// <summary>
    /// The greeting the running reveal belongs to. The layout is recomputed on many
    /// occasions that are nothing to do with the onboarding, and each one would
    /// otherwise start the reveal over from the first word.
    /// </summary>
    private string? _revealedFor;

    /// <summary>
    /// The reference reveals the greeting, then the intro once the greeting lands,
    /// then the line that closes it, and the mascot writes until the last of them is
    /// done — its <c>Xm</c> chain, and the <c>S = y ? "idle" : "writing"</c> beneath it.
    /// </summary>
    private void StartOnboardingReveal(Services.ChatWelcomeView view)
    {
        var greeting = view.OnboardingGreeting ?? "";
        if (_revealedFor == greeting)
        {
            return;
        }

        _revealedFor = greeting;
        _onboardingIntroText = view.OnboardingIntro ?? "";
        _onboardingStartText = view.OnboardingStart ?? "";

        OnboardingIntro.Visibility = Visibility.Collapsed;
        OnboardingStart.Visibility = Visibility.Collapsed;
        OnboardingSpark.State = Services.ChatWelcome.OnboardingMascot(revealed: false);

        OnboardingGreeting.Completed -= OnOnboardingGreetingRevealed;
        OnboardingIntro.Completed -= OnOnboardingIntroRevealed;
        OnboardingStart.Completed -= OnOnboardingStartRevealed;
        OnboardingGreeting.Completed += OnOnboardingGreetingRevealed;
        OnboardingIntro.Completed += OnOnboardingIntroRevealed;
        OnboardingStart.Completed += OnOnboardingStartRevealed;

        // Last, so a rebuild triggered while the panel was being prepared cannot
        // report into the step it is about to start.
        _revealStep = 0;
        OnboardingGreeting.Text = greeting;
        OnboardingGreeting.Restart();
    }

    private void OnOnboardingGreetingRevealed(object? sender, EventArgs e)
    {
        if (!_onboarding || _revealStep != 0)
        {
            return;
        }

        _revealStep = 1;
        OnboardingIntro.Visibility = Visibility.Visible;
        OnboardingIntro.Text = _onboardingIntroText;
        OnboardingIntro.Restart();
    }

    private void OnOnboardingIntroRevealed(object? sender, EventArgs e)
    {
        if (!_onboarding || _revealStep != 1)
        {
            return;
        }

        _revealStep = 2;
        OnboardingStart.Visibility = Visibility.Visible;
        OnboardingStart.Text = _onboardingStartText;
        OnboardingStart.Restart();
    }

    private void OnOnboardingStartRevealed(object? sender, EventArgs e)
    {
        if (!_onboarding || _revealStep != 2)
        {
            return;
        }

        _revealStep = RevealDone;
        OnboardingSpark.State = Services.ChatWelcome.OnboardingMascot(revealed: true);
    }

    /// <summary>
    /// The reference's <c>sm:</c> breakpoint on the empty screen's heading: below it
    /// the mascot sits above the greeting, at or above it they share a row.
    /// </summary>
    private void OnGreetingSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var row = e.NewSize.Width >= Services.ChatWelcome.GreetingRowBreakpoint;
        var gap = Services.ChatWelcome.GreetingGap;

        GreetingRow.Orientation = row ? Orientation.Horizontal : Orientation.Vertical;
        GreetingSpark.HorizontalAlignment = row ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        GreetingText.Margin = row ? new Thickness(gap, 0, 0, 0) : new Thickness(0, gap, 0, 0);
        GreetingText.HorizontalAlignment = row ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        GreetingText.TextAlignment = row ? TextAlignment.Left : TextAlignment.Center;
    }

    /// <summary>
    /// The reference's <c>animate-empty-chat-fade</c>: a 300ms fade on
    /// <c>cubic-bezier(.25,.1,.35,1)</c> after a 200ms delay, with the element held
    /// at zero through the delay (its <c>backwards</c> fill). A reader who has asked
    /// for reduced motion gets the screen without it, as its
    /// <c>motion-reduce:animate-none</c> does.
    /// </summary>
    private void PlayEmptyChatFade()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            GreetingPanel.BeginAnimation(OpacityProperty, null);
            GreetingPanel.Opacity = 1;
            return;
        }

        var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            BeginTime = TimeSpan.FromMilliseconds(200),
            Duration = TimeSpan.FromMilliseconds(300),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
        };
        fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
            0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new System.Windows.Media.Animation.SplineDoubleKeyFrame(
            1,
            System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
            new System.Windows.Media.Animation.KeySpline(0.25, 0.1, 0.35, 1)));

        GreetingPanel.Opacity = 0;
        GreetingPanel.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>How many chats exist; zero means this is the first one ever.</summary>
    private int _chatCountForWelcome = 1;

    /// <summary>The shell reports the chat count so the first chat of all can be recognised.</summary>
    public void SetChatCount(int count)
    {
        _chatCountForWelcome = count;

        // The onboarding screen and the ordinary one sit in different places, so the
        // count decides the row heights as well as the words; the layout pass resolves
        // the greeting on its way through.
        UpdateLayoutState();
    }

    private void OnComposerDraftChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateSendButton();

    /// <summary>
    /// The reference's <c>ie</c>: whether the composer is holding anything to send \u2014
    /// a typed prompt or an attachment. Mid-turn that is what decides between the
    /// primary Send and the Stop which otherwise sits in the same slot.
    /// </summary>
    private bool HasDraftContent =>
        InputBox.Text.Trim().Length > 0 || _vm is { ComposerAttachments.Count: > 0 };

    private void UpdateSendButton()
    {
        var running = _vm?.IsRunning ?? false;
        var stopping = Services.ChatComposerActions.ShowsStop(running, HasDraftContent);
        // The reference's primary action is an ArrowUp on a brand-filled button and
        // its stop is the secondary Stop, so the two swap style as well as glyph.
        SendGlyph.Text = stopping ? "\uE71A" : "\uE74A";
        SendButton.Style = (Style)FindResource(stopping ? "IconButton" : "BrandIconButton");
        if (stopping)
        {
            SendGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Danger100Brush");
        }
        else
        {
            SendGlyph.ClearValue(TextBlock.ForegroundProperty);
        }

        SendButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            stopping ? Services.ChatComposerActions.StopResponse : Services.ChatComposerActions.SendMessage);
        SendButton.IsEnabled = stopping || HasDraftContent;
        // The reference's send tooltip lists what the composer can do with the
        // message and the chord for each; stopping lists Escape instead. While a
        // turn runs the send hint gains the reference's own secondary action.
        SendButton.ToolTip = stopping
            ? Services.ChatComposerActions.StopTooltip + "  \u00b7  Esc"
            : (_vm is { IsCodeSurface: true }
                ? "Send  ·  Enter\nSend in a forked session  ·  Ctrl+Alt+Enter"
                : "Send  ·  Enter")
                + (running ? $"\n{Services.ChatComposerActions.Interrupt}  ·  Ctrl+Enter" : "");

        StatusSpinner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        UpdateRequestOverrideIndicator();
    }

    private void UpdateModelChip()
    {
        ModelChipText.Text = _vm?.CurrentModel?.DisplayName ?? "Choose model";
        UpdateEffortChip();
        ModelChip.ToolTip = "Model (Ctrl+Shift+I)";
        // The reference names the trigger after the model it is on, which is what
        // an assistive tool reads out where the chip shows only the name.
        System.Windows.Automation.AutomationProperties.SetName(
            ModelChip,
            _vm?.CurrentModel is { } model
                ? Services.ChatModelMenu.Trigger(model.DisplayName)
                : "Model");
    }

    private bool IsChatGptWebModel => _vm?.CurrentModel is { } model
        && model.ProviderId.Equals(ChatGptWebProvider.ProviderId, StringComparison.OrdinalIgnoreCase);

    private bool IsChatGptAutoModel => IsChatGptWebModel
        && string.Equals(_vm?.CurrentModel?.ModelId, ChatGptWebProvider.AutoModelSlug,
            StringComparison.OrdinalIgnoreCase);

    private bool SupportsEffortControl => (IsChatGptWebModel && !IsChatGptAutoModel)
        || _vm?.CurrentModel is not { } model
        || _services is null
        || ProviderCapabilities.For(_services.Providers.Get(model.ProviderId)).SupportsThinkingEffort;

    private bool SupportsWebSearchControl => _vm?.CurrentModel is not { } model || _services is null ||
        JarvisCode.Core.Providers.ProviderCapabilities.For(_services.Providers.Get(model.ProviderId)).SupportsWebSearchControl;

    private void ExplainAccountManagedEffort() => _vm?.Transcript.Add(new NoticeItem
    {
        Text = IsChatGptAutoModel
            ? "Choose an explicit model from ChatGPT's live model list before setting effort; Auto follows the browser and has no model-stable Power ladder."
            : "Thinking is controlled by this provider outside Jarvis; it does not expose an effort control.",
    });

    private void UpdateEffortChip()
    {
        if (_services is null)
        {
            return;
        }

        if (_vm is { IsCodeSurface: false } && !IsChatGptWebModel)
        {
            // Chat has no effort ladder and no thinking chip: the reference renders
            // its thinking control as a switch inside the model menu, right after the
            // model list, so the chin carries nothing for it here either.
            EffortChip.Visibility = Visibility.Collapsed;
            return;
        }

        EffortChip.Visibility = SupportsEffortControl ? Visibility.Visible : Visibility.Collapsed;
        EffortChipText.Opacity = 1;
        EffortChipText.Text = CurrentEffortName();
        EffortChip.ToolTip = IsChatGptWebModel
            ? "Thinking effort from ChatGPT (Ctrl+Shift+E)"
            : "Effort (Ctrl+Shift+E)";
    }

    /// <summary>Flips this conversation's Extended thinking switch (Chat surface).</summary>
    public void ToggleExtendedThinking()
    {
        if (_vm is not { IsCodeSurface: false } || _services is null)
        {
            return;
        }

        if (IsChatGptWebModel)
        {
            ToggleEffortMenu();
            return;
        }

        if (!SupportsEffortControl) { ExplainAccountManagedEffort(); return; }

        var settings = _services.ChatConversations;
        var on = settings.Get(_vm.Session.Id, _services.Settings.Current.EnableWebSearch).ExtendedThinking;
        settings.SetExtendedThinking(_vm.Session.Id, !on);
        UpdateEffortChip();
    }

    /// <summary>The reference's effort selector — the Ctrl+Shift+E popover.</summary>
    public void ToggleEffortMenu()
    {
        if (_services is null)
        {
            return;
        }

        if (_effortPopup?.IsOpen == true)
        {
            _effortPopup.Close();
            return;
        }

        if (!SupportsEffortControl) { ExplainAccountManagedEffort(); return; }

        if (_vm is { IsCodeSurface: false } && !IsChatGptWebModel)
        {
            ToggleExtendedThinking();
            return;
        }

        if (_chatGptEffortLoading)
        {
            return;
        }

        _ = OpenEffortMenuAsync();
    }

    private async Task OpenEffortMenuAsync()
    {
        if (_services is null || _vm is null)
        {
            return;
        }

        if (IsChatGptWebModel)
        {
            var owner = _vm;
            var scopeId = owner.Session.Id;
            var modelId = owner.CurrentModel!.ModelId;
            var read = new CancellationTokenSource();
            _chatGptEffortRead = read;
            _chatGptEffortLoading = true;
            EffortChipText.Text = Services.ChatModelMenu.LoadingModels;
            EffortChipText.Opacity = 0.65;
            try
            {
                var controls = await ReadChatGptControlsAsync(owner, scopeId, modelId, read.Token);
                if (!IsCurrentChatGptRead(owner, scopeId, modelId))
                {
                    return;
                }

                if (controls is null)
                {
                    return;
                }

                if (controls.Efforts.Count == 0)
                {
                    owner.Transcript.Add(new NoticeItem
                    {
                        Text = "ChatGPT did not expose a thinking-effort control for this model.",
                    });
                    return;
                }

                RememberEffectiveChatGptEffort(modelId, controls);
            }
            finally
            {
                read.Dispose();
                if (ReferenceEquals(_chatGptEffortRead, read))
                {
                    _chatGptEffortRead = null;
                    _chatGptEffortLoading = false;
                    if (ReferenceEquals(_vm, owner)) UpdateEffortChip();
                }
            }
        }

        _effortPopup ??= new Controls.EffortSliderPopup(
            EffortChip,
            CurrentEffortName,
            CurrentEffortLevels,
            name => CommitEffortSelection(name),
            onClosed: () => InputBox.Focus());
        _effortPopup.Toggle();
    }

    private string CurrentEffortName()
    {
        if (_services is null)
        {
            return Services.EffortLevels.Default;
        }

        if (IsChatGptWebModel && _vm?.CurrentModel is { } model)
        {
            if (_services.Settings.Current.EffortByModel.TryGetValue(model.ModelId, out var saved)
                && !string.IsNullOrWhiteSpace(saved))
            {
                return saved;
            }

            var controls = CurrentChatGptControls();
            return controls?.Efforts.FirstOrDefault(static option => option.Selected)?.Label
                ?? controls?.Efforts.FirstOrDefault()?.Label
                ?? "Thinking effort";
        }

        return _vm?.UltracodeMode == true
            ? Services.EffortLevels.Ultracode
            : Services.EffortLevels.Resolve(_services.Settings.Current.ThinkingEffortName);
    }

    private IReadOnlyList<string> CurrentEffortLevels()
    {
        if (!IsChatGptWebModel)
        {
            return Services.EffortLevels.Names;
        }

        var labels = CurrentChatGptControls()?.Efforts
            .Select(static option => option.Label)
            .Where(static label => !string.IsNullOrWhiteSpace(label))
            .ToArray();
        return labels is { Length: > 0 } ? labels : [CurrentEffortName()];
    }

    private async Task SetEffortFromCommandAsync(string argument)
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        if (!SupportsEffortControl)
        {
            ExplainAccountManagedEffort();
            return;
        }

        var value = argument.Trim();
        if (value.Length == 0)
        {
            ToggleEffortMenu();
            return;
        }

        if (IsChatGptWebModel && _vm.CurrentModel is { } webModel)
        {
            var owner = _vm;
            var scopeId = owner.Session.Id;
            var controls = CurrentChatGptControls();
            if (controls is null)
            {
                if (_chatGptEffortLoading)
                {
                    owner.Transcript.Add(new NoticeItem { Text = "ChatGPT's thinking control is still loading." });
                    return;
                }

                var read = new CancellationTokenSource();
                _chatGptEffortRead = read;
                _chatGptEffortLoading = true;
                try
                {
                    controls = await ReadChatGptControlsAsync(
                        owner, scopeId, webModel.ModelId, read.Token);
                }
                finally
                {
                    read.Dispose();
                    if (ReferenceEquals(_chatGptEffortRead, read))
                    {
                        _chatGptEffortRead = null;
                        _chatGptEffortLoading = false;
                        if (ReferenceEquals(_vm, owner)) UpdateEffortChip();
                    }
                }
            }
            if (controls is null
                || !IsCurrentChatGptRead(owner, scopeId, webModel.ModelId))
            {
                return;
            }

            var option = controls.Efforts.FirstOrDefault(candidate =>
                candidate.Label.Equals(value, StringComparison.OrdinalIgnoreCase)
                || candidate.Key.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (option is null)
            {
                owner.Transcript.Add(new NoticeItem
                {
                    Text = $"Unknown ChatGPT effort '{value}'. Levels: "
                        + string.Join(", ", controls.Efforts.Select(static candidate => candidate.Label)) + ".",
                    IsError = true,
                });
                return;
            }

            PersistChatGptEffort(webModel.ModelId, option);
            owner.Transcript.Add(new NoticeItem { Text = $"ChatGPT thinking effort set to {option.Label}." });
            return;
        }

        if (!Services.EffortLevels.TryResolve(value, out var match))
        {
            _vm.Transcript.Add(new NoticeItem
            {
                Text = $"Unknown effort level '{value}'. Levels: {string.Join(", ", Services.EffortLevels.Names)}.",
                IsError = true,
            });
            return;
        }

        CommitEffortSelection(match);
        _vm.Transcript.Add(new NoticeItem
        {
            Text = Services.EffortLevels.IsUltracode(match)
                ? "Set effort level to Ultracode (this session only): xhigh + multi-agent orchestration."
                : $"Effort set to {match}.",
        });
    }

    private ChatGptComposerControls? CurrentChatGptControls()
    {
        if (_vm?.CurrentModel is not { } model)
        {
            return null;
        }

        return _chatGptControlsByContext.GetValueOrDefault(
            ChatGptControlsKey(_vm.Session.Id, model.ModelId));
    }

    private async Task<ChatGptComposerControls?> ReadChatGptControlsAsync(
        ChatViewModel owner,
        string scopeId,
        string modelId,
        CancellationToken cancellationToken)
    {
        if (_services is null || Services.ChatGptComposerDiscovery.Provider(_services) is not { } provider)
        {
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            // Read on the provider's passive controls page, not the conversation page. The chosen
            // raw key is applied to the session atomically before its next prompt is pasted.
            var controls = await provider.ReadComposerControlsAsync(
                scopeId: null, modelId: modelId, cancellationToken: deadline.Token);
            if (IsCurrentChatGptRead(owner, scopeId, modelId))
            {
                _chatGptControlsByContext[ChatGptControlsKey(scopeId, modelId)] = controls;
            }
            return controls;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentChatGptRead(owner, scopeId, modelId)) owner.Transcript.Add(new NoticeItem
            {
                Text = "ChatGPT's thinking control did not answer in time.",
                IsError = true,
            });
        }
        catch (Exception ex)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write(
                $"chatgpt: live composer controls could not be read — {ex.Message}");
            if (IsCurrentChatGptRead(owner, scopeId, modelId)) owner.Transcript.Add(new NoticeItem
            {
                Text = $"ChatGPT's thinking control could not be read: {ex.Message}",
                IsError = true,
            });
        }

        return null;
    }

    private bool IsCurrentChatGptRead(ChatViewModel owner, string scopeId, string modelId) =>
        ReferenceEquals(_vm, owner)
        && owner.Session.Id == scopeId
        && string.Equals(owner.CurrentModel?.ModelId, modelId, StringComparison.OrdinalIgnoreCase);

    private static string ChatGptControlsKey(string scopeId, string modelId) => scopeId + "\n" + modelId;

    private void RememberEffectiveChatGptEffort(string modelId, ChatGptComposerControls controls)
    {
        var settings = _services!.Settings.Current;
        ChatGptPickerOption? selected = null;
        if (settings.ProviderOptionsByModel.TryGetValue(modelId, out var saved)
            && saved.TryGetValue(ChatGptWebProvider.PowerOptionKey, out var savedKey))
        {
            selected = controls.Efforts.FirstOrDefault(option =>
                option.Key.Equals(savedKey, StringComparison.Ordinal));
        }

        selected ??= controls.Efforts.FirstOrDefault(static option => option.Selected)
            ?? controls.Efforts.FirstOrDefault();
        if (selected is not null)
        {
            PersistChatGptEffort(modelId, selected);
        }
    }

    private void PersistChatGptEffort(string modelId, ChatGptPickerOption option)
    {
        var settings = _services!.Settings.Current;
        if (!settings.ProviderOptionsByModel.TryGetValue(modelId, out var options))
        {
            options = new Dictionary<string, string>(StringComparer.Ordinal);
            settings.ProviderOptionsByModel[modelId] = options;
        }

        var changed = !options.TryGetValue(ChatGptWebProvider.PowerOptionKey, out var savedKey)
            || !string.Equals(savedKey, option.Key, StringComparison.Ordinal);
        options[ChatGptWebProvider.PowerOptionKey] = option.Key;
        changed |= !settings.EffortByModel.TryGetValue(modelId, out var savedLabel)
            || !string.Equals(savedLabel, option.Label, StringComparison.Ordinal);
        settings.EffortByModel[modelId] = option.Label;
        if (changed)
        {
            _services.Settings.Save();
        }

        UpdateEffortChip();
    }

    /// <summary>
    /// A picked level persists as the default, like the reference's /effort;
    /// Ultracode is the exception — session-scoped, nothing saved (the reference
    /// stores it on the running session only).
    /// </summary>
    private void CommitEffortSelection(string name)
    {
        if (_services is null)
        {
            return;
        }

        if (!SupportsEffortControl) { ExplainAccountManagedEffort(); return; }

        if (IsChatGptWebModel && _vm?.CurrentModel is { } chatGptModel)
        {
            var option = CurrentChatGptControls()?.Efforts.FirstOrDefault(candidate =>
                candidate.Label.Equals(name, StringComparison.OrdinalIgnoreCase)
                || candidate.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (option is not null)
            {
                PersistChatGptEffort(chatGptModel.ModelId, option);
            }

            return;
        }

        // The Chat surface has no Agent to orchestrate with, so Ultracode
        // there falls back to its wire level.
        if (Services.EffortLevels.IsUltracode(name) && _vm?.IsCodeSurface != true)
        {
            name = "Extra high";
        }

        if (Services.EffortLevels.IsUltracode(name))
        {
            if (_vm is not null)
            {
                _vm.UltracodeMode = true;
            }
        }
        else
        {
            if (_vm is not null)
            {
                _vm.UltracodeMode = false;
            }

            var resolved = Services.EffortLevels.Resolve(name);
            _services.Settings.Current.ThinkingEffortName = resolved;
            // The reference saves the default effort per model, so each model keeps
            // its own when the user switches.
            if ((_vm?.Session.ModelId ?? _services.Settings.Current.DefaultModelId) is { Length: > 0 } modelId)
            {
                _services.Settings.Current.EffortByModel[modelId] = resolved;
            }

            _services.Settings.Save();
        }

        UpdateEffortChip();
    }

    /// <summary>Window-level keys for the open effort popover (Esc, digits, arrows, Ctrl+Shift+E).</summary>
    public bool EffortMenuHandleKey(KeyEventArgs e) => _effortPopup?.HandleKey(e) == true;

    private void OnEffortChipClick(object sender, RoutedEventArgs e) => ToggleEffortMenu();

    /// <summary>The queued stack's header — click to expand or collapse the rows.</summary>
    /// <summary>
    /// A spawn_task chip clicked: the suggestion becomes its own session, in its
    /// own worktree when it named a different project root, and the chip leaves
    /// the strip. The reference promises "one click spins it off into its own
    /// session", so the click really starts one rather than only filling the box.
    /// </summary>
    /// <summary>
    /// The reference's workspace-trust question, asked once per folder before a
    /// session works in it. A refusal leaves the session in place with the
    /// reference's own session-error card, which is what its "Approve this
    /// folder in the trust prompt to continue" says.
    /// </summary>
    private bool EnsureWorkspaceTrusted()
    {
        if (_vm is null || _services is null || !_vm.IsCodeSurface)
        {
            return true;
        }

        var directory = _vm.Session.WorkingDirectory;
        var trusted = _services.UiSettings.Current.TrustedWorkspaces;
        if (Services.WorkspaceTrust.IsTrusted(trusted, directory))
        {
            return true;
        }

        var sources = Services.WorkspaceTrust.ExecutionSources(directory);
        var footnote = sources.Count > 0
            ? $"{Services.SessionDialogs.ExecutionAllowedBy}\n{string.Join("\n", sources)}\n\n" +
              Services.SessionDialogs.TrustFootnote
            : Services.SessionDialogs.TrustFootnote;

        if (Views.ConfirmDialog.Ask(
                Window.GetWindow(this),
                Services.SessionDialogs.TrustTitle,
                Services.SessionDialogs.TrustBody,
                Services.SessionDialogs.TrustConfirm,
                subject: directory,
                footnote: footnote,
                focusCancel: true,
                danger: false))
        {
            Services.WorkspaceTrust.Trust(trusted, directory);
            _services.UiSettings.Save();
            return true;
        }

        var (title, body) = Services.ErrorCards.Describe(Services.ErrorCards.SessionErrorKind.TrustRequired);
        _vm.Transcript.Add(new ViewModels.ErrorCardItem { Headline = title, Hint = body });
        return false;
    }

    /// <summary>
    /// A session converted from a Claude Code CLI transcript is confirmed once
    /// before it is continued, which is the reference's "Resume imported
    /// session?".
    /// </summary>
    private bool EnsureImportedSessionResumable()
    {
        if (_vm is null || _services is null ||
            !_vm.Session.Title.StartsWith("Imported CLI session", StringComparison.Ordinal))
        {
            return true;
        }

        var resumed = _services.UiSettings.Current.ResumedImportedSessions;
        if (resumed.Contains(_vm.Session.Id, StringComparer.Ordinal))
        {
            return true;
        }

        if (!Views.ConfirmDialog.Ask(
                Window.GetWindow(this),
                Services.SessionDialogs.ResumeImportedTitle,
                Services.SessionDialogs.ResumeImportedBody,
                Services.SessionDialogs.Resume,
                focusCancel: true,
                danger: false))
        {
            return false;
        }

        resumed.Add(_vm.Session.Id);
        _services.UiSettings.Save();
        return true;
    }

    /// <summary>
    /// What git said when a worktree could not be set up, as one of the
    /// reference's session-error cards: its hook failure and its repair
    /// message are the two git names by itself, and anything else is the
    /// disk-space card only when git said so.
    /// </summary>
    private void ReportWorktreeFailure(string output)
    {
        if (_vm is null)
        {
            return;
        }

        var kind = output.Contains("worktree repair", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("not a working tree", StringComparison.OrdinalIgnoreCase)
            ? Services.ErrorCards.SessionErrorKind.WorktreeRepairNeeded
            : output.Contains("No space left", StringComparison.OrdinalIgnoreCase) ||
              output.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                ? Services.ErrorCards.SessionErrorKind.DiskFull
                : Services.ErrorCards.SessionErrorKind.WorktreeHookFailed;

        var (title, body) = Services.ErrorCards.Describe(kind);
        _vm.Transcript.Add(new ViewModels.ErrorCardItem
        {
            Headline = title,
            Hint = body,
            Details = output.Trim() is { Length: > 0 } detail ? detail : null,
        });
    }

    // ---- the API-error card ----

    private static ViewModels.ErrorCardItem? ErrorCardOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ViewModels.ErrorCardItem;

    // ---- the session-not-found card ----

    /// <summary>Raised by the card's Import CLI sessions button.</summary>
    public event EventHandler? ImportCliSessionsRequested;

    /// <summary>Raised by the card's Archive button, with the session it names.</summary>
    public event EventHandler<string>? SessionArchiveRequested;

    /// <summary>Raised by the card's Delete button, with the session it names.</summary>
    public event EventHandler<string>? SessionDeleteRequested;

    /// <summary>
    /// Shows the reference's "Session not found on disk" card for a session whose
    /// stored transcript has gone. The transcript is otherwise empty, which is the
    /// state the reference gates its own card on.
    /// </summary>
    public void ShowSessionNotFound(string sessionId)
    {
        if (_vm is null)
        {
            return;
        }

        _vm.Transcript.Add(new ViewModels.SessionNotFoundItem
        {
            SessionId = sessionId,
            CanImport = Services.SessionNotFound.CanImportCliSessions(),
        });
    }

    private static string? NotFoundSessionId(object sender) =>
        ((sender as FrameworkElement)?.DataContext as ViewModels.SessionNotFoundItem)?.SessionId;

    private void OnSessionNotFoundImport(object sender, RoutedEventArgs e) =>
        ImportCliSessionsRequested?.Invoke(this, EventArgs.Empty);

    private void OnSessionNotFoundArchive(object sender, RoutedEventArgs e)
    {
        if (NotFoundSessionId(sender) is { } id)
        {
            SessionArchiveRequested?.Invoke(this, id);
        }
    }

    private void OnSessionNotFoundDelete(object sender, RoutedEventArgs e)
    {
        if (NotFoundSessionId(sender) is { } id)
        {
            SessionDeleteRequested?.Invoke(this, id);
        }
    }

    private void OnErrorCardToggleDetails(object sender, RoutedEventArgs e)
    {
        if (ErrorCardOf(sender) is { } card)
        {
            card.DetailsExpanded = !card.DetailsExpanded;
        }
    }

    private async void OnErrorCardCopyDetails(object sender, RoutedEventArgs e)
    {
        if (ErrorCardOf(sender) is not { Details: { Length: > 0 } details } card)
        {
            return;
        }

        Controls.MarkdownView.TrySetClipboard(details);
        // The reference's own 1500ms before the button goes back to its label.
        card.Copied = true;
        await Task.Delay(1500);
        card.Copied = false;
    }

    private void OnErrorCardCompact(object sender, RoutedEventArgs e) => _ = _vm?.CompactAsync();

    /// <summary>Rewind from the card goes back to the last thing the user sent.</summary>
    private void OnErrorCardRewind(object sender, RoutedEventArgs e)
    {
        if (_vm?.Transcript.OfType<ViewModels.UserMessageItem>().LastOrDefault() is { } last)
        {
            _ = RewindMessageAsync(last);
        }
    }

    /// <summary>
    /// "When the model is overloaded, pick a different one right from the error
    /// card's retry menu": the card's switch opens the model menu, and the next
    /// send runs on whatever was picked.
    /// </summary>
    private void OnErrorCardSwitchModel(object sender, RoutedEventArgs e) => OpenModelMenu();

    private void OnErrorCardRetry(object sender, RoutedEventArgs e) => _ = _vm?.RetryLastTurnAsync();

    // ---- chapter dividers ----

    private static ViewModels.ChapterItem? ChapterOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ViewModels.ChapterItem;

    /// <summary>Double-click turns the title into a field, as the reference's does.</summary>
    private void OnChapterTitleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ChapterOf(sender) is { } chapter && chapter.DisplayTitle.Length > 0)
        {
            e.Handled = true;
            chapter.IsEditing = true;
        }
    }

    private void OnRenameChapter(object sender, RoutedEventArgs e)
    {
        if (ChapterOf(sender) is { } chapter)
        {
            chapter.IsEditing = true;
        }
    }

    private void OnHideChapter(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _services is null || ChapterOf(sender) is not { } chapter)
        {
            return;
        }

        var hidden = _services.UiSettings.Current.HiddenChapters;
        if (!hidden.Contains(chapter.Id, StringComparer.Ordinal))
        {
            hidden.Add(chapter.Id);
            _services.UiSettings.Save();
        }

        _vm.Transcript.Remove(chapter);
    }

    private void OnChapterEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void OnChapterEditorKey(object sender, KeyEventArgs e)
    {
        if (ChapterOf(sender) is not { } chapter)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitChapterTitle(chapter);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Escape restores whatever the divider read before the edit.
            chapter.DisplayTitle = _services is not null &&
                _services.UiSettings.Current.ChapterRenames.TryGetValue(chapter.Id, out var stored)
                    ? stored
                    : chapter.Title;
            chapter.IsEditing = false;
        }
    }

    private void OnChapterEditorCommit(object sender, RoutedEventArgs e)
    {
        if (ChapterOf(sender) is { } chapter && chapter.IsEditing)
        {
            CommitChapterTitle(chapter);
        }
    }

    /// <summary>
    /// The reference commits a trimmed, non-empty title that actually changed,
    /// and leaves the divider alone otherwise.
    /// </summary>
    private void CommitChapterTitle(ViewModels.ChapterItem chapter)
    {
        chapter.IsEditing = false;
        var title = chapter.DisplayTitle.Trim();
        if (_services is null || title.Length == 0)
        {
            chapter.DisplayTitle = chapter.Title;
            return;
        }

        chapter.DisplayTitle = title;
        if (title == chapter.Title)
        {
            _services.UiSettings.Current.ChapterRenames.Remove(chapter.Id);
        }
        else
        {
            _services.UiSettings.Current.ChapterRenames[chapter.Id] = title;
        }

        _services.UiSettings.Save();
    }

    private void OnStartTaskSuggestion(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _sessions is null ||
            (sender as FrameworkElement)?.Tag as string is not { } id)
        {
            return;
        }

        var source = _vm;
        if (source.Suggestions.BeginSpawn(id) is not { } suggestion)
        {
            source.RefreshTaskSuggestions();
            return;
        }

        source.RefreshTaskSuggestions();
        try
        {
            var cwd = suggestion.Cwd is { Length: > 0 } dir && System.IO.Directory.Exists(dir)
                ? dir
                : source.Session.WorkingDirectory;
            var spawned = _sessions.ForNewSession(cwd);
            if (suggestion.Title is { Length: > 0 } title)
            {
                spawned.Session.Title = title;
            }

            // Where the session came from, recorded on the session itself: that is
            // where the reference keeps it, and it is what lets the transcript
            // that suggested it still read "Started session" once the chip is
            // gone from the queue or the session has been reopened.
            _services.SessionGroups.RecordSpawnedFrom(spawned.Session.Id, source.Session.Id, id);
            Bind(spawned);
            SubmitExternal(suggestion.Prompt);
            source.Suggestions.CompleteSpawn(id, started: true);
            source.RefreshToolRuns();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the chip back rather than losing the suggestion.
            source.Suggestions.CompleteSpawn(id, started: false);
            source.Transcript.Add(new NoticeItem
            {
                Text = $"Could not start that task: {ex.Message}",
                IsError = true,
            });
        }

        source.RefreshTaskSuggestions();
    }

    /// <summary>
    /// "…or handle it here": the suggestion's own prompt goes into this
    /// session's composer instead of spinning off a session of its own.
    /// </summary>
    private void OnHandleTaskSuggestion(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as FrameworkElement)?.Tag as string is not { } id)
        {
            return;
        }

        var suggestion = _vm.Suggestions.Pending.FirstOrDefault(s => s.Id == id);
        if (suggestion is null)
        {
            return;
        }

        _vm.Suggestions.Dismiss(id);
        _vm.RefreshTaskSuggestions();
        InputBox.Text = suggestion.Prompt;
        InputBox.CaretIndex = InputBox.Text.Length;
        InputBox.Focus();
    }

    private void OnDismissTaskSuggestion(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as FrameworkElement)?.Tag as string is not { } id)
        {
            return;
        }

        _vm.Suggestions.Dismiss(id);
        _vm.RefreshTaskSuggestions();
    }

    // ---- prompt history (the reference's Ctrl+R search and Up/Down walk) ----

    private Services.ComposerHistory? _history;

    /// <summary>
    /// Ctrl+R opens the reverse search; while it or the Up/Down walk is open the
    /// arrows cycle, Escape puts the draft back and Enter keeps the match. Up on an
    /// empty composer starts the walk, which is where the reference starts it too.
    /// </summary>
    private bool HandleHistoryKey(KeyEventArgs e)
    {
        if (_services is null || _vm is null || CommandPopup.IsOpen || MentionPopup.IsOpen)
        {
            return false;
        }

        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _historyRichDraft = InputBox.Snapshot();
            _history = _services.ComposerHistory.Open();
            ApplyHistory(_history.BeginSearch(InputBox.Text));
            e.Handled = true;
            return true;
        }

        if (_history is { Mode: not Services.ComposerHistoryMode.Off } open)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    ApplyHistory(open.Cancel());
                    _history = null;
                    e.Handled = true;
                    return true;
                case Key.Up:
                    ApplyHistory(open.Mode == Services.ComposerHistoryMode.Search
                        ? open.Cycle(1)
                        : open.Navigate(1, InputBox.Text));
                    e.Handled = true;
                    return true;
                case Key.Down:
                    ApplyHistory(open.Mode == Services.ComposerHistoryMode.Search
                        ? open.Cycle(-1)
                        : open.Navigate(-1, InputBox.Text));
                    e.Handled = true;
                    return true;
                case Key.Enter when Keyboard.Modifiers == ModifierKeys.None
                                    && open.Mode == Services.ComposerHistoryMode.Search:
                    ApplyHistory(open.Accept());
                    _history = null;
                    e.Handled = true;
                    return true;
                default:
                    if (open.Mode == Services.ComposerHistoryMode.Search && e.Key is Key.Back)
                    {
                        ApplyHistory(open.Search(
                            _historyQuery.Length > 0 ? _historyQuery[..^1] : ""));
                        e.Handled = true;
                        return true;
                    }

                    break;
            }

            return false;
        }

        // A walk starts on Up with nothing typed, so an ordinary caret move is
        // never stolen from a message being written.
        if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None && InputBox.Text.Length == 0)
        {
            _history = _services.ComposerHistory.Open();
            ApplyHistory(_history.Navigate(1, ""));
            e.Handled = true;
            return true;
        }

        return false;
    }

    private string _historyQuery = "";

    /// <summary>Types into an open reverse search rather than into the message.</summary>
    private void OnInputTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_history is not { Mode: Services.ComposerHistoryMode.Search } search || e.Text.Length == 0)
        {
            return;
        }

        e.Handled = true;
        ApplyHistory(search.Search(_historyQuery + e.Text));
    }

    private void ApplyHistory(Services.ComposerHistoryView view)
    {
        _historyQuery = view.Query;
        if (view.Text is { } text)
        {
            if (view.Mode == Services.ComposerHistoryMode.Off && _historyRichDraft is { } draft && draft.Text == text)
                InputBox.Restore(draft);
            else InputBox.Text = text;
            InputBox.CaretIndex = text.Length;
        }

        if (view.Mode == Services.ComposerHistoryMode.Off)
        {
            _historyRichDraft = null;
            HistoryHint.Visibility = Visibility.Collapsed;
            _history = null;
            return;
        }

        HistoryHint.Visibility = Visibility.Visible;
        if (view.Mode == Services.ComposerHistoryMode.Search)
        {
            HistoryHintLabel.Text = Services.ComposerHistory.SearchHistory;
            HistoryHintLabel.SetResourceReference(
                TextBlock.ForegroundProperty, view.Failed ? "Danger100Brush" : "Text500Brush");
            HistoryHintQuery.Text = view.Query + "▎";
            HistoryHintKeys.Text = $"↑ ↓ {Services.ComposerHistory.CycleHint} · esc {Services.ComposerHistory.CancelHint}";
            System.Windows.Automation.AutomationProperties.SetName(
                HistoryHint,
                Services.ComposerHistory.SearchStatus(view.Query, view.Index, view.Total, view.Failed));
        }
        else
        {
            HistoryHintLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            HistoryHintLabel.Text = Services.ComposerHistory.NavigateLabel(view.Index, view.Total);
            HistoryHintQuery.Text = "";
            HistoryHintKeys.Text = view.HasDraft ? Services.ComposerHistory.RestoreDraft : "";
            System.Windows.Automation.AutomationProperties.SetName(HistoryHint, HistoryHintLabel.Text);
        }
    }

    // ---- the Chat surface's side chat ----

    private Chat.SideChatView? _sideChat;

    /// <summary>Whether the side chat is the drawer's current occupant.</summary>
    public bool IsSideChatOpen => _sideChat is not null && ActivityPanel.Visibility == Visibility.Visible
        && ReferenceEquals(ActivityDrawer.Content, _sideChat);

    /// <summary>
    /// The reference's side chat (Ctrl+; on Chat): a question answered beside the
    /// conversation, read-only over it.
    /// </summary>
    public void ToggleSideChat()
    {
        if (_vm is null || _services is null || _vm.IsCodeSurface)
        {
            return;
        }

        if (IsSideChatOpen)
        {
            ActivityPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (_sideChat is null)
        {
            _sideChat = new Chat.SideChatView();
            _sideChat.Initialize(_services, () => _vm!.Session);
            _sideChat.BranchRequested += (_, lines) => BranchSideChat(lines);
        }

        _sideChat.Refresh();
        ActivityPanelIcon.Text = "";
        ShowDrawer("Side Chat", "", _sideChat);
        _sideChat.FocusInput();
    }

    /// <summary>"Branch to new chat": the side conversation becomes a chat of its own.</summary>
    private void BranchSideChat(IReadOnlyList<Services.SideChatLine> lines)
    {
        if (_sessions is null)
        {
            return;
        }

        var branch = _sessions.ForNewSession(null);
        foreach (var line in lines)
        {
            branch.Session.Messages.Add(line.FromUser
                ? JarvisCode.Core.Models.ChatMessage.FromUserText(line.Text)
                : new JarvisCode.Core.Models.ChatMessage(
                    JarvisCode.Core.Models.Role.Assistant,
                    [new JarvisCode.Core.Models.TextBlock(line.Text)]));
        }

        ActivityPanel.Visibility = Visibility.Collapsed;
        Bind(branch);
    }

    // ---- the Chat surface's activity drawer ----

    /// <summary>Puts one thing in the drawer: a titled panel, or the activity lists.</summary>
    private void ShowDrawer(string title, string count, FrameworkElement? content)
    {
        ActivityPanelTitle.Text = title;
        ActivityPanelCount.Text = count;
        ActivityPanelCount.Visibility = count.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityDrawer.Content = content;
        ActivityDrawer.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible;
        ActivityPanelScroll.Visibility = content is null ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = Visibility.Visible;
    }

    private void OnSearchesChipClick(object sender, RoutedEventArgs e) => ShowActivityPanel(searches: true);

    private void OnToolCallsChipClick(object sender, RoutedEventArgs e) => ShowActivityPanel(searches: false);

    private void OnCloseActivityPanel(object sender, RoutedEventArgs e)
        => ActivityPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    /// The reference's detail panel: a title, a count line, and either the rows or
    /// its own centred empty state.
    /// </summary>
    private void ShowActivityPanel(bool searches)
    {
        if (_vm is null)
        {
            return;
        }

        ActivityPanelHost.Children.Clear();
        ActivityPanelIcon.Text = searches ? "" : "";
        ShowDrawer(
            searches
                ? Services.ChatActivityPanel.WebSearchTitle
                : Services.ChatActivityPanel.ToolCallCount(_vm.ToolCallCount),
            "",
            null);

        if (!searches)
        {
            ActivityPanelCount.Text = Services.ChatActivityPanel.ToolCallCount(_vm.ToolCallCount).ToUpperInvariant();
            ActivityPanelCount.Visibility = Visibility.Visible;
            foreach (var group in _vm.Transcript.OfType<ToolGroupItem>())
            {
                foreach (var call in group.Calls)
                {
                    ActivityPanelHost.Children.Add(ActivityRow(call.ToolName, call.Description, null));
                }
            }

            return;
        }

        // Newest first, as the reference sorts its own list.
        var found = _vm.Searches.Reverse().ToList();
        ActivityPanelCount.Text = Services.ChatActivityPanel.SearchCount(found.Count).ToUpperInvariant();
        ActivityPanelCount.Visibility = Visibility.Visible;
        if (found.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = Services.ChatActivityPanel.NoWebSearchesYet,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 16),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            ActivityPanelHost.Children.Add(empty);
            return;
        }

        foreach (var search in found)
        {
            ActivityPanelHost.Children.Add(ActivityRow(
                search.Query,
                Services.ChatActivityPanel.ResultCount(search.Results.Count),
                search.Results));
        }
    }

    private FrameworkElement ActivityRow(
        string title, string? subtitle, IReadOnlyList<Services.ChatSearchResult>? results)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var header = new TextBlock
        {
            Text = title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
        panel.Children.Add(header);

        if (subtitle is { Length: > 0 })
        {
            var secondary = new TextBlock { Text = subtitle, FontSize = 11.5 };
            secondary.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            panel.Children.Add(secondary);
        }

        foreach (var result in results ?? [])
        {
            var link = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var hyperlink = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(result.Title))
            {
                NavigateUri = Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) ? uri : null,
            };
            hyperlink.RequestNavigate += (_, args) =>
            {
                args.Handled = true;
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(args.Uri.ToString()) { UseShellExecute = true });
            };
            link.Inlines.Add(hyperlink);
            panel.Children.Add(link);

            if (result.Domain is { Length: > 0 } domain)
            {
                var site = new TextBlock { Text = domain, FontSize = 11 };
                site.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
                panel.Children.Add(site);
            }
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");
        return card;
    }

    /// <summary>
    /// Poses the Chat surface for a screen grab (the --open=chat flag). Nothing
    /// here runs a turn: each variant fills the state its own screen shows.
    /// </summary>
    public void ShowSampleChat(string variant)
    {
        if (_vm is null)
        {
            return;
        }

        switch (variant)
        {
            case "welcome":
                SetChatCount(0);
                break;

            case "greeting":
                // The ordinary empty screen, which only a second chat reaches: the
                // mascot beside the greeting rather than the first chat's onboarding.
                SetChatCount(1);
                break;

            case "incognito":
                StartIncognito();
                break;

            case "queue":
                _vm.QueuedMessages.Add(new QueuedMessageItem("Write the release notes for 2.4"));
                _vm.QueuedMessages.Add(new QueuedMessageItem("…then post them in the channel"));
                _vm.QueuedMessages.Add(new QueuedMessageItem(""));
                _vm.QueueExpanded = true;
                break;

            case "waiting":
                _vm.Transcript.Add(new UserMessageItem { Text = "Explain the compaction curve." });
                _vm.ChatStatus.BeginTurn(DateTimeOffset.Now.AddSeconds(-20), pick: 0);
                ChatStatus.Status = _vm.ChatStatus;
                ChatStatus.Refresh();
                UpdateLayoutState();
                break;

            case "compacting":
                _vm.Transcript.Add(new UserMessageItem { Text = "Keep going." });
                _vm.ChatStatus.BeginTurn(DateTimeOffset.Now.AddSeconds(-40), pick: 0);
                _vm.ChatStatus.OnCompacting(DateTimeOffset.Now.AddSeconds(-25));
                ChatStatus.Status = _vm.ChatStatus;
                ChatStatus.Refresh();
                UpdateLayoutState();
                break;

            case "error":
                _vm.Transcript.Add(new UserMessageItem { Text = "Summarise the diff." });
                _vm.Transcript.Add(new ChatErrorItem
                {
                    Text = Services.ChatTurnNotices.Interrupted,
                    Interrupted = true,
                });
                UpdateLayoutState();
                break;

            case "searches":
                _vm.Searches.Add(new Services.ChatSearch(
                    "wpf cubic bezier easing",
                    [
                        new Services.ChatSearchResult(
                            "Easing functions in WPF", "https://learn.microsoft.com/wpf/easing", "learn.microsoft.com"),
                        new Services.ChatSearchResult(
                            "cubic-bezier() - CSS", "https://developer.mozilla.org/cubic-bezier", "developer.mozilla.org"),
                    ]));
                _vm.Transcript.Add(new UserMessageItem { Text = "How does the reference ease that bar?" });
                UpdateActivityChips();
                UpdateLayoutState();
                ShowActivityPanel(searches: true);
                break;

            case "empty-searches":
                UpdateLayoutState();
                ShowActivityPanel(searches: true);
                break;

            case "sidechat":
                _vm.Transcript.Add(new UserMessageItem { Text = "Draft the release note." });
                _vm.Session.Messages.Add(
                    JarvisCode.Core.Models.ChatMessage.FromUserText("Draft the release note."));
                UpdateLayoutState();
                ToggleSideChat();
                break;
        }
    }

    /// <summary>Shows or hides the activity chips for what this conversation has done.</summary>
    private void UpdateActivityChips()
    {
        if (_vm is null || _vm.IsCodeSurface)
        {
            SearchesChip.Visibility = Visibility.Collapsed;
            ToolCallsChip.Visibility = Visibility.Collapsed;
            return;
        }

        SearchesChipText.Text = Services.ChatActivityPanel.SearchCount(_vm.Searches.Count);
        SearchesChip.Visibility = _vm.Searches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolCallsChipText.Text = Services.ChatActivityPanel.ToolCallCount(_vm.ToolCallCount);
        ToolCallsChip.Visibility = _vm.ToolCallCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly Services.ReadAloud _readAloud = new();
    private ViewModels.AssistantTextItem? _reading;

    /// <summary>
    /// "Read aloud" on an answer, and "Pause" on the one being read. Reading a
    /// second answer stops the first, as one voice must.
    /// </summary>
    private void ReadAloudResponse(ViewModels.AssistantTextItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (_reading is { } previous && !ReferenceEquals(previous, item))
        {
            _readAloud.Stop();
            previous.IsReading = false;
        }

        if (_readAloud.Toggle(item.Markdown) is { } error)
        {
            _vm?.Transcript.Add(new NoticeItem { Text = error, IsError = true });
            return;
        }

        item.IsReading = _readAloud.IsSpeaking;
        _reading = _readAloud.IsSpeaking ? item : null;
    }

    /// <summary>"Edit prompt" on the unfinished-turn card: the prompt goes back to the composer.</summary>
    private void OnChatErrorEditPrompt(object sender, RoutedEventArgs e) => _ = EditLastPromptAsync();

    private async Task EditLastPromptAsync()
    {
        if (_vm?.Transcript.OfType<UserMessageItem>().LastOrDefault() is { } last)
        {
            await RewindMessageAsync(last);
        }
    }

    /// <summary>"Try again" on the unfinished-turn card.</summary>
    private void OnChatErrorRetry(object sender, RoutedEventArgs e) => _ = _vm?.RetryLastTurnAsync();

    private void OnQueuedHeaderClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.QueueExpanded = !_vm.QueueExpanded;
        }
    }

    /// <summary>
    /// The reference's queued-row menu: edit the message back into the composer,
    /// send it now, or take it out of the queue — the last one destructive, and on
    /// Chat behind the confirmation the reference asks for.
    /// </summary>
    private void OnQueuedRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: ViewModels.QueuedMessageItem item } source)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = source,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var edit = new MenuItem { Header = Services.ChatQueue.EditInComposer };
        edit.Click += (_, _) =>
        {
            _vm.QueuedMessages.Remove(item);
            PrefillInput(item.Text);
        };
        menu.Items.Add(edit);

        var sendNow = new MenuItem { Header = Services.ChatQueue.SendNow };
        sendNow.Click += (_, _) =>
        {
            if (_vm.QueuedMessages.Remove(item))
            {
                _vm.QueuedMessages.Insert(0, item);
            }
        };
        menu.Items.Add(sendNow);

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = Services.ChatQueue.RemoveFromQueue };
        remove.Click += (_, _) => DiscardQueued(item);
        menu.Items.Add(remove);

        menu.IsOpen = true;
    }

    /// <summary>
    /// Takes a message out of the queue. The Chat surface asks first, which is what
    /// the reference's own queue rows do there — the Code composer's menu does not.
    /// </summary>
    private void DiscardQueued(ViewModels.QueuedMessageItem item)
    {
        if (_vm is null)
        {
            return;
        }

        if (!_vm.IsCodeSurface && !ConfirmDialog.Ask(
                Window.GetWindow(this),
                Services.ChatQueue.DiscardTitle,
                Services.ChatQueue.DiscardBody,
                Services.ChatQueue.Discard,
                focusCancel: true))
        {
            return;
        }

        _vm.QueuedMessages.Remove(item);
    }

    /// <summary>The composer's coordinator toggle — delegate work across parallel agents.</summary>
    private void OnCoordinatorClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _services is null || !_vm.IsCodeSurface)
        {
            return;
        }

        var on = !_vm.CoordinatorMode;
        _vm.CoordinatorMode = on;
        _services.SessionGroups.SetCoordinator(_vm.Session.Id, on);
        UpdateCoordinatorButton();
    }

    private void UpdateCoordinatorButton()
    {
        var isCode = _vm?.IsCodeSurface == true;
        CoordinatorButton.Visibility = isCode ? Visibility.Visible : Visibility.Collapsed;
        if (!isCode)
        {
            return;
        }

        if (_vm!.CoordinatorMode)
        {
            CoordinatorGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrandBrush");
            CoordinatorButton.ToolTip = "Turn off coordinator mode";
        }
        else
        {
            CoordinatorGlyph.ClearValue(TextBlock.ForegroundProperty);
            CoordinatorButton.ToolTip = "Delegate work across parallel agents";
        }
    }

    /// <summary>Ctrl+Alt+Enter: fork the session and send the typed prompt in the fork.</summary>
    private async Task SendInForkedSessionAsync()
    {
        if (_vm is null || _services is null || !_vm.IsCodeSurface || _vm.IsRunning)
        {
            return;
        }

        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _services.Toasts.AddSuccess(Services.ToastText.Forking);
        var (fork, _) = await Services.SessionActions.ForkAsync(_services, _vm.Session);
        if (fork is null)
        {
            _services.Toasts.AddError(Services.ToastText.ForkUnavailable);
            return;
        }

        InputBox.Clear();
        LoadSession(fork);
        SessionPersisted?.Invoke(this, EventArgs.Empty);
        await SubmitTextAsync(text);
    }

    /// <summary>/color — a per-session composer accent stored in ui-settings.</summary>
    private void ApplyPromptColor()
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        if (_services.UiSettings.Current.SessionPromptColors.TryGetValue(_vm.Session.Id, out var value) &&
            TryParseColor(value) is { } color)
        {
            ComposerBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(color);
        }
        else
        {
            ComposerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        }
    }

    private static System.Windows.Media.Color? TryParseColor(string value)
    {
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidCastException)
        {
            return null;
        }
    }

    private void UpdateModeChip()
    {
        if (_vm is null)
        {
            return;
        }

        ModeChipText.Text = Services.PermissionModeMenu.Describe(_vm.Gate.Mode).Label;
        // The reference's chip carries the group label and the chord it opens on,
        // except while the session sits in a mode that stops asking — there it says
        // what that costs instead.
        ModeChip.ToolTip = Services.PermissionModeMenu.IsDangerous(_vm.Gate.Mode, AutoIsDefaultMode)
            ? Services.PermissionModeMenu.DangerTooltip
            : $"{Services.PermissionModeMenu.Header} (Ctrl+Shift+M)";
    }

    // ---- input handling ----

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // An image-only or file clipboard never raises TextBox's Pasting event,
        // so Ctrl+V has to pick those up itself.
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && HandleSpecialPaste())
        {
            e.Handled = true;
            return;
        }

        if (HandleHistoryKey(e))
        {
            return;
        }

        // Ctrl+Alt+Enter sends in a forked session (Alt chords ride Key.System).
        var chordKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (chordKey == Key.Enter && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            e.Handled = true;
            _ = SendInForkedSessionAsync();
            return;
        }

        if (CommandPopup.IsOpen)
        {
            // Escape closes before anything else, and a list with nothing
            // selectable in it takes no other key.
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _commandDismissed = true;
                CloseCommandPopup();
                return;
            }

            if (_commandItems.Count > 0 && !_commandItems.All(Services.SlashMenuFilter.IsNonSelectable))
            {
                switch (e.Key)
                {
                    case Key.Down:
                    case Key.Up:
                        e.Handled = true;
                        _commandKeyboardMode = true;
                        _commandMouseMoved = false;
                        SelectCommandRow(
                            e.Key == Key.Down
                                ? Services.SlashMenuFilter.NextSelectable(_commandItems, _commandSelection)
                                : Services.SlashMenuFilter.PreviousSelectable(_commandItems, _commandSelection),
                            keyboard: true);
                        return;

                    // The reference activates on Tab exactly as it does on Enter.
                    case Key.Tab:
                    case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                        e.Handled = true;
                        _commandKeyboardMode = true;
                        _commandMouseMoved = false;
                        ActivateCommandSelection();
                        return;
                }
            }
        }

        if (MentionPopup.IsOpen && MentionContext() is { } context)
        {
            var options = VisibleMentions(context.Query);
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    _mentionSelection = Math.Min(_mentionSelection + 1, options.Count - 1);
                    RenderMentionPopup(options, context.Start);
                    return;
                case Key.Up:
                    e.Handled = true;
                    _mentionSelection = Math.Max(_mentionSelection - 1, 0);
                    RenderMentionPopup(options, context.Start);
                    return;
                case Key.Escape:
                    e.Handled = true;
                    MentionPopup.IsOpen = false;
                    return;
                case Key.Tab:
                case Key.Enter when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                    e.Handled = true;
                    if (_mentionSelection < options.Count)
                    {
                        InsertMention(context.Start, options[_mentionSelection]);
                    }

                    return;
            }
        }

        // While the approval card is open its own shortcuts win over the composer's Enter.
        if (_vm?.PendingPermission is { } awaiting && ApprovalShortcut(awaiting, e.Key) is { } chosen)
        {
            e.Handled = true;
            chosen.Execute(null);
            return;
        }

        // "Press a number key to pick an option when Jarvis asks a question."
        if (_vm?.PendingQuestion is { } asking && QuestionShortcut(asking, e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _vm is { IsRunning: true } live)
        {
            // The reference's mid-turn secondary action, "Interrupt · cmd+enter":
            // Enter alone sends into the queue, so stopping needs a chord of its own.
            e.Handled = true;
            live.CancelTurn();
        }
        else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            Send();
        }
        else if (e.Key == Key.Escape && _vm?.PendingPermission is { } pending)
        {
            e.Handled = true;
            pending.Resolve(JarvisCode.Core.Agent.PermissionDecision.Deny);
        }
        else if (e.Key == Key.Escape && _vm is { IsRunning: true })
        {
            // The sheet's "Stop Jarvis's response": Escape stops the turn once
            // there is no card in front of it waiting for an answer.
            e.Handled = true;
            _vm.CancelTurn();
        }
    }

    /// <summary>
    /// The approval card's own shortcuts: Ctrl+Enter and Ctrl+Shift+Enter for the two
    /// allow buttons, plus the digit printed on each button. Digits only count while the
    /// composer is empty, so a message that starts with one can still be typed.
    /// </summary>
    private RelayCommand? ApprovalShortcut(PermissionPromptViewModel prompt, Key key)
    {
        if (key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? prompt.AlwaysAllowOption?.Command
                : prompt.AllowOnceOption.Command;
        }

        if (Keyboard.Modifiers != ModifierKeys.None || InputBox.Text.Length > 0)
        {
            return null;
        }

        int digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
            _ => 0,
        };
        return digit == 0 ? null : prompt.OptionForNumber(digit.ToString())?.Command;
    }

    /// <summary>
    /// The digit printed on each option of a question card. Like the approval
    /// card's, digits only count while the composer is empty, so a message that
    /// starts with one can still be typed, and only a single-question card takes
    /// them — with several questions up there is no one list to number.
    /// </summary>
    private bool QuestionShortcut(QuestionPromptViewModel prompt, Key key)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || InputBox.Text.Length > 0 || prompt.Questions.Count != 1)
        {
            return false;
        }

        int digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
            _ => 0,
        };
        var question = prompt.Questions[0];
        if (digit == 0 || digit > question.Options.Count)
        {
            return false;
        }

        question.Options[digit - 1].PickCommand.Execute(null);
        return true;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Placeholder is null || SendButton is null || CommandPopup is null || MentionPopup is null) return;
        ScheduleComposerDraft();
        UpdateSkillArgumentHint();
        if (InputBox.ArgumentHint.Length > 0) Dispatcher.BeginInvoke(UpdateSkillArgumentHint, DispatcherPriority.Loaded);
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendButton();
        UpdateCommandPopup();
        UpdateMentionPopup();
    }

    /// <summary>The @-token context follows the caret, not just the text.</summary>
    private void OnInputSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (CommandPopup is null || MentionPopup is null) return;
        UpdateSkillArgumentHint();
        UpdateMentionPopup();

        // The caret hint is placed at the caret and only shown at the end of the
        // text, so it has to follow the caret and not only the text: a
        // programmatic edit moves the two in that order.
        UpdateFilterGhost();
    }

    // ---- slash commands ----
    //
    // The composer's command menu, ported from the reference desktop's
    // CCDSlashCommandMenu. The filter, ordering and navigation rules live in
    // Services/SlashMenu.cs so they can be checked against a recording of the
    // reference's own code; what is here is the rendering and the input.

    /// <summary>Every command as a menu row; rebuilt with the command list, not per keystroke.</summary>
    private List<Services.SlashMenuItem> _commandSource = [];

    /// <summary>The rows currently shown, and the element rendering each.</summary>
    private List<Services.SlashMenuItem> _commandItems = [];
    private readonly List<FrameworkElement?> _commandRows = [];

    /// <summary>The query behind the current rows: everything after the leading slash.</summary>
    private string _commandQuery = "";
    private int _commandRangeStart;
    private int _commandRangeLength;

    /// <summary>The row the user last moved to with the keyboard (the reference's explicit selection).</summary>
    private string? _commandNavigatedTo;

    /// <summary>
    /// The reference only lets hover move the selection once the mouse has
    /// actually moved, so the row under a resting cursor cannot steal it while
    /// the user is arrowing through the list.
    /// </summary>
    private bool _commandMouseMoved;
    private bool _commandKeyboardMode = true;

    /// <summary>The row a mouse press landed on, so a click is a press and release on the same row.</summary>
    private int _commandPressedRow = -1;

    /// <summary>Set while the menu writes the composer itself, so its own edit does not reopen it.</summary>
    private bool _commandCompleting;

    /// <summary>Set by Escape; the menu stays shut until the command line is left.</summary>
    private bool _commandDismissed;

    /// <summary>The reference's geometry for this menu, at the density it runs at.</summary>
    private const double CommandRowHeight = 32;
    private const double CommandRowPaddingX = 10;
    private const double CommandRowGap = 8;
    private const double CommandMenuMaxHeight = 384;
    private const double CommandMenuGapAboveCaret = 10;
    private const double CommandMenuCaretInset = 16;
    private const double CommandMenuViewportPadding = 8;

    /// <summary>Everything the user typed after the leading slash, or null when this is not a command line.</summary>
    private string? CommandQueryText()
    {
        if (InputBox.SlashQuery() is not { } query) return null;
        _commandRangeStart = query.Start;
        _commandRangeLength = query.Length;
        return query.Query;
    }

    /// <summary>Every command as a row the menu can rank and render.</summary>
    private List<Services.SlashMenuItem> BuildCommandMenuItems() =>
    [
        .. _slashCommands.Select(static command => new Services.SlashMenuItem
        {
            Kind = Services.SlashMenuItemKind.Skill,
            Label = command.Name,
            SkillId = command.Name,
            SkillDescription = command.Description,
            ArgumentHint = command.Hint ?? "",
            Aliases = command.Aliases ?? [],
            OnAction = command.Execute,
        }),
    ];

    private void CloseCommandPopup()
    {
        CommandPopup.IsOpen = false;
        CommandDescriptionPopup.IsOpen = false;
        FilterGhost.Visibility = Visibility.Collapsed;
        _commandItems = [];
        _commandRows.Clear();
        _commandNavigatedTo = null;
        _commandPressedRow = -1;
    }

    private void UpdateCommandPopup()
    {
        if (_commandCompleting)
        {
            _commandCompleting = false;
            CloseCommandPopup();
            return;
        }

        var query = CommandQueryText();
        if (query is null)
        {
            _commandDismissed = false;
            CloseCommandPopup();
            return;
        }

        // Escape shuts the menu until the command line is left, the way the
        // reference's suggestion plugin stays dismissed rather than reopening
        // on the next character.
        if (_commandDismissed)
        {
            return;
        }

        // The reference resolves a known skill once its arguments begin.
        // Keep those arguments verbatim, and wait until an IME composition ends.
        var separator = query.IndexOf(' ');
        if (separator > 0 && !InputBox.IsComposing && ResolveComposerSkill(query[..separator]) is { } typedSkill)
        {
            var text = InputBox.Text;
            var caret = InputBox.CaretIndex;
            var start = _commandRangeStart;
            var length = _commandRangeLength;
            Dispatcher.BeginInvoke(() =>
            {
                if (_commandDismissed || InputBox.Text != text || InputBox.CaretIndex != caret || InputBox.IsComposing || InputBox.SlashQuery() is null) return;
                _commandCompleting = true;
                InputBox.InsertSkill(typedSkill, start, length, query[separator..]);
            });
            return;
        }

        // Skills and custom commands may have changed on disk since the last open.
        if (!CommandPopup.IsOpen)
        {
            BuildSlashCommands();
            _commandSource = BuildCommandMenuItems();
        }

        var visible = Services.SlashMenuFilter.Filter(_commandSource, query);
        if (visible.Count == 0)
        {
            CloseCommandPopup();
            return;
        }

        // A new query starts on the first selectable row; the same query keeps
        // the row it was on, found again by identity.
        var previous = _commandSelection >= 0 && _commandSelection < _commandItems.Count
            ? Services.SlashMenuFilter.Identity(_commandItems[_commandSelection])
            : null;
        var queryChanged = !string.Equals(query, _commandQuery, StringComparison.Ordinal);

        _commandQuery = query;
        _commandItems = [.. visible];

        if (queryChanged)
        {
            _commandNavigatedTo = null;
            _commandSelection = Services.SlashMenuFilter.FirstSelectable(_commandItems);
        }
        else
        {
            var found = previous is null
                ? -1
                : _commandItems.FindIndex(item => Services.SlashMenuFilter.Identity(item) == previous);
            _commandSelection = found >= 0 ? found : Services.SlashMenuFilter.FirstSelectable(_commandItems);
        }

        RenderCommandPopup();
        CapCommandPopupHeight();
        CommandPopup.IsOpen = true;

        // The card is placed from the highlighted row's position inside the
        // menu, which is only known once the menu has been laid out.
        CommandPopupSurface.UpdateLayout();
        UpdateCommandDescriptionCard();
        UpdateFilterGhost();
    }

    /// <summary>
    /// The reference caps the menu at 384 and, below that, at the space above
    /// the caret less its own padding. Measured in the window's units rather
    /// than the screen's, so a scaled display does not shrink it.
    /// </summary>
    private void CapCommandPopupHeight()
    {
        UIElement reference = Window.GetWindow(this) ?? (UIElement)this;
        double above;
        try
        {
            above = InputBox.TranslatePoint(new Point(0, CaretRectInComposer().Top), reference).Y;
        }
        catch (InvalidOperationException)
        {
            // Not in one tree yet; the reference's own cap still applies.
            above = CommandMenuMaxHeight + CommandMenuGapAboveCaret + (2 * CommandMenuViewportPadding);
        }

        CommandPopupSurface.MaxHeight = Math.Max(
            48,
            Math.Min(
                CommandMenuMaxHeight,
                above - CommandMenuGapAboveCaret - (2 * CommandMenuViewportPadding)));
    }

    /// <summary>
    /// The reference anchors the menu to the typed slash — its top-start
    /// placement, ten above and sixteen left — and does not flip it below the
    /// composer when the space above runs short, only shifts it into view.
    /// </summary>
    private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceCommandPopup(Size popupSize, Size targetSize, Point offset)
    {
        var caret = CaretRectInComposer(_commandRangeStart);
        return
        [
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point(caret.Left - CommandMenuCaretInset, caret.Top - CommandMenuGapAboveCaret - popupSize.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
        ];
    }

    /// <summary>
    /// Where a character sits inside the composer, in the composer's own
    /// coordinates. The menu hangs off the typed slash and the caret hint off
    /// the caret, which are two different positions once anything is typed.
    /// </summary>
    private Rect CaretRectInComposer(int characterIndex = 0)
    {
        try
        {
            var rect = InputBox.GetRectFromCharacterIndex(Math.Clamp(characterIndex, 0, InputBox.Text.Length));
            if (!double.IsInfinity(rect.Left) && !double.IsInfinity(rect.Top) && !rect.IsEmpty)
            {
                return rect;
            }
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or InvalidOperationException)
        {
            // No layout yet; fall back to the box's own origin.
        }

        return new Rect(0, 0, 0, InputBox.ActualHeight);
    }

    private void RenderCommandPopup()
    {
        CommandListHost.Children.Clear();
        _commandRows.Clear();

        var query = _commandQuery.Trim().ToLowerInvariant();
        for (var i = 0; i < _commandItems.Count; i++)
        {
            var row = BuildCommandRow(_commandItems[i], i, query);
            _commandRows.Add(row);
            if (row is not null)
            {
                CommandListHost.Children.Add(row);
            }
        }
    }

    private FrameworkElement? BuildCommandRow(Services.SlashMenuItem item, int index, string query)
    {
        switch (item.Kind)
        {
            case Services.SlashMenuItemKind.Separator:
            {
                var rule = new Border { Height = 1, Margin = new Thickness(CommandRowPaddingX, 4, CommandRowPaddingX, 4) };
                rule.SetResourceReference(Border.BackgroundProperty, "BorderMidBrush");
                return rule;
            }

            case Services.SlashMenuItemKind.SectionHeader:
            {
                var header = new TextBlock
                {
                    Text = item.Label,
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    Margin = new Thickness(CommandRowPaddingX, 4, CommandRowPaddingX, 4),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                header.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
                return header;
            }

            case Services.SlashMenuItemKind.Loading:
            {
                // Nothing in this build produces one — the command list is read
                // synchronously — but the row keeps the reference's footprint.
                var placeholder = new Border
                {
                    Height = 16,
                    Width = 128,
                    CornerRadius = new CornerRadius(4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(CommandRowPaddingX, 8, CommandRowPaddingX, 8),
                };
                placeholder.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
                return placeholder;
            }
        }

        var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        AppendHighlighted(label, item.Label, query);
        content.Children.Add(label);

        // The alias the query actually matched, which is how the reference
        // explains a row whose name looks unrelated to what was typed.
        if (Services.SlashMenuFilter.MatchedAlias(item.Aliases, query) is { } alias)
        {
            var aliasText = new TextBlock
            {
                Text = "(" + alias + ")",
                FontSize = 14,
                Margin = new Thickness(CommandRowGap, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            aliasText.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            Grid.SetColumn(aliasText, 1);
            content.Children.Add(aliasText);
        }

        if (!string.IsNullOrEmpty(item.Subtitle))
        {
            var subtitle = new TextBlock
            {
                Text = item.Subtitle,
                FontSize = 12,
                Margin = new Thickness(CommandRowGap, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            Grid.SetColumn(subtitle, 2);
            content.Children.Add(subtitle);
        }

        var border = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(8),
            Height = CommandRowHeight,
            Padding = new Thickness(CommandRowPaddingX, 0, CommandRowPaddingX, 0),
            Background = System.Windows.Media.Brushes.Transparent,
        };

        if (index == _commandSelection)
        {
            border.SetResourceReference(Border.BackgroundProperty, "SelectedOverlayBrush");
        }

        border.MouseMove += (_, _) => OnCommandRowMouseMove(index);
        border.MouseEnter += (_, _) => OnCommandRowHover(index);
        border.PreviewMouseLeftButtonDown += (_, args) =>
        {
            // Pressing a row must not take focus off the composer.
            args.Handled = true;
            _commandPressedRow = index;
        };
        border.PreviewMouseLeftButtonUp += (_, args) =>
        {
            args.Handled = true;
            var pressed = _commandPressedRow;
            _commandPressedRow = -1;
            if (pressed != index)
            {
                return;
            }

            _commandSelection = index;
            ActivateCommandSelection();
        };

        return border;
    }

    /// <summary>Renders the label with the query's first occurrence in bold, as the reference does.</summary>
    private static void AppendHighlighted(TextBlock target, string text, string query)
    {
        target.Inlines.Clear();
        if (Services.SlashMenuFilter.HighlightRange(text, query) is not { } range)
        {
            target.Inlines.Add(new System.Windows.Documents.Run(text));
            return;
        }

        if (range.Start > 0)
        {
            target.Inlines.Add(new System.Windows.Documents.Run(text[..range.Start]));
        }

        target.Inlines.Add(new System.Windows.Documents.Run(text[range.Start..range.End]) { FontWeight = FontWeights.SemiBold });
        if (range.End < text.Length)
        {
            target.Inlines.Add(new System.Windows.Documents.Run(text[range.End..]));
        }
    }

    private void OnCommandRowMouseMove(int index)
    {
        if (_commandMouseMoved)
        {
            return;
        }

        _commandMouseMoved = true;
        _commandKeyboardMode = false;
        SelectCommandRow(index, keyboard: false);
    }

    private void OnCommandRowHover(int index)
    {
        if (_commandKeyboardMode && !_commandMouseMoved)
        {
            return;
        }

        _commandKeyboardMode = false;
        SelectCommandRow(index, keyboard: false);
    }

    private void SelectCommandRow(int index, bool keyboard)
    {
        if (index < 0 || index >= _commandItems.Count || index == _commandSelection)
        {
            return;
        }

        var previous = _commandSelection;
        _commandSelection = index;
        if (keyboard)
        {
            _commandNavigatedTo = Services.SlashMenuFilter.Identity(_commandItems[index]);
        }

        PaintCommandHighlight(previous, index);
        UpdateCommandDescriptionCard();
        if (keyboard && index < _commandRows.Count)
        {
            _commandRows[index]?.BringIntoView();
        }
    }

    /// <summary>Moves the highlight between two rows without rebuilding the list.</summary>
    private void PaintCommandHighlight(int previous, int current)
    {
        if (previous >= 0 && previous < _commandRows.Count && _commandRows[previous] is Border was)
        {
            was.Background = System.Windows.Media.Brushes.Transparent;
        }

        if (current >= 0 && current < _commandRows.Count && _commandRows[current] is Border now)
        {
            now.SetResourceReference(Border.BackgroundProperty, "SelectedOverlayBrush");
        }
    }

    /// <summary>
    /// The reference's Enter rule: a row the user never moved to only runs when
    /// it still answers what was typed, so a fuzzy match cannot be run by a
    /// return the user meant for something else.
    /// </summary>
    private void ActivateCommandSelection()
    {
        var item = _commandSelection >= 0 && _commandSelection < _commandItems.Count
            ? _commandItems[_commandSelection]
            : null;
        if (item is null || Services.SlashMenuFilter.IsNonSelectable(item))
        {
            CloseCommandPopup();
            return;
        }

        var query = _commandQuery.Trim().ToLowerInvariant();
        var chosenExplicitly = _commandNavigatedTo is not null
            && _commandNavigatedTo == Services.SlashMenuFilter.Identity(item);
        if (!chosenExplicitly && query.Length > 0 && !Services.SlashMenuFilter.LooselyMatches(item, query))
        {
            CloseCommandPopup();
            return;
        }

        CompleteCommand(item);
    }

    /// <summary>
    /// Selecting a row completes the composer rather than running the command:
    /// the reference replaces the typed text with the command and a trailing
    /// space, and submitting is what runs it.
    /// </summary>
    private void CompleteCommand(Services.SlashMenuItem item)
    {
        CloseCommandPopup();
        _commandCompleting = true;
        var id = item.SkillId.Length > 0 ? item.SkillId : item.Label;
        var separator = _commandQuery.IndexOf(' ');
        InputBox.InsertSkill(new Services.ComposerSkillChip(id, item.Label, item.SkillDescription, item.ArgumentHint),
            _commandRangeStart, Math.Min(_commandRangeLength, InputBox.Text.Length - _commandRangeStart),
            separator >= 0 ? _commandQuery[separator..] : null);
        InputBox.Focus();
    }

    private void UpdateCommandDescriptionCard()
    {
        var item = _commandSelection >= 0 && _commandSelection < _commandItems.Count
            ? _commandItems[_commandSelection]
            : null;

        var hasCard = item is { Kind: Services.SlashMenuItemKind.Skill }
            && (!string.IsNullOrEmpty(item.SkillDescription)
                || !string.IsNullOrEmpty(item.SourcePluginName)
                || !string.IsNullOrEmpty(item.SourceRepo));

        var row = _commandSelection >= 0 && _commandSelection < _commandRows.Count
            ? _commandRows[_commandSelection]
            : null;

        if (!hasCard || row is null || !CommandPopup.IsOpen)
        {
            CommandDescriptionPopup.IsOpen = false;
            return;
        }

        AppendHighlighted(CommandDescriptionText, item!.SkillDescription, _commandQuery.Trim().ToLowerInvariant());
        CommandDescriptionText.Visibility = string.IsNullOrEmpty(item.SkillDescription)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (!string.IsNullOrEmpty(item.SourceRepo))
        {
            CommandDescriptionSource.Text = $"via {item.SourceRepo}";
            CommandDescriptionSource.Visibility = Visibility.Visible;
        }
        else if (!string.IsNullOrEmpty(item.SourcePluginName))
        {
            CommandDescriptionSource.Text = $"{item.SourcePluginName} plugin";
            CommandDescriptionSource.Visibility = Visibility.Visible;
        }
        else
        {
            CommandDescriptionSource.Visibility = Visibility.Collapsed;
        }

        CommandDescriptionSource.Margin = new Thickness(
            0, CommandDescriptionText.Visibility == Visibility.Visible ? 4 : 0, 0, 0);

        // The card rides the menu's own edge, aligned to the highlighted row —
        // the same place anchoring to the row puts it, since a row spans the
        // menu — and hides when scrolling takes that row out of view.
        CommandDescriptionPopup.PlacementTarget = CommandPopupSurface;
        double top;
        try
        {
            top = row.TranslatePoint(new Point(0, 0), CommandPopupSurface).Y;
        }
        catch (InvalidOperationException)
        {
            CommandDescriptionPopup.IsOpen = false;
            return;
        }

        var height = CommandPopupSurface.ActualHeight;
        if (height > 0 && (top < 0 || top > height))
        {
            CommandDescriptionPopup.IsOpen = false;
            return;
        }

        CommandDescriptionPopup.VerticalOffset = top;
        CommandDescriptionPopup.IsOpen = true;
    }

    /// <summary>
    /// The reference's hint after the caret while the slash carries no query:
    /// only at the end of the text, and only when it fits before the box's edge.
    /// </summary>
    private void UpdateFilterGhost()
    {
        if (!CommandPopup.IsOpen
            || _commandQuery.Length != 0
            || InputBox.CaretIndex != InputBox.Text.Length)
        {
            FilterGhost.Visibility = Visibility.Collapsed;
            return;
        }

        // A collapsed element measures to nothing, so it has to be shown before
        // it can be measured — otherwise both the fit test and the centring
        // read zero and the hint lands half a line low.
        FilterGhost.Visibility = Visibility.Visible;
        FilterGhost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var caret = CaretRectInComposer(InputBox.CaretIndex);
        var left = caret.Left + 2;
        if (InputBox.ActualWidth - left < FilterGhost.DesiredSize.Width + 8)
        {
            FilterGhost.Visibility = Visibility.Collapsed;
            return;
        }

        FilterGhostOffset.X = left;
        FilterGhostOffset.Y = caret.Top + ((caret.Height - FilterGhost.DesiredSize.Height) / 2);
    }

    // ---- dev/verification hooks for --slash-selftest ----

    internal System.Windows.Controls.Primitives.Popup CommandMenuPopup => CommandPopup;

    internal Border CommandMenuSurface => CommandPopupSurface;

    internal System.Windows.Controls.Primitives.Popup CommandMenuDescriptionCard => CommandDescriptionPopup;

    internal TextBlock CommandMenuDescriptionText => CommandDescriptionText;

    internal TextBlock CommandMenuFilterGhost => FilterGhost;

    internal IReadOnlyList<Services.SlashMenuItem> CommandMenuItems => _commandItems;

    internal IReadOnlyList<FrameworkElement?> CommandMenuRows => _commandRows;

    internal int CommandMenuSelection => _commandSelection;

    internal string ComposerText => InputBox.Text;

    /// <summary>Poses the menu the way typing does, through the real text-changed path.</summary>
    internal void PoseCommandMenu(string text) => PrefillInput(text);

    /// <summary>Sends a key to the composer's own handler, so the menu's key rules are the ones exercised.</summary>
    internal void SendCommandMenuKey(Key key)
    {
        var source = PresentationSource.FromVisual(InputBox);
        if (source is null)
        {
            return;
        }

        OnInputKeyDown(
            InputBox,
            new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
    }

    // ---- @-mention file/folder completion ----

    private IReadOnlyList<string> _mentionEntries = [];
    private string _mentionRoot = "";
    private DateTime _mentionLoadedAt;
    private bool _mentionLoading;
    private int _mentionSelection;

    /// <summary>
    /// The bare @-token under the caret: an '@' at the start of the text or after
    /// whitespace, with no whitespace between it and the caret.
    /// </summary>
    private (int Start, string Query)? MentionContext()
    {
        var text = InputBox.Text;
        var caret = Math.Min(InputBox.CaretIndex, text.Length);
        if (caret == 0 || text.StartsWith('/'))
        {
            return null;
        }

        var at = text.LastIndexOf('@', caret - 1);
        if (at < 0 || (at > 0 && !char.IsWhiteSpace(text[at - 1])))
        {
            return null;
        }

        var segment = text[(at + 1)..caret];
        if (segment.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return (at, segment.TrimStart('"'));
    }

    private IReadOnlyList<string> _peerMentionEntries = [];

    /// <summary>
    /// The open windows the @-menu offers, by the name it shows. Pointing at one
    /// is what the reference's not-installed guidance tells the user to do — "type
    /// @ followed by the app name … that path does not depend on the index" — and
    /// the pick rides the next message as a &lt;cu_window_hints&gt; block.
    /// </summary>
    private IReadOnlyDictionary<string, Services.WindowMention> _windowMentions =
        new Dictionary<string, Services.WindowMention>(StringComparer.OrdinalIgnoreCase);
    private DateTime _peerMentionsLoadedAt;
    private bool _peerMentionsLoading;

    /// <summary>
    /// Refreshes the peers a mention may name (teammates and other local
    /// sessions). The view model keeps the resolved list so the send path never
    /// waits on a session listing.
    /// </summary>
    private void EnsurePeerMentions()
    {
        if (_vm is null || !_vm.IsCodeSurface || _services is null)
            return;
        // Listing every session touches disk, so it rides the same staleness
        // window as the file candidates rather than every keystroke.
        if (_peerMentionsLoading || DateTime.UtcNow - _peerMentionsLoadedAt < TimeSpan.FromSeconds(30))
            return;

        _peerMentionsLoading = true;
        var viewModel = _vm;
        var services = _services;
        _ = Task.Run(async () =>
        {
            var peers = await Services.PeerMentionSource.CollectAsync(services, viewModel);
            await Dispatcher.InvokeAsync(() =>
            {
                _peerMentionsLoading = false;
                _peerMentionsLoadedAt = DateTime.UtcNow;
                viewModel.SetPeerMentionCandidates(peers);
                _peerMentionEntries = [.. peers.Select(p => JarvisCode.Core.Agent.PeerMentions.Format(p.Token))];
            });
        });
    }

    /// <summary>
    /// The windows a Code session may be asked to drive, refreshed on the same
    /// 30s staleness the other candidates use. Offered only where computer use is
    /// switched on: nothing else can act on a window.
    /// </summary>
    private void EnsureWindowMentions()
    {
        if (_services is null || !_services.UiSettings.Current.ComputerUseEnabled)
        {
            _windowMentions = new Dictionary<string, Services.WindowMention>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        if (_windowMentionsLoading || DateTime.UtcNow - _windowMentionsLoadedAt < TimeSpan.FromSeconds(30))
            return;

        _windowMentionsLoading = true;
        _ = Task.Run(() =>
        {
            var windows = Services.ComputerUseWindowHints.Windows();
            _ = Dispatcher.InvokeAsync(() =>
            {
                _windowMentionsLoading = false;
                _windowMentionsLoadedAt = DateTime.UtcNow;
                var byLabel = new Dictionary<string, Services.WindowMention>(StringComparer.OrdinalIgnoreCase);
                foreach (var window in windows)
                {
                    // The name the user types is the application's; the title
                    // tells two of its windows apart.
                    var label = $"{window.App} — {window.Title}";
                    byLabel[label] = window;
                }

                _windowMentions = byLabel;
            });
        });
    }

    private bool _windowMentionsLoading;
    private DateTime _windowMentionsLoadedAt = DateTime.MinValue;

    /// <summary>Refreshes the candidate list off the UI thread when it has gone stale (30s, like the reference).</summary>
    private void EnsureMentionEntries()
    {
        EnsurePeerMentions();
        EnsureWindowMentions();
        var root = _vm?.Session.WorkingDirectory;
        if (string.IsNullOrEmpty(root))
        {
            _mentionEntries = [];
            return;
        }

        var stale = !string.Equals(_mentionRoot, root, StringComparison.OrdinalIgnoreCase)
            || DateTime.UtcNow - _mentionLoadedAt > TimeSpan.FromSeconds(30);
        if (!stale || _mentionLoading)
        {
            return;
        }

        _mentionLoading = true;
        var captured = root;
        _ = Task.Run(() =>
        {
            var entries = JarvisCode.Core.Utilities.FileMentions.ListCandidateEntries(captured);
            Dispatcher.InvokeAsync(() =>
            {
                _mentionLoading = false;
                _mentionRoot = captured;
                _mentionLoadedAt = DateTime.UtcNow;
                _mentionEntries = entries;
                UpdateMentionPopup();
            });
        });
    }

    private static string MentionEntryName(string entry) =>
        Path.GetFileName(entry.TrimEnd('/')) is { Length: > 0 } name ? name : entry;

    private List<string> VisibleMentions(string query)
    {
        // A mention token already carries its own '@'; the file list does not.
        var peers = _peerMentionEntries
            .Select(static entry => entry.TrimStart('@'))
            .Where(entry => query.Length == 0 || entry.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var windows = _windowMentions.Keys
            .Where(label => query.Length == 0 || label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static label => label, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        if (query.Length == 0)
        {
            return [.. peers, .. windows, .. _mentionEntries.Take(Math.Max(0, 20 - peers.Count - windows.Count))];
        }

        return peers.Concat(windows).Concat(_mentionEntries
            .Where(e => e.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => MentionEntryName(e).StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                : MentionEntryName(e).Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(static e => e.Length))
            .Take(20)
            .ToList();
    }

    private void UpdateMentionPopup()
    {
        if (_vm is null || !_vm.IsCodeSurface || MentionContext() is not { } context)
        {
            MentionPopup.IsOpen = false;
            return;
        }

        EnsureMentionEntries();
        var options = VisibleMentions(context.Query);
        if (options.Count == 0)
        {
            MentionPopup.IsOpen = false;
            return;
        }

        _mentionSelection = Math.Clamp(_mentionSelection, 0, options.Count - 1);
        RenderMentionPopup(options, context.Start);
        MentionPopup.IsOpen = true;
    }

    private void RenderMentionPopup(List<string> options, int mentionStart)
    {
        MentionListHost.Children.Clear();
        for (var i = 0; i < options.Count; i++)
        {
            var entry = options[i];
            var isDirectory = entry.EndsWith('/');
            var row = new DockPanel { Margin = new Thickness(8, 5, 8, 5), LastChildFill = true };

            var glyph = new TextBlock
            {
                Text = isDirectory ? "" : "",
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);

            var name = new TextBlock
            {
                Text = MentionEntryName(entry),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);

            var parent = Path.GetDirectoryName(entry.TrimEnd('/'))?.Replace('\\', '/');
            var description = new TextBlock
            {
                Text = parent is { Length: > 0 } ? parent : "",
                FontSize = 11.5,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            row.Children.Add(description);

            var border = new Border { Child = row, CornerRadius = new CornerRadius(7) };
            if (i == _mentionSelection)
            {
                border.SetResourceReference(Border.BackgroundProperty, "SelectedOverlayBrush");
            }

            var captured = entry;
            border.MouseLeftButtonDown += (_, _) => InsertMention(mentionStart, captured);
            MentionListHost.Children.Add(border);
        }
    }

    /// <summary>Replaces the @-token under the caret with the chosen entry, quoting paths with spaces.</summary>
    private void InsertMention(int mentionStart, string entry)
    {
        // A window pick is not a path: it is remembered for the next message and
        // typed as the application's own name, which is what request_access takes.
        if (_windowMentions.TryGetValue(entry, out var window))
        {
            Services.ComputerUseWindowHints.Point(_vm?.Session.Id, window);
            entry = window.App;
        }

        var text = InputBox.Text;
        var caret = Math.Min(InputBox.CaretIndex, text.Length);
        var token = entry.Contains(' ') ? $"@\"{entry}\"" : "@" + entry;
        InputBox.Text = text[..mentionStart] + token + " " + text[caret..];
        InputBox.CaretIndex = mentionStart + token.Length + 1;
        MentionPopup.IsOpen = false;
        FocusInput();
    }

    // ---- find in conversation ----

    public void ToggleFindBar()
    {
        if (FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
        }
        else
        {
            FindBar.Visibility = Visibility.Visible;
            FindBox.Focus();
            FindBox.SelectAll();
        }
    }

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _findMatches = [];
        _findIndex = -1;
        FindCount.Text = "";
        FocusInput();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = FindBox.Text.Trim();
        _findMatches = [];
        _findIndex = -1;
        if (query.Length >= 2 && _vm is not null)
        {
            // Find answers about the conversation rather than about the page of it
            // that happens to be loaded, so it asks for the rest first.
            _vm.LoadEntireTranscript();
            for (var i = 0; i < _vm.Transcript.Count; i++)
            {
                var haystack = _vm.Transcript[i] switch
                {
                    UserMessageItem user => user.Text,
                    AssistantTextItem assistant => assistant.Markdown,
                    ThinkingItem thinking => thinking.Text,
                    _ => "",
                };
                if (haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    _findMatches.Add(i);
                }
            }
        }

        if (_findMatches.Count > 0)
        {
            _findIndex = 0;
            NavigateToMatch();
        }
        else
        {
            FindCount.Text = query.Length >= 2 ? "0" : "";
        }
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                OnFindPrevClick(sender, e);
            }
            else
            {
                OnFindNextClick(sender, e);
            }
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseFindBar();
        }
    }

    private void OnFindNextClick(object sender, RoutedEventArgs e)
    {
        if (_findMatches.Count > 0)
        {
            _findIndex = (_findIndex + 1) % _findMatches.Count;
            NavigateToMatch();
        }
    }

    private void OnFindPrevClick(object sender, RoutedEventArgs e)
    {
        if (_findMatches.Count > 0)
        {
            _findIndex = (_findIndex - 1 + _findMatches.Count) % _findMatches.Count;
            NavigateToMatch();
        }
    }

    /// <summary>
    /// Find Next / Find Previous from the app menu or F3, which the reference
    /// serves whether or not the find box itself has focus: an unopened find bar
    /// opens rather than doing nothing.
    /// </summary>
    public void StepFind(int direction)
    {
        if (FindBar.Visibility != Visibility.Visible)
        {
            ToggleFindBar();
            return;
        }

        if (direction >= 0)
        {
            OnFindNextClick(this, new RoutedEventArgs());
        }
        else
        {
            OnFindPrevClick(this, new RoutedEventArgs());
        }
    }

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => CloseFindBar();

    private void NavigateToMatch()
    {
        FindCount.Text = $"{_findIndex + 1}/{_findMatches.Count}";
        ScrollTranscriptToIndex(_findMatches[_findIndex]);
    }

    /// <summary>
    /// Scrolls the conversation to one tool call and opens it — the Background
    /// tasks panel's "View transcript" on a background agent's row.
    /// </summary>
    /// <summary>The tool call with this id, anywhere in the open transcript.</summary>
    public ViewModels.ToolCallItem? FindToolCall(string callId)
    {
        if (_vm is null)
        {
            return null;
        }

        foreach (var item in _vm.Transcript)
        {
            if (item is ViewModels.ToolGroupItem group &&
                group.AllCalls.FirstOrDefault(c => c.CallId == callId) is { } call)
            {
                return call;
            }
        }

        return null;
    }

    /// <summary>
    /// A Agent call's own transcript, built for the Background tasks pane's
    /// subagent view — the reference's pane-hosted subagent transcript: the
    /// model it ran on, the prompt it was given, its entries, then its report.
    /// The element carries this surface's resources so the nested rows render
    /// with the same templates the transcript uses.
    /// </summary>
    public FrameworkElement CreateSubagentTranscript(ViewModels.ToolCallItem call)
    {
        var stack = new StackPanel { Margin = new Thickness(14, 4, 14, 14) };

        if (call.ModelText is { Length: > 0 } model)
        {
            var badge = new TextBlock
            {
                Text = model,
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 8),
            };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            stack.Children.Add(badge);
        }

        if (call.SubagentPrompt is { Length: > 0 } prompt)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = prompt,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    LineHeight = 18,
                },
            };
            box.SetResourceReference(BackgroundProperty, "UserMessageBgBrush");
            ((TextBlock)box.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text200Brush");
            System.Windows.Automation.AutomationProperties.SetName(box, "Prompt given to this agent");
            stack.Children.Add(box);
        }

        stack.Children.Add(new ItemsControl { ItemsSource = call.SubItems, Focusable = false });
        stack.Children.Add(BuildSubagentReport(call));

        var host = SubagentHost();
        host.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = stack,
        };
        return host;
    }

    /// <summary>
    /// The one element that carries this surface's resources into the pane, so
    /// the nested rows find the transcript's own DataTemplates by the ordinary
    /// tree walk. It is created once and refilled: assigning a resource
    /// dictionary registers the element as one of its owners, and a fresh host
    /// per push would add one every time.
    /// </summary>
    private ContentControl SubagentHost() =>
        _subagentHost ??= new ContentControl { Focusable = false, Resources = Resources };

    private ContentControl? _subagentHost;

    /// <summary>
    /// The tail of the subagent view: its report once it has one, and otherwise
    /// a line saying whether it is still working or left nothing behind — the
    /// case a reopened session hits, where the inner events were never stored.
    /// </summary>
    private static FrameworkElement BuildSubagentReport(ViewModels.ToolCallItem call)
    {
        var host = new ContentControl { Focusable = false, Margin = new Thickness(0, 4, 0, 0) };

        void Refresh()
        {
            var report = call.SubagentReportText;
            var tail = Services.BackgroundTaskPresentation.SubagentTail(
                call.SubagentIsWorking, report, call.SubItems.Count,
                didNotRun: call.IsDenied || call.IsInterrupted);
            if (tail is null)
            {
                host.Content = null;
                return;
            }

            if (!call.SubagentIsWorking && report.Length > 0)
            {
                var text = new TextBox
                {
                    Text = tail,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = System.Windows.Media.Brushes.Transparent,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                };
                text.SetResourceReference(Control.ForegroundProperty, call.IsFailed ? "Danger100Brush" : "Text200Brush");
                text.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "SelectionBrush");
                System.Windows.Automation.AutomationProperties.SetName(text, "Agent report");
                host.Content = text;
                return;
            }

            var notice = new TextBlock
            {
                Text = tail,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            };
            notice.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            host.Content = notice;
        }

        Refresh();
        var subscribed = false;
        void OnCallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ViewModels.ToolCallItem.Result)
                or nameof(ViewModels.ToolCallItem.IsRunning)
                or nameof(ViewModels.ToolCallItem.IsError)
                or nameof(ViewModels.ToolCallItem.IsDenied)
                or nameof(ViewModels.ToolCallItem.IsInterrupted)
                or nameof(ViewModels.ToolCallItem.SubagentReport))
            {
                Refresh();
            }
        }

        void OnItemsChanged(
                object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

        // The view outlives neither the pane nor the call, so the subscriptions
        // are dropped when the host leaves the tree.
        host.Loaded += (_, _) =>
        {
            // WPF raises Loaded again when an element is reparented, and a
            // doubled subscription would outlive the single Unloaded.
            if (!subscribed)
            {
                subscribed = true;
                call.PropertyChanged += OnCallChanged;
                call.SubItems.CollectionChanged += OnItemsChanged;
            }

            Refresh();
        };
        host.Unloaded += (_, _) =>
        {
            if (!subscribed)
            {
                return;
            }

            subscribed = false;
            call.PropertyChanged -= OnCallChanged;
            call.SubItems.CollectionChanged -= OnItemsChanged;
        };
        return host;
    }

    /// <summary>
    /// The composer's primary action. Mid-turn it is Stop only while there is
    /// nothing to send; with a draft in the box it is Send, exactly as the button
    /// draws itself — see <see cref="UpdateSendButton"/>.
    /// </summary>
    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_vm is { } vm && Services.ChatComposerActions.ShowsStop(vm.IsRunning, HasDraftContent))
        {
            vm.CancelTurn();
            return;
        }

        Send();
    }

    private void Send()
    {
        if (_vm is null)
        {
            return;
        }

        // Submitting mid-turn does not stop the turn: the reference's composer keeps
        // Enter as its send shortcut the whole time an answer is streaming and puts
        // the prompt in the queue, which folds into the running turn between model
        // calls. Stopping is Esc, Ctrl+Enter, or the button while nothing is typed.
        var text = InputBox.Text;
        if (text.Trim().Length == 0 && _vm.ComposerAttachments.Count == 0)
        {
            return;
        }

        // The reference asks about the workspace before the session works in
        // it, and about an imported session before it continues one.
        if (!EnsureWorkspaceTrusted() || !EnsureImportedSessionResumable())
        {
            return;
        }

        InputBox.Clear();
        CloseCommandPopup();
        // A sent prompt joins the history the composer's Ctrl+R searches.
        _services?.ComposerHistory.Add(text);
        if (_history is not null)
        {
            ApplyHistory(_history.Cancel());
        }

        _autoScroll = true;
        _ = SubmitTextAsync(text);
    }

    /// <summary>The composer submit path: slash commands and plain messages alike.</summary>
    /// <summary>
    /// The reference's /debug prompt for this session: it arms the diagnostic
    /// log, reads its tail and names the three settings files.
    /// </summary>
    private string BuildDebugPrompt(string argument)
    {
        var wasAlreadyOn = Services.SessionDebugLog.Enable();
        var path = Services.SessionDebugLog.Path ?? "(no debug log)";
        var cwd = _vm?.Session.WorkingDirectory ?? Environment.CurrentDirectory;
        var settings = new Services.DebugCommand.SettingsPaths(
            _services?.Paths.SettingsFile ?? "(unknown)",
            System.IO.Path.Combine(cwd, ".jarvis", "settings.json"),
            System.IO.Path.Combine(cwd, ".jarvis", "settings.local.json"));
        return Services.DebugCommand.Prompt(
            argument, path, Services.DebugCommand.ReadTail(Services.SessionDebugLog.Path), settings, wasAlreadyOn);
    }

    public async Task SubmitTextAsync(string text)
    {
        if (_vm is null)
        {
            return;
        }

        if (text.TrimStart().StartsWith('/') && !text.Contains('\n'))
        {
            BuildSlashCommands();
            var parts = text.Trim()[1..].Split(' ', 2);
            var command = _slashCommands.FirstOrDefault(c => c.Matches(parts[0]));
            if (command is not null)
            {
                await command.Execute(parts.Length > 1 ? parts[1] : "");
            }
            else if (!_vm.IsCodeSurface && _chatSkillNames.Contains(parts[0]))
            {
                // Skills are Code-only, like the reference desktop's
                // surface-filtered skill menu.
                _vm.Transcript.Add(new NoticeItem { Text = $"/{parts[0]} isn't available in Chat" });
            }
            else
            {
                var suggestion = JarvisCode.Core.Utilities.DidYouMean.Suggest(
                    parts[0], _slashCommands.SelectMany(static c => c.Names).ToList());
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = suggestion.Count > 0
                        ? $"Unknown command /{parts[0]}. Did you mean /{string.Join(", /", suggestion)}?"
                        : $"Unknown command /{parts[0]}.",
                });
            }

            return;
        }

        await _vm.SendAsync(text);
    }

    /// <summary>/list-agents (and its alias /peers): what the ListAgents tool reports, shown to the user.</summary>
    private async Task ShowAgentListingAsync()
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        _vm.Transcript.Add(new NoticeItem
        {
            Text = await Services.ListAgentsTool.BuildListingAsync(_services, _vm),
        });
    }

    /// <summary>
    /// /setup-bedrock and /setup-vertex. This app keeps a cloud provider's
    /// credential, region and model ids on one provider card, so "reconfigure"
    /// is that card — opened, and named in the transcript, because the page is
    /// a list and nothing else would say which row to expand.
    /// </summary>
    private Task OpenProviderSettings(string providerName, string what)
    {
        (Window.GetWindow(this) as MainWindow ?? Application.Current.MainWindow as MainWindow)
            ?.OpenSettings("Settings", "Providers");
        _vm?.Transcript.Add(new NoticeItem
        {
            Text = $"Settings › Providers is open. Expand the {providerName} card to change {what}.",
        });
        return Task.CompletedTask;
    }

   /// <summary>Ctrl+D: the composer's dictation, which the reference's own registry binds there.</summary>
    public void ToggleDictation() => OnMicClick(this, new RoutedEventArgs());

    private void OnMicClick(object sender, RoutedEventArgs e)
    {
        _voice ??= new Services.VoiceInput();
        if (_voice.IsListening)
        {
            _voice.Stop();
            MicGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            MicButton.ToolTip = "Dictate";
            SetDictation(Services.ChatDictation.Off);
            return;
        }

        _voice.Recognized -= OnVoiceRecognized;
        _voice.Recognized += OnVoiceRecognized;
        if (_voice.Start() is { } error)
        {
            MicButton.ToolTip = error;
            _vm?.Transcript.Add(new NoticeItem { Text = error });
            return;
        }

        MicGlyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrandBrush");
        MicButton.ToolTip = "Listening — click to stop";
        SetDictation(Services.ChatDictation.Listening);
    }

    /// <summary>
    /// Moves the composer's placeholder onto its dictation rung. Only
    /// <see cref="Services.ChatDictation.Listening"/> is reachable here: the other four
    /// describe the reference's cloud voice session, and this build dictates offline
    /// through System.Speech, which has no connecting, processing or speaking state.
    /// </summary>
    private void SetDictation(Services.ChatDictation state)
    {
        if (_dictation == state)
        {
            return;
        }

        _dictation = state;
        UpdateLayoutState();
    }

    private void OnVoiceRecognized(string text)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var current = InputBox.Text;
            InputBox.Text = current.Length == 0 || current.EndsWith(' ')
                ? current + text
                : current + " " + text;
            InputBox.CaretIndex = InputBox.Text.Length;
        });
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string starter })
        {
            InputBox.Text = starter;
            InputBox.CaretIndex = InputBox.Text.Length;
            FocusInput();
        }
    }

    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (_vm is { IsCodeSurface: false })
        {
            OpenChatPlusMenu();
            return;
        }

        ShowPlusMenu();
    }


    private MenuItem BuildCustomizeSubmenu(string header, string page, IReadOnlyList<string> items, Action<string>? onItem)
    {
        var root = new MenuItem { Header = header };
        foreach (var item in items.Take(12))
        {
            var child = new MenuItem { Header = item };
            if (onItem is not null)
            {
                var captured = item;
                child.Click += (_, _) => onItem(captured);
            }
            else
            {
                child.Click += (_, _) => CustomizeNavigateRequested?.Invoke(this, page);
            }

            root.Items.Add(child);
        }

        if (items.Count == 0)
        {
            root.Items.Add(new MenuItem { Header = $"No {header.ToLowerInvariant()} yet", IsEnabled = false });
        }

        root.Items.Add(new Separator());
        var manage = new MenuItem { Header = $"Manage {header.ToLowerInvariant()}…" };
        manage.Click += (_, _) => CustomizeNavigateRequested?.Invoke(this, page);
        root.Items.Add(manage);
        return root;
    }

    /// <summary>
    /// The Chat surface's "+" menu, in the reference's own three groups: files and
    /// context, then the tool sources, then the modes. Rows this build cannot
    /// honour are left out rather than rendered dead.
    /// </summary>
    private void OpenChatPlusMenu()
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        var conversation = _services.ChatConversations.Get(
            _vm.Session.Id, _services.Settings.Current.EnableWebSearch);
        var connected = _services.Mcp.ConnectedToolCounts;
        var connectors = ListConnectorNames()
            .Select(name => new Services.ChatConnectorRow(
                name,
                connected.ContainsKey(name),
                conversation.Connectors.Contains(name, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var rows = Services.ChatComposerMenu.Plus(
            screenshotAvailable: true,
            projectsAvailable: true,
            // "Add from GitHub" attaches a claude.ai sync source; there is nothing
            // behind it here, so the row is left out rather than faked.
            gitHubAvailable: false,
            connectors,
            conversation.ToolAccess,
            conversation.WebSearch);

        var menu = new ContextMenu
        {
            PlacementTarget = AttachButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        foreach (var item in RenderMenuRows(rows))
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private IEnumerable<object> RenderMenuRows(IReadOnlyList<Services.ChatMenuRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Id == "web-search" && !SupportsWebSearchControl)
            {
                yield return new MenuItem { Header = "Web search is controlled in ChatGPT", IsEnabled = false };
                continue;
            }
            if (row.Kind == Services.ChatMenuKind.Separator)
            {
                yield return new Separator();
                continue;
            }

            var item = new MenuItem
            {
                Header = row.Subtitle is null
                    ? row.Label
                    : BuildTwoLineHeader(row.Label, row.Subtitle),
                IsCheckable = row.Kind == Services.ChatMenuKind.Checkbox,
                IsChecked = row.Checked,
                StaysOpenOnClick = row.Kind == Services.ChatMenuKind.Checkbox,
            };
            if (row.Suffix is { } suffix)
            {
                Controls.MenuItemExtras.SetTrailing(item, suffix);
            }

            if (row.Id == "skills")
            {
                item = BuildCustomizeSubmenu(Services.ChatComposerMenu.Skills, "skills", ListSkillNames(), null);
            }
            else if (row.Id == "plugins")
            {
                item = BuildCustomizeSubmenu(Services.ChatComposerMenu.Plugins, "plugins", ListPluginNames(), null);
            }
            else if (row.Id == "project")
            {
                BuildProjectSubmenu(item);
            }
            else if (row.Items is { Count: > 0 } children)
            {
                foreach (var child in RenderMenuRows(children))
                {
                    item.Items.Add(child);
                }
            }
            else
            {
                var id = row.Id;
                item.Click += (_, _) => RunChatMenuAction(id);
            }

            yield return item;
        }
    }

    private void RunChatMenuAction(string? id)
    {
        if (_vm is null || _services is null || id is null)
        {
            return;
        }

        var session = _vm.Session.Id;
        switch (id)
        {
            case "files":
                AddFiles();
                break;
            case "screenshot":
                AttachScreenshot();
                break;
            case "web-search":
                if (!SupportsWebSearchControl)
                {
                    _vm.Transcript.Add(new NoticeItem { Text = "Web search is controlled in the ChatGPT browser." });
                    break;
                }
                _services.ChatConversations.SetWebSearch(
                    session,
                    !_services.ChatConversations.Get(session, _services.Settings.Current.EnableWebSearch).WebSearch);
                break;
            case "tool-access:on":
                _services.ChatConversations.SetToolAccess(session, Services.ChatToolAccess.LoadWhenNeeded);
                break;
            case "tool-access:off":
                _services.ChatConversations.SetToolAccess(session, Services.ChatToolAccess.AlreadyLoaded);
                break;
            case "manage-connectors":
                CustomizeRequested?.Invoke(this, "connectors");
                break;
            case "new-project":
                CreateProjectPrompt();
                break;
            default:
                if (id.StartsWith("connector:", StringComparison.Ordinal))
                {
                    var name = id["connector:".Length..];
                    var enabled = _services.ChatConversations
                        .Get(session, _services.Settings.Current.EnableWebSearch)
                        .Connectors.Contains(name, StringComparer.OrdinalIgnoreCase);
                    _services.ChatConversations.SetConnector(session, name, !enabled);
                    ChatToolsChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (id.StartsWith("project:", StringComparison.Ordinal))
                {
                    AssignProject(id["project:".Length..]);
                }

                break;
        }
    }

    /// <summary>
    /// "Add to project": the reference's submenu is a search box over the projects
    /// with "Start a new project" in its footer, and the chat's own project checked.
    /// </summary>
    private void BuildProjectSubmenu(MenuItem root)
    {
        var search = new TextBox
        {
            Style = (Style)FindResource("InputTextBox"),
            Margin = new Thickness(4, 2, 4, 4),
            MinWidth = 200,
        };
        System.Windows.Automation.AutomationProperties.SetName(search, Services.ChatComposerMenu.SearchProjects);
        var searchHost = new MenuItem { StaysOpenOnClick = true, Header = search, IsHitTestVisible = true };

        void Fill()
        {
            root.Items.Clear();
            root.Items.Add(searchHost);
            var store = _services!.Projects;
            var current = store.ForSession(_vm!.Session.Id)?.Name;
            var matches = store.Search(search.Text).Select(static p => p.Name).ToList();
            foreach (var item in RenderMenuRows(
                Services.ChatComposerMenu.ProjectPicker(matches, current, search.Text, store.All.Count > 0)))
            {
                root.Items.Add(item);
            }
        }

        search.TextChanged += (_, _) => Fill();
        Fill();
    }

    private void CreateProjectPrompt()
    {
        if (_services is null || _vm is null)
        {
            return;
        }

        var name = InputDialog.Prompt(Window.GetWindow(this), Services.ChatComposerMenu.StartANewProject);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var project = _services.Projects.Create(name);
        _services.Projects.Assign(_vm.Session.Id, project.Id);
        UpdateSessionHeader();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AssignProject(string name)
    {
        if (_services is null || _vm is null)
        {
            return;
        }

        var project = _services.Projects.All.FirstOrDefault(p => p.Name == name);
        var current = _services.Projects.ForSession(_vm.Session.Id);
        _services.Projects.Assign(
            _vm.Session.Id,
            project is null || project.Id == current?.Id ? null : project.Id);
        UpdateSessionHeader();
        ProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The chat was filed under a project, or taken out of one.</summary>
    public event EventHandler? ProjectChanged;

    /// <summary>The conversation's connectors changed; the surface rebuilds its tools.</summary>
    public event EventHandler? ChatToolsChanged;

    /// <summary>A page of Customize was asked for from a menu row.</summary>
    public event EventHandler<string>? CustomizeRequested;

    /// <summary>
    /// "Take a screenshot": the reference captures the screen and stages the image
    /// on the composer, and says "Could not capture screen." when it cannot.
    /// </summary>
    private void AttachScreenshot()
    {
        try
        {
            var capture = new Services.ComputerUseService(() => _services!.UiSettings.Current)
                .CaptureScreenshot();
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jarvis-screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            File.WriteAllBytes(path, Convert.FromBase64String(capture.Image.Base64Data));
            AddImageAttachment(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or FormatException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            _vm?.Transcript.Add(new NoticeItem
            {
                Text = Services.ChatComposerMenu.CouldNotCaptureScreen,
                IsError = true,
            });
        }
    }

    private IReadOnlyList<string> ListSkillNames()
    {
        // Skills are Code-only (the reference desktop's Chat gates them on a
        // server-side capability this app does not have).
        if (_services is null || _vm is null || !_vm.IsCodeSurface)
        {
            return [];
        }

        try
        {
            return [.. Services.SkillCatalog.ForComposer(Services.SkillCatalog.Enabled(
                    Services.SkillCatalog.LoadAll(_vm.Session.WorkingDirectory, _services.Paths),
                    _services.UiSettings.Current))
                .Select(static s => s.Name)];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private IReadOnlyList<string> ListConnectorNames()
    {
        if (_services is null || _vm is null)
        {
            return [];
        }

        try
        {
            var plugins = JarvisCode.Core.Customization.Plugins.Load(_services.Paths.PluginsDirectory);
            return [.. JarvisCode.Core.Mcp.McpConfig
                .Load(_vm.Session.WorkingDirectory, _services.Paths.UserMcpFile,
                    Services.DesktopExtensions.ConfigFiles(_services.Paths, plugins.McpFiles))
                .Select(static s => s.Name)];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private IReadOnlyList<string> ListPluginNames()
    {
        if (_services is null)
        {
            return [];
        }

        try
        {
            return [.. JarvisCode.Core.Customization.Plugins.Load(_services.Paths.PluginsDirectory)
                .Installed.Select(static p => p.Name)];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add files or photos to the conversation",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        AttachFiles(dialog.FileNames);
    }

    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add folder to session" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            AttachFolder(dialog.FolderName);
            FocusInput();
        }
    }

    private void OnModeChipClick(object sender, RoutedEventArgs e) => OpenModeMenu();

    /// <summary>
    /// The permission-mode menu — Ctrl+Shift+M, digits select. Ported from the
    /// reference desktop's Code surface: a "Mode" group label over one row per offered
    /// mode, each carrying its description, the accent check on the row the session is
    /// in and a "Default" badge on the row the settings default names.
    /// </summary>
    public void OpenModeMenu()
    {
        if (_vm is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = ModeChip,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            // The reference's popup is bounded rather than sized to its widest row.
            MinWidth = 128,
            MaxWidth = 320,
        };

        var header = new MenuItem { Header = Services.PermissionModeMenu.Header };
        header.SetResourceReference(FrameworkElement.StyleProperty, "ComposerMenuGroupLabel");
        // The style says the same, but it resolves only once the menu is in a tree, and
        // the digit accelerators read this before that happens.
        header.Focusable = false;
        menu.Items.Add(header);

        var settingsDefault = ConfiguredDefaultPermissionMode();
        foreach (var mode in Services.PermissionModeMenu.OfferedFor(_services.UiSettings.Current))
        {
            var info = Services.PermissionModeMenu.Describe(mode);
            var item = new MenuItem
            {
                Header = BuildTwoLineHeader(info.Label, info.Description, titleSize: 14, hintSize: 13),
                IsCheckable = true,
                IsChecked = _vm.Gate.Mode == mode,
            };
            item.SetResourceReference(FrameworkElement.StyleProperty, "ComposerMenuItemDescribed");
            if (mode == settingsDefault)
            {
                Controls.MenuItemExtras.SetTrailing(item, DefaultModeBadge());
            }

            var captured = mode;
            item.Click += (_, _) => SelectPermissionMode(captured);
            menu.Items.Add(item);
        }

        AttachDigitAccelerators(menu);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Auto is this build's shipped default, which is what the reference's danger
    /// predicate asks: it raises the "Enable auto mode?" consent only where auto is
    /// something the user has not been given yet.
    /// </summary>
    private const bool AutoIsDefaultMode = true;

    /// <summary>The mode Settings names as the default, or null when none is configured.</summary>
    private PermissionMode? ConfiguredDefaultPermissionMode() =>
        _services is not null
        && Enum.TryParse<PermissionMode>(_services.Settings.Current.PermissionModeName, ignoreCase: true, out var mode)
            ? mode
            : null;

    /// <summary>The reference's neutral "Default" pill on the settings-default row.</summary>
    private static Border DefaultModeBadge()
    {
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            BorderThickness = new Thickness(1),
        };
        badge.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        var text = new TextBlock { Text = Services.PermissionModeMenu.DefaultBadge, FontSize = 11.5 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        badge.Child = text;
        return badge;
    }

    /// <summary>
    /// The reference's select → guard → commit chain: a mode that takes permissions
    /// away is confirmed once per workspace before it takes effect, and a mode the
    /// session is already in is not a change at all.
    /// </summary>
    /// <summary>
    /// The reference's <c>cycleChipLevel</c> (Shift+Tab): steps the permission
    /// chip to the next mode it offers, wrapping. A mode that stops asking
    /// still goes through the same consent the menu raises.
    /// </summary>
    public bool CycleChipLevel()
    {
        if (_vm is null || !_vm.IsCodeSurface)
        {
            return false;
        }

        var offered = Services.PermissionModeMenu.Offered;
        var current = offered.ToList().IndexOf(_vm.Gate.Mode);
        SelectPermissionMode(offered[(current + 1) % offered.Count]);
        return true;
    }

    private void SelectPermissionMode(PermissionMode mode)
    {
        if (_vm is null || _services is null || _vm.Gate.Mode == mode)
        {
            return;
        }

        var workspace = PermissionWorkspaceKey();
        var scratch = workspace == Services.PermissionModeMenu.ScratchWorkspace;
        var ack = Services.PermissionModeMenu.AckKey(workspace, mode);
        if (Services.PermissionModeMenu.IsDangerous(mode, AutoIsDefaultMode)
            && (scratch || !_services.UiSettings.Current.PermissionModeAcks.Contains(ack, StringComparer.Ordinal)))
        {
            var consent = Services.PermissionModeMenu.BypassConsent(workspace);
            if (!Views.ConfirmDialog.Ask(
                    Window.GetWindow(this),
                    consent.Title,
                    consent.Description,
                    consent.ConfirmLabel,
                    consent.Workspace,
                    consent.Footnote,
                    focusCancel: true))
            {
                return;
            }

            // A session with no folder of its own is asked every time, as the reference
            // asks: there is nothing durable to file the consent against.
            if (!scratch)
            {
                _services.UiSettings.Current.PermissionModeAcks.Add(ack);
            }
        }

        CommitPermissionMode(mode, workspace);
    }

    /// <summary>
    /// Stores the choice, then moves the session into <paramref name="mode"/>. Storing
    /// first is what makes the failure path honest: a store that will not take the
    /// choice leaves the session exactly where it was rather than in a mode nothing
    /// recorded, which is the invariant the reference's own rollback protects. The
    /// settings default is deliberately untouched — the reference keeps picking a mode
    /// and configuring the default apart, so a one-off Bypass in one checkout does not
    /// become how every new session starts.
    /// </summary>
    private void CommitPermissionMode(PermissionMode mode, string workspace)
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        // Plan mode is how a turn starts, not how a folder is worked in, so the
        // reference never files it as the folder's choice.
        var folders = _services.UiSettings.Current.FolderPermissionModes;
        var remembers = mode != PermissionMode.Plan && workspace != Services.PermissionModeMenu.ScratchWorkspace;
        var restore = folders.TryGetValue(workspace, out var stored) ? stored : null;
        if (remembers)
        {
            folders[workspace] = mode.ToString();
        }

        try
        {
            _services.UiSettings.Save();
        }
        catch (IOException)
        {
            if (remembers)
            {
                if (restore is null)
                {
                    folders.Remove(workspace);
                }
                else
                {
                    folders[workspace] = restore;
                }
            }

            _vm.Transcript.Add(new ViewModels.NoticeItem { Text = Services.PermissionModeMenu.ChangeFailed });
            return;
        }

        if (mode == PermissionMode.Plan)
        {
            _vm.Gate.EnterPlanMode();
        }
        else
        {
            if (_vm.Gate.Mode == PermissionMode.Plan)
            {
                _vm.Gate.ExitPlanMode();
            }

            _vm.Gate.Mode = mode;
        }

        _vm.PermissionPickedInSession = true;
        UpdateModeChip();
    }

    /// <summary>
    /// The key a workspace's remembered mode and its consents are filed under — the
    /// session's own folder, or the reference's scratch sentinel when it has none.
    /// </summary>
    private string PermissionWorkspaceKey()
    {
        var directory = _vm?.Session.WorkingDirectory;
        return string.IsNullOrWhiteSpace(directory)
            ? Services.PermissionModeMenu.ScratchWorkspace
            : directory;
    }

    private void OnModelChipClick(object sender, RoutedEventArgs e) => OpenModelMenu();

    /// <summary>The model menu — Ctrl+Shift+I, digits select, default tagged Recommended.</summary>
    public void OpenModelMenu() => OpenModelMenuCore(refreshChatGptModels: true);

    private void OpenModelMenuCore(bool refreshChatGptModels)
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = ModelChip, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var models = _services.Settings.Models;
        var refreshLiveModels = refreshChatGptModels && ShouldRefreshChatGptModels();
        if (models.Count == 0 && !refreshLiveModels)
        {
            // No model ships built in, so a fresh installation would otherwise open an
            // empty menu; point at the page where a key and its model ids are entered.
            var empty = new MenuItem { Header = "No models yet — add one in Settings › Providers" };
            // Settings lives on the main window, which is not this surface's own when it
            // was moved out into a window of its own (a side chat, a popped-out panel).
            empty.Click += (_, _) =>
                (Window.GetWindow(this) as MainWindow ?? Application.Current.MainWindow as MainWindow)
                    ?.OpenSettings("Settings", "Providers");
            menu.Items.Add(empty);
            menu.IsOpen = true;
            return;
        }

        var defaultModelId = _services.Settings.Current.DefaultModelId;
        var multiple = models.Count > 1;
        // The reference's model selector carries a search box once the list is long
        // enough to need one; on Chat that is the whole difference from the Code
        // menu's shape, which stays as it is.
        TextBox? search = null;
        if (_vm is { IsCodeSurface: false } && models.Count > Services.ChatModelMenu.SearchAbove)
        {
            search = new TextBox
            {
                Style = (Style)FindResource("InputTextBox"),
                Margin = new Thickness(4, 2, 4, 4),
                MinWidth = 220,
            };
            System.Windows.Automation.AutomationProperties.SetName(search, Services.ChatModelMenu.SearchModels);
            menu.Items.Add(new MenuItem { StaysOpenOnClick = true, Header = search });
            search.TextChanged += (_, _) =>
            {
                var query = search.Text;
                var any = false;
                foreach (var item in menu.Items.OfType<MenuItem>().Skip(1))
                {
                    var visible = Services.ChatModelMenu.Matches(item.Header as string
                        ?? (item.Header as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text, query);
                    item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    any |= visible && item.IsEnabled;
                }

                foreach (var item in menu.Items.OfType<Separator>())
                {
                    item.Visibility = query.Trim().Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                }

                _modelNoMatches!.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            };
        }

        // Every model is one the user added, so nothing keeps a provider's entries
        // adjacent in the stored order: group them, and name the group on its rule.
        foreach (var group in Services.ModelMenuPresentation.Group(models, ProviderDisplayName))
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(ProviderSectionHeader(group.Label));
            foreach (var model in group.Models)
            {
                var item = new MenuItem
                {
                    Header = multiple && model.ModelId == defaultModelId
                        ? ModelHeaderWithTag(model.DisplayName, "Recommended")
                        : model.DisplayName,
                };
                if (_vm.CurrentModel?.ModelId == model.ModelId)
                {
                    item.FontWeight = FontWeights.SemiBold;
                }

                var captured = model;
                item.Click += async (_, _) =>
                {
                    // A pre_model_switch hook may refuse; the default must not be
                    // rewritten to a model the session did not actually move to.
                    if (!await _vm.SetModelAsync(captured, JarvisCode.Core.Hooks.ModelSwitchSource.Picker,
                            confirm: (reason, _) => Task.FromResult(ConfirmDialog.Ask(Window.GetWindow(this),
                                $"Switch to {captured.DisplayName}?", reason, "Continue", focusCancel: true, danger: false))))
                    {
                        return;
                    }

                    _services.Settings.Current.DefaultModelId = captured.ModelId;
                    _services.Settings.Save();
                    UpdateModelChip();
                };
                menu.Items.Add(item);
            }
        }

        MenuItem? loadingModels = null;
        Separator? loadingSeparator = null;
        if (refreshLiveModels)
        {
            if (menu.Items.Count > 0)
            {
                loadingSeparator = new Separator();
                menu.Items.Add(loadingSeparator);
            }

            loadingModels = new MenuItem
            {
                Header = Services.ChatModelMenu.LoadingModels,
                IsEnabled = false,
            };
            menu.Items.Add(loadingModels);
        }

        // The reference's thinking control is a row of this menu, sitting between the
        // model list and the effort menu (its `Ar` after `gT`) rather than a chip on
        // the composer's chin.
        if (_vm is { IsCodeSurface: false } && !IsChatGptWebModel)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildExtendedThinkingRow());
        }

        if (search is not null)
        {
            _modelNoMatches = new MenuItem
            {
                Header = Services.ChatModelMenu.NoMatches,
                IsEnabled = false,
                Visibility = Visibility.Collapsed,
            };
            menu.Items.Add(_modelNoMatches);
            menu.Opened += (_, _) => search.Focus();
        }

        AttachDigitAccelerators(menu);
        menu.IsOpen = true;
        if (loadingModels is not null)
        {
            _ = RefreshChatGptModelsAsync(menu, loadingModels, loadingSeparator);
        }
    }

    private bool ShouldRefreshChatGptModels()
    {
        if (_services is null
            || _chatGptModelsLoading
            || DateTimeOffset.UtcNow - _chatGptModelsReadAt < TimeSpan.FromMinutes(2)
            || _services.Settings.Current.GetApiKeys(ChatGptWebProvider.ProviderId).Count == 0)
        {
            return false;
        }

        return Services.ChatGptComposerDiscovery.Provider(_services) is not null;
    }

    private async Task RefreshChatGptModelsAsync(
        ContextMenu menu,
        MenuItem loadingItem,
        Separator? loadingSeparator)
    {
        if (_services is null || Services.ChatGptComposerDiscovery.Provider(_services) is not { } provider)
        {
            return;
        }

        _chatGptModelsLoading = true;
        using var menuClosed = new CancellationTokenSource();
        RoutedEventHandler onMenuClosed = (_, _) => menuClosed.Cancel();
        menu.Closed += onMenuClosed;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(menuClosed.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            // Catalogue refresh is passive and must never hold this conversation's page gate:
            // closing the menu and immediately sending should not queue behind a three-minute read.
            var controls = await provider.ReadComposerControlsAsync(
                scopeId: null, modelId: null, includeEfforts: false,
                cancellationToken: deadline.Token);
            _chatGptModelsReadAt = DateTimeOffset.UtcNow;

            var merged = Services.ChatGptComposerDiscovery.MergeModels(_services, controls);
            if (merged.Changed)
            {
                _services.Settings.Save();
                UpdateModelChip();
            }

            if (!menu.IsOpen)
            {
                return;
            }

            if (merged.Changed)
            {
                // Rebuilding gives the new rows the same grouping, search behaviour and digit
                // accelerators as entries that were present when the menu first opened.
                menu.IsOpen = false;
                _ = Dispatcher.BeginInvoke(() => OpenModelMenuCore(refreshChatGptModels: false));
            }
            else if (controls.Models.Count == 0)
            {
                loadingItem.Header = "No ChatGPT models found";
            }
            else
            {
                menu.Items.Remove(loadingItem);
                if (loadingSeparator is not null)
                {
                    menu.Items.Remove(loadingSeparator);
                }
            }
        }
        catch (Exception ex)
        {
            JarvisCode.Core.Utilities.DiagnosticLog.Write(
                $"chatgpt: live model refresh failed — {ex.Message}");
            if (menu.IsOpen)
            {
                loadingItem.Header = ex is OperationCanceledException
                    ? "ChatGPT model refresh timed out"
                    : "ChatGPT models unavailable";
                loadingItem.ToolTip = ex.Message;
            }
        }
        finally
        {
            menu.Closed -= onMenuClosed;
            _chatGptModelsLoading = false;
        }
    }

    /// <summary>
    /// The reference's thinking row: a menu item that keeps the menu open, carrying
    /// the mode's title and a switch that reads out by that title (its <c>jg</c>,
    /// <c>data-testid="thinking-mode-toggle"</c>). Its second line is the mode's own
    /// description, which comes from a model catalogue this build does not have, so
    /// the row is the title alone — the same shape its <c>description ?? ""</c> leaves.
    /// </summary>
    private MenuItem BuildExtendedThinkingRow()
    {
        if (!SupportsEffortControl) return new MenuItem
        {
            Header = "Thinking is controlled in ChatGPT",
            IsEnabled = false,
        };

        var on = _services!.ChatConversations
            .Get(_vm!.Session.Id, _services.Settings.Current.EnableWebSearch)
            .ExtendedThinking;

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("SwitchToggle"),
            IsChecked = on,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(
            toggle, Services.ChatComposerMenu.ExtendedThinking);

        var row = new DockPanel { LastChildFill = false, MinWidth = 200 };
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(new TextBlock
        {
            Text = Services.ChatComposerMenu.ExtendedThinking,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var item = new MenuItem { Header = row, StaysOpenOnClick = true };
        void Flip()
        {
            ToggleExtendedThinking();
            toggle.IsChecked = _services.ChatConversations
                .Get(_vm.Session.Id, _services.Settings.Current.EnableWebSearch)
                .ExtendedThinking;
        }

        item.Click += (_, _) => Flip();
        toggle.Click += (sender, e) =>
        {
            // The switch is inside the row, so its own click would otherwise flip the
            // conversation twice - once here and once through the row beneath it.
            e.Handled = true;
            Flip();
        };
        return item;
    }

    /// <summary>The model menu's "No matches" row, shown when its search finds nothing.</summary>
    private MenuItem? _modelNoMatches;

    /// <summary>The registered provider's own name, or null when this build has no such provider.</summary>
    private string? ProviderDisplayName(string providerId) => _services!.Providers.All
        .FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase))?.DisplayName;

    /// <summary>The label on the rule that separates one provider's models from the next.</summary>
    private static MenuItem ProviderSectionHeader(string label)
    {
        var header = Panels.SessionMenu.SectionHeader(label);
        // The style says the same, but it resolves only once the menu is in a tree, and
        // the digit accelerators read this before that happens.
        header.Focusable = false;
        return header;
    }

    /// <summary>"{name} · Recommended" with the tag dimmed, as the reference renders it.</summary>
    private static StackPanel ModelHeaderWithTag(string name, string tag)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = name });
        var suffix = new TextBlock { Text = $" · {tag}" };
        suffix.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        panel.Children.Add(suffix);
        return panel;
    }

    /// <summary>
    /// The reference numbers a menu's first nine items and lets a bare digit
    /// pick one while the menu is open.
    /// </summary>
    private static void AttachDigitAccelerators(ContextMenu menu)
    {
        // The reference hands digits out in row order to the rows that can actually be
        // picked. Section headers are rows in name only — they carry no command and
        // take no focus — and a disabled row never spends a number either.
        var items = menu.Items.OfType<MenuItem>().ToList();
        var eligible = items.ConvertAll(static i => i.Focusable && i.IsEnabled);
        var shortcuts = Services.PermissionModeMenu.Shortcuts(eligible);
        var byDigit = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            if (shortcuts[i] is not { } digit)
            {
                continue;
            }

            items[i].InputGestureText = digit;
            byDigit[digit] = items[i];
        }

        menu.PreviewKeyDown += (_, e) =>
        {
            int? digit = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D0,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad0,
                _ => null,
            };
            if (digit is { } n
                && Keyboard.Modifiers == ModifierKeys.None
                && byDigit.TryGetValue(n.ToString(System.Globalization.CultureInfo.InvariantCulture), out var item))
            {
                e.Handled = true;
                menu.IsOpen = false;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            }
        };
    }

    private static StackPanel BuildTwoLineHeader(
        string title, string subtitle, double titleSize = 13, double hintSize = 11)
    {
        var panel = new StackPanel();
        // Both lines truncate rather than wrap or clip, as the reference's rows do —
        // the popup is bounded, so a long description has to end somewhere visible.
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = titleSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var hint = new TextBlock
        {
            Text = subtitle,
            FontSize = hintSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        panel.Children.Add(hint);
        return panel;
    }

    // ---- composer attachments (context · pasted text · images) ----

    private const int MaxImageAttachments = 20;
    private const int PastedTextThreshold = 2000;

    /// <summary>Whole-message fallback for "Attach as context" without a selection.</summary>
    /// <summary>
    /// The message the pointer is over. Rewind and fork are offered only on a
    /// user message of a Code session, which is where the reference offers them.
    /// </summary>
    private Controls.TextSelectionScope.MessageTarget? ResolveTranscriptMessage(object source)
    {
        var data = source switch
        {
            FrameworkElement fe => fe.DataContext,
            FrameworkContentElement fce => fce.DataContext,
            _ => null,
        };

        switch (data)
        {
            case UserMessageItem user:
                var offerTurnActions = _vm is { IsCodeSurface: true };
                return new Controls.TextSelectionScope.MessageTarget(
                    user.Text,
                    user.Text,
                    offerTurnActions ? () => _ = RewindMessageAsync(user) : null,
                    offerTurnActions ? () => _ = BranchMessageAsync(user) : null);
            case AssistantTextItem assistant:
                // The reference offers its chapter toggle on an assistant turn
                // alone, anchored at the turn rather than at this run.
                var anchor = ChapterAnchorOf(assistant);
                var canPin = anchor >= 0 && ChapterPinToggleRequested is not null && _vm is not null;
                return new Controls.TextSelectionScope.MessageTarget(
                    assistant.Markdown,
                    assistant.Markdown,
                    null,
                    null,
                    canPin ? () => ChapterPinToggleRequested?.Invoke(_vm!, anchor) : null,
                    canPin && IsChapterPinned?.Invoke(_vm!, anchor) == true);
            default:
                return null;
        }
    }

    /// <summary>
    /// Where the assistant turn holding this run starts: the reference anchors a
    /// pinned chapter at the turn's first item, not at the run that was clicked.
    /// </summary>
    private int ChapterAnchorOf(AssistantTextItem item)
    {
        var transcript = _vm?.Transcript;
        if (transcript is null)
        {
            return -1;
        }

        var index = transcript.IndexOf(item);
        if (index < 0)
        {
            return -1;
        }

        // Walk back over everything this turn produced, stopping at the prompt
        // that opened it or at a chapter divider already standing there.
        while (index > 0 && transcript[index - 1] is not UserMessageItem and not ViewModels.ChapterItem)
        {
            index--;
        }

        return index;
    }

    /// <summary>The rendered image the pointer is over, for Copy image / Save image.</summary>
    private static System.Windows.Media.Imaging.BitmapSource? ResolveTranscriptImage(object source)
    {
        for (var node = source as DependencyObject; node is not null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is Image { Source: System.Windows.Media.Imaging.BitmapSource bitmap })
            {
                return bitmap;
            }
        }

        return null;
    }

    private string? ResolveTranscriptContext(object source)
    {
        var data = source switch
        {
            FrameworkElement fe => fe.DataContext,
            FrameworkContentElement fce => fce.DataContext,
            _ => null,
        };
        return data switch
        {
            UserMessageItem user => user.Text,
            AssistantTextItem assistant => assistant.Markdown,
            ThinkingItem thinking => thinking.Text,
            ToolCallItem call => call.Result.Length > 0 ? call.Result : call.InputText,
            _ => null,
        };
    }

    private void AttachContext(string text)
    {
        if (_vm is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Length > 60_000)
        {
            text = text[..60_000] + "\n… [truncated]";
        }

        _vm.ComposerAttachments.Add(new ComposerAttachment { Kind = ComposerAttachmentKind.Context, Text = text });
        FocusInput();
    }

    /// <summary>Ctrl+V with files or an image on the clipboard: attach instead of pasting.</summary>
    private bool HandleSpecialPaste()
    {
        if (_vm is null)
        {
            return false;
        }

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count > 0)
                {
                    AttachFiles(files);
                    return true;
                }
            }

            // The reference attaches the image whenever the clipboard carries one,
            // even when text rides along (browser and Office copies do both).
            if (Clipboard.ContainsImage())
            {
                AttachClipboardImage();
                return true;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard briefly locked by another process.
        }

        return false;
    }

    private void OnComposerPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        // The reference Code composer pastes text inline at any length; only the
        // Chat surface turns a long paste into a "Pasted text" chip.
        if (_vm.IsCodeSurface)
        {
            return;
        }

        // A very long paste becomes a "Pasted text" chip instead of flooding the box.
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) &&
            e.DataObject.GetData(DataFormats.UnicodeText) is string text &&
            text.Length > PastedTextThreshold)
        {
            e.CancelCommand();
            _vm.ComposerAttachments.Add(new ComposerAttachment
            {
                Kind = ComposerAttachmentKind.PastedText,
                Text = text,
            });
        }
    }

    private void AttachClipboardImage()
    {
        if (_vm is null || _services is null)
        {
            return;
        }

        System.Windows.Media.Imaging.BitmapSource? image;
        try
        {
            image = Clipboard.GetImage();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        if (image is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(_services.Paths.Root, "attachments");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            AddImageAttachment(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _vm.Transcript.Add(new NoticeItem { Text = $"Could not attach the image: {ex.Message}", IsError = true });
        }
    }

    // The reference accepts exactly PNG, JPEG, GIF, and WebP as images.
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    // Images it does not accept: attaching one as an opaque file would leave the
    // model unable to see a picture the user plainly meant it to look at, so the
    // reference refuses it by name instead.
    private static readonly string[] OtherImageExtensions =
        [".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".svg", ".ico"];
    /// <summary>
    /// Attaches images from outside the composer — the screenshots Quick Entry
    /// carries — under the same size and count rules a paste obeys.
    /// </summary>
    public void AttachImages(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            AddImageAttachment(path);
        }
    }

    private void AddImageAttachment(string path)
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            if (new FileInfo(path).Length > Services.ImageAttachments.MaxSourceBytes)
            {
                _vm.Transcript.Add(new NoticeItem
                {
                    Text = "Some images were too large to attach. Maximum size is 30 MB.",
                    IsError = true,
                });
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm.Transcript.Add(new NoticeItem
            {
                Text = $"Could not attach {Path.GetFileName(path)}: {ex.Message}",
                IsError = true,
            });
            return;
        }

        var images = _vm.ComposerAttachments.Count(a => a.Kind == ComposerAttachmentKind.Image);
        if (images >= MaxImageAttachments)
        {
            // The reference counts what it dropped and names the cap.
            _vm.Transcript.Add(new NoticeItem
            {
                Text = Services.ChatAttachmentNotices.RemovedOverCap(1, MaxImageAttachments),
            });
            return;
        }

        _vm.ComposerAttachments.Add(new ComposerAttachment { Kind = ComposerAttachmentKind.Image, FilePath = path });
    }

    /// <summary>
    /// Dropped, pasted, or picked paths, routed the way the reference session
    /// dropzone routes them: images and other files become chips, folders become
    /// folder chips (Code surface).
    /// </summary>
    private void AttachFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                AttachFolder(path);
            }
            else if (!File.Exists(path))
            {
                // Path no longer resolves (e.g. stale clipboard entry).
            }
            else if (OtherImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                // An image the model cannot be sent: the reference names the four
                // formats it takes rather than attaching it as an opaque file.
                _vm?.Transcript.Add(new NoticeItem
                {
                    Text = Services.ChatAttachmentNotices.UnsupportedImage,
                    IsError = true,
                });
            }
            else if (ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                AddImageAttachment(path);
            }
            else
            {
                AddFileAttachment(path);
            }
        }

        FocusInput();
    }

    private void AddFileAttachment(string path)
    {
        _vm?.ComposerAttachments.Add(new ComposerAttachment
        {
            Kind = ComposerAttachmentKind.File,
            FilePath = path,
            RelativePath = RelativizePath(path),
        });
    }

    /// <summary>
    /// Folder attach, reference-style: a Code session without a working folder
    /// adopts the first one; otherwise the folder becomes a chip, and a folder
    /// outside the working directory is also registered as an additional
    /// working directory for the session.
    /// </summary>
    private void AttachFolder(string path)
    {
        if (_vm is null || !_vm.IsCodeSurface)
        {
            return;
        }

        path = Path.GetFullPath(path).TrimEnd('\\', '/');
        var cwd = _vm.Session.WorkingDirectory;
        if (string.IsNullOrEmpty(cwd))
        {
            _vm.SetWorkingDirectory(path);
            if (_services is not null)
            {
                _services.Settings.Current.LastWorkingDirectory = path;
                _services.Settings.Save();
            }

            _gitBannerDismissed = false;
            UpdateContextBar();
            _ = RefreshGitBannerAsync();
            return;
        }

        _vm.ComposerAttachments.Add(new ComposerAttachment
        {
            Kind = ComposerAttachmentKind.Folder,
            FilePath = path,
            RelativePath = RelativizePath(path),
        });

        if (!IsUnderDirectory(path, cwd))
        {
            _vm.AddAdditionalDirectory(path);
            UpdateContextBar();
        }
    }

    private string RelativizePath(string path)
    {
        var cwd = _vm?.Session.WorkingDirectory;
        if (!string.IsNullOrEmpty(cwd) && IsUnderDirectory(path, cwd))
        {
            path = Path.GetRelativePath(cwd, path);
        }

        return path.Replace('\\', '/');
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var root = directory.TrimEnd('\\', '/');
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase);
    }

    // ---- session-view dropzone (the whole surface accepts files, like the reference) ----

    private int _dragDepth;

    private static bool HasFileDrop(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnSurfaceDragEnter(object sender, DragEventArgs e)
    {
        if (!HasFileDrop(e))
        {
            return;
        }

        _dragDepth++;
        // The reference Code dropzone is a bare highlight; the Chat one carries a label.
        DropOverlayLabel.Visibility = _vm?.IsCodeSurface == true ? Visibility.Collapsed : Visibility.Visible;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnSurfaceDragLeave(object sender, DragEventArgs e)
    {
        if (_dragDepth > 0 && --_dragDepth == 0)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSurfaceDragOver(object sender, DragEventArgs e)
    {
        // Tunneling keeps the TextBox's own drag-drop handling out of the way.
        e.Effects = HasFileDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = HasFileDrop(e);
    }

    private void OnSurfaceDrop(object sender, DragEventArgs e)
    {
        _dragDepth = 0;
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            AttachFiles(files);
        }
    }

    // ---- queued-message reorder (drag) ----

    private QueuedMessageItem? _queuedDragCandidate;
    private Point _queuedDragOrigin;

    private void OnQueuedRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueuedMessageItem item })
        {
            _queuedDragCandidate = item;
            _queuedDragOrigin = e.GetPosition(this);
        }
    }

    private void OnQueuedRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_queuedDragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _queuedDragOrigin.X) < 4 && Math.Abs(p.Y - _queuedDragOrigin.Y) < 4)
        {
            return;
        }

        var item = _queuedDragCandidate;
        _queuedDragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(QueuedMessageItem), item), DragDropEffects.Move);
    }

    private void OnQueuedRowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(QueuedMessageItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnQueuedRowDrop(object sender, DragEventArgs e)
    {
        if (_vm is null ||
            e.Data.GetData(typeof(QueuedMessageItem)) is not QueuedMessageItem dragged ||
            sender is not FrameworkElement { DataContext: QueuedMessageItem target } ||
            ReferenceEquals(dragged, target))
        {
            return;
        }

        var from = _vm.QueuedMessages.IndexOf(dragged);
        var to = _vm.QueuedMessages.IndexOf(target);
        if (from >= 0 && to >= 0)
        {
            _vm.QueuedMessages.Move(from, to);
        }

        e.Handled = true;
    }

    // ---- transcript navigation ----

    /// <summary>Jump to previous (-1) / next (+1) prompt, aligning it with the viewport top.</summary>
    /// <summary>
    /// The virtualizer laying the transcript out, once it exists. Everything that
    /// used to ask the container generator where a row is has to ask this instead:
    /// the generator only knows the rows that were built.
    /// </summary>
    private Controls.VirtualizingTranscriptPanel? TranscriptPanel =>
        Controls.VirtualizingTranscriptPanel.GetPanel(TranscriptItems);

    /// <summary>
    /// Put a row on screen whether or not it has been built. Falls back to the
    /// container while the panel has not laid out yet, which is the case for the
    /// first frame after a session opens.
    /// </summary>
    private void ScrollTranscriptToIndex(int index)
    {
        if (index < 0)
        {
            return;
        }

        _autoScroll = false;
        if (TranscriptPanel is { } panel && panel.ScrollToIndex(index))
        {
            return;
        }

        if (TranscriptItems.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
        }
    }

    /// <summary>
    /// Where a transcript row's top sits inside the viewport, from the offsets the
    /// virtualizer keeps for every row rather than from the element, which exists
    /// only for the rows on screen.
    /// </summary>
    private double? TranscriptRowViewportY(int index)
    {
        if (TranscriptPanel is { } panel)
        {
            return panel.OffsetOfRow(index) - TranscriptScroll.VerticalOffset;
        }

        if (TranscriptItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
            !container.IsVisible)
        {
            return null;
        }

        try
        {
            return container.TranslatePoint(new Point(0, 0), TranscriptScroll).Y;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remember where this session's transcript was left and how tall its rows
    /// turned out to be, so coming back to it neither re-measures a thousand rows
    /// nor lands somewhere else. The reference saves exactly this record.
    /// </summary>
    private void SaveTranscriptViewport()
    {
        if (_vm is null || TranscriptPanel is not { } panel)
        {
            return;
        }

        bool pinned = Services.TranscriptScrolling.IsAtTail(
            TranscriptScroll.VerticalOffset, TranscriptScroll.ScrollableHeight);
        var anchor = pinned ? null : panel.AnchorAt(TranscriptScroll.VerticalOffset);
        Services.TranscriptViewportStore.Current.Save(_vm.Session.Id, new()
        {
            IsPinned = pinned,
            AnchorKey = anchor?.Key,
            AnchorOffsetPx = anchor?.Offset ?? 0,
            Sizes = new Dictionary<string, double>(panel.Sizes),
            ColumnWidth = TranscriptItems.ActualWidth,
            TextSize = Controls.MarkdownView.TranscriptTextSize,
        });
    }

    /// <summary>Put a saved viewport back, once the panel has been laid out for it.</summary>
    private void RestoreTranscriptViewport(ChatViewModel viewModel)
    {
        var snapshot = Services.TranscriptViewportStore.Current.Load(
            viewModel.Session.Id, TranscriptItems.ActualWidth, Controls.MarkdownView.TranscriptTextSize);
        if (snapshot is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ReferenceEquals(_vm, viewModel) || TranscriptPanel is not { } panel)
            {
                return;
            }

            if (snapshot.Sizes.Count > 0)
            {
                panel.RestoreSizes(snapshot.Sizes);
            }

            if (snapshot.IsPinned || snapshot.AnchorKey is null)
            {
                return;
            }

            UpdateLayout();
            if (panel.ScrollToKey(snapshot.AnchorKey, snapshot.AnchorOffsetPx))
            {
                _autoScroll = false;
            }
        });
    }

    public void JumpToPrompt(int direction)
    {
        if (_vm is null || TranscriptScroll.Visibility != Visibility.Visible)
        {
            return;
        }

        var prompts = new List<(int Index, double Y)>();
        for (var i = 0; i < _vm.Transcript.Count; i++)
        {
            if (_vm.Transcript[i] is not UserMessageItem)
            {
                continue;
            }

            if (TranscriptRowViewportY(i) is { } y)
            {
                prompts.Add((i, y));
            }
        }

        if (prompts.Count == 0)
        {
            return;
        }

        (int Index, double Y)? target = direction > 0
            ? prompts.Where(p => p.Y > 6).OrderBy(p => p.Y).Cast<(int, double)?>().FirstOrDefault()
            : prompts.Where(p => p.Y < -6).OrderByDescending(p => p.Y).Cast<(int, double)?>().FirstOrDefault();
        if (target is not { } hit)
        {
            return;
        }

        _autoScroll = false;
        TranscriptScroll.ScrollToVerticalOffset(TranscriptScroll.VerticalOffset + hit.Y - 4);
    }

    /// <summary>Whether anything arrived while the reader was scrolled away from the tail.</summary>
    private bool _newMessagesWhileAway;

    private void OnScrollToBottomClick(object sender, RoutedEventArgs e) => ScrollToBottom();

    /// <summary>Hover copy on a thinking block: the reference's blockquote format, tick feedback.</summary>
    private async void OnCopyThinking(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ViewModels.ThinkingItem thinking })
        {
            return;
        }

        try
        {
            Clipboard.SetText(thinking.CopyText);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return; // Clipboard busy; nothing sensible to do.
        }

        thinking.IsCopied = true;
        await Task.Delay(1500);
        thinking.IsCopied = false;
    }

    /// <summary>Which agent's transcript the pane was asked to show, and its title.</summary>
    public sealed record SubagentViewRequest(string CallId, string Description);

    /// <summary>An inline preview card wants something from the workspace's preview servers.</summary>
    public sealed record PreviewCardAction(ViewModels.ToolCallItem Item, string Action);

    /// <summary>Raised by preview-card buttons; the workspace owns the servers and the Browser panel.</summary>
    public event EventHandler<PreviewCardAction>? PreviewActionRequested;

    private void RaisePreviewAction(object sender, string action)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.ToolCallItem { IsPreviewCard: true } item })
        {
            PreviewActionRequested?.Invoke(this, new PreviewCardAction(item, action));
        }
    }

    private void OnPreviewCardLoaded(object sender, RoutedEventArgs e) => RaisePreviewAction(sender, "thumbnail");

    private void OnPreviewOpen(object sender, RoutedEventArgs e) => RaisePreviewAction(sender, "open");

    private void OnPreviewViewLogs(object sender, RoutedEventArgs e) => RaisePreviewAction(sender, "logs");

    private void OnPreviewStop(object sender, RoutedEventArgs e) => RaisePreviewAction(sender, "stop");

    /// <summary>The message footer's Copy: the turn's answer onto the clipboard.</summary>
    private void CopyTurn(ViewModels.AssistantFooterItem? item)
    {
        if (item?.Text is not { Length: > 0 } text)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process holds the clipboard; the tick simply does not show.
            return;
        }

        item.IsCopied = true;
        var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            item.IsCopied = false;
        };
        revert.Start();
    }

    /// <summary>
    /// "Fork from here": a new session carrying the conversation through this
    /// answer, left for the user to continue — the original is untouched.
    /// </summary>
    private async Task ForkTurnAsync(ViewModels.AssistantFooterItem? item)
    {
        if (item is null || _vm is null)
        {
            return;
        }

        var fork = await _vm.ForkFromAsync(item);
        if (fork is null)
        {
            return;
        }

        LoadSession(fork);
        ViewModel.Transcript.Add(new NoticeItem
        {
            Text = "Forked into a new session — the original is untouched.",
        });
        UpdateLayoutState();
    }

    /// <summary>
    /// "Run in background": this turn's running calls carry on off the turn and
    /// show up in the Background tasks pane. The reference's failure notice is
    /// what a call that has already finished gets.
    /// </summary>
    private void RunTurnInBackground(ViewModels.AssistantFooterItem? item)
    {
        if (item is null || _vm is null)
        {
            return;
        }

        var moved = 0;
        foreach (var call in item.MovableCalls.ToList())
        {
            if (_vm.BackgroundMoves.Request(call.CallId))
            {
                moved++;
            }
        }

        if (moved == 0)
        {
            _vm.Transcript.Add(new ViewModels.NoticeItem
            {
                Text = "Tool call couldn't be moved to the background. Try again.",
                IsError = true,
            });
        }
    }

    /// <summary>
    /// A Agent row was clicked: the reference opens that agent's own
    /// transcript in the Background tasks pane rather than expanding the row.
    /// </summary>
    public event EventHandler<SubagentViewRequest>? SubagentViewRequested;

    /// <summary>Background agent rows open the runs panel on click, like the reference's task rows.</summary>
    /// <summary>
    /// An agent row was activated — by a click, by Space or Enter on the focused
    /// row, or by an automation peer, which all raise Click on the plain button
    /// those rows use. It opens that agent's transcript in the Background tasks
    /// pane, the reference's single opener for both entry points.
    /// </summary>
    private void OnToolRowActivated(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.ToolCallItem { IsSubagentRow: true } row } &&
            _vm is { IsCodeSurface: true })
        {
            SubagentViewRequested?.Invoke(this, new SubagentViewRequest(row.CallId, row.SubagentTitle));
            e.Handled = true;
        }
    }

    /// <summary>
    /// The file a row names, opened where the reference opens it: the diff for a
    /// file this session changed, the file itself otherwise. The reference draws
    /// that meta as a file reference rather than as dead text, which is what
    /// makes an "Edited Base.xaml" row a way to get to Base.xaml.
    /// </summary>
    private void OnToolMetaFile(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.ToolCallItem { MetaFilePath: { Length: > 0 } path } })
        {
            Controls.MarkdownView.FileActivationRequested?.Invoke(path, null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The body's Copy: the call's arguments and its output, one blank line
    /// apart, with the reference's own 1200ms tick on the button afterwards.
    /// </summary>
    private async void OnToolBodyCopy(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ViewModels.ToolCallItem call })
        {
            return;
        }

        e.Handled = true;
        var text = call.CopyBodyText;
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return;
        }

        call.IsBodyCopied = true;
        await Task.Delay(1200);
        call.IsBodyCopied = false;
    }

    /// <summary>A returned screenshot opens in its viewer, at the one that was clicked.</summary>
    private void OnToolImageActivated(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JarvisCode.Core.Models.ImageBlock image } element)
        {
            return;
        }

        e.Handled = true;
        var call = FindAncestorDataContext<ViewModels.ToolCallItem>(element);
        var images = call?.Images ?? [image];
        var index = Math.Max(0, images.ToList().IndexOf(image));
        ImageLightboxWindow.Open(Window.GetWindow(this), images, index);
    }

    private static T? FindAncestorDataContext<T>(DependencyObject element) where T : class
    {
        for (var node = element; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: T match })
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>A tool row's meta link (an opened PR) — open in the default browser.</summary>
    private void OnToolMetaLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri is { IsAbsoluteUri: true } uri && uri.Scheme is "http" or "https")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Browser launch failed; the link stays copyable from the expanded row.
            }
        }

        e.Handled = true;
    }

    public void ScrollToBottom()
    {
        _autoScroll = true;
        TranscriptScroll.ScrollToEnd();
    }

    // ---- scrolling ----

    /// <summary>
    /// The wheel belongs to the page unless the region under the pointer still has
    /// somewhere to scroll, which is how a browser treats a nested scroll box. WPF's
    /// ScrollViewer marks the wheel handled whether or not it moved, so the renderer's
    /// own viewports — the one a table sits in, a diagram's, and the capped tool-call
    /// bodies — used to swallow it and leave the transcript standing still.
    /// </summary>
    private void OnTranscriptWheel(object sender, MouseWheelEventArgs e)
    {
        if (InnerScrollerUnder(e.OriginalSource) is { } inner && CanScrollFurther(inner, e.Delta))
        {
            return;
        }

        // One step for the whole transcript: a fence, a table and the paragraph
        // between them move by the same amount, and /scroll-speed multiplies all
        // three. Auto-scroll is deliberately not touched here — OnScrollChanged
        // re-reads it from where the view actually landed, so a wheel event that
        // could not move (already at an end) leaves the tail pinned.
        e.Handled = true;
        TranscriptScroll.ScrollToVerticalOffset(
            TranscriptScroll.VerticalOffset + Services.TranscriptScrolling.VerticalOffsetDelta(
                e.Delta,
                ScrollSpeed,
                SystemParameters.WheelScrollLines,
                TranscriptScroll.ViewportHeight));
    }

    /// <summary>Whether a scrolling region has room left in the wheel's direction.</summary>
    private static bool CanScrollFurther(FrameworkElement scroller, int delta)
    {
        var (extent, viewport, offset) = scroller switch
        {
            System.Windows.Controls.Primitives.TextBoxBase box =>
                (box.ExtentHeight, box.ViewportHeight, box.VerticalOffset),
            ScrollViewer view => (view.ExtentHeight, view.ViewportHeight, view.VerticalOffset),
            _ => (0d, 0d, 0d),
        };

        if (extent <= viewport + 1)
        {
            return false;
        }

        return delta < 0 ? offset < extent - viewport - 1 : offset > 1;
    }

    /// <summary>/scroll-speed — the wheel multiplier, clamped to a usable range.</summary>
    private double ScrollSpeed => _services is null
        ? 1.0
        : Math.Clamp(_services.UiSettings.Current.ScrollSpeed, MinScrollSpeed, MaxScrollSpeed);

    internal const double MinScrollSpeed = 0.25;
    internal const double MaxScrollSpeed = 5.0;

    /// <summary>
    /// The first region between the pointer and the transcript that scrolls on its
    /// own — a read-only box, or one of the viewports the markdown and tool-row
    /// renderers wrap wide or capped content in.
    /// </summary>
    private FrameworkElement? InnerScrollerUnder(object source)
    {
        var node = source as DependencyObject;
        while (node is not null && !ReferenceEquals(node, TranscriptScroll))
        {
            if (node is System.Windows.Controls.Primitives.TextBoxBase or ScrollViewer)
            {
                return (FrameworkElement)node;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>
    /// A row the reader clicked is the anchor for the layout that click causes.
    /// ToggleButton raises Click after it has flipped and before the pass that
    /// resizes the transcript, so this is the last moment the old position can be
    /// read; <see cref="RestoreScrollAnchor"/> puts it back afterwards. A click
    /// that changes no height measures a delta of zero and does nothing.
    /// </summary>
    private void OnTranscriptRowClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement row || !row.IsVisible || !IsDisclosure(row))
        {
            return;
        }

        if (ViewportOffsetOf(row) is not { } y)
        {
            return;
        }

        if (_scrollAnchor is null)
        {
            // The pass this click causes is the one to measure against, so the
            // correction rides the layout rather than a guessed dispatcher slot.
            TranscriptScroll.LayoutUpdated += OnAnchoredLayout;
        }

        _scrollAnchor = row;
        _scrollAnchorY = y;
        _anchoringScroll = true;
        if (TranscriptPanel is { } panel)
        {
            panel.SuspendCompensation = true;
        }
    }

    /// <summary>
    /// Whether a click is one of the transcript's disclosures — a tool run, a tool
    /// row, a widget, or either half of a thinking cell. Those are the controls that
    /// change how tall the transcript is; anchoring on anything else would hold the
    /// view still against the content a turn is still streaming in.
    /// </summary>
    private static bool IsDisclosure(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.ToggleButton or ThinkingCell)
            {
                return true;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>Where an element's top sits inside the transcript viewport, or null.</summary>
    private double? ViewportOffsetOf(FrameworkElement element)
    {
        try
        {
            return element.TransformToAncestor(TranscriptScroll).Transform(default).Y;
        }
        catch (InvalidOperationException)
        {
            // Not under the transcript, or no layout to transform through.
            return null;
        }
    }

    private void OnAnchoredLayout(object? sender, EventArgs e)
    {
        TranscriptScroll.LayoutUpdated -= OnAnchoredLayout;
        var anchor = _scrollAnchor;
        _scrollAnchor = null;
        if (anchor is not null && anchor.IsVisible && ViewportOffsetOf(anchor) is { } now)
        {
            var drift = now - _scrollAnchorY;
            if (Math.Abs(drift) >= 0.5)
            {
                TranscriptScroll.ScrollToVerticalOffset(TranscriptScroll.VerticalOffset + drift);
            }
        }

        // The suspension outlives this pass by a beat: whether the scroll viewer
        // reports its new extent before or after this handler is its own business,
        // and either way that report must not re-pin the view.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _anchoringScroll = false;
            if (TranscriptPanel is { } panel)
            {
                panel.SuspendCompensation = false;
            }

            // Holding the anchor can take the view off the tail, which is a real
            // scroll position rather than a suspended one: track it like any other.
            _autoScroll = Services.TranscriptScrolling.IsAtTail(
                TranscriptScroll.VerticalOffset, TranscriptScroll.ScrollableHeight);
        });
    }

    /// <summary>
    /// Take the page of history above this one and stay where the reader was. The
    /// reference's two triggers, both carried: the reader coming within a viewport
    /// of the top, and a viewport the content does not fill.
    /// </summary>
    private void LoadOlderTranscriptPage()
    {
        if (_loadingOlder || _vm is not { TranscriptHasOlder: true } viewModel)
        {
            return;
        }

        _loadingOlder = true;
        try
        {
            // The rows already on screen keep their keys across the reload, so the
            // anchor names the same row afterwards and the heights measured for it
            // are still filed under it.
            var anchor = TranscriptPanel?.AnchorAt(TranscriptScroll.VerticalOffset);
            if (!viewModel.LoadOlderTranscript())
            {
                return;
            }

            UpdateLayout();
            if (anchor is { } saved && TranscriptPanel is { } panel)
            {
                panel.ScrollToKey(saved.Key, saved.Offset);
            }
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_olderQueued && _vm is { TranscriptHasOlder: true } &&
            (TranscriptScroll.VerticalOffset < TranscriptScroll.ViewportHeight ||
             TranscriptScroll.ExtentHeight <= TranscriptScroll.ViewportHeight))
        {
            // One page per drain: a scroll raises this handler several times over,
            // and each queued call would take another page before the first had
            // laid out.
            _olderQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _olderQueued = false;
                LoadOlderTranscriptPage();
            });
        }

        if (e.ExtentHeightChange == 0)
        {
            // User-driven scroll: track whether we're pinned to the bottom.
            _autoScroll = Services.TranscriptScrolling.IsAtTail(
                TranscriptScroll.VerticalOffset, TranscriptScroll.ScrollableHeight);
        }
        else if (_autoScroll && !_anchoringScroll)
        {
            TranscriptScroll.ScrollToEnd();
        }

        var awayFromTail =
            TranscriptScroll.Visibility == Visibility.Visible &&
            TranscriptScroll.ScrollableHeight > 0 &&
            !Services.TranscriptScrolling.IsAtTail(
                TranscriptScroll.VerticalOffset, TranscriptScroll.ScrollableHeight);
        ScrollToBottomButton.Visibility = awayFromTail ? Visibility.Visible : Visibility.Collapsed;

        // Content that arrived while the reader was away turns the pill into the
        // reference's "New messages" indicator; going back to the tail clears it.
        if (!awayFromTail)
        {
            _newMessagesWhileAway = false;
        }
        else if (e.ExtentHeightChange > 0)
        {
            _newMessagesWhileAway = true;
        }

        if (ScrollToBottomButton.Template?.FindName("NewMessagesLabel", ScrollToBottomButton) is TextBlock label)
        {
            label.Visibility = _newMessagesWhileAway ? Visibility.Visible : Visibility.Collapsed;
        }

        ScrollToBottomButton.SetValue(
            System.Windows.Automation.AutomationProperties.NameProperty,
            _newMessagesWhileAway ? "New messages" : "Scroll to bottom");
    }
}
