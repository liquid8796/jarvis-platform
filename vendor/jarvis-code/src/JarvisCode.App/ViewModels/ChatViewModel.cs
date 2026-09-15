using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using JarvisCode.App.Composition;
using JarvisCode.App.Infrastructure;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Tools.BuiltIn;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.ViewModels;

/// <summary>
/// One open conversation: its transcript, streaming state, permission prompts,
/// and the turn loop against the orchestrator.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly TurnContextFactory _factory;

    /// <summary>
    /// The reference's "Run in background": running tool calls that can be moved
    /// off the turn register here, and the transcript's button asks through it.
    /// </summary>
    public JarvisCode.Core.BackgroundTasks.BackgroundMoveRequests BackgroundMoves { get; } = new();
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource _sessionLifetime = new();
    private static readonly SessionIdleSubscriptions IdleSubscriptions = new();
    private LocalSessionMailbox? _mailbox;

    public LocalSessionMailbox EnsureMailbox()
    {
        if (_mailbox?.SessionId == Session.Id) return _mailbox;
        if (_mailbox is { } prior) _ = prior.DisposeAsync();
        var owner = Session;
        var lifetime = _sessionLifetime.Token;
        _mailbox = new LocalSessionMailbox(_services.Paths.Root, owner.Id, () => owner.Title,
            (peer, message) => DispatchIncoming(() =>
                DeliverTaskNotification("session-" + peer.SessionId, "message", "Message from " + peer.Title, message), owner.Id, lifetime),
            notice => DispatchIncoming(() => DeliverIdleNotice(notice), owner.Id, lifetime));
        return _mailbox;
    }

    private void DispatchIncoming(Action deliver, string sessionId, CancellationToken lifetime)
    {
        Interlocked.Increment(ref _mailboxDeliveries);
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_discarded || Session.Id != sessionId || lifetime.IsCancellationRequested)
                    throw new InvalidOperationException("The receiving session has closed.");
                if (!IsRunning) _incomingWork = true;
                deliver();
            });
        }
        finally { Interlocked.Decrement(ref _mailboxDeliveries); }
    }
    private readonly Dictionary<string, ToolCallItem> _callsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WidgetItem> _streamingWidgets = new(StringComparer.Ordinal);
    private readonly Action<JarvisCode.Core.BackgroundTasks.BackgroundTaskInfo> _taskExitedHandler;

    private CancellationTokenSource? _turnCts;
    private AssistantTextItem? _currentText;
    private ThinkingItem? _currentThinking;
    private DateTimeOffset? _thinkingRunStart;
    private DateTimeOffset? _thinkingRunEnd;
    private ToolGroupItem? _currentGroup;
    private bool _isRunning;
    private bool _discarded;
    private bool _finishingTurn;
    private volatile bool _incomingWork;
    private int _mailboxDeliveries;

    /// <summary>Work that still needs this session host after its visible model turn becomes idle.</summary>
    public bool HasSessionBackgroundWork => IsRunning || _finishingTurn || _incomingWork || _summaryBusy || Volatile.Read(ref _mailboxDeliveries) > 0 ||
        (_mailbox?.HasPendingSubscriptions ?? false) || IdleSubscriptions.HasFor(Session.Id) ||
        (_workers?.RunningCount ?? 0) > 0 || _loops.Count > 0 || QueuedMessages.Count > 0 || _pendingNotifications.Count > 0 ||
        _services.BackgroundTasks.List().Any(t => t.SessionId == Session.Id && t.Status == JarvisCode.Core.BackgroundTasks.BackgroundTaskStatus.Running) ||
        new JarvisCode.Core.Routines.RoutineStore(_services.Paths.RoutinesFile).Load()
            .Any(r => r.Enabled && (r.OwnerSessionId == Session.Id || r.CronSessionId == Session.Id));
    private string _contextUsageText = "";
    private PermissionPromptViewModel? _pendingPermission;
    private QuestionPromptViewModel? _pendingQuestion;
    private PlanApprovalViewModel? _pendingPlanApproval;
    private Usage _turnUsage = Usage.Zero;
    private long _lastContextTokens;
    /// <summary>When the last model response landed — what decides whether its prompt cache is still warm.</summary>
    private DateTimeOffset? _lastUsageAt;
    /// <summary>Armed by an interrupt that left background agents running.</summary>
    private bool _stopKillsAgents;
    private long _compactionFromTokens;
    private string? _lastThinkingText;

    public ChatViewModel(AppServices services, bool isCodeSurface)
    {
        _services = services;
        _factory = new TurnContextFactory(services);
        _dispatcher = Dispatcher.CurrentDispatcher;
        // spawn_task and dismiss_task run on the turn's thread; the chip strip
        // repaints on the UI one.
        Suggestions.Changed += () => _dispatcher.InvokeAsync(RefreshTaskSuggestions);
        IsCodeSurface = isCodeSurface;
        _totalTokens = ResolveTotalTokens(services);
        Gate = new UiPermissionGate
        {
            BlockReadsOutsideWorkingDirectories = services.Settings.Current.BlockReadsOutsideWorkingDirectories,
            PersistBlockReadsAsync = () =>
            {
                services.Settings.Current.BlockReadsOutsideWorkingDirectories = true;
                services.Settings.Save();
                return Task.CompletedTask;
            },
            GlobalRuleLines = services.Settings.Current.PermissionRuleLines,
            PromptAsync = ShowPermissionPromptAsync,
        };
        // Backgrounded shell rows stay live until their task exits.
        _taskExitedHandler = info => _dispatcher.InvokeAsync(() => OnBackgroundTaskExited(info));
        services.BackgroundTasks.TaskExited += _taskExitedHandler;

        RemoveQueuedCommand = new RelayCommand<QueuedMessageItem>(item =>
        {
            if (item is not null)
            {
                QueuedMessages.Remove(item);
            }
        });
        RemoveAttachmentCommand = new RelayCommand<ComposerAttachment>(item =>
        {
            if (item is not null)
            {
                ComposerAttachments.Remove(item);
            }
        });
        ClearQueuedCommand = new RelayCommand(() => QueuedMessages.Clear());
        QueuedMessages.CollectionChanged += (_, _) =>
        {
            if (QueuedMessages.Count == 0)
            {
                _queueExpanded = false;
                OnPropertyChanged(nameof(QueueExpanded));
            }

            OnPropertyChanged(nameof(HasQueuedMessages));
            OnPropertyChanged(nameof(QueuedHeader));
            OnPropertyChanged(nameof(QueuedStatus));
            OnPropertyChanged(nameof(QueuedNext));
            OnPropertyChanged(nameof(QueuedNextVisible));
            OnPropertyChanged(nameof(QueueRowsVisible));
        };

        Session = Session.CreateNew(DefaultWorkingDirectory());
        Gate.WorkingDirectory = Session.WorkingDirectory;
        ApplyWorkspacePermissionMode();
    }

    /// <summary>
    /// Whether the user picked a permission mode in this session. The reference's own
    /// layering puts that choice above every stored one, so a workspace's remembered
    /// mode never takes a session back off the mode its user just chose.
    /// </summary>
    public bool PermissionPickedInSession { get; set; }

    /// <summary>
    /// The reference's permission-mode layering, with the layers this build has:
    /// the pick made in this session, then the project's own settings files
    /// (whose "auto" and "bypassPermissions" are ignored, as the reference ignores
    /// them), then the configured default, then the mode last picked in this
    /// workspace, then the built-in default the gate starts on. A stored Plan is
    /// ignored, as it is there — plan mode is how a turn starts, not how a folder
    /// is worked in.
    /// </summary>
    private void ApplyWorkspacePermissionMode()
    {
        if (PermissionPickedInSession || Gate.Mode == PermissionMode.Plan)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Session.WorkingDirectory) &&
            JarvisCode.Core.Permissions.ProjectPermissions.LoadDefaultMode(Session.WorkingDirectory) is { } projectMode)
        {
            Gate.Mode = Services.PermissionModeMenu.Clamp(projectMode, Services.PermissionModeMenu.OfferedFor(_services.UiSettings.Current));
            return;
        }

        // Both layers are read back from text a hand-edited file can put anything in,
        // so the answer is clamped to what the picker actually offers — a session in a
        // mode with no row to check would be a picker that shows nothing selected.
        if (Enum.TryParse<PermissionMode>(
                _services.Settings.Current.PermissionModeName, ignoreCase: true, out var configured))
        {
            Gate.Mode = Services.PermissionModeMenu.Clamp(configured, Services.PermissionModeMenu.OfferedFor(_services.UiSettings.Current));
            return;
        }

        var workspace = Session.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(workspace)
            && _services.UiSettings.Current.FolderPermissionModes.TryGetValue(workspace, out var stored)
            && Enum.TryParse<PermissionMode>(stored, ignoreCase: true, out var remembered)
            && remembered != PermissionMode.Plan)
        {
            Gate.Mode = Services.PermissionModeMenu.Clamp(remembered, Services.PermissionModeMenu.OfferedFor(_services.UiSettings.Current));
        }
    }

    public bool IsCodeSurface { get; }

    private JsonSessionStore Store => IsCodeSurface ? _services.Sessions : _services.ChatSessions;

    /// <summary>
    /// An incognito chat, which the reference does not persist at all: it opens
    /// from /new?incognito=true, is never written to the store and never appears in
    /// the list. Everything else about it is an ordinary chat.
    /// </summary>
    public bool Incognito { get; set; }

    /// <summary>Writes a session unless this one is incognito.</summary>
    private Task SaveSessionAsync(JarvisCode.Core.Sessions.Session? session = null) =>
        Incognito ? Task.CompletedTask : Store.SaveAsync(session ?? Session);

    public UiPermissionGate Gate { get; }

    /// <summary>
    /// Host-provided tools joined into every turn on this surface — artifact, browser and
    /// computer use on Code; nothing, or desktop control alone, on Chat.
    /// </summary>
    public IReadOnlyList<JarvisCode.Core.Tools.ITool> ExtraTools { get; set; } = [];

    /// <summary>
    /// The <c># MCP Server Instructions</c> section the in-process servers
    /// supply, rendered by the window that composed them. Null when no server
    /// contributed — which is every Chat session, and a Code session whose
    /// servers are all switched off.
    /// </summary>
    public string? McpServerInstructions { get; set; }

    /// <summary>
    /// The in-process servers' blocks, so a turn can render them beside the
    /// configured servers' own <c>instructions</c> under one header.
    /// </summary>
    internal IReadOnlyList<Services.McpServerInstructions.Block> McpServerInstructionBlocks { get; set; } = [];

    public Session Session { get; private set; }

    public ObservableCollection<TranscriptItem> Transcript { get; } = [];

    /// <summary>
    /// How many stored messages one page of transcript covers. The reference pages
    /// its history because the history lives on its server; here it is already on
    /// disk, so what a page saves is the view models — measured at about 0.09ms
    /// each, so a page is several screens deep and still well under one frame.
    /// </summary>
    public const int TranscriptPageSize = 400;

    private int _transcriptFrom;
    private bool _transcriptPaged;
    private int _rebuildMessage = -1;
    private int _rebuildOrdinal;

    /// <summary>
    /// There is conversation above what the transcript holds. The reference's
    /// <c>hasOlder</c>: the reader scrolling near the top is what asks for it.
    /// </summary>
    public bool TranscriptHasOlder => _transcriptFrom > 0;

    /// <summary>
    /// Take one more page of history. Answers false when the transcript already
    /// starts at the first message.
    /// </summary>
    public bool LoadOlderTranscript()
    {
        if (_transcriptFrom <= 0)
        {
            return false;
        }

        _transcriptFrom = Math.Max(0, _transcriptFrom - TranscriptPageSize);
        ReloadTranscriptPage();
        return true;
    }

    /// <summary>
    /// Take all of it. Find and Select all answer about the whole conversation, so
    /// they ask for the whole conversation before they read it.
    /// </summary>
    public void LoadEntireTranscript()
    {
        if (_transcriptFrom <= 0)
        {
            return;
        }

        _transcriptFrom = 0;
        ReloadTranscriptPage();
    }

    private void ReloadTranscriptPage()
    {
        ResetTranscript();
        RebuildTranscriptFromHistory(Session);
        OnPropertyChanged(nameof(TranscriptHasOlder));
    }

    /// <summary>
    /// Stamps a rebuilt row with the key its position in the stored session gives
    /// it. Two things need that key to be stable: the measured height the
    /// virtualizer files under it, and the anchor a viewport snapshot names — and
    /// both have to survive a page of older history arriving above the row.
    /// </summary>
    private T Rebuilt<T>(T item) where T : TranscriptItem
    {
        if (_rebuildMessage >= 0)
        {
            item.RowKey = Services.TranscriptKeys.ForHistory(_rebuildMessage, _rebuildOrdinal++);
        }

        return item;
    }

    public ObservableCollection<TodoItem> Todos { get; } = [];

    /// <summary>Prompts typed while a turn runs; sent one per turn once it ends.</summary>
    /// <summary>
    /// The Chat surface's waiting line. The Code surface narrates a turn through
    /// <c>TurnStatus</c>; the reference runs a different component on Chat, with its
    /// own labels and timings, so this one is kept beside it rather than folded in.
    /// </summary>
    public ChatTurnStatus ChatStatus { get; } = new();

    public ObservableCollection<QueuedMessageItem> QueuedMessages { get; } = [];

    public bool HasQueuedMessages => QueuedMessages.Count > 0;

    /// <summary>
    /// The chip queue behind mcp__ccd_session__spawn_task: out-of-scope issues
    /// the model flagged, each one click away from its own session. Lives on the
    /// session, as the reference's does, so switching sessions switches chips.
    /// </summary>
    public Services.BackgroundTaskSuggestions Suggestions { get; } = new();

    /// <summary>The pending chips, mirrored for binding.</summary>
    public ObservableCollection<Services.TaskSuggestion> TaskSuggestions { get; } = [];

    public bool HasTaskSuggestions => TaskSuggestions.Count > 0;

    /// <summary>Repaints the chip strip from the queue after a spawn or dismiss.</summary>
    public void RefreshTaskSuggestions()
    {
        TaskSuggestions.Clear();
        foreach (var suggestion in Suggestions.Pending)
        {
            TaskSuggestions.Add(suggestion);
        }

        OnPropertyChanged(nameof(HasTaskSuggestions));
    }

    private bool _queueExpanded;

    /// <summary>Several queued messages collapse into a stack; the header click expands them.</summary>
    public bool QueueExpanded
    {
        get => _queueExpanded;
        set
        {
            if (_queueExpanded != value)
            {
                _queueExpanded = value;
                OnPropertyChanged(nameof(QueueExpanded));
                OnPropertyChanged(nameof(QueueRowsVisible));
                OnPropertyChanged(nameof(QueuedNextVisible));
            }
        }
    }

    /// <summary>A single queued message always shows its row; a stack shows rows only when expanded.</summary>
    public bool QueueRowsVisible => QueueExpanded || QueuedMessages.Count <= 1;

    /// <summary>
    /// The web searches this conversation ran, newest last, for the Chat surface's
    /// activity drawer. Only searches this engine can see are listed: a search
    /// Anthropic runs server-side reports its name and nothing else.
    /// </summary>
    public ObservableCollection<Services.ChatSearch> Searches { get; } = [];

    private int _toolCallCount;

    /// <summary>How many tool calls this conversation has made.</summary>
    public int ToolCallCount
    {
        get => _toolCallCount;
        private set => SetProperty(ref _toolCallCount, value);
    }

    /// <summary>Records a finished call for the activity drawer.</summary>
    private void RecordActivity(string toolName, string argumentsJson, string result, bool isError)
    {
        ToolCallCount++;
        if (toolName is "web_search" or "WebSearch")
        {
            Searches.Add(Services.ChatActivityPanel.Describe(argumentsJson, result, isError));
        }
    }

    /// <summary>The count, which is all the reference's own header carries.</summary>
    public string QueuedHeader => Services.ChatQueue.CountLabel(QueuedMessages.Count);

    /// <summary>
    /// The sentence an assistive reader is given. The reference draws the count and
    /// the next message's preview and says this out of sight, so it rides the
    /// stack's accessible name rather than the screen.
    /// </summary>
    public string QueuedStatus => Services.ChatQueue.QueuedStatus(QueuedMessages.Count);

    /// <summary>What a collapsed stack shows beside the count.</summary>
    public string QueuedNext => QueuedMessages.Count > 0
        ? Services.ChatQueue.NextLabel(QueuedMessages[0].Preview)
        : "";

    /// <summary>The collapsed stack shows the next message; expanded, the rows do.</summary>
    public bool QueuedNextVisible => QueuedMessages.Count > 1 && !QueueRowsVisible;

    public RelayCommand<QueuedMessageItem> RemoveQueuedCommand { get; }

    /// <summary>"Clear all" — the reference's footer action under an expanded stack.</summary>
    public RelayCommand ClearQueuedCommand { get; }

    /// <summary>
    /// Chips above the composer input — attached context, pasted text, images —
    /// folded into the next message on send.
    /// </summary>
    public ObservableCollection<ComposerAttachment> ComposerAttachments { get; } = [];

    public RelayCommand<ComposerAttachment> RemoveAttachmentCommand { get; }

    public event EventHandler? SessionPersisted;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => Transcript.Count == 0 && !IsRunning;

    /// <summary>
    /// Re-reads <see cref="IsEmpty"/> after the transcript was changed from
    /// outside a turn, which is what the dev poses do.
    /// </summary>
    public void NotifyTranscriptChanged() => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Live state for the transcript-tail turn status line.</summary>
    public TurnStatus TurnStatus { get; } = new();

    private JarvisCode.Core.Agent.AgentWorkerManager? _workers;

    /// <summary>This session's background agents; results come back as task notifications.</summary>
    public JarvisCode.Core.Agent.AgentWorkerManager Workers
    {
        get
        {
            if (_workers is not null) return _workers;
            var ownerSession = Session.Id;
            var lifetime = _sessionLifetime.Token;
            var manager = new JarvisCode.Core.Agent.AgentWorkerManager(
                SessionStatePath(System.IO.Path.Combine(_services.Paths.Root, "agents")));
            manager.WorkerFinished += (info, result) => _dispatcher.InvokeAsync(() =>
            {
                if (!_discarded && Session.Id == ownerSession && !lifetime.IsCancellationRequested && ReferenceEquals(_workers, manager))
                    OnWorkerFinished(info, result);
            });
            return _workers = manager;
        }
    }

    private JarvisCode.Core.Agent.TaskBoard? _tasks;
    private JarvisCode.Core.Agent.TeamStore? _teams;

    /// <summary>
    /// The session's structured task board (TaskCreate/get/list/update). It is
    /// created on first use and persisted under the profile so a restart keeps
    /// the list the teammates were working from.
    /// </summary>
    public JarvisCode.Core.Agent.TaskBoard Tasks =>
        _tasks ??= new JarvisCode.Core.Agent.TaskBoard(SessionStatePath(_services.Paths.TasksDirectory));

    /// <summary>
    /// Where this session keeps one of its state files, or null before the
    /// session exists — construction order must never decide whether the app
    /// starts, so a session-less view model simply keeps that state in memory.
    /// </summary>
    private string? SessionStatePath(string directory) =>
        Session is null ? null : System.IO.Path.Combine(directory, Session.Id + ".json");

    private IReadOnlyList<JarvisCode.Core.Agent.PeerCandidate> _peerMentionCandidates = [];

    /// <summary>
    /// Peers the composer may @-mention, refreshed by the surface while the user
    /// types so the send path never waits on a session listing.
    /// </summary>
    public void SetPeerMentionCandidates(IReadOnlyList<JarvisCode.Core.Agent.PeerCandidate> candidates) =>
        _peerMentionCandidates = candidates;

    /// <summary>The session's implicit team, as the reference keeps exactly one.</summary>
    public JarvisCode.Core.Agent.TeamStore Teams =>
        _teams ??= new JarvisCode.Core.Agent.TeamStore(_services.Paths.TeamsDirectory);

    /// <summary>Plan approvals teammates raised with this session as their lead.</summary>
    public JarvisCode.Core.Agent.PlanApprovalRegistry PlanApprovals { get; } = new();

    /// <summary>
    /// Delivers a running teammate's message to this session, the way the
    /// reference writes it into the lead's inbox.
    /// </summary>
    private Task DeliverTeammateMessage(string from, string message, string? summary)
    {
        var headline = string.IsNullOrWhiteSpace(summary) ? message : summary!;
        var deliver = () => DeliverTaskNotification(
            from, "message", $"Message from {from}: {headline}", message);
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(deliver);
        else
            deliver();
        return Task.CompletedTask;
    }

    /// <summary>Real context size (input+output) the provider reported last; 0 before the first call.</summary>
    public long LastContextTokens => _lastContextTokens;

    public string ContextUsageText
    {
        get => _contextUsageText;
        private set => SetProperty(ref _contextUsageText, value);
    }

    public PermissionPromptViewModel? PendingPermission
    {
        get => _pendingPermission;
        private set => SetProperty(ref _pendingPermission, value);
    }

    public QuestionPromptViewModel? PendingQuestion
    {
        get => _pendingQuestion;
        private set => SetProperty(ref _pendingQuestion, value);
    }

    public PlanApprovalViewModel? PendingPlanApproval
    {
        get => _pendingPlanApproval;
        private set => SetProperty(ref _pendingPlanApproval, value);
    }

    /// <summary>Raised when the model's flow changed Gate.Mode (an approved plan).</summary>
    public event EventHandler? PermissionModeChanged;

    /// <summary>A turn ended, whatever the reason — the shell may ping an away user.</summary>
    public event EventHandler? TurnFinished;

    /// <summary>A permission, question or plan card is waiting on the user.</summary>
    public event EventHandler? AttentionRequested;

    public ModelInfo? CurrentModel => _factory.ResolveModel(Session);

    /// <summary>Dev/verification hook: shows the approval card without a live tool call.</summary>
    public void ShowSamplePermission(PermissionPrompt prompt) =>
        PendingPermission = new PermissionPromptViewModel(prompt);

    /// <summary>Dev/verification hook: shows the AskUserQuestion card without a live call.</summary>
    public void ShowSampleQuestion(IReadOnlyList<JarvisCode.Core.Tools.BuiltIn.UserQuestion> questions) =>
        PendingQuestion = new QuestionPromptViewModel(questions);

    /// <summary>Dev/verification hook: shows the plan approval card without a live call.</summary>
    public void ShowSamplePlanApproval(string planMarkdown) =>
        PendingPlanApproval = new PlanApprovalViewModel(planMarkdown);

    /// <summary>
    /// Dev/verification hook: delivers a goal check-in through the real builder
    /// and the real delivery path, without waiting out an interval.
    /// </summary>
    public void ShowSampleGoalCheckin()
    {
        // A check-in only ever arrives into a conversation, so the pose has one.
        Transcript.Add(new UserMessageItem { Text = "port the pricing rules, then make the suite green" });
        Transcript.Add(new AssistantTextItem
        {
            Markdown = "Started an agent on the port and a shell on the suite; I will report when they land.",
            IsStreaming = false,
        });
        OnPropertyChanged(nameof(IsEmpty));
        SetSessionGoal("every test in the suite passes");
        var tasks = new[]
        {
            new DeferringTask("agent-2", "subagent", "port the pricing rules and run the suite", DateTimeOffset.Now),
            new DeferringTask("task-4", "shell", "dotnet test tests/JarvisCode.Core.Tests", DateTimeOffset.Now),
        };
        var message = GoalCheckins.Message(SessionGoal!, TimeSpan.FromMinutes(30), tasks);
        DeliverGoalCheckin(message.Summary, message.Body);
    }

    /// <summary>
    /// Dev/verification hook: fills the transcript with the renderer's hard cases —
    /// fenced code in several languages and an expanded tool row — without a turn.
    /// </summary>
    /// <summary>
    /// Dev/verification hook: the Chat surface's thinking cells in every state the
    /// reference has — settled and collapsed, settled and open past the clamp, one
    /// short enough to need no clamp, and one still streaming.
    /// </summary>
    public void ShowSampleThinking(string longThinking, string shortThinking)
    {
        Transcript.Add(new UserMessageItem { Text = "Giải thích cách bạn chọn ngưỡng clamp." });

        Transcript.Add(new ThinkingItem
        {
            Text = shortThinking,
            IsStreaming = false,
            IsChatCell = !IsCodeSurface,
        });

        var opened = new ThinkingItem
        {
            Text = longThinking,
            IsStreaming = false,
            IsChatCell = !IsCodeSurface,
            IsExpanded = true,
        };
        opened.RestoreThinkingSpans([TimeSpan.FromSeconds(12)]);
        Transcript.Add(opened);

        var unclamped = new ThinkingItem
        {
            Text = longThinking,
            IsStreaming = false,
            IsChatCell = !IsCodeSurface,
            IsExpanded = true,
            ShowsFullText = true,
        };
        unclamped.RestoreThinkingSpans([TimeSpan.FromHours(1), TimeSpan.FromMinutes(4)]);
        Transcript.Add(unclamped);

        var collapsed = new ThinkingItem
        {
            Text = longThinking,
            IsStreaming = false,
            IsChatCell = !IsCodeSurface,
        };
        collapsed.RestoreThinkingSpans([TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(7)]);
        Transcript.Add(collapsed);

        Transcript.Add(new ThinkingItem { Text = shortThinking, IsChatCell = !IsCodeSurface });
        Transcript.Add(new AssistantTextItem { Markdown = "Ngưỡng là **200px**.", IsStreaming = false });
        TurnStatus.ShowSample(TurnPhase.Thinking, DateTimeOffset.Now - TimeSpan.FromSeconds(18), 320);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// Poses one assistant answer that exercises every markdown construct, so the
    /// two renderer profiles can be compared against the reference side by side.
    /// </summary>
    public void ShowSampleMarkdown(string markdown)
    {
        Transcript.Add(new UserMessageItem { Text = "Show me every markdown construct." });
        Transcript.Add(new AssistantTextItem { Markdown = markdown, IsStreaming = false });
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void ShowSampleTranscript(
        string markdown, IReadOnlyList<ToolCallItem> calls, IReadOnlyList<ToolCallItem>? bareRuns = null)
    {
        Transcript.Add(new AssistantTextItem { Markdown = markdown, IsStreaming = false });

        // Through the same run splitting a live turn goes through, so the pose
        // shows the runs a turn would really produce rather than one group the
        // sample built by hand.
        ToolGroupItem? group = null;
        foreach (var call in calls)
        {
            EnsureGroup(call.ToolName);
            _currentGroup!.IsExpanded = true;
            _currentGroup.AddCall(call);
            _currentGroup.RefreshTitle();
            group = _currentGroup;
        }

        _currentGroup = null;

        // Each of these is a run of its own, which is what the reference draws as
        // a bare row: no summary sentence over it and no card around it.
        foreach (var bare in bareRuns ?? [])
        {
            var run = new ToolGroupItem { IsExpanded = true };
            run.AddCall(bare);
            run.RefreshTitle();
            Transcript.Add(run);
        }
        if (IsCodeSurface && group is not null)
        {
            var footer = new AssistantFooterItem
            {
                ModelLabel = CurrentModel?.DisplayName,
                Text = markdown,
                CompletedAt = DateTimeOffset.Now - TimeSpan.FromMinutes(3),
            };
            footer.Track(group);
            Transcript.Add(footer);
        }
        // Pose the status line's "55s · 441 tokens · Waiting for Jarvis…" state
        // (the reference's card, with this app's name in it), and a context size
        // so the context popup has something to break down.
        TurnStatus.ShowSample(TurnPhase.WaitingForModel, DateTimeOffset.Now - TimeSpan.FromSeconds(55), 441);
        _lastContextTokens = 9_500;
        if (CurrentModel is { } sampleModel)
        {
            ContextUsageText = $"{FormatTokens(9_500)} / {FormatTokens(sampleModel.MaxContextTokens)}";
        }

        OnPropertyChanged(nameof(IsEmpty));

        // A loop tick: the harness submitting a turn, which the reference draws
        // as an event rather than as something the user typed.
        Transcript.Add(new NoticeItem
        {
            Text = LoopTickNotice("continue verifying ten billion years site"),
        });
    }

    // ---- session lifecycle ----

    public void NewSession(string? workingDirectory = null)
    {
        CancelTurn();
        EndSessionRuntime(renew: true);
        ResetFileWatcher();
        Session = Session.CreateNew(workingDirectory ?? DefaultWorkingDirectory());
        EnsureMailbox();
        Session.ModelId = _services.Settings.Current.DefaultModelId;
        Gate.WorkingDirectory = Session.WorkingDirectory;
        Gate.AdditionalDirectories = Session.AdditionalDirectories;
        PermissionPickedInSession = false;
        ApplyWorkspacePermissionMode();
        Gate.ResetSessionGrants();
        ResetTranscript();
        RefreshMcp();
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(CurrentModel));
    }

    public void LoadSession(Session session)
    {
        CancelTurn();
        if (Session.Id != session.Id) EndSessionRuntime(renew: true);
        ResetFileWatcher();
        Session = session;
        EnsureMailbox();
        // The board, the team and the agent register are per session: a view
        // model that is handed a different one starts them again.
        _tasks = null;
        _teams = null;
        // EndSessionRuntime clears workers when the identity changes; reloading the same session keeps its live workers.
        Gate.WorkingDirectory = session.WorkingDirectory;
        Gate.AdditionalDirectories = session.AdditionalDirectories;
        PermissionPickedInSession = false;
        ApplyWorkspacePermissionMode();
        Gate.ResetSessionGrants();
        ResetTranscript();
        RebuildTranscriptFromHistory(session);
        RefreshMcp();
        _ = _dispatcher.InvokeAsync(RestorePrAutoFix, DispatcherPriority.ContextIdle);
        // Background agents do not survive the process. The reference reports
        // the ones with no completion record rather than pretending they ran.
        if (Workers.PreviousRunNotice() is { } unfinished)
        {
            Transcript.Add(new NoticeItem { Text = unfinished });
        }

        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(CurrentModel));
    }

    public void SetWorkingDirectory(string directory)
    {
        Session.WorkingDirectory = directory;
        Gate.WorkingDirectory = directory;
        ApplyWorkspacePermissionMode();
        RefreshMcp();
        // Project settings now come from somewhere else, and so do the
        // instruction files: the reference sends the new directory's along with
        // the move, so the next message carries them.
        MarkInstructionsForReload();
        _nextInstructionLoadReason = "session_start";
        lock (_nestedInstructionTriggers)
        {
            _nestedInstructionTriggers.Clear();
        }

        FireHook(JarvisCode.Core.Hooks.HookEvent.CwdChanged,
            new System.Text.Json.Nodes.JsonObject { ["cwd"] = directory });
        OnPropertyChanged(nameof(Session));
    }

    /// <summary>
    /// Switches the session's model, giving the pre_model_switch hooks their
    /// veto first and reporting the switch to post_model_switch afterwards.
    /// False means a hook refused: the model is unchanged and the reason is in
    /// the transcript, so the caller must not persist the choice either.
    /// </summary>
    public async Task<bool> SetModelAsync(
        ModelInfo model,
        JarvisCode.Core.Hooks.ModelSwitchSource source,
        string? requestedModel = null,
        Func<string, CancellationToken, Task<bool>>? confirm = null)
    {
        if (JarvisCode.Core.Hooks.ModelSwitch.From(
                Session.ModelId, model.ModelId, requestedModel, source,
                _lastContextTokens, _lastUsageAt, DateTimeOffset.Now, targetModel: model) is not { } change)
        {
            Session.ModelId = model.ModelId;
            OnPropertyChanged(nameof(CurrentModel));
            return true;
        }

        var hooks = _lastHooks ?? JarvisCode.Core.Hooks.HookRunner.Load(
            Session.WorkingDirectory, _services.Paths.UserHooksFile);

        if (change.RunsPreHooks && hooks.Has(JarvisCode.Core.Hooks.HookEvent.PreModelSwitch))
        {
            var decision = await hooks.RunPreModelSwitchAsync(change, CancellationToken.None, confirm);
            if (!decision.Allowed)
            {
                Transcript.Add(new NoticeItem
                {
                    Text = $"Model switch to {model.DisplayName} was refused by a pre_model_switch hook: " +
                           decision.BlockReason,
                    IsError = true,
                });
                return false;
            }
        }

        Session.ModelId = model.ModelId;
        ApplySavedEffortFor(model.ModelId);
        OnPropertyChanged(nameof(CurrentModel));
        FireHook(JarvisCode.Core.Hooks.HookEvent.PostModelSwitch, change.ToPayload());
        return true;
    }

    public async Task RenameAsync(string newTitle)
    {
        newTitle = newTitle.Trim();
        if (newTitle.Length == 0)
        {
            return;
        }

        Session.Title = newTitle;
        try
        {
            await SaveSessionAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Transcript.Add(new NoticeItem { Text = $"Could not save the session: {ex.Message}", IsError = true });
        }
    }

    public void AddAdditionalDirectory(string directory)
    {
        if (JarvisCode.Core.Utilities.NetworkPaths.IsNetworkPath(directory))
        {
            // The reference refuses a network path before touching it.
            Transcript.Add(new NoticeItem
            {
                Text = JarvisCode.Core.Utilities.NetworkPaths.AddDirectoryRefusal(directory),
                IsError = true,
            });
            return;
        }

        if (!Session.AdditionalDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            Session.AdditionalDirectories.Add(directory);
            Gate.AdditionalDirectories = Session.AdditionalDirectories;
            FireHook(JarvisCode.Core.Hooks.HookEvent.DirectoryAdded,
                new System.Text.Json.Nodes.JsonObject { ["directory"] = directory });
        }
    }

    public void RemoveAdditionalDirectory(string directory)
    {
        Session.AdditionalDirectories.RemoveAll(d => string.Equals(d, directory, StringComparison.OrdinalIgnoreCase));
        Gate.AdditionalDirectories = Session.AdditionalDirectories;
    }

    /// <summary>
    /// Ends the thinking block that is streaming, if any, and books its duration on
    /// the message's cell. The reference sums block durations, so the gap between
    /// two blocks — a tool call, or text in between — is deliberately not counted.
    /// </summary>
    private void CloseThinkingRun()
    {
        if (_thinkingRunStart is { } start && _thinkingRunEnd is { } end && _currentThinking is not null)
        {
            _currentThinking.AddThinkingSpan(end - start);
        }

        _thinkingRunStart = null;
        _thinkingRunEnd = null;
    }

    /// <summary>
    /// Copies the measured block durations onto the assistant message just stored, so
    /// a reloaded session still shows "Thought for …" rather than the reference's
    /// untimed "Thought process" fallback.
    /// </summary>
    private void PersistThinkingDurations(ThinkingItem item)
    {
        if (item.Spans.Count == 0)
        {
            return;
        }

        for (var index = Session.Messages.Count - 1; index >= 0; index--)
        {
            var message = Session.Messages[index];
            if (message.Role != JarvisCode.Core.Models.Role.Assistant)
            {
                continue;
            }

            if (ThinkingDurations.Apply(message.Content, item.Spans, item.Text) is { } updated)
            {
                Session.Messages[index] = message with { Content = updated };
            }

            return;
        }
    }

    private void ResetTranscript()
    {
        Transcript.Clear();
        if (_summaryVisible) Transcript.Add(_summaryItem);
        Todos.Clear();
        _callsById.Clear();
        _currentText = null;
        _currentThinking = null;
        _thinkingRunStart = null;
        _thinkingRunEnd = null;
        _currentGroup = null;
        ContextUsageText = "";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RebuildTranscriptFromHistory(Session session)
    {
        // The reference opens a conversation on its most recent page and pages
        // older history in as the reader climbs. The page start is remembered, so a
        // rebuild after a rewind or a compaction shows what was already shown.
        // A rewind can cut the session shorter than the page start, which would
        // leave the transcript reading from past its end — so a page start that no
        // longer names a message re-anchors on the tail, as a first build does.
        var first = _transcriptPaged && _transcriptFrom < session.Messages.Count
            ? Math.Max(0, _transcriptFrom)
            : Math.Max(0, session.Messages.Count - TranscriptPageSize);
        _transcriptPaged = true;
        _transcriptFrom = first;

        // A prompt's number is the reader's own count from the top of the session,
        // so a page that starts mid-history still numbers its prompts correctly.
        var turn = 0;
        for (var before = 0; before < first; before++)
        {
            if (session.Messages[before].Role == JarvisCode.Core.Models.Role.User &&
                session.Messages[before].Content.OfType<JarvisCode.Core.Models.ToolResultBlock>().Any() is false &&
                !string.IsNullOrWhiteSpace(SystemReminders.DisplayText(session.Messages[before])))
            {
                turn++;
            }
        }

        var runItems = 0;
        string? runAnswer = null;
        for (var index = first; index < session.Messages.Count; index++)
        {
            var message = session.Messages[index];
            _rebuildMessage = index;
            _rebuildOrdinal = 0;
            if (message.Role == JarvisCode.Core.Models.Role.User)
            {
                // System-reminder attachments stay in the stored message for the
                // model but never show in the bubble; a skill expansion shows
                // as its typed "/name args".
                var text = SystemReminders.DisplayText(message);
                var toolResults = message.Content.OfType<JarvisCode.Core.Models.ToolResultBlock>().ToList();
                if (toolResults.Count > 0)
                {
                    foreach (var result in toolResults)
                    {
                        if (_callsById.TryGetValue(result.ToolCallId, out var call))
                        {
                            call.Complete(Cap(result.Content), result.IsError, result.Images);
                        }
                    }

                    _currentGroup?.RefreshTitle();
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    _currentGroup = null;
                    Transcript.Add(Rebuilt(new UserMessageItem
                    {
                        Text = text,
                        CreatedAt = session.UpdatedAt,
                        MessageIndex = index,
                        TurnNumber = ++turn,
                    }));
                }
            }
            else
            {
                runItems += 1;

                // A message's thinking blocks share one cell, exactly as they do
                // while streaming, and their stored durations sum into its header.
                var thinkingBlocks = message.Content
                    .OfType<JarvisCode.Core.Models.ThinkingBlock>()
                    .Where(block => !string.IsNullOrWhiteSpace(block.Thinking))
                    .ToList();
                if (thinkingBlocks.Count > 0)
                {
                    var restored = new ThinkingItem
                    {
                        Text = string.Concat(thinkingBlocks.Select(block => block.Thinking)),
                        IsStreaming = false,
                        IsChatCell = !IsCodeSurface,
                    };
                    restored.RestoreThinkingSpans(thinkingBlocks
                        .Select(block => TimeSpan.FromSeconds(block.DurationSeconds ?? 0)));
                    _lastThinkingText = restored.Text;
                    Transcript.Add(Rebuilt(restored));
                }

                foreach (var block in message.Content)
                {
                    switch (block)
                    {
                        case JarvisCode.Core.Models.ThinkingBlock:
                            break;
                        case JarvisCode.Core.Models.TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                            _currentGroup = null;
                            Transcript.Add(Rebuilt(new AssistantTextItem
                            {
                                Markdown = JarvisCode.Core.Models.CitationMarkdown.Format(text),
                                IsStreaming = false,
                                MessageIndex = index,
                            }));
                            break;
                        case JarvisCode.Core.Models.ToolCallBlock call
                            when Services.VisualizeWidgetCalls.IsShowWidget(call.Name):
                            _currentGroup = null;
                            Transcript.Add(Rebuilt(NewWidget(call.Id, call.Name, call.ArgumentsJson)));
                            break;
                        case JarvisCode.Core.Models.ToolCallBlock call:
                            EnsureGroup(call.Name);
                            var item = new ToolCallItem
                            {
                                CallId = call.Id,
                                ToolName = call.Name,
                                Description = DescribeHistoricCall(call),
                                // Without the arguments a reopened session's expanded row
                                // would lose the command a live one showed.
                                ArgumentsJson = call.ArgumentsJson,
                                IsRunning = false,
                                // Inner events and worker reports ride the turn, not
                                // the session file, so a restored background agent's
                                // report is known to be nothing rather than pending.
                                SubagentReport = "",
                            };
                            _currentGroup!.AddCall(item);
                            _callsById[call.Id] = item;
                            _currentGroup.RefreshTitle();
                            break;
                    }
                }

                if (message.GetText() is { Length: > 0 } answer)
                {
                    runAnswer = answer;
                }

                // The footer closes the run, so a turn spanning several assistant
                // messages leaves one bar, exactly as a live turn does.
                if (index + 1 >= session.Messages.Count ||
                    session.Messages[index + 1].Role == JarvisCode.Core.Models.Role.User)
                {
                    AddRebuiltFooter(session, index, runItems, runAnswer);
                    runItems = 0;
                    runAnswer = null;
                }
            }
        }

        _currentGroup = null;
        _rebuildMessage = -1;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(TranscriptHasOlder));
    }

    /// <summary>
    /// A reopened session shows the same message footer a live turn leaves
    /// behind; nothing in it is running, so it offers no move.
    /// </summary>
    private void AddRebuiltFooter(Session session, int index, int runItems, string? answer)
    {
        if (!IsCodeSurface || runItems == 0)
        {
            return;
        }

        Transcript.Add(Rebuilt(new AssistantFooterItem
        {
            ModelLabel = CurrentModel?.DisplayName,
            MessageIndex = index + 1,
            Text = answer,
            CompletedAt = session.UpdatedAt,
        }));
    }

    private static string DescribeHistoricCall(JarvisCode.Core.Models.ToolCallBlock call)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(call.ArgumentsJson) is System.Text.Json.Nodes.JsonObject args)
            {
                var subject = args["command"]?.GetValue<string>()
                    ?? args["file_path"]?.GetValue<string>()
                    ?? args["pattern"]?.GetValue<string>()
                    ?? args["url"]?.GetValue<string>()
                    ?? args["prompt"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(subject))
                {
                    if (subject.Length > 64)
                    {
                        subject = subject[..64] + "…";
                    }

                    return $"{call.Name}({subject})";
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return call.Name;
    }

    // ---- sending ----

    /// <param name="meta">
    /// Whether the harness is submitting this turn rather than the person — a
    /// loop tick, or a wakeup the model scheduled. The reference marks such a
    /// turn <c>isMeta</c>: the text still reaches the model as a user message,
    /// but its transcript renders an event instead of a user bubble and nothing
    /// counts it as the user having spoken.
    /// </param>
    public async Task SendAsync(string text, bool meta = false)
    {
        text = text.Trim();
        if (text.Length == 0 && ComposerAttachments.Count == 0)
        {
            return;
        }

        // The reference queues prompts typed while a turn runs and sends them
        // one per turn after the current response; each keeps its own attachments.
        if (IsRunning)
        {
            QueuedMessages.Add(new QueuedMessageItem(text, [.. ComposerAttachments]));
            ComposerAttachments.Clear();
            return;
        }

        var chips = ComposerAttachments.ToList();
        ComposerAttachments.Clear();
        await SendWithAttachmentsAsync(text, chips, meta: meta);
    }

    /// <summary>
    /// A typed /skill invocation: the composer shows the typed text, the wire
    /// carries the reference envelope plus the rendered skill body per stacked
    /// command. Queued like any prompt while a turn runs.
    /// </summary>
    public async Task SendSkillExpansionAsync(
        string typedText, IReadOnlyList<SkillExpansionPart> parts, string? hookContext = null)
    {
        _pendingExpansionHookContext = hookContext;
        if (IsRunning)
        {
            QueuedMessages.Add(new QueuedMessageItem(typedText, [.. ComposerAttachments]) { Expansion = parts });
            ComposerAttachments.Clear();
            return;
        }

        var chips = ComposerAttachments.ToList();
        ComposerAttachments.Clear();
        await SendWithAttachmentsAsync(typedText, chips, parts);
    }

    /// <summary>Additional context the user_prompt_expansion hooks emitted for the pending expansion.</summary>
    private string? _pendingExpansionHookContext;

    /// <summary>
    /// The reference submit assembly: a leading line of @"path" mentions for
    /// file/folder chips, then attach-as-context snippets, then the typed text.
    /// The remaining chips fold in as content blocks below.
    /// </summary>
    internal async Task SendWithAttachmentsAsync(
        string text, IReadOnlyList<ComposerAttachment> chips,
        IReadOnlyList<SkillExpansionPart>? expansion = null,
        bool meta = false)
    {
        text = ComposerSerialization.ComposeOutgoingText(text, chips, Session.Id);
        if (text.Length == 0 && expansion is null && !chips.Any(static c =>
                c.Kind is ComposerAttachmentKind.Image or ComposerAttachmentKind.PastedText))
        {
            return;
        }

        var isFirst = Session.Messages.Count == 0;
        JarvisCode.Core.Models.ChatMessage userMessage;
        // The skill listing no longer rides the user turn: it goes out in the
        // harness system message added after it, as the reference sends it.
        string? skillListingBody = null;
        // What the session's mode and style tell the model. These close the
        // harness system turn instead of riding the user's message.
        var harnessNotices = new List<string>();
        IReadOnlyList<string> attachments = [];
        if (IsCodeSurface)
        {
            // A new prompt re-arms the token budget (the reference's
            // reanchorTaskBudget): the block that follows this message reads the
            // full figure again.
            _totalTokens.ReanchorTaskBudget(_lastContextTokens);

            if (expansion is not null)
            {
                // Envelope then body per command, like the reference's
                // two-message pair; @-mentions inside the rendered body still
                // resolve.
                var blocks = new List<JarvisCode.Core.Models.ContentBlock>();
                var attached = new List<string>();
                foreach (var part in expansion)
                {
                    blocks.Add(new JarvisCode.Core.Models.TextBlock(part.Envelope));
                    var expanded = FileMentions.BuildUserMessage(part.Body, Session.WorkingDirectory);
                    blocks.AddRange(expanded.Message.Content);
                    attached.AddRange(expanded.AttachedPaths);
                }
                userMessage = SystemReminders.UserMessage(
                    new JarvisCode.Core.Models.ChatMessage(JarvisCode.Core.Models.Role.User, blocks));
                if (_pendingExpansionHookContext is { Length: > 0 } hookContext)
                {
                    // stdout from allowed user_prompt_expansion hooks joins the
                    // message as additional context (reference
                    // hook_additional_context).
                    userMessage = SystemReminders.Attach(
                        userMessage,
                        "<system-reminder>\nuser_prompt_expansion hook additional context:\n" +
                        hookContext + "\n</system-reminder>");
                    _pendingExpansionHookContext = null;
                }
                attachments = attached;
            }
            else
            {
                var mention = FileMentions.BuildUserMessage(text, Session.WorkingDirectory);
                userMessage = SystemReminders.UserMessage(mention.Message);
                attachments = mention.AttachedPaths;
            }

            // The reference's first message carries a context reminder holding
            // the project instructions, the memory index and the date; gitStatus
            // rides the system prompt instead. Both placements are measured.
            // The first message carries the instruction files; so does the first
            // message after a compaction took them out of the live context.
            if (isFirst || _instructionsReloadPending)
            {
                _instructionsReloadPending = false;
                var cwd = Session.WorkingDirectory;
                var memoryRoot = _services.Paths.MemoryRoot;
                var paused = MemoryPaused;
                await EnsureExternalImportsAnsweredAsync();
                var scope = InstructionScope();
                var loaded = await Task.Run(
                    () => JarvisCode.Core.Agent.ProjectInstructions.LoadAll(scope));
                _eagerInstructions = loaded;
                foreach (var file in loaded.Files)
                {
                    _loadedInstructionPaths.Add(file.FilePath);
                }

                var context = await Task.Run(() =>
                {
                    var memoryDirectory = paused
                        ? null
                        : JarvisCode.Core.Memory.ProjectMemory.DirectoryFor(memoryRoot, cwd);
                    return SystemReminders.ContextReminder(
                        loaded,
                        memoryDirectory is null ? null : System.IO.Path.Combine(memoryDirectory, "MEMORY.md"),
                        memoryDirectory is null
                            ? null
                            : JarvisCode.Core.Memory.ProjectMemory.ReadIndexForPrompt(memoryDirectory),
                        DateOnly.FromDateTime(DateTime.Now),
                        SystemReminders.UserEmail(cwd));
                });
                if (context is not null)
                {
                    userMessage = SystemReminders.Attach(userMessage, context);
                }

                ReportOversizedInstructions(loaded);
            }

            // An instruction file below the working directory, or a rule whose
            // paths: frontmatter matches, joins the turn once a tool has touched
            // a file it governs (the reference's nested-memory attachment).
            userMessage = await AttachNestedInstructionsAsync(userMessage);

            // The skill listing rides a system-reminder: everything on the
            // first message, then only newly discovered skills (reference
            // skill_listing attachment). After a compaction cleared invoked
            // skills' content, the invoked-skills reminder precedes it.
            SkillState.CurrentTurnTypedText = text;
            if (SkillState.PendingInvokedSkillsReminder && SkillState.Tracker.HasAny)
            {
                SkillState.PendingInvokedSkillsReminder = false;
                userMessage = SystemReminders.Attach(userMessage, SystemReminders.InvokedSkills(
                    JarvisCode.Core.Customization.SkillInvocation.BuildInvokedSkillsReminder(
                        SkillState.Tracker.Invoked())));
            }

            skillListingBody = await Task.Run(BuildSkillListingBody);

            // Plan mode, the permission mode and the output style all speak
            // through the harness system turn rather than the user's message —
            // measured across four permission modes and two turns of one
            // session. The workflow is stated once and the short form keeps it
            // in view afterwards.
            if (Gate.Mode == JarvisCode.Core.Permissions.PermissionMode.Plan)
            {
                var planFile = PlanModeTools.PlanFilePath(Session.WorkingDirectory, Session.Id);
                // Plain text: a lean model's system turn carries the workflow
                // unwrapped, and the composer adds the reminder tags only for a
                // model that receives it on its user message.
                harnessNotices.Add(_planWorkflowSent
                    ? PlanModePrompts.Sparse(planFile)
                    : PlanModePrompts.Full(
                        planFile,
                        planExists: System.IO.File.Exists(planFile),
                        withAgents: CurrentProfile().PlanWorkflowUsesAgents));
                _planWorkflowSent = true;
            }
            else
            {
                _planWorkflowSent = false;
            }

            // A permission mode that stops asking says so once per session, not
            // once per turn: a two-turn capture shows the notice on the first
            // request and absent from the second.
            if (!_modeNoticeSent
                && SessionModeNotices.ForMode(Gate.Mode, IsCodeSurface, CurrentProfile().BypassNoticeForced)
                    is { } modeNotice)
            {
                harnessNotices.Add(modeNotice);
                _modeNoticeSent = true;
            }

            if (SessionGoal is { Length: > 0 } goal)
            {
                userMessage = SystemReminders.Attach(userMessage, SystemReminders.Goal(goal));
            }

            if (BriefMode)
            {
                userMessage = SystemReminders.Attach(userMessage, SystemReminders.Brief);
            }

            // Two of the reference's output styles carry a turn reminder beside
            // their prompt. It repeats every turn — unlike the mode notices —
            // and rides its own system message after the user's, which is why
            // it joins the harness notices rather than the reminders.
            if (OutputStyles.TurnReminder(_services.UiSettings.Current.SessionOutputStyles.GetValueOrDefault(
                    Session.Id, _services.UiSettings.Current.OutputStyle), HasRunningBackgroundWork(),
                    Session.WorkingDirectory, _services.Paths.Root)
                is { } styleReminder)
            {
                harnessNotices.Add(styleReminder);
            }

            // Ultracode session state (reference ultra_effort_enter/exit): the
            // full reminder announces it, the short form keeps it in view, and
            // the exit reminder restores the normal delegation rule.
            if (UltracodeMode)
            {
                userMessage = SystemReminders.Attach(
                    userMessage,
                    _ultracodeAnnounced ? SystemReminders.UltracodeStillOn : SystemReminders.UltracodeEnter);
                _ultracodeAnnounced = true;
            }
            else if (_ultracodeAnnounced)
            {
                userMessage = SystemReminders.Attach(userMessage, SystemReminders.UltracodeExit);
                _ultracodeAnnounced = false;
            }

            // The reference keywords ride the turn as reminders; neither touches
            // the request payload.
            if (EffortLevels.HasUltrathinkKeyword(text))
            {
                userMessage = SystemReminders.Attach(userMessage, SystemReminders.Ultrathink);
            }

            if (!UltracodeMode && EffortLevels.HasUltracodeKeyword(text))
            {
                userMessage = SystemReminders.Attach(userMessage, SystemReminders.UltracodeKeyword);
            }

            // Topic-based memory recall: a memory file sharing enough words with
            // the prompt rides it as a reminder (paused by /pause-memory).
            if (!MemoryPaused)
            {
                var memoryDirectory = JarvisCode.Core.Memory.ProjectMemory.DirectoryFor(
                    _services.Paths.MemoryRoot, Session.WorkingDirectory);
                var recalled = await Task.Run(() => MemoryRecall.BuildReminder(memoryDirectory, text));
                if (recalled is not null)
                {
                    userMessage = SystemReminders.Attach(userMessage, recalled);
                }
            }
        }
        else
        {
            userMessage = SystemReminders.UserMessage(
                JarvisCode.Core.Models.ChatMessage.FromUserText(text));
        }

        (userMessage, attachments) = ApplyComposerAttachments(userMessage, attachments, chips);

        // An @-mention of a teammate or another session rides the message as the
        // reference's peer_mention attachment: it tells the model who was meant,
        // never messages them by itself.
        if (IsCodeSurface && _peerMentionCandidates.Count > 0)
        {
            var peerNotes = Services.PeerMentionSource.Attachments(text, _peerMentionCandidates, attachments);
            if (peerNotes.Count > 0)
            {
                userMessage = SystemReminders.Attach(
                    userMessage, SystemReminders.WrapReminder(string.Join("\n\n", peerNotes)));
            }
        }

        // An attachments-only send has an empty typed-text block; drop it so the
        // message starts with the real content (the reference sends no empty block).
        if (userMessage.Content.Count > 1 &&
            userMessage.Content[0] is JarvisCode.Core.Models.TextBlock { Text.Length: 0 })
        {
            userMessage = SystemReminders.Restamp(
                userMessage with { Content = [.. userMessage.Content.Skip(1)] });
        }

        // The factory picks the same number when it opens this turn's checkpoint below.
        var turnNumber = IsCodeSurface ? _services.Checkpoints.NextTurnNumber(Session.Id) : 0;
        Session.Messages.Add(meta ? userMessage with { IsMeta = true } : userMessage);
        Transcript.Add(meta
            ? new NoticeItem { Text = LoopTickNotice(text) }
            : new UserMessageItem
            {
                Text = text,
                AttachedPaths = attachments,
                MessageIndex = Session.Messages.Count - 1,
                TurnNumber = turnNumber,
            });
        OnPropertyChanged(nameof(IsEmpty));

        // The sidebar lists what is on disk, so an unsaved conversation is invisible
        // there. Saving the first prompt right away — instead of waiting for the turn
        // to finish — is what puts a new session in the list, with its title and its
        // running spinner, while the answer is still streaming. Later prompts already
        // have their row and are covered by the save at turn end.
        if (isFirst)
        {
            Session.Title = MakeTitle(text.Length > 0 ? text : string.Join(", ", attachments));
            Session.UpdatedAt = DateTimeOffset.Now;
            try
            {
                await SaveSessionAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Transcript.Add(new NoticeItem { Text = $"Could not save the session: {ex.Message}", IsError = true });
            }

            SessionPersisted?.Invoke(this, EventArgs.Empty);
        }

        var setup = IsCodeSurface
            ? _factory.CreateForCode(
                Session, Gate, text, OnTodosChanged, OnSubagentActivity, ExtraTools, _lastContextTokens,
                ShowQuestionAsync, ShowPlanApprovalAsync, Workers, CoordinatorMode, MemoryPaused, FireHook, CrossSessionSend, OnSubagentEvent, ultracodeMode: UltracodeMode, skillState: SkillState, backgroundMoves: BackgroundMoves, tasks: Tasks, teams: Teams, teammateMessage: DeliverTeammateMessage, subscribeIdle: CrossSessionIdleSubscribe,
                planApprovals: PlanApprovals, sessionGoal: SessionGoal, functionHooks: FunctionHooks,
                totalTokens: _totalTokens, pendingDelivery: HasPendingQueuedMessage)
            : _factory.CreateForChat(Session, Gate, ExtraTools, SkillState);
        if (setup is null)
        {
            Transcript.Add(new NoticeItem
            {
                Text = "No model is configured. Add an API key and pick a default model in Settings.",
                IsError = true,
            });
            return;
        }

        _stopHookActive = false;
        if (setup.TurnHooks is { } turnHooks)
        {
            turnHooks.Diagnostics ??= text => Transcript.Add(new NoticeItem { Text = text, IsError = true });

            // session_start runs once per session per app run, its stdout riding
            // the first message as context, like the reference's SessionStart. Its
            // source says whether this process started the session or resumed one.
            if (!_sessionStartRan && turnHooks.Has(JarvisCode.Core.Hooks.HookEvent.SessionStart))
            {
                var source = Session.Messages.Count > 1 ? "resume" : "startup";
                try
                {
                    if (await turnHooks.RunSessionStartAsync(Session.Id, CancellationToken.None, source) is { } startContext)
                    {
                        Session.Messages[^1] = SystemReminders.AttachSessionStartContext(
                            Session.Messages[^1], startContext, source);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Transcript.Add(new NoticeItem { Text = $"session_start hook failed: {ex.Message}", IsError = true });
                }
            }

            _sessionStartRan = true;

            if (!_instructionsLoadedRan)
            {
                _instructionsLoadedRan = true;
                var eager = _eagerInstructions
                    ?? JarvisCode.Core.Agent.ProjectInstructions.LoadAll(InstructionScope());
                foreach (var file in eager.Files)
                {
                    FireInstructionsLoaded(turnHooks, file, _nextInstructionLoadReason);
                }

                _nextInstructionLoadReason = "session_start";
            }

            var submit = await turnHooks.RunUserPromptSubmitAsync(text, CancellationToken.None);
            if (!submit.Allowed)
            {
                Session.Messages.RemoveAt(Session.Messages.Count - 1);
                if (Transcript.Count > 0 && Transcript[^1] is UserMessageItem)
                {
                    Transcript.RemoveAt(Transcript.Count - 1);
                }

                Transcript.Add(new NoticeItem
                {
                    Text = $"Prompt blocked by a user_prompt_submit hook: {submit.BlockReason}",
                    IsError = true,
                });
                await SaveSessionAsync();
                return;
            }

            if (submit.AdditionalContext is { } extraContext)
            {
                Session.Messages[^1] = SystemReminders.AttachPromptHookContext(Session.Messages[^1], extraContext);
            }
        }

        // Last, once the hooks have finished with the user message: the roster,
        // the skill listing and the notices, as a system turn or as reminders on
        // the message, whichever this model takes.
        PlaceHarnessSections(setup, skillListingBody, harnessNotices);

        await RunTurnAsync(setup);
    }

    /// <summary>
    /// Folds the remaining chips into the outgoing message: pasted text as a
    /// labelled block, images as image blocks, and — on the toolless Chat
    /// surface — file chips as inlined content. File, folder, and context chips
    /// already ride the composed text; here they only contribute labels.
    /// </summary>
    private (JarvisCode.Core.Models.ChatMessage Message, IReadOnlyList<string> Labels) ApplyComposerAttachments(
        JarvisCode.Core.Models.ChatMessage message,
        IReadOnlyList<string> attachedPaths,
        IReadOnlyList<ComposerAttachment> chips)
    {
        if (chips.Count == 0)
        {
            return (message, attachedPaths);
        }

        // Attachments are the user's, so they join ahead of any harness blocks
        // already on the message rather than after them (SystemReminders.AddUserBlocks).
        var blocks = new List<JarvisCode.Core.Models.ContentBlock>();
        var labels = new List<string>(attachedPaths);
        foreach (var attachment in chips)
        {
            switch (attachment.Kind)
            {
                case ComposerAttachmentKind.Context:
                    labels.Add("Attached context");
                    break;
                case ComposerAttachmentKind.PastedText:
                    blocks.Add(new JarvisCode.Core.Models.TextBlock(
                        $"[Pasted text]\n\"\"\"\n{attachment.Text.TrimEnd()}\n\"\"\""));
                    labels.Add("Pasted text");
                    break;
                case ComposerAttachmentKind.File when attachment.FilePath is { } filePath:
                    // On Code the mention expansion already reports the attached
                    // path; a second label here would double up the summary line.
                    if (!IsCodeSurface)
                    {
                        labels.Add(attachment.FileName);
                        AppendFileContent(blocks, filePath);
                    }

                    break;
                case ComposerAttachmentKind.Folder:
                    labels.Add(attachment.FileName + "/");
                    break;
                case ComposerAttachmentKind.Image when attachment.FilePath is { } path:
                    try
                    {
                        var (mediaType, data) = Services.ImageAttachments.PrepareForSend(
                            File.ReadAllBytes(path), path);
                        blocks.Add(new JarvisCode.Core.Models.ImageBlock(
                            mediaType, Convert.ToBase64String(data)));
                        labels.Add(Path.GetFileName(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Transcript.Add(new NoticeItem
                        {
                            Text = $"Could not attach {Path.GetFileName(path)}: {ex.Message}",
                            IsError = true,
                        });
                    }
                    catch (Exception ex) when (ex is NotSupportedException or System.IO.FileFormatException)
                    {
                        Transcript.Add(new NoticeItem
                        {
                            Text = "Failed to process image. You can try again.",
                            IsError = true,
                        });
                    }

                    break;
            }
        }

        return (SystemReminders.AddUserBlocks(message, blocks), labels);
    }

    /// <summary>Chat has no read tools, so a file chip's content is folded in directly.</summary>
    private void AppendFileContent(List<JarvisCode.Core.Models.ContentBlock> blocks, string path)
    {
        try
        {
            // Same binary sniff Core's file mentions use: a NUL in the first 8 KB.
            using (var probe = File.OpenRead(path))
            {
                var buffer = new byte[8192];
                var read = probe.Read(buffer, 0, buffer.Length);
                if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                {
                    return;
                }
            }

            var content = File.ReadAllText(path);
            if (content.Length > 40_000)
            {
                content = content[..40_000] + "\n… [file truncated]";
            }

            blocks.Add(new JarvisCode.Core.Models.TextBlock(
                $"[Attached file: {path}]\n```\n{content.TrimEnd()}\n```"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Transcript.Add(new NoticeItem
            {
                Text = $"Could not attach {Path.GetFileName(path)}: {ex.Message}",
                IsError = true,
            });
        }
    }

    // ---- message actions (copy · rewind · branch · edit · retry) ----

    /// <summary>
    /// Chat's Retry: cuts the conversation back to the user prompt that produced
    /// this response and regenerates. No file state is involved on Chat.
    /// </summary>
    public async Task RetryAssistantAsync(AssistantTextItem item)
    {
        if (IsRunning || item.MessageIndex < 0 || item.MessageIndex >= Session.Messages.Count)
        {
            return;
        }

        // Keep everything up to and including the preceding user prompt.
        int cut = item.MessageIndex;
        while (cut > 0 && Session.Messages[cut - 1].Role != JarvisCode.Core.Models.Role.User)
        {
            cut--;
        }

        if (cut == 0)
        {
            return;
        }

        Session.Messages.RemoveRange(cut, Session.Messages.Count - cut);
        ResetTranscript();
        RebuildTranscriptFromHistory(Session);
        await SaveSessionAsync();

        var setup = IsCodeSurface
            ? _factory.CreateForCode(
                Session, Gate, Session.Messages[^1].GetText(), OnTodosChanged, OnSubagentActivity, ExtraTools,
                _lastContextTokens, ShowQuestionAsync, ShowPlanApprovalAsync, Workers, CoordinatorMode, MemoryPaused, FireHook, CrossSessionSend, OnSubagentEvent, ultracodeMode: UltracodeMode, skillState: SkillState, backgroundMoves: BackgroundMoves, tasks: Tasks, teams: Teams, teammateMessage: DeliverTeammateMessage, subscribeIdle: CrossSessionIdleSubscribe,
                planApprovals: PlanApprovals, sessionGoal: SessionGoal, functionHooks: FunctionHooks,
                totalTokens: _totalTokens, pendingDelivery: HasPendingQueuedMessage)
            : _factory.CreateForChat(Session, Gate, ExtraTools, SkillState);
        if (setup is not null)
        {
            await RunTurnAsync(setup);
        }
    }

    /// <summary>
    /// The error card's "Try again": runs the conversation again as it stands.
    /// A failed turn left the user's message in the history and added no answer
    /// to it, so the turn is re-run rather than the prompt re-sent — sending it
    /// again would put a second copy of it in the conversation.
    /// </summary>
    public async Task RetryLastTurnAsync()
    {
        if (IsRunning || Session.Messages.Count == 0 ||
            Session.Messages[^1].Role != JarvisCode.Core.Models.Role.User)
        {
            return;
        }

        var setup = IsCodeSurface
            ? _factory.CreateForCode(
                Session, Gate, Session.Messages[^1].GetText(), OnTodosChanged, OnSubagentActivity, ExtraTools,
                _lastContextTokens, ShowQuestionAsync, ShowPlanApprovalAsync, Workers, CoordinatorMode, MemoryPaused,
                FireHook, CrossSessionSend, OnSubagentEvent, ultracodeMode: UltracodeMode, skillState: SkillState,
                backgroundMoves: BackgroundMoves, tasks: Tasks, teams: Teams,
                teammateMessage: DeliverTeammateMessage, subscribeIdle: CrossSessionIdleSubscribe,
                planApprovals: PlanApprovals, sessionGoal: SessionGoal, functionHooks: FunctionHooks,
                totalTokens: _totalTokens, pendingDelivery: HasPendingQueuedMessage)
            : _factory.CreateForChat(Session, Gate, ExtraTools, SkillState);
        if (setup is not null)
        {
            await RunTurnAsync(setup);
        }
    }

    /// <summary>
    /// Chat's Edit message: removes the prompt (and everything after it) and hands
    /// the text back to the composer for editing and resending.
    /// </summary>
    public async Task<string> EditUserMessageAsync(UserMessageItem item)
    {
        CancelTurn();
        var text = item.Text;
        if (item.MessageIndex >= 0 && item.MessageIndex < Session.Messages.Count)
        {
            Session.Messages.RemoveRange(item.MessageIndex, Session.Messages.Count - item.MessageIndex);
            ResetTranscript();
            RebuildTranscriptFromHistory(Session);
            await SaveSessionAsync();
        }

        return text;
    }

    /// <summary>
    /// Rewind: restores every file the agent changed from this prompt onwards and cuts the
    /// conversation back to just before it. Returns the prompt so the composer can hold it again.
    /// </summary>
    public async Task<string> RewindToAsync(UserMessageItem item)
    {
        CancelTurn();

        string? restoreNote = null;
        if (IsCodeSurface && item.TurnNumber > 0)
        {
            try
            {
                var result = await _services.Checkpoints.RewindAsync(Session.Id, item.TurnNumber);
                if (result is not null)
                {
                    var count = result.RestoredFiles.Count;
                    restoreNote = count == 0
                        ? "No file changes needed restoring."
                        : $"Restored {count} file{(count == 1 ? "" : "s")}.";
                    if (result.Skipped.Count > 0)
                    {
                        restoreNote += $" {result.Skipped.Count} could not be restored.";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                restoreNote = $"Files could not be restored: {ex.Message}";
            }
        }

        if (item.MessageIndex >= 0 && item.MessageIndex < Session.Messages.Count)
        {
            Session.Messages.RemoveRange(item.MessageIndex, Session.Messages.Count - item.MessageIndex);
        }

        ResetTranscript();
        RebuildTranscriptFromHistory(Session);
        Transcript.Add(new NoticeItem
        {
            Text = restoreNote is null ? "Rewound to this message." : $"Rewound to this message. {restoreNote}",
        });
        OnPropertyChanged(nameof(IsEmpty));
        await SaveSessionAsync();
        return item.Text;
    }

    /// <summary>
    /// Branch: copies the conversation up to this prompt into a new session, leaving this
    /// one — and any turn it happens to be running — untouched. The surface opens the copy;
    /// the returned prompt goes back into the composer. Branch is null if it could not be
    /// saved, and the reason is already in this transcript.
    /// </summary>
    public async Task<(Session? Branch, string Prompt)> BranchFromAsync(UserMessageItem item)
    {
        var branch = await CopySessionUpToAsync(item.MessageIndex);
        return (branch, branch is null ? "" : item.Text);
    }

    /// <summary>
    /// "Fork from here" on a message footer: a copy of the conversation through
    /// this answer, left ready to continue.
    /// </summary>
    public Task<Session?> ForkFromAsync(AssistantFooterItem item) => CopySessionUpToAsync(item.MessageIndex);

    /// <summary>Saves a new session holding the first <paramref name="cut"/> messages.</summary>
    private async Task<Session?> CopySessionUpToAsync(int cut)
    {
        cut = Math.Clamp(cut, 0, Session.Messages.Count);
        var branch = Session.CreateNew(Session.WorkingDirectory);
        branch.Title = Session.Title.EndsWith(" (branch)", StringComparison.Ordinal)
            ? Session.Title
            : $"{Session.Title} (branch)";
        branch.ModelId = Session.ModelId;
        branch.AdditionalDirectories = [.. Session.AdditionalDirectories];
        branch.Messages = [.. Session.Messages.Take(cut)];

        try
        {
            await SaveSessionAsync(branch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Transcript.Add(new NoticeItem { Text = $"Could not branch the session: {ex.Message}", IsError = true });
            return null;
        }

        SessionPersisted?.Invoke(this, EventArgs.Empty);
        return branch;
    }

    private async Task SaveSessionAsync()
    {
        Session.UpdatedAt = DateTimeOffset.Now;
        try
        {
            // Named explicitly: a zero-argument call binds to this method rather
            // than to the incognito-guarded overload beside it, which is a call
            // to itself and a stack overflow on the first save of every session.
            await SaveSessionAsync(Session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Transcript.Add(new NoticeItem { Text = $"Could not save the session: {ex.Message}", IsError = true });
        }

        SessionPersisted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reruns the loop without a new user message (e.g. after rewind).</summary>
    public async Task RunTurnAsync(TurnSetup setup)
    {
        _incomingWork = false;
        EnsureMailbox().NotifyBusy();
        SessionCronDispatch.Register(Session.Id,
            () => !_discarded && !IsRunning && QueuedMessages.Count == 0 && _pendingNotifications.Count == 0,
            prompt => _ = SendAsync(LoopPrompts.ResolveFire(prompt, Session.WorkingDirectory, LoopDelivery), meta: true));
        // Hand edits made in the request inspector ride every model call of the turn.
        // Read here rather than in the factory so an edit applied mid-conversation
        // takes effect on the very next turn, whichever path built the setup.
        if (Services.RequestOverrides.For(Session.Id) is { } bodyOverride)
        {
            // Merged onto whatever the factory already pinned (the opt-in request
            // metadata), so a hand edit adds to it rather than dropping it.
            setup = setup with
            {
                Context = setup.Context with
                {
                    BodyOverride = JarvisCode.Core.Providers.RequestBodyOverride.Apply(
                        setup.Context.BodyOverride is { } existing
                            ? (System.Text.Json.Nodes.JsonObject)existing.DeepClone()
                            : new System.Text.Json.Nodes.JsonObject(),
                        bodyOverride),
                },
            };
        }

        // Prompts typed while this turn runs join it between model calls instead
        // of waiting for it to end, which is the reference's mid-turn queue fold.
        var foldTurnNumber = setup.Checkpoint?.TurnNumber ?? 0;
        var monitorSession = Session.Id;
        var monitorLifetime = _sessionLifetime.Token;
        setup = setup with
        {
            Context = setup.Context with
            {
                FoldQueuedMessages = () => FoldQueuedMessages(foldTurnNumber),
                ToolContext = setup.Context.ToolContext with
                {
                    SessionLifetime = monitorLifetime,
                    MonitorEvent = (id, description, output) => _dispatcher.InvokeAsync(() =>
                    {
                        if (!_discarded && Session.Id == monitorSession && !monitorLifetime.IsCancellationRequested)
                            DeliverTaskNotification(id, "event", description, output);
                    }),
                },
            },
        };

        IsRunning = true;
        TurnStatus.BeginTurn(DateTimeOffset.Now);
        ChatStatus.BeginTurn(DateTimeOffset.Now);
        _lastThinkingText = null;
        _turnUsage = Usage.Zero;
        var stopwatch = Stopwatch.StartNew();
        _turnCts = new CancellationTokenSource();
        var reason = TurnEndReason.Completed;
        string? detail = null;
        var batchTools = new List<string>();
        AssistantTextItem? answerPreview = null;
        void ClearAnswerPreview()
        {
            if (answerPreview is not null) Transcript.Remove(answerPreview);
            answerPreview = null;
        }
        if (setup.TurnHooks is { } watcherHooks)
        {
            EnsureFileWatcher(watcherHooks);
        }

        void FlushToolBatch()
        {
            if (batchTools.Count > 0 && setup.TurnHooks is { } hooks &&
                hooks.Has(JarvisCode.Core.Hooks.HookEvent.PostToolBatch))
            {
                var tools = new System.Text.Json.Nodes.JsonArray();
                foreach (var name in batchTools)
                {
                    tools.Add(name);
                }

                _ = RunObservationalHookAsync(hooks, JarvisCode.Core.Hooks.HookEvent.PostToolBatch,
                    new System.Text.Json.Nodes.JsonObject { ["tools"] = tools });
            }

            batchTools.Clear();
        }

        BeginTurnFooter(setup.Model);
        // A transport retry is not a silent gap: the reference names the cause and
        // counts the wait down under the composer. The sink rides the async flow, so
        // a notice raised deep inside a provider lands on the turn that provoked it.
        using var retryNotices = JarvisCode.Core.Providers.ProviderRetries.Observe(notice =>
            _dispatcher.BeginInvoke(() => ChatStatus.OnRetry(
                Services.ChatStatusLabels.CauseFrom(notice.Cause),
                notice.Attempt,
                notice.MaxAttempts,
                notice.Delay,
                notice.ReceivedAt)));
        try
        {
            if (IsCodeSurface) await IdeServices.AddSelectionAsync(Session, _turnCts.Token);
            await foreach (var evt in _services.Orchestrator.RunTurnAsync(setup.Context, _turnCts.Token))
            {
                // Anything arriving means the call the notice described came back.
                ChatStatus.OnRetrySettled();
                switch (evt)
                {
                    case AssistantTextPreviewed preview:
                        if (preview.Text.Length == 0) ClearAnswerPreview();
                        else
                        {
                            CloseThinkingRun();
                            TurnStatus.OnTextDelta(DateTimeOffset.Now);
                            ChatStatus.OnContent();
                            answerPreview ??= AddItem(new AssistantTextItem());
                            answerPreview.Markdown = preview.Text;
                        }
                        break;

                    case AssistantThinkingDelta thinking:
                        var thinkingAt = DateTimeOffset.Now;
                        TurnStatus.OnThinkingDelta(thinkingAt);
                        ChatStatus.OnContent();
                        _currentThinking ??= AddItem(new ThinkingItem { IsChatCell = !IsCodeSurface });
                        _currentThinking.Append(thinking.Delta);
                        // The reference times each thinking block rather than the turn,
                        // so a run starts at its first delta and ends as soon as
                        // anything else arrives.
                        _thinkingRunStart ??= thinkingAt;
                        _thinkingRunEnd = thinkingAt;
                        break;

                    case AssistantTextDelta delta:
                        ClearAnswerPreview();
                        CloseThinkingRun();
                        TurnStatus.OnTextDelta(DateTimeOffset.Now);
                        ChatStatus.OnContent();
                        if (_currentText is null)
                        {
                            _currentGroup = null;
                            _currentText = AddItem(new AssistantTextItem());
                        }

                        _currentText.Append(delta.Delta);
                        break;

                    case AssistantCitationDelta citation:
                        if (_currentText is not null)
                            _currentText.Append(JarvisCode.Core.Models.CitationMarkdown.Link(citation.Citation));
                        break;

                    case AssistantMessageCompleted completedMessage:
                        ClearAnswerPreview();
                        CloseThinkingRun();
                        TurnStatus.OnMessageCompleted();
                        FlushToolBatch();
                        if (_currentText is not null)
                        {
                            if (completedMessage.Message.Content.OfType<JarvisCode.Core.Models.TextBlock>()
                                .Any(block => block.Citations is { Count: > 0 }))
                                _currentText.Markdown = string.Concat(completedMessage.Message.Content
                                    .OfType<JarvisCode.Core.Models.TextBlock>()
                                    .Select(JarvisCode.Core.Models.CitationMarkdown.Format));
                            if (setup.TurnHooks is { } displayHooks &&
                                displayHooks.Has(JarvisCode.Core.Hooks.HookEvent.MessageDisplay) &&
                                _currentText.Markdown is { Length: > 0 } displayed)
                            {
                                _ = RunObservationalHookAsync(displayHooks, JarvisCode.Core.Hooks.HookEvent.MessageDisplay,
                                    new System.Text.Json.Nodes.JsonObject
                                    {
                                        ["text"] = displayed.Length > 4000 ? displayed[..4000] : displayed,
                                    });
                            }

                            _currentText.MessageIndex = Session.Messages.Count - 1;
                            _currentText.IsStreaming = false;
                            _lastAssistantItem = _currentText;
                            _currentText = null;
                        }
                        else
                        {
                            _lastAssistantItem = null;
                        }

                        if (_currentThinking is not null)
                        {
                            _lastThinkingText = _currentThinking.Text;
                            _currentThinking.IsStreaming = false;
                            PersistThinkingDurations(_currentThinking);
                            _currentThinking = null;
                        }

                        break;

                    case AssistantMessageRetracted withdrawn:
                        // The message claimed a tool call that never arrived and the
                        // engine took it back out of the conversation; the transcript
                        // drops what it rendered for it, as the reference does.
                        if (_lastAssistantItem is { } retracted)
                        {
                            Transcript.Remove(retracted);
                            _lastAssistantItem = null;
                        }
                        foreach (var withdrawnCall in withdrawn.Message.ToolCalls)
                            if (_streamingWidgets.Remove(withdrawnCall.Id, out var withdrawnWidget)) Transcript.Remove(withdrawnWidget);

                        break;

                    case ServerToolActivity:
                        TurnStatus.OnServerToolActivity();
                        break;

                    case ToolInputStarted input when Services.VisualizeWidgetCalls.IsShowWidget(input.ToolName):
                        CloseThinkingRun();
                        _currentGroup?.RefreshTitle();
                        _currentGroup = null;
                        var streamingWidget = NewWidget(input.CallId, input.ToolName, null);
                        streamingWidget.AppendInput("");
                        _streamingWidgets[input.CallId] = streamingWidget;
                        AddItem(streamingWidget);
                        break;

                    case ToolInputDelta input when _streamingWidgets.TryGetValue(input.CallId, out var widget):
                        widget.AppendInput(input.Delta);
                        break;

                    case ToolExecutionStarted started:
                        CloseThinkingRun();
                        TurnStatus.OnToolStarted();
                        TrackTouchedFile(started.ArgumentsJson);
                        ActivateConditionalSkills(started.ToolName, started.ArgumentsJson);
                        // An approved call that already sat in the group as
                        // awaiting_approval flips to running in place.
                        if (_callsById.TryGetValue(started.CallId, out var approved) && approved.IsAwaitingApproval)
                        {
                            approved.IsAwaitingApproval = false;
                            approved.IsRunning = true;
                            _currentGroup?.RefreshTitle();
                            break;
                        }

                        // A visualize widget is not a tool row beside the widget:
                        // the reference's MCP-app row *is* the widget, so the call
                        // renders as one instead of as "Used visualize: show_widget".
                        // It is also its own run there (its lQ files an MCP-app tool
                        // under "standalone"), so the run open above it is closed:
                        // without that, the next call rejoins the group sitting above
                        // the widget and the transcript reads out of order.
                        if (Services.VisualizeWidgetCalls.IsShowWidget(started.ToolName))
                        {
                            _currentGroup?.RefreshTitle();
                            _currentGroup = null;
                            if (_streamingWidgets.Remove(started.CallId, out var widget))
                                widget.CompleteInput(started.ArgumentsJson);
                            else AddItem(NewWidget(started.CallId, started.ToolName, started.ArgumentsJson));
                            break;
                        }

                        EnsureGroup(started.ToolName);
                        var call = new ToolCallItem
                        {
                            CallId = started.CallId,
                            ToolName = started.ToolName,
                            Description = started.CallDescription,
                            ArgumentsJson = started.ArgumentsJson,
                            TaskStarted = TaskStartedASession,
                        };
                        _currentGroup!.AddCall(call);
                        _callsById[started.CallId] = call;
                        _currentGroup.RefreshTitle();
                        break;

                    case ToolExecutionCompleted completed:
                        TurnStatus.OnToolFinished();
                        // The next model call starts with an empty answer again, and
                        // the reference's waiting line follows the streaming message
                        // rather than the turn, so it comes back for the round trip.
                        ChatStatus.OnAwaitingModel(DateTimeOffset.Now);
                        RecordActivity(
                            completed.ToolName,
                            _callsById.TryGetValue(completed.CallId, out var recorded) ? recorded.ArgumentsJson ?? "" : "",
                            completed.Result,
                            completed.IsError);
                        batchTools.Add(completed.ToolName);
                        if (_callsById.TryGetValue(completed.CallId, out var done))
                        {
                            done.Complete(Cap(completed.Result), completed.IsError, completed.Images);
                            // A backgrounded shell call keeps its running look until
                            // the task itself exits (TaskExited settles it), like the
                            // reference's background-confirmed rows.
                            if (done.BackgroundTaskId is not null)
                            {
                                done.IsRunning = true;
                            }
                        }

                        // The model can flip the session into plan mode; the
                        // composer's mode chip must follow.
                        if (completed.ToolName == "EnterPlanMode" && !completed.IsError)
                        {
                            PermissionModeChanged?.Invoke(this, EventArgs.Empty);
                        }

                        _currentGroup?.RefreshTitle();
                        break;

                    case ToolExecutionDenied denied:
                        TurnStatus.OnToolFinished();
                        if (_streamingWidgets.Remove(denied.CallId, out var deniedWidget)) deniedWidget.StopInput();
                        if (setup.TurnHooks is { } deniedHooks &&
                            deniedHooks.Has(JarvisCode.Core.Hooks.HookEvent.PermissionDenied))
                        {
                            _ = RunObservationalHookAsync(deniedHooks, JarvisCode.Core.Hooks.HookEvent.PermissionDenied,
                                new System.Text.Json.Nodes.JsonObject { ["tool"] = denied.ToolName });
                        }

                        if (_callsById.TryGetValue(denied.CallId, out var deniedCall))
                        {
                            deniedCall.IsAwaitingApproval = false;
                            deniedCall.IsDenied = true;
                            deniedCall.IsRunning = false;
                        }

                        _currentGroup?.RefreshTitle();
                        break;

                    case UsageReported usage:
                        _turnUsage = usage.Total;
                        _lastContextTokens = usage.LastCall.TotalInputTokens + usage.LastCall.OutputTokens;
                        _totalTokens.Observe(usage.LastCall);
                        _lastUsageAt = DateTimeOffset.Now;
                        TurnStatus.OutputTokens = usage.Total.OutputTokens;
                        UpdateContextUsage(usage.LastCall, setup.Model);
                        break;

                    case ConversationCompacting:
                        _compactionFromTokens = _lastContextTokens;
                        // The compacted context is banked so the budget keeps
                        // counting across the summary (the reference's rollOverContext).
                        _totalTokens.RollOverContext(_lastContextTokens);
                        TurnStatus.OnCompacting();
                        ChatStatus.OnCompacting(DateTimeOffset.Now);
                        break;

                    // A microcompact boundary is silent in the reference, whose
                    // renderer answers the marker with nothing at all.
                    case ConversationMicrocompacted:
                        break;

                    case ConversationCompacted compacted:
                        Session.ArchivedMessages.AddRange(compacted.Result.Archived);
                        _lastContextTokens = 0;
                        TurnStatus.OnCompacted();
                        ChatStatus.OnCompacted();
                        // Loop sentinels re-deliver their full instructions on the
                        // next fire — the delivered copy just left the context.
                        LoopDelivery.Reset();
                        // Invoked skills' content left the live context: the next
                        // message carries the invoked-skills reminder, and a
                        // re-invocation reloads instead of eliding.
                        SkillState.Tracker.MarkAllCompacted();
                        SkillState.PendingInvokedSkillsReminder = SkillState.Tracker.HasAny;
                        // The instruction files left the live context with the
                        // history: the reference re-reads them and says so.
                        MarkInstructionsForReload();
                        if (setup.TurnHooks is { } compactHooks &&
                            compactHooks.Has(JarvisCode.Core.Hooks.HookEvent.PostCompact))
                        {
                            _ = RunObservationalHookAsync(compactHooks, JarvisCode.Core.Hooks.HookEvent.PostCompact,
                                new System.Text.Json.Nodes.JsonObject { ["trigger"] = "auto" });
                        }

                        // The reference's compaction row says what the pass saved
                        // when it knows both sides, and what it started from when
                        // it only knows that.
                        Transcript.Add(new CompactionItem
                        {
                            PreTokens = _compactionFromTokens,
                            PostTokens = EstimateContextTokens(compacted.Result.Messages),
                        });
                        break;

                    case TurnCompleted completed:
                        reason = completed.Reason;
                        detail = completed.Detail;
                        break;
                }
            }
        }
        finally
        {
            ClearAnswerPreview();
            stopwatch.Stop();
            foreach (var widget in _streamingWidgets.Values) widget.StopInput();
            _streamingWidgets.Clear();
            EndTurnFooter(_currentText?.Markdown);
            FinishStreamingItems(interrupted: reason == TurnEndReason.Cancelled);
            _finishingTurn = true;
            IsRunning = false;
            TurnStatus.EndTurn();
            ChatStatus.EndTurn();
            _turnCts?.Dispose();
            _turnCts = null;
        }

        FlushToolBatch();
        if (reason == TurnEndReason.Error && setup.TurnHooks is { } failureHooks &&
            failureHooks.Has(JarvisCode.Core.Hooks.HookEvent.StopFailure))
        {
            _ = RunObservationalHookAsync(failureHooks, JarvisCode.Core.Hooks.HookEvent.StopFailure,
                new System.Text.Json.Nodes.JsonObject { ["error"] = detail ?? "unknown error" });
        }

        AppendTurnEnd(reason, detail, stopwatch.Elapsed, setup.Model);
        OnLoopTurnEnded(reason);
        LastTurnEnd = (reason, detail);
        TurnFinished?.Invoke(this, EventArgs.Empty);
        NotifyIdleSubscribers();
        if (_summaryVisible) _ = RefreshLiveSummaryAsync();
        try { await PersistAsync(setup); }
        finally { _finishingTurn = false; }
        DrainQueuedMessages();
    }

    /// <summary>
    /// The reference's mid-turn queue fold: plain prompts typed while the turn is
    /// running are handed to the running turn between model calls rather than
    /// waiting for it to finish. A queued prompt carrying attachments or a skill
    /// expansion is left alone — those compose into several content blocks and go
    /// through the ordinary send path when the turn ends.
    /// </summary>
    /// <summary>
    /// Whether a message the user queued is waiting to join this turn. The
    /// reference stands the batching reminder down when a <c>queued_command</c>
    /// attachment follows the tool results; this build folds queued messages in
    /// at the top of the next iteration, so the equivalent question is whether
    /// one is pending. The predicate is <see cref="FoldQueuedMessages"/>'s, so a
    /// queued message that will not fold does not silence the reminder either.
    ///
    /// The reference's other two suppressing types have no counterpart on a Code
    /// session: <c>teammate_mailbox</c> is the worker inbox
    /// (<c>AgentWorkers.DrainInbox</c>), which a top-level session has none of,
    /// and nothing here polls events.
    /// </summary>
    internal bool HasPendingQueuedMessage()
    {
        if (_discarded)
        {
            return false;
        }

        return _dispatcher.Invoke(() => QueuedMessages.Any(queued =>
            queued.Expansion is null &&
            queued.Attachments.Count == 0 &&
            queued.Text.Trim().Length > 0));
    }

    private IReadOnlyList<string> FoldQueuedMessages(int turnNumber)
    {
        if (_discarded)
        {
            return [];
        }

        List<string>? folded = null;
        _dispatcher.Invoke(() =>
        {
            for (int i = 0; i < QueuedMessages.Count;)
            {
                var queued = QueuedMessages[i];
                if (queued.Expansion is not null || queued.Attachments.Count > 0 ||
                    queued.Text.Trim().Length == 0)
                {
                    i++;
                    continue;
                }

                QueuedMessages.RemoveAt(i);
                (folded ??= []).Add(queued.Text);
                Transcript.Add(new UserMessageItem
                {
                    Text = queued.Text,
                    // The engine appends these in order the moment this returns,
                    // so the bubble knows where its message will land — which is
                    // what makes branching from it copy the right history.
                    MessageIndex = Session.Messages.Count + folded.Count - 1,
                    // A folded prompt belongs to the turn already running, so a
                    // rewind from it restores that turn's files.
                    TurnNumber = turnNumber,
                });
            }
        });

        return folded ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Applies what the stop hooks decided. A blocked ending sends the reason
    /// back and keeps working; a met — or impossible — goal retires itself, as
    /// the reference retires the hook it registered for it. The consecutive-block
    /// cap is the reference's own guard against a hook that never lets go.
    /// </summary>
    private void HandleStopHookOutcome(JarvisCode.Core.Hooks.StopHookOutcome stop)
    {
        var goal = ActiveGoal;
        if (stop.Impossible)
        {
            SetSessionGoal(null);
            _stopHookBlocks = 0;
            Transcript.Add(new NoticeItem
            {
                Text = "Goal could not be achieved" +
                    (stop.Decision.BlockReason is { Length: > 0 } why ? $" — {why}" : ""),
                IsError = true,
            });
            return;
        }

        if (!stop.Decision.Allowed && stop.Decision.BlockReason is { Length: > 0 } feedback)
        {
            int cap = StopHookBlockCap();
            _stopHookBlocks++;
            if (cap > 0 && _stopHookBlocks > cap)
            {
                _stopHookBlocks = 0;
                _stopHookActive = false;
                Transcript.Add(new NoticeItem
                {
                    Text = JarvisCode.Core.Hooks.PromptHooks.StopBlockCapReached(cap),
                    IsError = true,
                });
                return;
            }

            if (goal is not null && stop.Condition == goal.Condition)
            {
                goal.Iterations++;
                Transcript.Add(new NoticeItem { Text = "Goal not yet met… continuing" });
            }

            _stopHookActive = true;
            _pendingNotifications.Add(
                "<system-reminder>\nA stop hook blocked ending the turn. Its feedback:\n" +
                feedback + "\n</system-reminder>");
            DrainQueuedMessages();
            return;
        }

        _stopHookBlocks = 0;
        if (goal is not null && stop.Condition == goal.Condition)
        {
            // Nothing blocked and a goal was in play: the condition is met, so it
            // retires rather than being re-judged on every later turn.
            SetSessionGoal(null);
            Transcript.Add(new NoticeItem { Text = "Goal achieved" });
        }
    }

    // ---- goal check-ins: what a deferred goal says while it waits --------------

    private System.Threading.Timer? _goalIdleTimer;

    /// <summary>
    /// The background work holding this session's goal open: agents still running
    /// and shells still running, each named the way the reference names it.
    /// </summary>
    private IReadOnlyList<DeferringTask> DeferringTasks()
    {
        var tasks = new List<DeferringTask>();
        foreach (var worker in Workers.List())
        {
            if (worker.Status == WorkerStatus.Running)
            {
                tasks.Add(new DeferringTask(
                    worker.Id,
                    worker.Name is { Length: > 0 } ? "teammate" : "subagent",
                    worker.PromptPreview,
                    worker.StartedAt ?? DateTimeOffset.Now));
            }
        }

        foreach (var task in _services.BackgroundTasks.List())
        {
            if (task.Status != JarvisCode.Core.BackgroundTasks.BackgroundTaskStatus.Running ||
                (task.SessionId is not null && task.SessionId != Session.Id))
            {
                continue;
            }

            tasks.Add(new DeferringTask(
                task.Id,
                task.Kind switch
                {
                    "Workflow" => "workflow",
                    "Monitor" => "monitor",
                    null or "" => "shell",
                    var kind => kind.ToLowerInvariant(),
                },
                task.Kind is null or "" ? task.Command : task.Description ?? task.Command,
                task.StartedAt ?? DateTimeOffset.Now));
        }

        return tasks;
    }

    /// <summary>
    /// Stands the goal's Stop hook down for this turn when background work is
    /// running, reporting the wait once it has gone on long enough. Answers true
    /// when the goal was deferred, which is what the caller skips the hook on.
    /// </summary>
    private bool DeferGoalIfBackgroundWorkIsRunning()
    {
        if (ActiveGoal is not { } goal)
        {
            return false;
        }

        var tasks = DeferringTasks();
        if (tasks.Count == 0)
        {
            // The work is gone: the goal is judged normally again from here.
            goal.Deferral = GoalDeferral.None;
            return false;
        }

        var (deferral, checkinText) = GoalCheckins.AtTurnEnd(
            goal.Condition, goal.Deferral, tasks, DateTimeOffset.Now);
        goal.Deferral = deferral;
        if (checkinText is { Length: > 0 })
        {
            DeliverGoalCheckin(GoalCheckins.Message(goal.Condition, TimeSpan.Zero, tasks).Summary, checkinText);
        }

        return true;
    }

    /// <summary>
    /// Arms the idle check-in for a goal that is waiting, and disarms it for one
    /// that is not. The timer only ever looks: whether it says anything is
    /// decided when it fires.
    /// </summary>
    private void ArmGoalIdleCheckin()
    {
        _goalIdleTimer?.Dispose();
        _goalIdleTimer = null;
        if (_discarded || ActiveGoal is not { Deferral.IsDeferred: true } goal)
        {
            return;
        }

        var interval = GoalCheckins.Interval();
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        var delay = GoalCheckins.IdleDelay(goal.Deferral, DateTimeOffset.Now, interval);
        _goalIdleTimer = new System.Threading.Timer(
            _ => _dispatcher.InvokeAsync(() => OnGoalIdleCheckin(goal)),
            null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The idle tick. A running turn, a goal that has moved on, or a run that has
    /// spent its three check-ins all mean "look again later" rather than "say
    /// something now" — the reference keeps the tick alive in every one of those
    /// cases so a resumed session still notices.
    /// </summary>
    private void OnGoalIdleCheckin(GoalState goal)
    {
        if (_discarded || !ReferenceEquals(ActiveGoal, goal) || !goal.Deferral.IsDeferred)
        {
            return;
        }

        var interval = GoalCheckins.Interval();
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        if (IsRunning || GoalCheckins.IdleCapReached(goal.Deferral))
        {
            RearmGoalIdleCheckin(goal, GoalCheckins.MinimumIdleDelay);
            return;
        }

        var now = DateTimeOffset.Now;
        var tasks = DeferringTasks();
        var (opened, isNewRun) = GoalCheckins.BeginPass(goal.Deferral, tasks, now, interval);
        if (isNewRun)
        {
            goal.Deferral = opened with { LastDeferralPassAt = now };
            ArmGoalIdleCheckin();
            return;
        }

        var deferred = now - (opened.DeferredSince ?? now);
        goal.Deferral = opened with
        {
            DeferredSince = now,
            CheckinCount = opened.CheckinCount + 1,
            LastDeferralPassAt = now,
            IdleCheckinCount = opened.IdleCheckinCount + 1,
        };

        var message = GoalCheckins.Message(goal.Condition, deferred, tasks);
        if (GoalCheckins.IdleCapReached(goal.Deferral))
        {
            message = GoalCheckins.Paused(message);
        }

        DeliverGoalCheckin(message.Summary, message.Body);
        ArmGoalIdleCheckin();
    }

    private void RearmGoalIdleCheckin(GoalState goal, TimeSpan delay)
    {
        _goalIdleTimer?.Dispose();
        _goalIdleTimer = new System.Threading.Timer(
            _ => _dispatcher.InvokeAsync(() => OnGoalIdleCheckin(goal)),
            null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A check-in reaches the session the way any other harness input does.</summary>
    private void DeliverGoalCheckin(string summary, string body) =>
        DeliverTaskNotification("goal-checkin", "waiting", summary, body);

    /// <summary>The reference's CLAUDE_CODE_STOP_HOOK_BLOCK_CAP, default 8.</summary>
    private static int StopHookBlockCap() =>
        int.TryParse(
            Environment.GetEnvironmentVariable(
                JarvisCode.Core.Hooks.PromptHooks.StopBlockCapEnvironmentVariable),
            out int configured)
            ? configured
            : JarvisCode.Core.Hooks.PromptHooks.StopBlockCap;

    private int _stopHookBlocks;

    /// <summary>Whether this plan-mode run has already carried the full workflow.</summary>
    private bool _planWorkflowSent;

    /// <summary>A permission-mode notice is stated once per session, not per turn.</summary>
    private bool _modeNoticeSent;

    /// <summary>Sends pending task notifications, then queued prompts, one per turn.</summary>
    private void DrainQueuedMessages()
    {
        if (_discarded || IsRunning)
        {
            return;
        }

        if (_pendingNotifications.Count > 0)
        {
            var notification = _pendingNotifications[0];
            _pendingNotifications.RemoveAt(0);
            var sessionId = Session.Id;
            var lifetime = _sessionLifetime.Token;
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (Session.Id == sessionId && !lifetime.IsCancellationRequested && !_discarded)
                    _ = SendNotificationAsync(notification);
            });
            return;
        }

        if (QueuedMessages.Count == 0)
        {
            return;
        }

        var next = QueuedMessages[0];
        QueuedMessages.RemoveAt(0);
        var queuedSession = Session.Id;
        var queuedLifetime = _sessionLifetime.Token;
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (Session.Id == queuedSession && !queuedLifetime.IsCancellationRequested && !_discarded)
                _ = SendWithAttachmentsAsync(next.Text, next.Attachments, next.Expansion);
        });
    }

    private readonly List<string> _pendingNotifications = [];

    /// <summary>The text item of the assistant message just completed, for a retraction.</summary>
    private AssistantTextItem? _lastAssistantItem;
    /// <summary>/pause-memory: stops topic recall and hides the memory tool for this session.</summary>
    public bool MemoryPaused { get; set; }

    /// <summary>
    /// How the last turn ended, and what it said. <see cref="TurnFinished"/>
    /// carries no reason, and the comparison view needs one: a pair where one
    /// arm failed is not a pair to prefer between.
    /// </summary>
    public (TurnEndReason Reason, string? Detail)? LastTurnEnd { get; private set; }

    /// <summary>
    /// SendMessage's cross-session half: delivers a message to another local
    /// session by title or id prefix. Set by the surface on bind (Code only).
    /// </summary>
    public Func<string, string, Task<string?>>? CrossSessionSend { get; set; }

    /// <summary>
    /// Subscribes this session to another local session's next idle (the
    /// reference SendMessage's notify_when_idle); bound by the surface, which
    /// knows the other sessions. Answers an error, or null once subscribed.
    /// </summary>
    public Func<string, Task<string?>>? CrossSessionIdleSubscribe { get; set; }

    /// <summary>The reference's cap on a session's outstanding idle subscriptions.</summary>
    public const int MaxIdleSubscriptions = SessionIdleSubscriptions.MaximumSubscriptions;

    /// <summary>
    /// Records that <paramref name="subscriber"/> wants one notice when this
    /// session next goes idle. Returns the reference's refusal when the
    /// subscriber already holds too many, else null.
    /// </summary>
    public string? AddIdleSubscriber(ChatViewModel subscriber, string label)
    {
        return IdleSubscriptions.Subscribe(Session.Id, subscriber.Session.Id, label,
            notice => subscriber._dispatcher.InvokeAsync(() => subscriber.DeliverIdleNotice(notice)));
    }

    /// <summary>
    /// Idle is a turn that ended with nothing queued and no notification
    /// waiting; every subscriber gets its one notice and is forgotten.
    /// </summary>
    private void NotifyIdleSubscribers()
    {
        if (QueuedMessages.Count > 0 || _pendingNotifications.Count > 0)
        {
            return;
        }

        IdleSubscriptions.Idle(Session.Id, DateTimeOffset.Now,
            Session.Messages.LastOrDefault(m => m.Role == JarvisCode.Core.Models.Role.Assistant)?.GetText());
        _mailbox?.NotifyIdle(Session.Messages.LastOrDefault(m => m.Role == JarvisCode.Core.Models.Role.Assistant)?.GetText());
    }

    /// <summary>
    /// The reference's cross-session idle notice, as its subscriber receives it
    /// (its <c>[Cross-session idle notice]</c> line with the finish time).
    /// </summary>
    public void DeliverIdleNotice(string label, DateTime finishedAt)
        => DeliverIdleNotice(new SessionIdleNotice("idle", label, new DateTimeOffset(finishedAt)));

    private void DeliverIdleNotice(SessionIdleNotice notice)
    {
        if (_discarded)
        {
            return;
        }

        Transcript.Add(new NoticeItem { Text = notice.Message });
        _pendingNotifications.Add(notice.Message);
        DrainQueuedMessages();
    }

    // ---- /loop: recurring prompts, fixed-interval or model-paced (ScheduleWakeup) ----

    public sealed record LoopInfo(
        int Id, string Prompt, TimeSpan? Interval, bool Dynamic, DateTimeOffset? NextRunAt = null);

    private sealed class LoopEntry
    {
        public required int Id { get; init; }
        public required string Prompt { get; set; }
        public TimeSpan? Interval { get; init; }
        public bool Dynamic { get; init; }
        public required System.Windows.Threading.DispatcherTimer Timer { get; init; }

        /// <summary>When this loop fires next — the Background tasks panel counts down to it.</summary>
        public DateTimeOffset NextRunAt { get; set; }
    }

    private readonly List<LoopEntry> _loops = [];
    private int _nextLoopId;

    /// <summary>
    /// The transcript line a harness-submitted turn leaves in place of a user
    /// bubble. The reference renders an <c>isMeta</c> turn as an event rather
    /// than as something the user typed; what that event says for a loop tick is
    /// this build's own wording, since its own loop has never fired on the
    /// machine this was measured against.
    /// </summary>
    internal static string LoopTickNotice(string prompt)
    {
        var line = prompt.Trim().ReplaceLineEndings(" ");
        return line.Length <= 120 ? $"Loop tick: {line}" : $"Loop tick: {line[..119]}…";
    }

    /// <summary>
    /// Sentinel-delivery state for loop ticks (the reference's lastLoopFileDelivered/
    /// autonomousPreambleDelivered pair): the full instructions ride the first fire and
    /// again after a compact or a loop.md edit; later fires get the short reminder.
    /// </summary>
    internal readonly Services.LoopPrompts.DeliveryState LoopDelivery = new();

    /// <summary>The dynamic tick whose turn is running and hasn't rescheduled or stopped yet.</summary>
    private string? _dynamicTickPending;

    /// <summary>Keepalive re-arms once when the model forgets to reschedule (reference budget 1).</summary>
    private int _loopKeepaliveCount;

    // The noop streak (reference: consecutive noop:true ticks collapse in the
    // transcript view): tick boundaries are marked at fire, the tool reports the
    // model's noop verdict, and the turn end folds a quiet tick into the notice.
    private int? _loopTickStartIndex;
    private bool? _loopTickNoop;
    private int _loopNoopStreak;
    private NoticeItem? _loopStreakNotice;

    public IReadOnlyList<LoopInfo> Loops
    {
        get
        {
            lock (_loops)
            {
                return [.. _loops.Select(l => new LoopInfo(l.Id, l.Prompt, l.Interval, l.Dynamic, l.NextRunAt))];
            }
        }
    }

    /// <summary>Starts a fixed-interval loop; a tick that lands mid-turn is skipped.</summary>
    public int StartLoop(TimeSpan interval, string prompt)
    {
        var entry = new LoopEntry
        {
            Id = ++_nextLoopId,
            Prompt = prompt,
            Interval = interval,
            Timer = new System.Windows.Threading.DispatcherTimer { Interval = interval },
        };
        entry.Timer.Tick += (_, _) =>
        {
            entry.NextRunAt = DateTimeOffset.Now + interval;
            if (!_discarded && !IsRunning)
            {
                _ = SendAsync(Services.LoopPrompts.ResolveFire(
                    entry.Prompt, Session.WorkingDirectory, LoopDelivery), meta: true);
            }
        };
        entry.NextRunAt = DateTimeOffset.Now + interval;
        entry.Timer.Start();
        lock (_loops)
        {
            _loops.Add(entry);
        }

        return entry.Id;
    }

    /// <summary>
    /// ScheduleWakeup: one model-paced wakeup. Replaces any pending dynamic
    /// wakeup; firing mid-turn re-arms 30s later instead of interrupting. A model
    /// call settles the in-flight tick (its noop verdict feeds the streak) and
    /// resets the keepalive budget; a keepalive re-arm leaves both alone.
    /// </summary>
    public void ScheduleWakeup(TimeSpan delay, string prompt, bool? noop = null, bool viaKeepalive = false)
    {
        if (!viaKeepalive)
        {
            _dynamicTickPending = null;
            _loopKeepaliveCount = 0;
            _loopTickNoop = noop;
        }

        lock (_loops)
        {
            foreach (var stale in _loops.Where(static l => l.Dynamic).ToList())
            {
                stale.Timer.Stop();
                _loops.Remove(stale);
            }
        }

        var entry = new LoopEntry
        {
            Id = ++_nextLoopId,
            Prompt = prompt,
            Dynamic = true,
            Timer = new System.Windows.Threading.DispatcherTimer { Interval = delay },
        };
        entry.Timer.Tick += (_, _) =>
        {
            if (_discarded)
            {
                entry.Timer.Stop();
                return;
            }

            if (IsRunning)
            {
                entry.Timer.Interval = TimeSpan.FromSeconds(30);
                entry.NextRunAt = DateTimeOffset.Now + entry.Timer.Interval;
                return;
            }

            entry.Timer.Stop();
            lock (_loops)
            {
                _loops.Remove(entry);
            }

            // Mark the tick before sending so the turn end can fold a quiet one.
            _loopTickStartIndex = Transcript.Count;
            _loopTickNoop = null;
            _dynamicTickPending = entry.Prompt;
            _ = SendAsync(Services.LoopPrompts.ResolveFire(
                entry.Prompt, Session.WorkingDirectory, LoopDelivery), meta: true);
        };
        entry.NextRunAt = DateTimeOffset.Now + delay;
        entry.Timer.Start();
        lock (_loops)
        {
            _loops.Add(entry);
        }
    }

    /// <summary>
    /// ScheduleWakeup stop:true — ends the dynamic loop only (the reference's
    /// Csn): pending dynamic wakeups are cancelled and the in-flight tick is
    /// settled, while fixed-interval loops keep ticking.
    /// </summary>
    public int StopDynamicLoops()
    {
        _dynamicTickPending = null;
        _loopKeepaliveCount = 0;
        lock (_loops)
        {
            var dynamic = _loops.Where(static l => l.Dynamic).ToList();
            foreach (var entry in dynamic)
            {
                entry.Timer.Stop();
                _loops.Remove(entry);
            }

            return dynamic.Count;
        }
    }

    /// <summary>
    /// The loop bookkeeping a finished turn owes (called once per turn): a user
    /// abort cancels the dynamic loop (reference user_abort semantics), a dynamic
    /// tick the model never rescheduled gets one keepalive re-arm when the
    /// reference's CLAUDE_CODE_LOOP_KEEPALIVE opt-in is set (budget 1 — a second
    /// decline ends the loop), and consecutive noop:true ticks collapse into a
    /// single streak notice in the transcript view.
    /// </summary>
    private void OnLoopTurnEnded(TurnEndReason reason)
    {
        if (reason == TurnEndReason.Cancelled)
        {
            StopDynamicLoops();
            _loopTickStartIndex = null;
            _loopTickNoop = null;
            return;
        }

        if (_dynamicTickPending is { } pending)
        {
            _dynamicTickPending = null;
            if (Services.LoopPrompts.KeepaliveEnabled &&
                _loopKeepaliveCount < Services.LoopPrompts.KeepaliveBudget)
            {
                _loopKeepaliveCount++;
                ScheduleWakeup(TimeSpan.FromSeconds(Services.LoopPrompts.KeepaliveDelaySeconds),
                    pending, viaKeepalive: true);
            }
        }

        if (_loopTickStartIndex is { } start && _loopTickNoop == true &&
            reason == TurnEndReason.Completed && start <= Transcript.Count)
        {
            while (Transcript.Count > start)
            {
                Transcript.RemoveAt(Transcript.Count - 1);
            }

            _loopNoopStreak++;
            if (_loopStreakNotice is not null)
            {
                Transcript.Remove(_loopStreakNotice);
            }

            _loopStreakNotice = new NoticeItem
            {
                Text = _loopNoopStreak == 1
                    ? "Quiet loop tick — nothing to report."
                    : $"{_loopNoopStreak} quiet loop ticks — nothing to report.",
            };
            Transcript.Add(_loopStreakNotice);
        }
        else if (_loopTickNoop == false || _loopTickStartIndex is null)
        {
            _loopNoopStreak = 0;
            _loopStreakNotice = null;
        }

        _loopTickStartIndex = null;
        _loopTickNoop = null;
    }

    public bool StopLoop(int id)
    {
        lock (_loops)
        {
            if (_loops.FirstOrDefault(l => l.Id == id) is not { } entry)
            {
                return false;
            }

            entry.Timer.Stop();
            _loops.Remove(entry);
            return true;
        }
    }

    public int StopAllLoops()
    {
        lock (_loops)
        {
            int count = _loops.Count;
            foreach (var entry in _loops)
            {
                entry.Timer.Stop();
            }

            _loops.Clear();
            return count;
        }
    }

    /// <summary>
    /// /goal — the standing goal, carried as a Stop hook the model answers: the
    /// turn cannot end while a model reading the transcript says the condition is
    /// not met yet. Set through <see cref="SetSessionGoal"/>, which also records
    /// when it started and how many turns it has taken.
    /// </summary>
    public string? SessionGoal { get; private set; }

    /// <summary>The reference's activeGoal: the condition plus what it has cost so far.</summary>
    public sealed class GoalState(string condition, long tokensAtStart)
    {
        public string Condition { get; } = condition;

        public DateTimeOffset SetAt { get; } = DateTimeOffset.Now;

        public long TokensAtStart { get; } = tokensAtStart;

        /// <summary>How many times the goal has held the turn open.</summary>
        public int Iterations { get; set; }

        /// <summary>
        /// How long this goal has been waiting on background work, and how often
        /// that wait has been reported. A goal cannot be judged from a transcript
        /// that is still being written, so it defers instead.
        /// </summary>
        public GoalDeferral Deferral { get; set; } = GoalDeferral.None;
    }

    /// <summary>The active goal, or null when none is set.</summary>
    public GoalState? ActiveGoal { get; private set; }

    /// <summary>Sets or clears the standing goal (null clears).</summary>
    public void SetSessionGoal(string? condition)
    {
        SessionGoal = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        ActiveGoal = SessionGoal is null ? null : new GoalState(SessionGoal, _lastContextTokens);

        // A goal that is gone has nothing to check in about.
        _goalIdleTimer?.Dispose();
        _goalIdleTimer = null;
    }

    /// <summary>
    /// In-process hooks joined into every Code turn — the host's own, the way the
    /// reference registers its preview-verification pair. Set by the workspace.
    /// </summary>
    public IReadOnlyList<JarvisCode.Core.Hooks.FunctionHook>? FunctionHooks { get; set; }

    /// <summary>/brief — brief-only mode for this session.</summary>
    public bool BriefMode { get; set; }

    /// <summary>
    /// Ultracode (reference CLI): session-scoped, never persisted — the turn
    /// runs at xhigh and the harness flips through the enter/still-on/exit
    /// system-reminders attached as messages go out.
    /// </summary>
    public bool UltracodeMode { get; set; }

    /// <summary>
    /// The reference's per-model default effort: switching to a model the user
    /// once picked an effort for restores that effort.
    /// </summary>
    private void ApplySavedEffortFor(string modelId)
    {
        var settings = _services.Settings.Current;
        // A provider-native ladder keeps its own opaque value and display label. Feeding a label
        // such as ChatGPT's "Pro" back into the fixed API effort setting would overwrite the
        // user's ordinary-provider default and then silently resolve the unknown label to High.
        if (settings.ProviderOptionsByModel.TryGetValue(modelId, out var providerOptions)
            && providerOptions.Count > 0)
        {
            EffortChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (settings.EffortByModel.TryGetValue(modelId, out var saved) &&
            saved != settings.ThinkingEffortName)
        {
            settings.ThinkingEffortName = saved;
            _services.Settings.Save();
            EffortChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when a model switch restored that model's saved effort, so the chip re-reads it.</summary>
    public event EventHandler? EffortChanged;

    /// <summary>True once the enter reminder went out, so exit knows to announce.</summary>
    private bool _ultracodeAnnounced;

    /// <summary>
    /// Session-scoped skill state: invoked-skill tracking (elision + the
    /// post-compaction reminder), announced listing entries, conditional
    /// (paths:) activation, skill-contributed hooks and overrides.
    /// </summary>
    public SkillSessionState SkillState { get; } = new();

    /// <summary>
    /// The skill-listing system-reminder for this message: the full listing on
    /// a session's first message, only newly discovered skills afterwards,
    /// null when nothing is new. Budgeted like the reference (1% of the
    /// context window in chars).
    /// </summary>
    /// <summary>
    /// Adds the reference's mid-conversation system turn behind the user message
    /// it accompanies: the agent-type roster the first time, plus any newly
    /// discovered skills. Nothing is added when there is nothing to announce,
    /// which is the ordinary case after the first turn.
    /// </summary>
    /// <summary>
    /// The harness's own sections for this turn — roster, MCP instructions, skill
    /// listing, notices and the live token block — placed the way this model
    /// takes them: a trailing system turn for a model with the mid-conversation
    /// system role, reminder blocks leading the user message otherwise. Runs
    /// after the turn's setup so the roster can name the registry's tools.
    /// </summary>
    private void PlaceHarnessSections(TurnSetup setup, string? skillListingBody, IReadOnlyList<string> tailNotices)
    {
        if (!IsCodeSurface || Session.Messages.Count == 0)
        {
            return;
        }

        var includeAgentTypes = !SkillState.AnnouncedAgentTypes;
        var customAgents = includeAgentTypes
            ? JarvisCode.Core.Customization.CustomAgents.Load(
                Session.WorkingDirectory, _services.Paths.UserAgentsDirectory)
            : [];

        // The reference sends a server's instructions once, when the set of
        // connected servers changes, and its later deltas only carry what moved.
        // The shell's servers do not come and go inside a session, so the first
        // announcement is the only one.
        // The reference sends a server's instructions once and retracts them
        // when the server goes away; a configured server's own instructions come
        // off its initialize handshake and join the shell's under one header.
        var configured = _services.Mcp.ServerInstructions;
        var freshInstructions = configured
            .Where(server => !SkillState.AnnouncedMcpInstructionServers.Contains(server.Name))
            .ToList();
        var departed = SkillState.AnnouncedMcpInstructionServers
            .Where(name => !configured.Any(server => server.Name == name))
            .Order(StringComparer.Ordinal)
            .ToList();
        var instructions = SkillState.AnnouncedMcpInstructions && freshInstructions.Count == 0
            ? null
            : Services.McpServerInstructions.RenderAll(
                SkillState.AnnouncedMcpInstructions ? [] : McpServerInstructionBlocks, freshInstructions);
        foreach (var server in freshInstructions)
        {
            SkillState.AnnouncedMcpInstructionServers.Add(server.Name);
        }

        foreach (var name in departed)
        {
            SkillState.AnnouncedMcpInstructionServers.Remove(name);
        }
        var sections = new List<string>();
        // The reference's deferred_tools_delta leads the harness turn, ahead of
        // the agent roster: it names the tools the request no longer advertises
        // and the servers whose tools are unavailable.
        if (DeferredToolNotice.Build(SkillState, setup.Deferred, _services.Mcp, nonInteractive: false) is { } deferredBlock)
        {
            sections.Add(deferredBlock);
        }

        sections.AddRange(HarnessSystemMessage.Sections(
            customAgents, skillListingBody, includeAgentTypes, instructions, tailNotices, setup.Roster));
        // The live token block closes the turn's harness content whenever the
        // reminder follows user prompts (the reference's afterUserTurn, on by default).
        if (_totalTokens.Enabled && _totalTokens.AfterUserTurn &&
            _totalTokens.RenderLive(setup.Model.MaxContextTokens) is { } tokens)
        {
            sections.Add(tokens);
        }

        if (departed.Count > 0)
        {
            sections.Add(JarvisCode.Core.Agent.DeferredToolAnnouncements.Disconnected(departed));
        }

        SkillState.AnnouncedAgentTypes = true;
        SkillState.AnnouncedMcpInstructions = true;

        var (userMessage, body) = HarnessTurnComposer.Place(setup.Model.ModelId, Session.Messages[^1], sections);
        if (body is not null)
        {
            Session.Messages.Add(SystemReminders.HarnessSystemMessage(body));
        }
        else
        {
            Session.Messages[^1] = userMessage;
        }
    }

    /// <summary>The token-budget reminder for this session's main agent.</summary>
    private readonly JarvisCode.Core.Agent.TotalTokensReminder _totalTokens;

    private static JarvisCode.Core.Agent.TotalTokensReminder ResolveTotalTokens(AppServices services)
    {
        var settings = services.Settings.Current;
        return new JarvisCode.Core.Agent.TotalTokensReminder(
            JarvisCode.Core.Agent.TotalTokensReminder.ResolveMode(
                Environment.GetEnvironmentVariable(JarvisCode.Core.Agent.TotalTokensReminder.ModeVariable),
                settings.TotalTokensReminder),
            JarvisCode.Core.Agent.TotalTokensReminder.ResolveBudget(
                Environment.GetEnvironmentVariable(JarvisCode.Core.Agent.TotalTokensReminder.BudgetVariable),
                settings.TotalTokensReminderBudget),
            JarvisCode.Core.Agent.TotalTokensReminder.ResolveAfterUserTurn(
                Environment.GetEnvironmentVariable(JarvisCode.Core.Agent.TotalTokensReminder.AfterUserTurnVariable),
                settings.TotalTokensReminderAfterUserTurn));
    }

    /// <summary>Which of the reference's prompt sections the session's model receives.</summary>
    private PromptModelProfile CurrentProfile() =>
        PromptModelProfile.For(_factory.ResolveModel(Session)?.ModelId ?? "");

    /// <summary>
    /// True while a background shell or monitor this session started is still
    /// running — the case in which a proactive style's reminder tells the model
    /// to end its turn rather than fill the wait.
    /// </summary>
    private bool HasRunningBackgroundWork() =>
        _services.BackgroundTasks.List().Any(t =>
            t.SessionId == Session.Id && t.Status == JarvisCode.Core.BackgroundTasks.BackgroundTaskStatus.Running);

    private string? BuildSkillListingBody()
    {
        try
        {
            var ui = _services.UiSettings.Current;
            var all = SkillCatalog.LoadAll(Session.WorkingDirectory, _services.Paths);
            var enabled = SkillCatalog.Enabled(all, ui);
            var forListing = SkillCatalog.ForListing(enabled, ui, SkillState);
            var fresh = forListing.Where(s => !SkillState.AnnouncedListingNames.Contains(s.Name)).ToList();
            if (fresh.Count == 0)
            {
                return null;
            }

            foreach (var skill in fresh)
            {
                SkillState.AnnouncedListingNames.Add(skill.Name);
            }

            var contextTokens = _factory.ResolveModel(Session)?.MaxContextTokens ?? 200_000;
            var budget = Math.Max(1, (int)(contextTokens * 4L * JarvisCode.Core.Customization.SkillInvocation.ListingBudgetFraction));
            var body = JarvisCode.Core.Customization.SkillInvocation.BuildListing(
                fresh, SkillCatalog.UsageScores(ui), budget);
            return body.Length == 0 ? null : body;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private System.Windows.Threading.DispatcherTimer? _checkInTimer;
    private bool _coordinatorMode;

    /// <summary>
    /// Coordinator mode (reference CLI): this session delegates to background
    /// workers, and while dispatched work runs it receives periodic check-ins.
    /// </summary>
    public bool CoordinatorMode
    {
        get => _coordinatorMode;
        set
        {
            if (_coordinatorMode == value)
            {
                return;
            }

            _coordinatorMode = value;
            if (value)
            {
                _checkInTimer ??= new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromSeconds(60),
                    System.Windows.Threading.DispatcherPriority.Background,
                    (_, _) => DeliverCoordinatorCheckIn(),
                    _dispatcher);
                _checkInTimer.Start();
            }
            else
            {
                _checkInTimer?.Stop();
            }
        }
    }

    /// <summary>An idle coordinator hears about still-running workers once a minute.</summary>
    private void DeliverCoordinatorCheckIn()
    {
        if (_discarded || !CoordinatorMode || IsRunning)
        {
            return;
        }

        if (Services.CoordinatorMode.BuildCheckIn(Workers.List()) is { } checkIn)
        {
            _pendingNotifications.Add("<system-reminder>\n" + checkIn + "\n</system-reminder>");
            DrainQueuedMessages();
        }
    }

    /// <summary>A background agent finished: deliver its reference task notification.</summary>
    private void OnWorkerFinished(JarvisCode.Core.Agent.WorkerInfo info, JarvisCode.Core.Tools.ToolResult result)
    {
        var status = info.Status switch
        {
            JarvisCode.Core.Agent.WorkerStatus.Completed => "completed",
            JarvisCode.Core.Agent.WorkerStatus.Killed => "killed",
            _ => "failed",
        };
        // The Agent call was answered at launch, so its row only learns the
        // agent's actual report here — which is what its subagent view shows.
        if (info.ToolUseId is { Length: > 0 } toolUseId &&
            _callsById.TryGetValue(toolUseId, out var launched))
        {
            launched.SubagentReport = result.Content;
        }

        DeliverTaskNotification(
            info.Id, status,
            $"Background agent {info.Id} ({info.AgentType}) {status}: {info.PromptPreview}",
            result.Content,
            isError: info.Status == JarvisCode.Core.Agent.WorkerStatus.Failed);

        if (_lastHooks is { } hooks && hooks.Has(JarvisCode.Core.Hooks.HookEvent.SubagentStop))
        {
            _ = RunObservationalHookAsync(hooks, JarvisCode.Core.Hooks.HookEvent.SubagentStop,
                new System.Text.Json.Nodes.JsonObject
                {
                    ["agent_id"] = info.Id,
                    ["agent_type"] = info.AgentType,
                    ["status"] = status,
                });
        }
    }

    /// <summary>
    /// Delivers a reference task notification into the conversation: immediately
    /// when idle, after the current response otherwise. Callers marshal to the
    /// UI thread; PR watchers and workers both come through here.
    /// </summary>
    /// <summary>
    /// The most of a task's output a notification carries. The reference caps its
    /// background-task notifications after very large failure output made a
    /// request exceed the API size limit (its 2.1.25x fix); its figure is not
    /// published, so this one is this build's own.
    /// </summary>
    public const int MaxNotificationBodyChars = 20_000;

    public void DeliverTaskNotification(string taskId, string status, string summary, string body, bool isError = false)
    {
        if (_discarded)
        {
            return;
        }

        if (!IsRunning) _incomingWork = true;

        if (body.Length > MaxNotificationBodyChars)
        {
            body = body[..MaxNotificationBodyChars] +
                   $"\n[… {body.Length - MaxNotificationBodyChars} more characters omitted from this notification]";
        }

        // The reference wraps a task notification in its own paragraph before
        // the reminder tags (its bbn): the event arrives on a user turn and
        // would otherwise read as the person answering a pending question.
        var notification = JarvisCode.Core.Agent.TaskNotifications.Wrap(
            JarvisCode.Core.Agent.TaskNotifications.Element(taskId, status, summary, body),
            inHumanTurn: IsRunning);

        Transcript.Add(new NoticeItem { Text = summary, IsError = isError });
        _pendingNotifications.Add(notification);
        DrainQueuedMessages();
    }

    /// <summary>Runs a turn on a task notification (a user-role message the bubble hides).</summary>
    private async Task SendNotificationAsync(string notificationText)
    {
        if (IsRunning || _discarded)
        {
            _pendingNotifications.Insert(0, notificationText);
            return;
        }

        Session.Messages.Add(SystemReminders.HarnessMessage(notificationText) with { IsMeta = true });

        // A notification turn carries no typed /name, so disable-model-invocation stays locked.
        SkillState.CurrentTurnTypedText = null;
        var setup = _factory.CreateForCode(
            Session, Gate, "task notification", OnTodosChanged, OnSubagentActivity, ExtraTools,
            _lastContextTokens, ShowQuestionAsync, ShowPlanApprovalAsync, Workers, CoordinatorMode, MemoryPaused, FireHook, CrossSessionSend, OnSubagentEvent, ultracodeMode: UltracodeMode, skillState: SkillState, backgroundMoves: BackgroundMoves, tasks: Tasks, teams: Teams, teammateMessage: DeliverTeammateMessage, subscribeIdle: CrossSessionIdleSubscribe,
                planApprovals: PlanApprovals, sessionGoal: SessionGoal, functionHooks: FunctionHooks);
        if (setup is null)
        {
            return;
        }

        await RunTurnAsync(setup);
    }

    private void FinishStreamingItems(bool interrupted = false)
    {
        if (_currentText is not null)
        {
            _currentText.IsStreaming = false;
            _currentText = null;
        }

        // A turn that was stopped mid-thought still books what it measured, so the
        // cell settles with a header instead of streaming for ever.
        CloseThinkingRun();
        if (_currentThinking is not null)
        {
            _currentThinking.IsStreaming = false;
            PersistThinkingDurations(_currentThinking);
            _currentThinking = null;
        }

        foreach (var call in _callsById.Values)
        {
            // A backgrounded shell row legitimately outlives the turn — its task
            // still runs; TaskExited settles it.
            if (call.IsRunning && call.BackgroundTaskId is null)
            {
                // A call still running when the user stopped the turn settles the
                // reference way: no failed treatment, the CLI's interrupted line.
                if (interrupted)
                {
                    call.Complete("[Request interrupted by user for tool use]", isError: false, interrupted: true);
                }
                else
                {
                    call.IsRunning = false;
                }
            }

            // A row still awaiting approval when the turn ends was never decided.
            if (call.IsAwaitingApproval)
            {
                call.IsAwaitingApproval = false;
                call.IsInterrupted = true;
            }
        }

        _currentGroup?.RefreshTitle();
        _currentGroup = null;
    }

    private void AppendTurnEnd(TurnEndReason reason, string? detail, TimeSpan elapsed, ModelInfo model)
    {
        switch (reason)
        {
            case TurnEndReason.Completed:
                // The reference leaves nothing behind a finished turn — the live
                // status line simply retires (duration and tokens live in /status
                // and the usage popup).
                break;
            case TurnEndReason.Cancelled:
            case TurnEndReason.AbortedTools:
                // Code carries the CLI's own line; Chat carries the reference's
                // card, which names what happened and offers the two ways out.
                Transcript.Add(IsCodeSurface
                    ? new NoticeItem { Text = "[Request interrupted by user]", IsError = true }
                    : new ChatErrorItem
                    {
                        Text = Services.ChatTurnNotices.Interrupted,
                        Interrupted = true,
                    });
                if (Workers.RunningCount > 0)
                {
                    _stopKillsAgents = true;
                    Transcript.Add(new NoticeItem { Text = "Press stop again to stop background agents" });
                }

                break;
            // The reference ends these two turns with a message of its own and
            // nothing else: the context is past the wall, and the next thing to
            // do is named in the sentence rather than added under it.
            case TurnEndReason.BlockingLimit:
            case TurnEndReason.RapidRefillBreaker:
                Transcript.Add(new NoticeItem
                {
                    Text = detail ?? JarvisCode.Core.Agent.AgentOrchestrator.PromptTooLongMessage,
                    IsError = true,
                });
                break;
            case TurnEndReason.PromptTooLong:
                Transcript.Add(BuildErrorCard(detail, Services.ApiErrorCategory.ContextLength));
                break;
            case TurnEndReason.Error:
            case TurnEndReason.ModelError:
            case TurnEndReason.ImageError:
            case TurnEndReason.TurnSetupFailed:
                Transcript.Add(BuildErrorCard(detail));
                break;
            case TurnEndReason.MalformedToolUseExhausted:
                Transcript.Add(new NoticeItem
                {
                    Text = detail ?? TurnRecovery.MalformedToolUseExhausted,
                    IsError = true,
                });
                break;
            case TurnEndReason.HookStopped:
            case TurnEndReason.StopHookPrevented:
                Transcript.Add(new NoticeItem { Text = detail ?? "A hook ended the turn.", IsError = true });
                break;
            case TurnEndReason.MaxIterationsReached:
                // Only a session that asked for a turn cap can reach this; an
                // interactive session runs uncapped, as the reference's does.
                Transcript.Add(new NoticeItem { Text = detail ?? "Stopped: the turn hit its turn limit." });
                break;
        }
    }

    /// <summary>
    /// The reference's API-error card for whatever the provider said: its own
    /// classifier picks the category, its table supplies the wording, and the
    /// message itself rides along behind "View details".
    /// </summary>
    private ErrorCardItem BuildErrorCard(string? detail, Services.ApiErrorCategory? forced = null)
    {
        var message = detail ?? "";
        var category = forced ?? Services.ErrorCards.Classify(message);
        // Rewinding needs a user message to rewind to.
        var canRewind = Transcript.OfType<UserMessageItem>().Any();
        var presentation = Services.ErrorCards.Describe(category, canRewind);
        return new ErrorCardItem
        {
            Headline = presentation.Headline,
            Hint = presentation.Hint,
            Details = message.Length > 0 ? message : null,
            RequestId = Services.ErrorCards.RequestIdIn(message),
            Category = category,
            RetryHelps = presentation.RetryHelps,
            RewindHelps = presentation.RewindHelps,
            CompactHelps = presentation.CompactHelps,
        };
    }

    private async Task PersistAsync(TurnSetup setup)
    {
        // A cancelled turn still runs its tail. If the session was deleted underneath it,
        // saving here would write the file straight back.
        if (_discarded)
        {
            return;
        }

        Session.UpdatedAt = DateTimeOffset.Now;
        try
        {
            await SaveSessionAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Transcript.Add(new NoticeItem { Text = $"Could not save the session: {ex.Message}", IsError = true });
        }

        try
        {
            _services.UsageStats.Record(
                DateTimeOffset.Now,
                setup.Model.ModelId,
                messages: 1,
                tokens: _turnUsage.TotalInputTokens + _turnUsage.OutputTokens,
                surface: IsCodeSurface ? "code" : "chat",
                inputTokens: _turnUsage.TotalInputTokens,
                outputTokens: _turnUsage.OutputTokens);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Usage stats are best-effort.
        }

        // Windows this turn hid to act on a granted one go back where they were.
        // The reference unhides at turn end behind its chicagoAutoUnhide, and
        // forgets what it hid either way.
        Services.ComputerUseHide.UnhideAll(_services.UiSettings.Current.ComputerUseAutoUnhide);

        if (setup.TurnHooks is not null)
        {
            _lastHooks = setup.TurnHooks;
            try
            {
                await setup.TurnHooks.RunTurnCompletedAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Transcript.Add(new NoticeItem { Text = $"turn-completed hook failed: {ex.Message}", IsError = true });
            }

            // stop hooks may refuse to let the turn end: their feedback goes
            // back to the model and the conversation continues. A standing
            // goal is one of these, judged by a model against the transcript.
            try
            {
                // Background work still running means the transcript is still being
                // written, so the goal is not judged this turn — it defers, and says
                // where things stand at a widening interval.
                var deferred = DeferGoalIfBackgroundWorkIsRunning();
                var stop = await setup.TurnHooks.RunStopHooksAsync(
                    JarvisCode.Core.Hooks.HookEvent.Stop, _stopHookActive, CancellationToken.None,
                    skip: hook => deferred &&
                        hook.Kind == JarvisCode.Core.Hooks.HookKind.Prompt &&
                        hook.Prompt == ActiveGoal?.Condition);
                HandleStopHookOutcome(stop);
                ArmGoalIdleCheckin();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Transcript.Add(new NoticeItem { Text = $"stop hook failed: {ex.Message}", IsError = true });
            }
        }

        SessionPersisted?.Invoke(this, EventArgs.Empty);
    }

    private JarvisCode.Core.Hooks.HookRunner? _lastHooks;
    private bool _sessionStartRan;
    private bool _instructionsLoadedRan;
    private bool _stopHookActive;

    /// <summary>What the eager load found, kept so the hook reports the same set.</summary>
    private JarvisCode.Core.Agent.ProjectInstructions.InstructionSet? _eagerInstructions;

    /// <summary>
    /// Instruction files already in this conversation, eager and nested alike, so
    /// a directory touched twice contributes its instructions once.
    /// </summary>
    private readonly HashSet<string> _loadedInstructionPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files tools touched since the last message. The reference drains exactly
    /// this queue when it composes the next one, which is why a nested
    /// instruction file arrives with the user's next turn rather than mid-turn.
    /// </summary>
    private readonly List<string> _nestedInstructionTriggers = [];

    /// <summary>
    /// Why the eager set is being announced. A compaction re-reads the files and
    /// says so, as the reference does; everything else is the session opening.
    /// </summary>
    private string _nextInstructionLoadReason = "session_start";

    /// <summary>This session's instruction scope: the host's, pointed here.</summary>
    private JarvisCode.Core.Agent.ProjectInstructions.InstructionScope InstructionScope() =>
        JarvisCode.Core.Agent.ProjectInstructions.ScopeFor(
            Session.WorkingDirectory, [.. Session.AdditionalDirectories]);

    /// <summary>
    /// Remembers a file a tool touched, so the next message can carry whatever
    /// instructions govern it.
    /// </summary>
    private void TrackNestedInstructionTrigger(string filePath)
    {
        if (!IsCodeSurface || filePath.Length == 0)
        {
            return;
        }

        lock (_nestedInstructionTriggers)
        {
            if (!_nestedInstructionTriggers.Contains(filePath, StringComparer.OrdinalIgnoreCase))
            {
                _nestedInstructionTriggers.Add(filePath);
            }
        }
    }

    /// <summary>
    /// Loads the instructions the files touched since the last message bring in,
    /// and attaches one reminder per file - the reference's nested_memory
    /// attachment, which carries the path and the body and no tier label.
    /// </summary>
    private async Task<JarvisCode.Core.Models.ChatMessage> AttachNestedInstructionsAsync(
        JarvisCode.Core.Models.ChatMessage message)
    {
        if (!IsCodeSurface)
        {
            return message;
        }

        List<string> triggers;
        lock (_nestedInstructionTriggers)
        {
            if (_nestedInstructionTriggers.Count == 0)
            {
                return message;
            }

            triggers = [.. _nestedInstructionTriggers];
            _nestedInstructionTriggers.Clear();
        }

        var scope = InstructionScope();
        var loaded = _loadedInstructionPaths;
        var found = await Task.Run(() =>
        {
            var results =
                new List<(string Trigger, JarvisCode.Core.Agent.ProjectInstructions.InstructionFile File)>();
            foreach (var trigger in triggers)
            {
                foreach (var file in JarvisCode.Core.Agent.ProjectInstructions.LoadForTouchedFile(
                             scope, trigger, loaded))
                {
                    if (loaded.Add(file.FilePath))
                    {
                        results.Add((trigger, file));
                    }
                }
            }

            return results;
        });

        foreach (var (trigger, file) in found)
        {
            message = SystemReminders.Attach(
                message, SystemReminders.NestedInstructions(file.FilePath, file.Content));
            Transcript.Add(new NoticeItem { Text = $"Loaded {DisplayInstructionPath(file.FilePath)}" });
            if (_lastHooks is { } hooks)
            {
                FireInstructionsLoaded(hooks, file, LoadReasonFor(file, nested: true), trigger);
            }
        }

        return message;
    }

    /// <summary>
    /// The reference's own reason ladder: a rule that matched by its paths:
    /// frontmatter, a file another one imported, then the plain traversal.
    /// </summary>
    private string LoadReasonFor(
        JarvisCode.Core.Agent.ProjectInstructions.InstructionFile file, bool nested) =>
        file.Globs is { Count: > 0 } ? "path_glob_match"
        : file.ParentFilePath is not null ? "include"
        : nested ? "nested_traversal"
        : _nextInstructionLoadReason;

    /// <summary>
    /// The reference's InstructionsLoaded payload: which file, which tier, why it
    /// loaded, and the two paths that explain a conditional or imported one. The
    /// matcher is tested against the reason, not against a tool name.
    /// </summary>
    private void FireInstructionsLoaded(
        JarvisCode.Core.Hooks.HookRunner hooks,
        JarvisCode.Core.Agent.ProjectInstructions.InstructionFile file,
        string reason,
        string? triggerFilePath = null)
    {
        if (!hooks.Has(JarvisCode.Core.Hooks.HookEvent.InstructionsLoaded))
        {
            return;
        }

        var loadReason = file.ParentFilePath is not null ? "include" : reason;
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["file_path"] = file.FilePath,
            ["memory_type"] = file.Type.ToString(),
            ["load_reason"] = loadReason,
        };
        if (file.Globs is { Count: > 0 } globs)
        {
            var array = new System.Text.Json.Nodes.JsonArray();
            foreach (var glob in globs)
            {
                array.Add(glob);
            }

            payload["globs"] = array;
        }

        if (triggerFilePath is { Length: > 0 })
        {
            payload["trigger_file_path"] = triggerFilePath;
        }

        if (file.ParentFilePath is { Length: > 0 } parent)
        {
            payload["parent_file_path"] = parent;
        }

        _ = RunObservationalHookAsync(
            hooks, JarvisCode.Core.Hooks.HookEvent.InstructionsLoaded, payload, loadReason);
    }

    /// <summary>
    /// The reference's warning about an instruction file large enough to cost
    /// real context. Nothing is cut for it: it only says which file to trim.
    /// </summary>
    private void ReportOversizedInstructions(JarvisCode.Core.Agent.ProjectInstructions.InstructionSet loaded)
    {
        int limit = JarvisCode.Core.Agent.ProjectInstructions.LargeFileThreshold(
            _factory.ResolveModel(Session)?.MaxContextTokens ?? 200_000);
        foreach (var file in loaded.Files)
        {
            if (file.Content.Length > limit)
            {
                Transcript.Add(new NoticeItem
                {
                    Text = $"{DisplayInstructionPath(file.FilePath)} is over the {limit:N0}-char limit " +
                           $"({file.Content.Length:N0} chars) - /memory to free up space",
                });
            }
        }
    }

    /// <summary>
    /// Asks, once per project, whether this project's instructions may import
    /// files from outside it — the reference's external-includes dialog, raised
    /// before the files are read and remembered whichever way it is answered, so
    /// a "no" is as final as a "yes". A project with no such import is never
    /// asked.
    /// </summary>
    private async Task EnsureExternalImportsAnsweredAsync()
    {
        if (!IsCodeSurface)
        {
            return;
        }

        var key = Services.InstructionScopes.ProjectKey(Session.WorkingDirectory);
        var answered = _services.UiSettings.Current.ExternalInstructionImportsByProject;
        // One card slot: asking now would strand whatever is already waiting on
        // it, so the question waits for the next message instead.
        if (answered.ContainsKey(key) || PendingQuestion is not null)
        {
            return;
        }

        var scope = InstructionScope();
        var imports = await Task.Run(
            () => JarvisCode.Core.Agent.ProjectInstructions.ExternalImports(scope));
        if (Services.InstructionPrompts.ExternalImports(imports) is not { } question)
        {
            return;
        }

        var answers = await ShowQuestionAsync([question], CancellationToken.None);
        answered[key] = Services.InstructionPrompts.Approved(answers);
        _services.UiSettings.Save();
    }

    /// <summary>
    /// The reference's /memory picker: the instruction files in force, plus the
    /// two that do not exist yet, with the chosen one opened for editing. A file
    /// picked before it exists is created, which is what "(new)" offers.
    /// </summary>
    public async Task ShowMemoryFilesAsync(string autoMemoryDirectory)
    {
        if (PendingQuestion is not null)
        {
            return;
        }

        var loaded = await Task.Run(
            () => Services.SessionInfo.BuildMemoryFiles(Session.WorkingDirectory, _services.Paths.Root));
        // The reference's picker carries this row beside the instruction files.
        var rows = new List<Services.SessionInfo.MemoryFileRow>(loaded)
        {
            new("Open auto-memory folder", autoMemoryDirectory, autoMemoryDirectory, Exists: true),
        };

        var question = new JarvisCode.Core.Tools.BuiltIn.UserQuestion(
            "Which instruction file do you want to edit?",
            "Memory files",
            [.. rows.Select(row => new JarvisCode.Core.Tools.BuiltIn.UserQuestionOption(
                row.Label, row.Description))],
            MultiSelect: false);

        var answers = await ShowQuestionAsync([question], CancellationToken.None);
        if (answers is null)
        {
            return;
        }

        var picked = rows.FirstOrDefault(row =>
            answers.Answers.Values.Any(value => string.Equals(value, row.Label, StringComparison.Ordinal)));
        if (picked is null)
        {
            return;
        }

        try
        {
            if (!picked.Exists || !System.IO.File.Exists(picked.FilePath))
            {
                if (System.IO.Directory.Exists(picked.FilePath))
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(picked.FilePath) { UseShellExecute = true });
                    return;
                }

                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(picked.FilePath) ?? Session.WorkingDirectory);
                System.IO.File.WriteAllText(picked.FilePath, string.Empty);
            }

            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(picked.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            Transcript.Add(new NoticeItem
            {
                Text = $"Could not open {picked.FilePath}: {ex.Message}",
                IsError = true,
            });
        }
    }

    /// <summary>
    /// Re-arms the eager load. A compaction takes the instruction files out of
    /// the live context with the rest of the history, so the next message
    /// carries them again and the hook says the reason was the compaction.
    /// </summary>
    private void MarkInstructionsForReload()
    {
        _instructionsLoadedRan = false;
        _instructionsReloadPending = true;
        _eagerInstructions = null;
        _nextInstructionLoadReason = "compact";
        _loadedInstructionPaths.Clear();
    }

    /// <summary>Set while the eager set is owed to the next message.</summary>
    private bool _instructionsReloadPending;

    /// <summary>A path relative to the session when it is inside it, else in full.</summary>
    private string DisplayInstructionPath(string filePath)
    {
        try
        {
            var relative = System.IO.Path.GetRelativePath(Session.WorkingDirectory, filePath);
            return relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative)
                ? filePath
                : relative;
        }
        catch (ArgumentException)
        {
            return filePath;
        }
    }

    /// <summary>notification hooks — fired when the app pings the user about this session.</summary>
    public void RunNotificationHooks(string message)
    {
        if (_lastHooks is { } hooks && hooks.Has(JarvisCode.Core.Hooks.HookEvent.Notification))
        {
            _ = RunObservationalHookAsync(hooks, JarvisCode.Core.Hooks.HookEvent.Notification,
                new System.Text.Json.Nodes.JsonObject { ["message"] = message });
        }
    }

    /// <summary>session_end hooks — the window runs these while shutting down.</summary>
    public Task RunSessionEndHooksAsync()
    {
        if (_lastHooks is { } hooks && hooks.Has(JarvisCode.Core.Hooks.HookEvent.SessionEnd))
        {
            return RunObservationalHookAsync(hooks, JarvisCode.Core.Hooks.HookEvent.SessionEnd,
                new System.Text.Json.Nodes.JsonObject { ["session_id"] = Session.Id });
        }

        return Task.CompletedTask;
    }

    private async Task RunObservationalHookAsync(
        JarvisCode.Core.Hooks.HookRunner hooks,
        JarvisCode.Core.Hooks.HookEvent hookEvent,
        System.Text.Json.Nodes.JsonObject payload,
        string? matchQuery = null)
    {
        try
        {
            await hooks.RunEventAsync(hookEvent, payload, CancellationToken.None, matchQuery);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = _dispatcher.InvokeAsync(() =>
                Transcript.Add(new NoticeItem { Text = $"{hookEvent} hook failed: {ex.Message}", IsError = true }));
        }
    }

    /// <summary>
    /// Fires an observational hook event if anything subscribes to it. Outside a
    /// turn the definitions load fresh from disk (the turn's runner also carries
    /// plugin hook files, so it wins when available).
    /// </summary>
    public void FireHook(JarvisCode.Core.Hooks.HookEvent hookEvent, System.Text.Json.Nodes.JsonObject payload)
    {
        var hooks = _lastHooks ?? JarvisCode.Core.Hooks.HookRunner.Load(
            Session.WorkingDirectory, _services.Paths.UserHooksFile);
        if (hooks.Has(hookEvent))
        {
            _ = RunObservationalHookAsync(hooks, hookEvent, payload);
        }
    }

    // ---- file_changed: a watcher that exists only while something subscribes ----

    private System.IO.FileSystemWatcher? _fileWatcher;
    private readonly HashSet<string> _touchedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _fileChangedDebounce = new(StringComparer.OrdinalIgnoreCase);

    private void EnsureFileWatcher(JarvisCode.Core.Hooks.HookRunner hooks)
    {
        if (!hooks.Has(JarvisCode.Core.Hooks.HookEvent.FileChanged))
        {
            return;
        }

        if (_fileWatcher is not null &&
            string.Equals(_fileWatcher.Path, Session.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ResetFileWatcher();
        try
        {
            _fileWatcher = new System.IO.FileSystemWatcher(Session.WorkingDirectory)
            {
                IncludeSubdirectories = true,
            };
            _fileWatcher.Changed += OnWatchedFileChanged;
            _fileWatcher.Created += OnWatchedFileChanged;
            _fileWatcher.Deleted += OnWatchedFileChanged;
            _fileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _fileWatcher = null;
        }
    }

    private void ResetFileWatcher()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        _touchedFiles.Clear();
        lock (_fileChangedDebounce)
        {
            _fileChangedDebounce.Clear();
        }
    }

    /// <summary>
    /// Only files this session's tools touched count, and only outside a turn —
    /// changes during one are usually the session's own writes.
    /// </summary>
    private void OnWatchedFileChanged(object sender, System.IO.FileSystemEventArgs e)
    {
        if (IsRunning || _discarded || !_touchedFiles.Contains(e.FullPath))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        lock (_fileChangedDebounce)
        {
            if (_fileChangedDebounce.TryGetValue(e.FullPath, out var last) && now - last < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _fileChangedDebounce[e.FullPath] = now;
        }

        FireHook(JarvisCode.Core.Hooks.HookEvent.FileChanged, new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = e.FullPath,
            ["change"] = e.ChangeType.ToString().ToLowerInvariant(),
        });
    }

    /// <summary>Remembers file paths from tool arguments for the file_changed watcher.</summary>
    /// <summary>
    /// Conditional (paths:) skills wake when a read or edit touches a matching
    /// file (reference behavior); the next message's listing announces them.
    /// </summary>
    private void ActivateConditionalSkills(string toolName, string argumentsJson)
    {
        if (!IsCodeSurface || toolName is not ("Read" or "Edit" or "Write" or "NotebookEdit"))
        {
            return;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(argumentsJson) is not System.Text.Json.Nodes.JsonObject args)
            {
                return;
            }

            var path = JarvisCode.Core.Tools.JsonArgs.GetString(args, "file_path")
                ?? JarvisCode.Core.Tools.JsonArgs.GetString(args, "notebook_path");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // A read - and only a read - wakes the instruction files that
            // govern the file's own directory: the reference pushes that
            // trigger from the Read tool's own body and from nowhere else,
            // while a conditional skill wakes on edits too.
            if (toolName == "Read")
            {
                TrackNestedInstructionTrigger(path);
            }
            if (SkillState.ConditionalCandidates.Count == 0)
            {
                return;
            }

            foreach (var name in Services.SkillCatalog.ActivateConditional(
                SkillState.ConditionalCandidates, SkillState, Session.WorkingDirectory, path))
            {
                JarvisCode.Core.Utilities.DiagnosticLog.Write(
                    $"[skills] Activated conditional skill '{name}' (matched path: {path})");
            }
            SkillState.ConditionalCandidates.RemoveAll(s => SkillState.ActivatedConditionalSkills.Contains(s.Name));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
        }
    }

    private void TrackTouchedFile(string argumentsJson)
    {
        if (_fileWatcher is null)
        {
            return;
        }

        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(argumentsJson) is not System.Text.Json.Nodes.JsonObject args)
            {
                return;
            }

            foreach (var key in (string[])["file_path", "path", "notebook_path"])
            {
                if (args[key] is System.Text.Json.Nodes.JsonValue value &&
                    value.TryGetValue<string>(out var path) &&
                    path.Length > 0)
                {
                    _touchedFiles.Add(System.IO.Path.GetFullPath(path, Session.WorkingDirectory));
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            // Unparseable arguments simply aren't tracked.
        }
    }

    /// <summary>/compact: summarizes older history, keeping the recent tail verbatim.</summary>
    public async Task CompactAsync(string? customInstructions = null)
    {
        if (IsRunning)
        {
            return;
        }

        var model = CurrentModel;
        if (model is null)
        {
            Transcript.Add(new NoticeItem { Text = "No model configured — cannot compact.", IsError = true });
            return;
        }

        if (_lastHooks is { } preCompactHooks &&
            preCompactHooks.Has(JarvisCode.Core.Hooks.HookEvent.PreCompact) &&
            await preCompactHooks.RunPreCompactAsync("manual", Session.Id, CancellationToken.None)
                is { Allowed: false } refusal)
        {
            Transcript.Add(new NoticeItem
            {
                Text = $"Compaction blocked by PreCompact hook: {refusal.BlockReason}",
                IsError = true,
            });
            return;
        }

        var compactor = new ConversationCompactor();
        if (!compactor.CanCompact(Session.Messages))
        {
            Transcript.Add(new NoticeItem { Text = "The conversation is too short to compact." });
            return;
        }

        IsRunning = true;
        TurnStatus.BeginCompacting(DateTimeOffset.Now);
        try
        {
            var provider = _services.Providers.Get(model.ProviderId);
            var result = await compactor.CompactAsync(
                provider, model.ModelId, [.. Session.Messages], CancellationToken.None, customInstructions);
            Session.ArchivedMessages.AddRange(result.Archived);
            Session.Messages.Clear();
            Session.Messages.AddRange(result.Messages);
            _lastContextTokens = 0;
            SkillState.Tracker.MarkAllCompacted();
            SkillState.PendingInvokedSkillsReminder = SkillState.Tracker.HasAny;
            MarkInstructionsForReload();
            LoopDelivery.Reset();
            ResetTranscript();
            RebuildTranscriptFromHistory(Session);
            Transcript.Add(new NoticeItem { Text = $"Conversation compacted — older history was summarized ({FormatTokens(result.UsageSpent.TotalInputTokens + result.UsageSpent.OutputTokens)} tokens spent)." });
            FireHook(JarvisCode.Core.Hooks.HookEvent.PostCompact,
                new System.Text.Json.Nodes.JsonObject { ["trigger"] = "manual" });
            Session.UpdatedAt = DateTimeOffset.Now;
            await SaveSessionAsync();
            SessionPersisted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is JarvisCode.Core.Providers.ProviderException or InvalidOperationException)
        {
            Transcript.Add(new NoticeItem { Text = $"Compaction failed: {ex.Message}", IsError = true });
        }
        finally
        {
            IsRunning = false;
            TurnStatus.EndTurn();
        }
    }

    /// <summary>Reconnects MCP servers for the session's project (code surface only).</summary>
    public void RefreshMcp()
    {
        if (!IsCodeSurface || string.IsNullOrEmpty(Session.WorkingDirectory))
        {
            return;
        }

        var cwd = Session.WorkingDirectory;
        _ = Task.Run(async () =>
        {
            try
            {
                var plugins = Services.PluginLibrary.LoadUser(_services.Paths);
                await _services.Mcp.RefreshAsync(cwd, _services.Paths.UserMcpFile, CancellationToken.None,
                    Services.DesktopExtensions.ConfigFiles(_services.Paths, plugins.McpFiles));
            }
            catch (Exception)
            {
                // MCP problems surface in the Developer panel status, not as a crash.
            }
        });
    }

    /// <summary>
    /// The session behind this view model is gone: stop its turn and make sure the turn's
    /// tail does not save the file back after the delete.
    /// </summary>
    public void Discard()
    {
        if (_discarded) return;
        EndSessionRuntime(renew: false);
        _discarded = true;
        _services.BackgroundTasks.TaskExited -= _taskExitedHandler;
        ResetFileWatcher();
        StopAllLoops();
        _goalIdleTimer?.Dispose();
        _goalIdleTimer = null;
        Services.TurnContextFactory.ForgetSession(Session.Id);
        CancelTurn();
        _factory.Dispose();
    }

    private void EndSessionRuntime(bool renew)
    {
        ResetSummary();
        _incomingWork = false;
        StopPrAutoFix();
        _pendingNotifications.Clear();
        QueuedMessages.Clear();
        _workers?.KillAll();
        _workers = null;
        _tasks = null;
        _teams = null;
        if (_mailbox is { } mailbox) _ = mailbox.DisposeAsync();
        _mailbox = null;
        SessionCronDispatch.Unregister(Session.Id);
        new JarvisCode.Core.Routines.RoutineStore(_services.Paths.RoutinesFile).RemoveOwnedBy(Session.Id);
        IdleSubscriptions.Exit(Session.Id);
        _sessionLifetime.Cancel();
        _sessionLifetime.Dispose();
        if (renew) _sessionLifetime = new CancellationTokenSource();
    }

    public void CancelTurn()
    {
        // A stop with no turn running is the reference's second interrupt: the
        // one that stops the background agents the first press left alone.
        if (_turnCts is null && _stopKillsAgents)
        {
            _stopKillsAgents = false;
            if (Workers.KillAll() is { } stopped)
            {
                Transcript.Add(new NoticeItem { Text = stopped, IsError = true });
            }

            return;
        }

        try
        {
            if (_turnCts is not null)
            {
                TurnStatus.OnStopping();
                _turnCts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        var pending = PendingPermission;
        if (pending is not null)
        {
            pending.Resolve(PermissionDecision.Deny);
            PendingPermission = null;
        }
    }

    // ---- helpers ----

    private T AddItem<T>(T item) where T : TranscriptItem
    {
        // A rebuild reaches here through EnsureGroup; a live turn stamps nothing.
        Rebuilt(item);

        // The turn's action bar stays last, so the turn's own items go above it.
        if (_turnFooter is not null && Transcript.Count > 0 && ReferenceEquals(Transcript[^1], _turnFooter))
        {
            Transcript.Insert(Transcript.Count - 1, item);
        }
        else
        {
            Transcript.Add(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
        return item;
    }

    /// <summary>The action bar under the turn being streamed (Code surface only).</summary>
    private AssistantFooterItem? _turnFooter;

    /// <summary>
    /// Opens the reference's message footer for this turn. Chat keeps its own
    /// response bar, so this is Code-only.
    /// </summary>
    private void BeginTurnFooter(ModelInfo model)
    {
        if (!IsCodeSurface)
        {
            return;
        }

        _turnFooter = new AssistantFooterItem
        {
            ModelLabel = model.DisplayName,
            MessageIndex = Session.Messages.Count,
        };
        Transcript.Add(_turnFooter);
    }

    /// <summary>Settles the footer: the answer it can copy, when it finished, and its place.</summary>
    private void EndTurnFooter(string? answer)
    {
        if (_turnFooter is not { } footer)
        {
            return;
        }

        footer.Text = answer;
        footer.CompletedAt = DateTimeOffset.Now;
        footer.MessageIndex = Session.Messages.Count;
        var at = Transcript.IndexOf(footer);
        if (at >= 0 && at != Transcript.Count - 1)
        {
            // A notice raised mid-turn lands after the bar; put it back on the tail.
            Transcript.Move(at, Transcript.Count - 1);
        }

        _turnFooter = null;
    }

    /// <summary>
    /// Reads a resource out of the window's in-process MCP shell. The widget row
    /// loads its runtime page through this, which is what the reference's own
    /// host does with the tool's <c>_meta.ui.resourceUri</c>.
    /// </summary>
    public Func<string, Services.InternalMcpResourceContents?>? ReadInternalMcpResource { get; set; }

    /// <summary>
    /// Dev/verification hook (--open=widget): the transcript row a show_widget
    /// call would leave, built from arguments nothing streamed.
    /// </summary>
    public void ShowSampleWidget(string title, string widgetCode)
    {
        Transcript.Add(NewWidget(
            "sample",
            Services.VisualizeWidgetCalls.ShowWidgetWireName,
            new System.Text.Json.Nodes.JsonObject
            {
                ["title"] = title,
                ["widget_code"] = widgetCode,
                ["loading_messages"] = new System.Text.Json.Nodes.JsonArray("Counting the deploys"),
            }.ToJsonString()));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>The transcript row one show_widget call renders as.</summary>
    private WidgetItem NewWidget(string callId, string toolName, string? argumentsJson)
    {
        var call = Services.VisualizeWidgetCalls.Parse(argumentsJson);
        return new WidgetItem
        {
            CallId = callId,
            WireName = toolName,
            ServerName = Services.InternalMcpServerNames.Visualize,
            ToolName = Services.InternalMcpServers.ShortName(toolName),
            Title = call.Title,
            WidgetCode = call.WidgetCode,
            LoadingMessages = call.LoadingMessages,
            ResourceReader = ReadInternalMcpResource,
        };
    }

    /// <summary>
    /// The run a call joins. The reference's lQ opens a new one whenever the
    /// bucket changes, and a spawn_task is the one bucket this build has beside
    /// the default: its run is counted by what became of the chips, so a call of
    /// another kind may not sit in it.
    /// </summary>
    private void EnsureGroup(string toolName)
    {
        if (_currentGroup is { Calls.Count: > 0 } open &&
            open.IsSpawnTaskRun != Services.ToolGroupSummary.IsSpawnTask(toolName))
        {
            open.RefreshTitle();
            _currentGroup = null;
        }

        if (_currentGroup is null)
        {
            _currentGroup = new ToolGroupItem
            {
                ThinkingRecap = FirstLine(_currentThinking?.Text ?? _lastThinkingText),
                ShowRecap = ShowThinkingRecaps,
                ForceExpanded = ForceExpandGroups,
                MemoryDirectory = SessionMemoryDirectory(),
                TaskStarted = TaskStartedASession,
            };
            AddItem(_currentGroup);
            _turnFooter?.Track(_currentGroup);
        }
    }

    /// <summary>
    /// Whether a chip this session suggested went on to start a session. The
    /// reference asks its session store, which records where each session came
    /// from, so the answer survives the chip being evicted and the transcript
    /// being reopened; this asks the same question of the same kind of store.
    /// </summary>
    private bool TaskStartedASession(string taskId) =>
        _services.SessionGroups.WasSpawned(Session.Id, taskId);

    /// <summary>
    /// Re-reads every run's shape and sentence. Starting a chip changes what a
    /// spawn_task row in this transcript says, and that row is already on screen.
    /// </summary>
    public void RefreshToolRuns()
    {
        foreach (var group in Transcript.OfType<ToolGroupItem>())
        {
            group.RefreshTitle();
        }
    }

    /// <summary>
    /// This session's memory folder, which the tool-group header reads so a write
    /// into it says "saved a memory". Null while memory is paused, which is what
    /// the reference does with a session that has no memory path.
    /// </summary>
    private string? SessionMemoryDirectory() => MemoryPaused
        ? null
        : JarvisCode.Core.Memory.ProjectMemory.DirectoryFor(
            _services.Paths.MemoryRoot, Session.WorkingDirectory);

    /// <summary>The surface flips this with the transcript view; new groups inherit it.</summary>
    public bool ShowThinkingRecaps { get; set; }

    /// <summary>Verbose view holds tool groups open; new groups inherit it too.</summary>
    public bool ForceExpandGroups { get; set; }

    private static string? FirstLine(string? thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking))
        {
            return null;
        }

        var line = thinking.TrimStart().Split('\n', 2)[0].Trim();
        return line.Length > 140 ? line[..140] + "…" : line.Length > 0 ? line : null;
    }

    private void OnTodosChanged(IReadOnlyList<TodoItem> todos)
    {
        _dispatcher.InvokeAsync(() =>
        {
            Todos.Clear();
            foreach (var todo in todos)
            {
                Todos.Add(todo);
            }
        });
    }

    private void OnSubagentActivity(string callId, string activity)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (_callsById.TryGetValue(callId, out var call))
            {
                call.Progress = activity;
            }
        });
    }

    /// <summary>An inner event of a running subagent — folds into its row's nested transcript.</summary>
    private void OnSubagentEvent(string callId, JarvisCode.Core.Agent.AgentEvent agentEvent)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (_callsById.TryGetValue(callId, out var call))
            {
                call.AppendSubagentEvent(agentEvent);
            }
        });
    }

    /// <summary>A background shell task exited; settle the row that started it.</summary>
    private void OnBackgroundTaskExited(JarvisCode.Core.BackgroundTasks.BackgroundTaskInfo info)
    {
        var row = _callsById.Values.FirstOrDefault(c => c.BackgroundTaskId == info.Id);
        if (row is null)
        {
            return;
        }

        row.FinishBackground(
            killed: info.Status == JarvisCode.Core.BackgroundTasks.BackgroundTaskStatus.Killed,
            info.ExitCode);
        foreach (var group in Transcript.OfType<ToolGroupItem>())
        {
            if (group.AllCalls.Contains(row))
            {
                group.RefreshTitle();
                break;
            }
        }
    }

    private Task<PermissionDecision> ShowPermissionPromptAsync(PermissionPrompt prompt, CancellationToken cancellationToken)
    {
        var vm = new PermissionPromptViewModel(prompt);
        _dispatcher.InvokeAsync(() =>
        {
            ShowAwaitingApprovalRow(prompt);
            PendingPermission = vm;
            AttentionRequested?.Invoke(this, EventArgs.Empty);
        });
        cancellationToken.Register(() => vm.Resolve(PermissionDecision.Deny));
        return vm.Task.ContinueWith(
            t =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(PendingPermission, vm))
                    {
                        PendingPermission = null;
                    }
                });
                return t.Result;
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// The reference's awaiting_approval row: the call shows in its group while
    /// the permission card is up. Started (approval) flips it to running in place;
    /// denial lands through ToolExecutionDenied. Prompts raised by a subagent's
    /// inner call get no top-level row — their row appears in the parent agent
    /// row's nested transcript once approved.
    /// </summary>
    private void ShowAwaitingApprovalRow(PermissionPrompt prompt)
    {
        if (prompt.CallId is not { } callId || _callsById.ContainsKey(callId) ||
            _callsById.Values.Any(static c => c.ToolName == "Agent" && c.IsRunning))
        {
            return;
        }

        EnsureGroup(prompt.ToolName);
        var item = new ToolCallItem
        {
            CallId = callId,
            ToolName = prompt.ToolName,
            Description = prompt.CallDescription ?? prompt.ToolName,
            ArgumentsJson = prompt.ArgumentsJson,
            IsRunning = false,
            IsAwaitingApproval = true,
        };
        _currentGroup!.AddCall(item);
        _callsById[callId] = item;
        _currentGroup.RefreshTitle();
    }

    private Task<JarvisCode.Core.Tools.BuiltIn.UserQuestionAnswers?> ShowQuestionAsync(
        IReadOnlyList<JarvisCode.Core.Tools.BuiltIn.UserQuestion> questions, CancellationToken cancellationToken)
    {
        var vm = new QuestionPromptViewModel(questions);
        _dispatcher.InvokeAsync(() =>
        {
            PendingQuestion = vm;
            AttentionRequested?.Invoke(this, EventArgs.Empty);
        });
        cancellationToken.Register(vm.Cancel);
        return vm.Task.ContinueWith(
            t =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(PendingQuestion, vm))
                    {
                        PendingQuestion = null;
                    }
                });
                return t.Result;
            },
            TaskScheduler.Default);
    }

    private Task<JarvisCode.Core.Tools.BuiltIn.PlanApprovalDecision> ShowPlanApprovalAsync(
        string planMarkdown, CancellationToken cancellationToken)
    {
        var vm = new PlanApprovalViewModel(planMarkdown);
        _dispatcher.InvokeAsync(() =>
        {
            PendingPlanApproval = vm;
            AttentionRequested?.Invoke(this, EventArgs.Empty);
        });
        cancellationToken.Register(vm.Cancel);
        return vm.Task.ContinueWith(
            t =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(PendingPlanApproval, vm))
                    {
                        PendingPlanApproval = null;
                    }

                    if (t.Result.Approved && Gate.Mode == PermissionMode.Plan)
                    {
                        // The approval IS the exit from plan mode; the live
                        // PlanModeProvider frees the tools for the rest of the turn.
                        // The session returns to the mode it was in before planning,
                        // as the reference returns to its prePlanMode.
                        Gate.ExitPlanMode();
                        PermissionModeChanged?.Invoke(this, EventArgs.Empty);
                    }
                });
                return t.Result;
            },
            TaskScheduler.Default);
    }

    private void UpdateContextUsage(Usage lastCall, ModelInfo model)
    {
        var used = lastCall.TotalInputTokens + lastCall.OutputTokens;
        if (used > 0 && model.MaxContextTokens > 0)
        {
            ContextUsageText = $"{(lastCall.IsEstimated ? "~" : "")}{FormatTokens(used)} / {FormatTokens(model.MaxContextTokens)}";
        }
    }

    /// <summary>
    /// What the compacted conversation costs, by the same char/4 estimate the
    /// context pill uses. The reference reads the real number from its own
    /// accounting; here the first call after the compaction is what would carry
    /// it, and the row is drawn before that call is made.
    /// </summary>
    private static long EstimateContextTokens(IReadOnlyList<JarvisCode.Core.Models.ChatMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            foreach (var block in message.Content)
            {
                total += block switch
                {
                    JarvisCode.Core.Models.TextBlock text =>
                        Services.ContextBreakdown.EstimateTokens(text.Text),
                    JarvisCode.Core.Models.ToolCallBlock call =>
                        Services.ContextBreakdown.EstimateTokens(call.Name) +
                        Services.ContextBreakdown.EstimateTokens(call.ArgumentsJson),
                    JarvisCode.Core.Models.ToolResultBlock result =>
                        Services.ContextBreakdown.EstimateTokens(result.Content),
                    JarvisCode.Core.Models.ThinkingBlock thinking =>
                        Services.ContextBreakdown.EstimateTokens(thinking.Thinking),
                    _ => 0,
                };
            }
        }

        return total;
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString(),
    };

    private static string Cap(string text)
        => text.Length <= 20_000 ? text : text[..20_000] + "\n… (truncated in view)";

    private static string MakeTitle(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 48 ? line : line[..48].TrimEnd() + "…";
    }

    private string DefaultWorkingDirectory()
        => _services.Settings.Current.LastWorkingDirectory is { Length: > 0 } last && System.IO.Directory.Exists(last)
            ? last
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
