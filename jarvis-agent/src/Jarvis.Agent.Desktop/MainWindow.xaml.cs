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
    private const int WmMouseWheel = 0x020A;
    public MainWindow() : this(null, true) { }
    public MainWindow(string? settingsRoot, bool loadProfile)
    {
        InitializeComponent();
        AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(HandleContainerMouseWheel), true);
        _model = new MainViewModel(this, settingsRoot, loadProfile); DataContext = _model;
        if (_model.InitialProfile is { } profile) try { TokenBox.Password = AgentProfile.GetToken(profile); } catch (Exception) { }
        _trayIcon = LoadTrayIcon();
        _tray = new Forms.NotifyIcon { Text = "Jarvis Agent â€” local tool permissions", Icon = _trayIcon, Visible = true };
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
    private void ForwardInputMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // PasswordBox/TextBox templates can stop WPF wheel routing. Forward directly to the page owner.
        var scrollViewer = ConnectionScrollViewer;
        if (scrollViewer.ScrollableHeight <= 0) return;

        var nextOffset = scrollViewer.VerticalOffset - (e.Delta / 3.0);
        nextOffset = Math.Clamp(nextOffset, 0, scrollViewer.ScrollableHeight);
        if (Math.Abs(nextOffset - scrollViewer.VerticalOffset) < 0.1) return;

        scrollViewer.ScrollToVerticalOffset(nextOffset);
        e.Handled = true;
    }

    private void HandleContainerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject ?? Mouse.DirectlyOver as DependencyObject;
        var scrollViewer = FindParent<ScrollViewer>(source) ?? FindScrollViewerUnderMouse() ?? ConnectionScrollViewer;
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0) return;

        var nextOffset = scrollViewer.VerticalOffset - (e.Delta / 3.0);
        nextOffset = Math.Clamp(nextOffset, 0, scrollViewer.ScrollableHeight);
        if (Math.Abs(nextOffset - scrollViewer.VerticalOffset) < 0.1) return;

        scrollViewer.ScrollToVerticalOffset(nextOffset);
        e.Handled = true;
    }

    private ScrollViewer? FindScrollViewerUnderMouse()
    {
        var point = Mouse.GetPosition(this);
        var hit = InputHitTest(point) as DependencyObject;
        return FindParent<ScrollViewer>(hit);
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindChildren<T>(child)) yield return descendant;
        }
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
    {
        if (message == WmMouseWheel && TryHandleNativeMouseWheel(wParam))
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (message == 0x0312 && wParam.ToInt32() == PauseHotkey)
        {
            _model.Pause();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private bool TryHandleNativeMouseWheel(IntPtr wParam)
    {
        var delta = (short)((wParam.ToInt64() >> 16) & 0xffff);
        if (delta == 0) return false;
        var viewer = FindParent<ScrollViewer>(Mouse.DirectlyOver as DependencyObject);
        if (viewer is null || viewer.ScrollableHeight <= 0) return false;
        var next = Math.Clamp(viewer.VerticalOffset - (delta / 3.0), 0, viewer.ScrollableHeight);
        if (Math.Abs(next - viewer.VerticalOffset) < 0.1) return false;
        viewer.ScrollToVerticalOffset(next);
        return true;
    }
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

