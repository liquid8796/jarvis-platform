using System.ComponentModel;
using JarvisCode.App.Composition;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.ViewModels;

/// <summary>
/// One view model per session on a surface, kept alive while its turn runs.
///
/// A turn belongs to the view model that started it — the cancellation token, the
/// streaming transcript items and the half-built tool group all live there. Pointing a
/// single view model at another conversation therefore had to cancel the turn and throw
/// the answer away, which is exactly what selecting another session used to do. Holding
/// the view models here instead means switching only rebinds the view: the hidden turn
/// keeps streaming into its own transcript and is still there on the way back.
/// </summary>
public sealed class SessionViewModels(AppServices services, bool isCodeSurface)
{
    private readonly Dictionary<string, ChatViewModel> _byId = new(StringComparer.Ordinal);

    /// <summary>Any view model saved its session — including one nobody is looking at.</summary>
    public event EventHandler? SessionPersisted;

    /// <summary>A turn started or ended anywhere, so the sidebar's spinners are stale.</summary>
    public event EventHandler? RunningChanged;

    /// <summary>A turn finished on any view model, shown or not.</summary>
    public event Action<ChatViewModel>? TurnFinished;

    /// <summary>A view model is waiting on the user (permission, question, plan).</summary>
    public event Action<ChatViewModel>? AttentionRequested;

    /// <summary>The sessions with a turn in flight, on screen or not.</summary>
    public IReadOnlyCollection<string> RunningSessionIds =>
        [.. _byId.Values.Where(vm => vm.IsRunning).Select(vm => vm.Session.Id)];

    /// <summary>
    /// The view model for an existing session. A session that is already open wins over
    /// the copy the caller loaded from disk: the live one holds the running turn, and the
    /// file on disk is a turn behind until it finishes.
    /// </summary>
    public ChatViewModel ForSession(Session session)
    {
        if (_byId.TryGetValue(session.Id, out var open))
        {
            return open;
        }

        var viewModel = Track(new ChatViewModel(services, isCodeSurface));
        viewModel.LoadSession(session);
        _byId[session.Id] = viewModel;
        return viewModel;
    }

    public ChatViewModel ForNewSession(string? workingDirectory)
    {
        var viewModel = Track(new ChatViewModel(services, isCodeSurface));
        viewModel.NewSession(workingDirectory);
        _byId[viewModel.Session.Id] = viewModel;
        return viewModel;
    }

    /// <summary>Stops and drops a session's turn — used when the session itself is gone.</summary>
    public void Forget(string sessionId)
    {
        // The request inspector's override outlives the view model, so it goes too.
        Services.RequestOverrides.Forget(sessionId);
        if (_byId.Remove(sessionId, out var viewModel))
        {
            viewModel.Discard();
            Untrack(viewModel);
        }
    }

    /// <summary>
    /// Drops everything that has nothing left to do — anything not on screen and not
    /// streaming — so browsing a long session list does not pile view models up.
    /// </summary>
    public void EvictIdle(ChatViewModel keep)
    {
        foreach (var (id, viewModel) in _byId.ToList())
        {
            if (!ReferenceEquals(viewModel, keep) && !viewModel.HasSessionBackgroundWork && !viewModel.HasPrAutoFixMonitor)
            {
                _byId.Remove(id);
                viewModel.Discard();
                Untrack(viewModel);
            }
        }
    }

    private ChatViewModel Track(ChatViewModel viewModel)
    {
        viewModel.SessionPersisted += OnSessionPersisted;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TurnFinished += OnTurnFinished;
        viewModel.AttentionRequested += OnAttentionRequested;
        return viewModel;
    }

    private void Untrack(ChatViewModel viewModel)
    {
        viewModel.SessionPersisted -= OnSessionPersisted;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.TurnFinished -= OnTurnFinished;
        viewModel.AttentionRequested -= OnAttentionRequested;
    }

    private void OnTurnFinished(object? sender, EventArgs e)
    {
        if (sender is ChatViewModel viewModel)
        {
            TurnFinished?.Invoke(viewModel);
        }
    }

    private void OnAttentionRequested(object? sender, EventArgs e)
    {
        if (sender is ChatViewModel viewModel)
        {
            AttentionRequested?.Invoke(viewModel);
        }
    }

    private void OnSessionPersisted(object? sender, EventArgs e) => SessionPersisted?.Invoke(this, EventArgs.Empty);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsRunning))
        {
            RunningChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
