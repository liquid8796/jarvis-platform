using System.Windows;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

public partial class BrowserPanel
{
    internal sealed class BrowserContext(string key, string partition)
    {
        public string Key { get; } = key;
        public string Partition { get; } = partition;
        public List<PaneTab> Tabs { get; } = [];
        public PaneTab? Active { get; set; }
        public ElectronPaneSession? Session { get; set; }
        public Task<ElectronPaneSession?>? Initializing { get; set; }
        public ElectronPaneView? View { get; set; }
        public int NextTabId { get; set; }
        public int PreviewPort { get; set; }
    }

    private readonly string _unscopedSessionId = Guid.NewGuid().ToString("N");
    private string? _codeSessionId;
    private BrowserContext _context = new("unconfigured", "");
    private readonly Dictionary<string, BrowserContext> _browserContexts = new(StringComparer.Ordinal);

    private void SelectBrowserContext()
    {
        if (string.IsNullOrEmpty(_workingDirectory)) return;
        var sessionId = _codeSessionId ?? _unscopedSessionId;
        var mode = BrowserStorageProfile.Mode(_settings?.Current);
        var partition = mode == "shared"
            ? BrowserStorageProfile.SharedPartition(_userDataFolder, _workingDirectory)
            : BrowserStorageProfile.Partition(mode, _workingDirectory, sessionId);
        var key = sessionId + "\n" + partition;
        if (_context.Key == key) return;

        _hiddenReapTimer.Stop();
        if (_context.View is { } previous) previous.Visibility = Visibility.Collapsed;

        if (!_browserContexts.TryGetValue(key, out var next))
        {
            next = new BrowserContext(key, partition);
            _browserContexts.Add(key, next);
        }

        _context = next;
        Tabs.Items.Clear();
        foreach (var tab in _tabs) Tabs.Items.Add(tab.Button);
        if (_tabs.Count == 0) AddTab();
        if (_paneView is { } view) view.Visibility = Visibility.Visible;
        _serverStateShowing = false;
        AddressBox.Text = _active?.Url ?? "";
        UpdateChrome();
        ArmHiddenReap();
    }

    private void OnBrowserSettingsSaved(object? sender, EventArgs args)
    {
        SelectBrowserContext();
        ResyncColorSchemes();
    }
}
