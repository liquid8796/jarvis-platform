using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// The integrated terminal tile: a real ConPTY-backed shell with a scrollback
/// view, a line input, Ctrl+C, and restart-in-directory.
/// </summary>
public sealed class TerminalView : UserControl, IDisposable
{
    private readonly TextBox _output;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _throttle;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private ConPtyTerminal? _pty;
    private TerminalScreen _screen = new();
    private string _workingDirectory = "";
    private bool _dirty;

    /// <summary>The selected slice of terminal output, for Ctrl+Shift+L attach-as-context.</summary>
    public string? SelectedOutputText => _output.SelectedText is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The whole screen as plain text, already ANSI-stripped by
    /// <see cref="TerminalScreen"/>. This is what mcp__terminal__read_terminal
    /// reads; it is deliberately the screen model rather than the TextBox, so a
    /// read is correct even before the throttle has repainted.
    /// </summary>
    public string ScreenText => _screen.GetText();

    /// <summary>Raised on every chunk the shell produces, so a read can wait for fresh output.</summary>
    public event Action? OutputChanged;

    /// <summary>Raised when the shell ends, so the pane can title the tab accordingly.</summary>
    public event Action? Exited;

    /// <summary>
    /// The reference's floating "Ask about this" over a terminal selection: the selected
    /// output becomes a context chip on the composer.
    /// </summary>
    public event Action<string>? AskAboutRequested;

    /// <summary>Whether the shell has ended — the pane draws the exit banner over it.</summary>
    public bool HasExited { get; private set; }

    public TerminalView()
    {
        Focusable = false;

        _output = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12,
            Padding = new Thickness(12, 6, 12, 6),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            AcceptsReturn = true,
        };
        _output.SetResourceReference(ForegroundProperty, "Text200Brush");
        _output.SetResourceReference(TextBoxBase.SelectionBrushProperty, "SelectionBrush");
        _output.SetResourceReference(FontFamilyProperty, "MonoFontFamily");

        _input = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12.5,
            Padding = new Thickness(6, 5, 6, 5),
        };
        _input.SetResourceReference(ForegroundProperty, "Text100Brush");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "Text100Brush");
        _input.SetResourceReference(FontFamilyProperty, "MonoFontFamily");
        _input.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Terminal input");
        _input.PreviewKeyDown += OnInputKey;

        var prompt = new TextBlock
        {
            Text = ">",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        prompt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrandBrush");
        prompt.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");

        var interrupt = MakeButton("Ctrl+C", () => _pty?.Interrupt());
        var restart = MakeButton("Restart", () => Start(_workingDirectory));

        _status = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        var inputRow = new Grid { Margin = new Thickness(0, 2, 6, 6) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition());
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(prompt);
        Grid.SetColumn(_input, 1);
        inputRow.Children.Add(_input);
        Grid.SetColumn(_status, 2);
        inputRow.Children.Add(_status);
        Grid.SetColumn(interrupt, 3);
        inputRow.Children.Add(interrupt);
        Grid.SetColumn(restart, 4);
        inputRow.Children.Add(restart);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_output);
        root.Children.Add(BuildExitBanner());
        root.Children.Add(BuildAskAbout());
        var divider = new Border { Height = 1 };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        var bottom = new StackPanel();
        bottom.Children.Add(divider);
        bottom.Children.Add(inputRow);
        Grid.SetRow(bottom, 1);
        root.Children.Add(bottom);
        Content = root;

        _throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _throttle.Tick += (_, _) => FlushOutput();
        _throttle.Start();
    }

    private Border? _exitBanner;
    private TextBlock? _exitText;
    private Button? _askAbout;

    /// <summary>
    /// The reference's exit overlay: "Shell exited." — or "Shell disconnected: {reason}"
    /// when it knows one — over a "Restart shell" button, centred on the terminal.
    /// </summary>
    private Border BuildExitBanner()
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _exitText = new TextBlock
        {
            Text = "Shell exited.",
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _exitText.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
        stack.Children.Add(_exitText);

        var restart = new Button
        {
            Content = "Restart shell",
            FontSize = 11.5,
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        restart.SetResourceReference(StyleProperty, "GhostButton");
        restart.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Restart shell");
        restart.Click += (_, _) => Start(_workingDirectory);
        stack.Children.Add(restart);

        _exitBanner = new Border { Visibility = Visibility.Collapsed, Child = stack };
        _exitBanner.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        return _exitBanner;
    }

    /// <summary>The reference's floating "Ask about this", shown while output is selected.</summary>
    private Button BuildAskAbout()
    {
        _askAbout = new Button
        {
            Content = "Ask about this",
            FontSize = 11,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 14, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };
        _askAbout.SetResourceReference(StyleProperty, "GhostButton");
        _askAbout.Click += (_, _) =>
        {
            if (SelectedOutputText is { } text)
            {
                AskAboutRequested?.Invoke(text);
            }
        };
        _output.SelectionChanged += (_, _) =>
            _askAbout.Visibility = SelectedOutputText is null ? Visibility.Collapsed : Visibility.Visible;
        return _askAbout;
    }

    /// <summary>Shows the exit overlay; a reason spells the reference's second sentence.</summary>
    public void ShowExitBanner(string? reason)
    {
        if (_exitBanner is null || _exitText is null)
        {
            return;
        }

        _exitText.Text = reason is { Length: > 0 }
            ? $"Shell disconnected: {reason}"
            : "Shell exited.";
        _exitBanner.Visibility = Visibility.Visible;
    }

    private Button MakeButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>(Re)starts the shell in the given directory.</summary>
    public void Start(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        _pty?.Dispose();
        _screen = new TerminalScreen();
        _screen.Changed += () =>
        {
            _dirty = true;
            OutputChanged?.Invoke();
        };
        _input.IsEnabled = true;
        _status.Text = "";
        HasExited = false;
        if (_exitBanner is not null)
        {
            _exitBanner.Visibility = Visibility.Collapsed;
        }

        _pty = new ConPtyTerminal();
        _pty.OutputReceived += chunk => _screen.Append(chunk);
        var terminal = _pty;
        _pty.Exited += () => Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_pty, terminal))
            {
                _status.Text = "exited";
                _input.IsEnabled = false;
                HasExited = true;
                ShowExitBanner(null);
                Exited?.Invoke();
            }
        });

        try
        {
            var shell = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            _pty.Start($"\"{shell}\"", workingDirectory);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _screen.Append($"Could not start the shell: {ex.Message}\r\n");
            _input.IsEnabled = false;
        }

        _dirty = true;
        FlushOutput();
        _input.Focus();
    }

    public bool IsStarted => _pty is not null;

    public string WorkingDirectory => _workingDirectory;

    /// <summary>
    /// Types a command into the shell and runs it, which is what the Run button
    /// on a shell-tagged fence does. Trailing newlines are stripped so a fence
    /// that ends with one does not submit twice; the command is not otherwise
    /// inspected, because the user chose to run it.
    /// </summary>
    public void Send(string command)
    {
        if (_pty is null)
        {
            return;
        }

        _pty.Write(command.TrimEnd('\r', '\n') + "\r");
    }

    /// <summary>
    /// Types a command at the prompt without running it, so the user reads it
    /// before pressing Enter -- the reference's "Open in terminal".
    /// </summary>
    public void Type(string command)
    {
        _pty?.Write(command.TrimEnd('\r', '\n'));
    }

    private void FlushOutput()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var pinned = _output.VerticalOffset >= _output.ExtentHeight - _output.ViewportHeight - 24;
        _output.Text = _screen.GetText();
        if (pinned)
        {
            _output.ScrollToEnd();
        }
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                var line = _input.Text;
                _input.Clear();
                if (line.Length > 0)
                {
                    _history.Add(line);
                }

                _historyIndex = _history.Count;
                _pty?.Write(line + "\r\n");
                break;
            case Key.Up when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Max(0, _historyIndex - 1);
                _input.Text = _history[_historyIndex];
                _input.CaretIndex = _input.Text.Length;
                break;
            case Key.Down when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                _input.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
                _input.CaretIndex = _input.Text.Length;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _input.Text.Length == 0:
                e.Handled = true;
                _pty?.Interrupt();
                break;
        }
    }

    public void Dispose()
    {
        _throttle.Stop();
        _pty?.Dispose();
        _pty = null;
    }
}
