using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>
/// The terminal side pane: the reference's tab strip (ion-dist chunk c360a9e1c-DUoNQd2W.js,
/// its BM) over real ConPTY views. Tabs are named "Terminal {n}" as soon as there are two,
/// carry Rename terminal / Close terminal / Close other terminals on their own menu, close
/// on Delete or Backspace while focused, and fold into a "More terminals" overflow once
/// the strip is full.
/// </summary>
public partial class TerminalPanel : UserControl
{
    /// <summary>One terminal: its tab, its view, the ordinal it opened with and its name.</summary>
    private sealed class Shell
    {
        public required ToggleButton Tab { get; init; }
        public required TerminalView View { get; init; }
        public required int Ordinal { get; init; }
        public string? Name { get; set; }
    }

    /// <summary>How many tabs the strip shows before the rest fold into the overflow.</summary>
    private const int MaxVisibleTabs = 4;

    private readonly List<Shell> _shells = [];
    private string _workingDirectory = "";
    private int _nextOrdinal = 1;

    public TerminalPanel()
    {
        InitializeComponent();
    }

    /// <summary>The reference's floating "Ask about this": the selection reaches the composer.</summary>
    public event EventHandler<string>? AskAboutRequested;

    /// <summary>Starts the first shell, or re-points a later one at a new project.</summary>
    public void Start(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        if (_shells.Count == 0)
        {
            AddShell();
            return;
        }

        foreach (var shell in _shells)
        {
            if (!shell.View.IsStarted)
            {
                shell.View.Start(_workingDirectory);
            }
        }
    }

    /// <summary>
    /// Runs a command in the visible shell, starting one if the panel has none
    /// yet. Returns false when there is no shell to run it in, so the caller
    /// can leave the user where they were instead of claiming it ran.
    /// </summary>
    public bool Send(string command) => Dispatch(command, run: true);

    /// <summary>
    /// Types a command into the visible shell without running it. Same contract
    /// as <see cref="Send"/>, including the false return when there is no shell.
    /// </summary>
    public bool Show(string command) => Dispatch(command, run: false);

    private bool Dispatch(string command, bool run)
    {
        if (_shells.Count == 0)
        {
            if (_workingDirectory.Length == 0)
            {
                return false;
            }

            AddShell();
        }

        foreach (var shell in _shells)
        {
            if (shell.View.Visibility == Visibility.Visible)
            {
                if (!shell.View.IsStarted)
                {
                    shell.View.Start(_workingDirectory);
                }

                if (run)
                {
                    shell.View.Send(command);
                }
                else
                {
                    shell.View.Type(command);
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>The visible shell's selected output, if any.</summary>
    public string? ActiveSelection
    {
        get
        {
            foreach (var shell in _shells)
            {
                if (shell.View.Visibility == Visibility.Visible && shell.View.SelectedOutputText is { } text)
                {
                    return text;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// One tab's screen text for mcp__terminal__read_terminal. Tabs are numbered
    /// from 1, matching the <c>tab:N</c> sentinel the reference writes into an
    /// attached terminal snippet; a null or unparsable id reads the visible tab.
    /// Returns null when the panel has no shell at all.
    /// </summary>
    public string? ReadTab(string? tabId)
    {
        if (_shells.Count == 0)
        {
            return null;
        }

        return Resolve(tabId)?.ScreenText;
    }

    /// <summary>How many shells the panel has open, for read_terminal's tab list.</summary>
    public int ShellCount => _shells.Count;

    /// <summary>
    /// Waits for the named tab to produce output, up to <paramref name="timeout"/>.
    /// A timeout is not an error: the reference notes it and still reads the
    /// buffer. A missing shell is the third outcome, and answers differently.
    /// </summary>
    public async Task<TerminalWaitOutcome> WaitForOutputAsync(
        string? tabId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Resolve(tabId) is not { } view)
        {
            return TerminalWaitOutcome.NoShell;
        }

        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput() => arrived.TrySetResult(true);
        view.OutputChanged += OnOutput;
        try
        {
            using var timer = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken);
            await using var registration = linked.Token.Register(() => arrived.TrySetResult(false));
            return await arrived.Task
                ? TerminalWaitOutcome.Output
                : TerminalWaitOutcome.TimedOut;
        }
        finally
        {
            view.OutputChanged -= OnOutput;
        }
    }

    /// <summary>The tab a read addresses: the 1-based id when given, else the visible one.</summary>
    private TerminalView? Resolve(string? tabId)
    {
        if (!string.IsNullOrWhiteSpace(tabId) &&
            int.TryParse(tabId.Trim(), out var index) &&
            index >= 1 && index <= _shells.Count)
        {
            return _shells[index - 1].View;
        }

        foreach (var shell in _shells)
        {
            if (shell.View.Visibility == Visibility.Visible)
            {
                return shell.View;
            }
        }

        return _shells.Count > 0 ? _shells[0].View : null;
    }

    public void DisposeShells()
    {
        foreach (var shell in _shells)
        {
            shell.View.Dispose();
        }

        _shells.Clear();
        Tabs.Items.Clear();
        TerminalHost.Children.Clear();
        _nextOrdinal = 1;
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e) => AddShell();

    private void AddShell()
    {
        var view = new TerminalView();
        view.AskAboutRequested += text => AskAboutRequested?.Invoke(this, text);
        TerminalHost.Children.Add(view);

        var tab = new ToggleButton();
        tab.SetResourceReference(StyleProperty, "PanelTab");
        var shell = new Shell { Tab = tab, View = view, Ordinal = _nextOrdinal++ };
        tab.Click += (_, _) => Select(view);
        tab.PreviewKeyDown += (_, args) => OnTabKey(shell, args);
        tab.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            ShowTabMenu(shell);
        };
        tab.ToolTip = TerminalTabs.CloseTabDeleteHint;
        Tabs.Items.Add(tab);

        _shells.Add(shell);
        RelabelTabs();
        Select(view);
        if (_workingDirectory.Length > 0)
        {
            view.Start(_workingDirectory);
        }
    }

    /// <summary>
    /// The reference relabels the whole strip when its length changes: one tab reads
    /// "Terminal", two or more read "Terminal {n}" unless the user named them.
    /// </summary>
    private void RelabelTabs()
    {
        foreach (var shell in _shells)
        {
            var label = TerminalTabs.Label(shell.Name, shell.Ordinal, _shells.Count);
            shell.Tab.Content = label;
            shell.Tab.SetValue(AutomationProperties.NameProperty, label);
        }

        MoreTabsButton.Visibility = _shells.Count > MaxVisibleTabs ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _shells.Count; i++)
        {
            _shells[i].Tab.Visibility = i < MaxVisibleTabs ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Delete or Backspace on a focused tab closes it, as the reference's hint says.</summary>
    private void OnTabKey(Shell shell, KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back)
        {
            e.Handled = true;
            CloseShell(shell);
        }
    }

    private void ShowTabMenu(Shell shell)
    {
        var menu = new ContextMenu { PlacementTarget = shell.Tab, MinWidth = 180 };
        var rename = new MenuItem { Header = TerminalTabs.RenameTab };
        rename.Click += (_, _) => RenameShell(shell);
        menu.Items.Add(rename);

        var close = new MenuItem { Header = TerminalTabs.CloseTab };
        close.Click += (_, _) => CloseShell(shell);
        menu.Items.Add(close);

        if (_shells.Count > 1)
        {
            var others = new MenuItem { Header = TerminalTabs.CloseOtherTabs };
            others.Click += (_, _) => CloseOthers(shell);
            menu.Items.Add(others);
        }

        menu.IsOpen = true;
    }

    private void RenameShell(Shell shell)
    {
        var current = shell.Name ?? TerminalTabs.Label(null, shell.Ordinal, _shells.Count);
        if (InputDialog.Prompt(Window.GetWindow(this), TerminalTabs.RenameTab, current, "Rename") is not { } name)
        {
            return;
        }

        shell.Name = name.Trim().Length == 0 ? null : name.Trim();
        RelabelTabs();
    }

    private void CloseShell(Shell shell)
    {
        var wasActive = shell.Tab.IsChecked == true;
        shell.View.Dispose();
        TerminalHost.Children.Remove(shell.View);
        Tabs.Items.Remove(shell.Tab);
        _shells.Remove(shell);
        RelabelTabs();
        if (_shells.Count > 0 && wasActive)
        {
            Select(_shells[0].View);
        }
    }

    private void CloseOthers(Shell keep)
    {
        foreach (var shell in _shells.Where(s => !ReferenceEquals(s, keep)).ToList())
        {
            shell.View.Dispose();
            TerminalHost.Children.Remove(shell.View);
            Tabs.Items.Remove(shell.Tab);
            _shells.Remove(shell);
        }

        RelabelTabs();
        Select(keep.View);
    }

    /// <summary>"More terminals": the tabs the strip could not show.</summary>
    private void OnMoreTabsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreTabsButton, MinWidth = 180 };
        foreach (var shell in _shells.Skip(MaxVisibleTabs))
        {
            var item = new MenuItem
            {
                Header = TerminalTabs.Label(shell.Name, shell.Ordinal, _shells.Count),
                IsCheckable = true,
                IsChecked = shell.Tab.IsChecked == true,
            };
            item.Click += (_, _) => Select(shell.View);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void Select(TerminalView view)
    {
        foreach (var shell in _shells)
        {
            var isActive = ReferenceEquals(shell.View, view);
            shell.Tab.IsChecked = isActive;
            shell.View.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }

        view.Focus();
    }
}
