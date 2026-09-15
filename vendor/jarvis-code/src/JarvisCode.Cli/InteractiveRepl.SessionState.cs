using System.Diagnostics;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Input;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Core.Models;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Sessions;
using JarvisCode.Core.Tools;

namespace JarvisCode.Cli;

internal sealed partial class InteractiveRepl
{
    private sealed class SessionState(Session session)
    {
        public Session Session { get; } = session;
        public object Sync { get; } = new();
        public Composer _composer = null!;
        public HistoryNavigator _navigator = null!;
        public HistorySearch? _search;
        public Autocomplete? _completion;
        public List<string> _queued = [];
        public CliTurnRunner _runner = null!;
        public UiPermissionGate _gate = null!;
        public ModelInfo _model = null!;
        public CancellationTokenSource? _turnCts;
        public Task? _turn;
        public string? _pendingNotice;
        public string? _sessionGoal;
        public bool _memoryPaused;
        public string? _suggestedPrompt;
        public PermissionPromptDialog? _permission;
        public TaskCompletionSource<PermissionDecision>? _permissionAnswer;
        public PlanApproval? _plan;
        public TaskCompletionSource<Core.Tools.BuiltIn.PlanApprovalDecision>? _planAnswer;
        public AskUserQuestionDialog? _question;
        public TaskCompletionSource<Core.Tools.BuiltIn.UserQuestionAnswers?>? _questionAnswer;
        public ResumePicker? _resume;
        public ChoiceDialog? _choice;
        public Func<ChoiceDialog, string, CancellationToken, Task>? _choiceAccepted;
        public Stopwatch _turnClock = new();
        public string _verb = SpinnerVerbs.Fallback;
        public TurnPhase _phase = TurnPhase.Requesting;
        public long _turnTokens;
        public bool _thinking;
        public string _answerPreview = "";
        public bool _compacting;
        public JarvisCode.Core.Agent.AgentWorkerManager _workers = new();
        public CancellationTokenSource? _sessionLifetime;
        public CliPendingWork? _sessionPending;
        public CliSessionCrons? _sessionCrons;
        public LocalSessionMailbox? _sessionMailbox;
        public string? _runtimeSessionId;
        public bool _dontAsk;
        public Stopwatch _wallClock = Stopwatch.StartNew();
        public TimeSpan _apiDuration;
        public long _sessionInputTokens;
        public long _sessionOutputTokens;
        public long _sessionCacheReadInputTokens;
        public long _sessionCacheCreationInputTokens;
        public bool _sessionUsageIsEstimated;
        public int _linesAdded;
        public int _linesRemoved;
        public Action<Core.Agent.WorkerInfo, ToolResult>? _workerFinished;
        public System.Collections.Concurrent.ConcurrentQueue<ChatMessage> _notifications = new();
        public PrAutoFixMonitor? _prMonitor;
        public ScrollableDocument? Document;
        public DiffViewer? Diff;
        public bool ShowAllTranscript;
    }

    private readonly Dictionary<string, SessionState> _sessionTabs = new(StringComparer.Ordinal);
    private readonly AsyncLocal<SessionState?> _executingSession = new();
    private SessionState _selectedSession = null!;
    private SessionState State => _executingSession.Value ?? _selectedSession;
    private bool _showSessionStrip = true;
    private void StopBrowserTurn()
    {
        try { _browserOwner?._turnCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _browserOwner?._workers.KillAll();
    }
    internal IReadOnlyList<Session> OpenSessions => [.. _sessionTabs.Values.Select(state => state.Session)];
    internal string ActiveSessionId => _selectedSession.Session.Id;
    private Session _session
    {
        get => State.Session;
        set
        {
            if (!_sessionTabs.TryGetValue(value.Id, out var state))
            {
                state = new SessionState(value);
                _sessionTabs.Add(value.Id, state);
                _selectedSession = state;
                state._composer = new Composer(vimEnabled: !options.SafeMode && services.App.UiSettings.Current.EditorMode == "vim");
                state._navigator = new HistoryNavigator(_history, value.WorkingDirectory, value.Id);
                state._gate = BuildGate(value.WorkingDirectory);
                state._model = services.Factory.ResolveModel(value)!;
            }
            else _selectedSession = state;
        }
    }
    private Composer _composer { get => State._composer; set => State._composer = value; }
    private HistoryNavigator _navigator { get => State._navigator; set => State._navigator = value; }
    private HistorySearch? _search { get => State._search; set => State._search = value; }
    private Autocomplete? _completion { get => State._completion; set => State._completion = value; }
    private List<string> _queued { get => State._queued; }
    private CliTurnRunner _runner { get => State._runner; set => State._runner = value; }
    private UiPermissionGate _gate { get => State._gate; set => State._gate = value; }
    private ModelInfo _model { get => State._model; set => State._model = value; }
    private CancellationTokenSource? _turnCts { get => State._turnCts; set => State._turnCts = value; }
    private Task? _turn { get => State._turn; set => State._turn = value; }
    private string? _pendingNotice { get => State._pendingNotice; set => State._pendingNotice = value; }
    private string? _sessionGoal { get => State._sessionGoal; set => State._sessionGoal = value; }
    private bool _memoryPaused { get => State._memoryPaused; set => State._memoryPaused = value; }
    private string? _suggestedPrompt { get => State._suggestedPrompt; set => State._suggestedPrompt = value; }
    private PermissionPromptDialog? _permission { get => State._permission; set => State._permission = value; }
    private TaskCompletionSource<PermissionDecision>? _permissionAnswer { get => State._permissionAnswer; set => State._permissionAnswer = value; }
    private PlanApproval? _plan { get => State._plan; set => State._plan = value; }
    private TaskCompletionSource<Core.Tools.BuiltIn.PlanApprovalDecision>? _planAnswer { get => State._planAnswer; set => State._planAnswer = value; }
    private AskUserQuestionDialog? _question { get => State._question; set => State._question = value; }
    private TaskCompletionSource<Core.Tools.BuiltIn.UserQuestionAnswers?>? _questionAnswer { get => State._questionAnswer; set => State._questionAnswer = value; }
    private ResumePicker? _resume { get => State._resume; set => State._resume = value; }
    private ChoiceDialog? _choice { get => State._choice; set => State._choice = value; }
    private Func<ChoiceDialog, string, CancellationToken, Task>? _choiceAccepted { get => State._choiceAccepted; set => State._choiceAccepted = value; }
    private Stopwatch _turnClock { get => State._turnClock; }
    private string _verb { get => State._verb; set => State._verb = value; }
    private TurnPhase _phase { get => State._phase; set => State._phase = value; }
    private long _turnTokens { get => State._turnTokens; set => State._turnTokens = value; }
    private bool _thinking { get => State._thinking; set => State._thinking = value; }
    private string _answerPreview { get => State._answerPreview; set => State._answerPreview = value; }
    private bool _compacting { get => State._compacting; set => State._compacting = value; }
    private JarvisCode.Core.Agent.AgentWorkerManager _workers { get => State._workers; set => State._workers = value; }
    private CancellationTokenSource? _sessionLifetime { get => State._sessionLifetime; set => State._sessionLifetime = value; }
    private CliPendingWork? _sessionPending { get => State._sessionPending; set => State._sessionPending = value; }
    private CliSessionCrons? _sessionCrons { get => State._sessionCrons; set => State._sessionCrons = value; }
    private LocalSessionMailbox? _sessionMailbox { get => State._sessionMailbox; set => State._sessionMailbox = value; }
    private string? _runtimeSessionId { get => State._runtimeSessionId; set => State._runtimeSessionId = value; }
    private bool _dontAsk { get => State._dontAsk; set => State._dontAsk = value; }
    private Stopwatch _wallClock { get => State._wallClock; }
    private TimeSpan _apiDuration { get => State._apiDuration; set => State._apiDuration = value; }
    private long _sessionInputTokens { get => State._sessionInputTokens; set => State._sessionInputTokens = value; }
    private long _sessionOutputTokens { get => State._sessionOutputTokens; set => State._sessionOutputTokens = value; }
    private long _sessionCacheReadInputTokens { get => State._sessionCacheReadInputTokens; set => State._sessionCacheReadInputTokens = value; }
    private long _sessionCacheCreationInputTokens { get => State._sessionCacheCreationInputTokens; set => State._sessionCacheCreationInputTokens = value; }
    private bool _sessionUsageIsEstimated { get => State._sessionUsageIsEstimated; set => State._sessionUsageIsEstimated = value; }
    private int _linesAdded { get => State._linesAdded; set => State._linesAdded = value; }
    private int _linesRemoved { get => State._linesRemoved; set => State._linesRemoved = value; }
    private Action<Core.Agent.WorkerInfo, ToolResult>? _workerFinished { get => State._workerFinished; set => State._workerFinished = value; }
    private System.Collections.Concurrent.ConcurrentQueue<ChatMessage> _notifications { get => State._notifications; }
    private PrAutoFixMonitor? _prMonitor { get => State._prMonitor; set => State._prMonitor = value; }

    private void InSession(SessionState state, Action action)
    {
        var previous = _executingSession.Value;
        _executingSession.Value = state;
        try { action(); }
        finally { _executingSession.Value = previous; }
    }

    private IReadOnlyList<string> SessionStrip() => !_showSessionStrip ? [] :
        [string.Join("  ", _sessionTabs.Values.Select((state, index) =>
            (ReferenceEquals(state, _selectedSession) ? "[" : " ") + (index + 1) + " " +
            state.Session.Title + (state._turn is { IsCompleted: false } ? " •" : "") +
            (state._permission is not null || state._question is not null || state._plan is not null ? " ?" : "") +
            (ReferenceEquals(state, _selectedSession) ? "]" : " ")))];

    private Task SwitchTabAsync(int index, CancellationToken cancellationToken)
    {
        var rows = _sessionTabs.Values.ToArray();
        if (index < 0 || index >= rows.Length) return Task.CompletedTask;
        _selectedSession = rows[index];
        _screen.ClearAll();
        ReplayTranscript();
        DeliverNotifications();
        return Task.CompletedTask;
    }
}
