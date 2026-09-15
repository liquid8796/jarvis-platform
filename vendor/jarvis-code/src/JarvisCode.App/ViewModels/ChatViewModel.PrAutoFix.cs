using System.IO;
using System.Windows.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.ViewModels;

public sealed partial class ChatViewModel
{
    private PrAutoFixMonitor? _prAutoFixMonitor;
    private FileStream? _prAutoFixLease;
    private long _prAutoFixGeneration;
    internal Func<PrAutoFixBinding, CancellationToken, Task<System.Text.Json.Nodes.JsonObject?>>? PrSnapshotReader { get; set; }
    public bool HasPrAutoFixMonitor => _prAutoFixMonitor is not null;
    public PrAutoFixBinding? PrAutoFix => _services.UiSettings.Current.SessionPrAutoFix.GetValueOrDefault(Session.Id);

    public void ConfigurePrAutoFix(PrAutoFixBinding binding)
    {
        if (!binding.IsValid || !Path.GetFullPath(binding.WorkingDirectory).Equals(
                Path.GetFullPath(Session.WorkingDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The pull request must be bound to this session's working directory.");
        if (binding.AutoFix && CurrentModel is null)
            throw new InvalidOperationException("Choose a model for this session before enabling Auto-fix.");
        _services.UiSettings.Current.SessionPrAutoFix[Session.Id] = binding;
        _services.UiSettings.Save();
        StopPrAutoFix();
        if (!binding.AutoFix)
            _pendingNotifications.RemoveAll(message => message.StartsWith("<ci-monitor-event>", StringComparison.Ordinal));
        RestorePrAutoFix();
        OnPropertyChanged(nameof(PrAutoFix));
    }

    private void RestorePrAutoFix()
    {
        if (!IsCodeSurface || _discarded || _prAutoFixMonitor is not null ||
            PrAutoFix is not { IsValid: true } binding || (!binding.AutoFix && !binding.AutoArchive)) return;
        if (!Path.GetFullPath(binding.WorkingDirectory).Equals(Path.GetFullPath(Session.WorkingDirectory), StringComparison.OrdinalIgnoreCase)) return;
        var sessionId = Session.Id;
        var generation = _prAutoFixGeneration;
        var directory = Path.Combine(_services.Paths.Root, "pr-monitors");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId)));
        try { _prAutoFixLease = File.Open(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; } // Another surface/process already owns this session's watch.
        _prAutoFixMonitor = new PrAutoFixMonitor(binding, notification =>
            _ = _dispatcher.InvokeAsync(() =>
            {
                if (_discarded || Session.Id != sessionId || PrAutoFix != binding || generation != _prAutoFixGeneration) return;
                if (notification.Kind is "merged" or "closed" or "paused")
                {
                    _services.UiSettings.Current.SessionPrAutoFix[sessionId] = binding with { AutoFix = false, AutoArchive = false };
                    _services.UiSettings.Save();
                    Transcript.Add(new NoticeItem { Text = notification.Summary, IsError = notification.Kind == "paused" });
                    if (binding.AutoArchive && notification.Kind is "merged" or "closed")
                        _services.SessionGroups.SetArchived(sessionId, true);
                    StopPrAutoFix();
                    return;
                }
                if (!binding.AutoFix) return;
                // This entry point, not an XML string found in tool output,
                // creates a genuine standalone event from the desktop host.
                Transcript.Add(new NoticeItem { Text = notification.Summary });
                _pendingNotifications.Add(notification.Render());
                DrainQueuedMessages();
            }, DispatcherPriority.Background), PrSnapshotReader);
        _prAutoFixMonitor.Start();
        Gate.PrAutoFixActive = binding.AutoFix;
    }

    private void StopPrAutoFix()
    {
        _prAutoFixGeneration++;
        var previous = _prAutoFixMonitor;
        _prAutoFixMonitor = null;
        Gate.PrAutoFixActive = false;
        if (previous is not null) _ = previous.DisposeAsync();
        _prAutoFixLease?.Dispose();
        _prAutoFixLease = null;
    }
}
