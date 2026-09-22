using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Jarvis.Agent.Desktop.ViewModels;
using Jarvis.Agent.Windows;
using Forms = System.Windows.Forms;
namespace Jarvis.Agent.Desktop;
public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private readonly Forms.NotifyIcon _tray;
    private readonly System.Drawing.Icon _trayIcon;
    private bool _exiting;
    private HwndSource? _source;
    private const int PauseHotkey = 9126;
    public MainWindow() : this(null, true) { }
    public MainWindow(string? settingsRoot, bool loadProfile)
    {
        InitializeComponent();
        PreviewMouseWheel += HandleContainerMouseWheel;
        _model = new MainViewModel(this, settingsRoot, loadProfile); DataContext = _model;
        if (_model.InitialProfile is { } profile) try { TokenBox.Password = AgentProfile.GetToken(profile); } catch (Exception) { }
        _trayIcon = LoadTrayIcon();
        _tray = new Forms.NotifyIcon { Text = "Jarvis Agent — local tool permissions", Icon = _trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Jarvis Agent", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Pause remote control", null, (_, _) => Dispatcher.Invoke(_model.Pause));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); _source?.AddHook(WindowMessage);
            if (!RegisterHotKey(new WindowInteropHelper(this).Handle, PauseHotkey, 0x4000 | 0x0002 | 0x0001, 0x13))
                _tray.ShowBalloonTip(4000, "Pause hotkey unavailable", "Another application owns Ctrl+Alt+Pause. Use the tray's Pause command.", Forms.ToolTipIcon.Warning);
        };
    }
    private void HandleContainerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var scrollViewer = FindParent<ScrollViewer>(e.OriginalSource as DependencyObject);
        if (scrollViewer is null) return;
        var canScroll = e.Delta < 0 ? scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight : scrollViewer.VerticalOffset > 0;
        if (!canScroll) return;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T result) return result;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Jarvis.Agent.Desktop;component/Assets/JarvisAgent.ico"))
            ?? throw new InvalidOperationException("The embedded Jarvis Agent icon is missing.");
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        // The clone owns its handle after the resource stream and temporary icon are disposed.
        return (System.Drawing.Icon)icon.Clone();
    }
    private void TokenChanged(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel model) model.Token = TokenBox.Password; }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    { if (message == 0x0312 && wParam.ToInt32() == PauseHotkey) { _model.Pause(); handled = true; } return IntPtr.Zero; }
    protected override void OnClosing(CancelEventArgs e) { if (!_exiting) { e.Cancel = true; Hide(); } base.OnClosing(e); }
    private async Task ExitAsync()
    {
        if (_exiting) return; _exiting = true;
        await _model.DisposeAsync();
        UnregisterHotKey(new WindowInteropHelper(this).Handle, PauseHotkey); _source?.RemoveHook(WindowMessage);
        _tray.Visible = false; _tray.Dispose(); _trayIcon.Dispose(); Close(); Application.Current?.Shutdown();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
