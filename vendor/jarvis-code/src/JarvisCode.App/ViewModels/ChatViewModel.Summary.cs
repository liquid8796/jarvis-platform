using System.Windows.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.ViewModels;

public sealed class SessionSummaryItem : TranscriptItem
{
    private string _markdown = "";
    private string _status = "";
    public string Markdown { get => _markdown; set => SetProperty(ref _markdown, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    private System.Windows.Input.ICommand? _refreshCommand;
    public System.Windows.Input.ICommand? RefreshCommand { get => _refreshCommand; set => SetProperty(ref _refreshCommand, value); }
}

public sealed partial class ChatViewModel
{
    private readonly SessionSummaryItem _summaryItem = new();
    private DispatcherTimer? _summaryTimer;
    private CancellationTokenSource? _summaryCancellation;
    private bool _summaryVisible;
    private bool _summaryBusy;
    private string? _lastSummaryAttempt;

    public void SetLiveSummaryVisible(bool visible)
    {
        _summaryVisible = visible;
        if (!visible) { StopSummary(); return; }
        if (!Transcript.Contains(_summaryItem)) Transcript.Insert(0, _summaryItem);
        _summaryItem.RefreshCommand ??= new Infrastructure.RelayCommand(() => _ = RefreshLiveSummaryAsync(force: true));
        if (new SessionSummaryStore(_services.Paths.Root, Session.Id).Load() is { } cached) ShowSummary(cached);
        _summaryTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            (_, _) => _ = RefreshLiveSummaryAsync(), _dispatcher);
        _summaryTimer.Start();
        _ = RefreshLiveSummaryAsync();
    }

    public async Task RefreshLiveSummaryAsync(bool force = false)
    {
        if (!_summaryVisible || _summaryBusy || _discarded) return;
        if (CurrentModel is not { } model) { _summaryItem.Status = "Choose a model to generate a summary."; return; }
        var id = Session.Id;
        var messages = Session.Messages.ToArray();
        if (messages.Length == 0) { _summaryItem.Status = "No messages to summarize yet."; return; }
        var attempt = SessionSummaryStore.Source(messages).Hash + "|" + model.ProviderId + "|" + model.ModelId;
        if (!force && _lastSummaryAttempt == attempt) return;
        _lastSummaryAttempt = attempt;
        _summaryBusy = true;
        _summaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionLifetime.Token);
        _summaryItem.Status = "Updating summary…";
        try
        {
            // Obtain the provider from the same registry as the coding turn, so
            // host metering and provider traffic capture cover this call too.
            var summary = await new SessionSummaryStore(_services.Paths.Root, id).GenerateAsync(
                _services.Providers.Get(model.ProviderId), model.ModelId, messages, _summaryCancellation.Token, force);
            if (_summaryVisible && Session.Id == id && !_discarded)
            {
                if (!Transcript.Contains(_summaryItem)) Transcript.Insert(0, _summaryItem);
                ShowSummary(summary);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_summaryVisible && Session.Id == id) _summaryItem.Status = "Summary could not be updated: " + ex.Message;
        }
        finally
        {
            var cancelled = _summaryCancellation.IsCancellationRequested;
            _summaryCancellation.Dispose(); _summaryCancellation = null; _summaryBusy = false;
            if (cancelled) _lastSummaryAttempt = null;
            if (_summaryVisible && (cancelled || id != Session.Id)) _ = _dispatcher.InvokeAsync(() => _ = RefreshLiveSummaryAsync());
        }
    }

    private void ShowSummary(GeneratedSessionSummary summary)
    {
        _summaryItem.Markdown = summary.Markdown;
        _summaryItem.Status = $"{summary.ModelId} · {summary.SourceMessages} messages{(summary.Excerpted ? " (excerpts)" : "")} · {summary.UpdatedAt:yyyy-MM-dd HH:mm}" +
            (summary.StopReason == "max_tokens" ? " · output truncated" : "");
    }

    private void StopSummary()
    {
        _summaryTimer?.Stop(); _summaryCancellation?.Cancel();
    }

    private void ResetSummary()
    {
        _summaryVisible = false; StopSummary(); _lastSummaryAttempt = null;
        _summaryItem.Markdown = ""; _summaryItem.Status = "";
    }
}
