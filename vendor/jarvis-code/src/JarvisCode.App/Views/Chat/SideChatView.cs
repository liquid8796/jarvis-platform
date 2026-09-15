using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Composition;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Views.Chat;

/// <summary>
/// The Chat surface's side chat: a question answered beside the conversation
/// without joining it. Ported from the reference's own panel — a header saying how
/// much of the main chat it can see and that it is read-only, an empty state that
/// says what it is for, right-aligned questions with markdown answers under a
/// hover-revealed Copy answer / Branch to new chat pair, and its own composer.
/// </summary>
public sealed class SideChatView : UserControl
{
    private readonly TextBlock _seesText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _lines = new() { Margin = new Thickness(10, 10, 10, 10) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly TextBlock _empty;
    private readonly TextBlock _thinking;
    private readonly List<SideChatLine> _history = [];

    private AppServices? _services;
    private Func<Session>? _mainSession;
    private CancellationTokenSource? _turn;
    private bool _pinned = true;

    public SideChatView()
    {
        var header = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _seesText.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
        header.Children.Add(_seesText);
        var readOnly = new TextBlock
        {
            Text = SideChatStrings.ReadOnly,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        readOnly.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        Grid.SetColumn(readOnly, 1);
        header.Children.Add(readOnly);

        var headerBorder = new Border
        {
            Child = header,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        headerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");

        _empty = new TextBlock
        {
            Text = SideChatStrings.EmptyState,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 24, 16, 24),
        };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        _thinking = new TextBlock
        {
            Text = SideChatStrings.Thinking,
            FontSize = 12,
            Margin = new Thickness(4, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _thinking.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(
            _thinking, System.Windows.Automation.AutomationLiveSetting.Polite);

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _empty, _lines, _thinking } },
        };
        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0)
            {
                _pinned = _scroll.ExtentHeight - _scroll.VerticalOffset - _scroll.ViewportHeight
                    < SideChatStrings.PinThreshold;
            }
        };

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 13,
            MinHeight = 20,
            MaxHeight = 120,
            Padding = new Thickness(8, 6, 6, 6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(_input, SideChatStrings.Placeholder);
        _input.SetResourceReference(ForegroundProperty, "Text100Brush");
        _input.PreviewKeyDown += OnInputKeyDown;

        var placeholder = Controls.PlaceholderText.Overlay(_input, SideChatStrings.Placeholder);

        var send = new Button
        {
                        Content = new TextBlock
            {
                Text = "",
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFontFamily"],
                FontSize = 12,
            },
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 4, 4),
            Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(send, SideChatStrings.Send);
        send.Click += (_, _) => Ask();

        var composer = new Grid();
        composer.ColumnDefinitions.Add(new ColumnDefinition());
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.Children.Add(_input);
        composer.Children.Add(placeholder);
        Grid.SetColumn(send, 1);
        composer.Children.Add(send);

        var composerBorder = new Border
        {
            Child = composer,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10, 6, 10, 10),
        };
        composerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        composerBorder.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(headerBorder);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        Grid.SetRow(composerBorder, 2);
        root.Children.Add(composerBorder);
        Content = root;
    }

    /// <summary>A side answer was branched into a chat of its own.</summary>
    public event EventHandler<IReadOnlyList<SideChatLine>>? BranchRequested;

    /// <summary>Binds the panel to the conversation it reads and the services it runs on.</summary>
    public void Initialize(AppServices services, Func<Session> mainSession)
    {
        _services = services;
        _mainSession = mainSession;
        Refresh();
    }

    /// <summary>Re-reads how much of the main chat the header should claim.</summary>
    public void Refresh()
    {
        var count = _mainSession?.Invoke().Messages.Count ?? 0;
        _seesText.Text = SideChatStrings.SeesMessages(count);
    }

    public void FocusInput() => _input.Focus();

    /// <summary>Clears the side conversation, leaving the main one untouched.</summary>
    public void Clear()
    {
        _turn?.Cancel();
        _history.Clear();
        _lines.Children.Clear();
        _empty.Visibility = Visibility.Visible;
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Ask();
        }
    }

    private void Ask()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _services is null || _mainSession is null || _turn is not null)
        {
            return;
        }

        _input.Clear();
        AddLine(new SideChatLine(FromUser: true, text));
        _ = AnswerAsync(text);
    }

    private async Task AnswerAsync(string question)
    {
        var services = _services!;
        var main = _mainSession!();

        // Read-only over the conversation, which is what Core's side chat already
        // models: the main transcript rides the system prompt as an excerpt, the
        // side questions ride the messages, and nothing here reaches the main
        // session at all.
        var session = Session.CreateNew(main.WorkingDirectory);
        session.ModelId = main.ModelId;
        session.Messages.AddRange(_history.Select(line => line.FromUser
            ? JarvisCode.Core.Models.ChatMessage.FromUserText(line.Text)
            : new JarvisCode.Core.Models.ChatMessage(
                JarvisCode.Core.Models.Role.Assistant,
                [new JarvisCode.Core.Models.TextBlock(line.Text)])));
        session.Messages.Add(JarvisCode.Core.Models.ChatMessage.FromUserText(question));

        var gate = new UiPermissionGate { WorkingDirectory = session.WorkingDirectory };
        if (new TurnContextFactory(services).CreateForChat(session, gate) is not { } setup)
        {
            AddLine(new SideChatLine(false, "No model is configured — add one in Settings › Providers."));
            return;
        }

        setup = setup with
        {
            Context = setup.Context with
            {
                SystemPrompt = JarvisCode.Core.Agent.SideChat.BuildSystemPrompt(
                    main.WorkingDirectory,
                    JarvisCode.Core.Agent.SideChat.BuildContextExcerpt(main.Messages)),
            },
        };

        _turn = new CancellationTokenSource();
        _thinking.Visibility = Visibility.Visible;
        var answer = new System.Text.StringBuilder();
        MarkdownView? view = null;
        try
        {
            await foreach (var evt in services.Orchestrator.RunTurnAsync(setup.Context, _turn.Token))
            {
                if (evt is AssistantTextDelta delta)
                {
                    answer.Append(delta.Delta);
                    view ??= AddAnswer();
                    view.Markdown = answer.ToString();
                    ScrollIfPinned();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled side question leaves whatever it had said.
        }
        finally
        {
            _thinking.Visibility = Visibility.Collapsed;
            _turn?.Dispose();
            _turn = null;
        }

        if (answer.Length > 0)
        {
            _history.Add(new SideChatLine(false, answer.ToString()));
        }
    }

    private void AddLine(SideChatLine line)
    {
        _history.Add(line);
        _empty.Visibility = Visibility.Collapsed;

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 300,
            Child = new TextBlock { Text = line.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "UserMessageBgBrush");
        _lines.Children.Add(bubble);
        ScrollIfPinned();
    }

    /// <summary>Opens an answer block with the reference's two hover actions under it.</summary>
    private MarkdownView AddAnswer()
    {
        _empty.Visibility = Visibility.Collapsed;
        var view = new MarkdownView { Profile = MarkdownProfile.Chat, Margin = new Thickness(2, 0, 2, 2) };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 0, 0, 10),
            Opacity = 0,
        };
        var index = _history.Count;
        actions.Children.Add(ActionButton("", SideChatStrings.CopyAnswer, () =>
        {
            if (index < _history.Count)
            {
                Clipboard.SetText(_history[index].Text);
            }
        }));
        actions.Children.Add(ActionButton("", SideChatStrings.BranchToNewChat, () =>
            BranchRequested?.Invoke(this, SideChatStrings.Branch(_history, index))));

        var group = new StackPanel { Children = { view, actions } };
        group.MouseEnter += (_, _) => actions.Opacity = 1;
        group.MouseLeave += (_, _) => actions.Opacity = 0;
        _lines.Children.Add(group);
        return view;
    }

    private static Button ActionButton(string glyph, string name, Action run)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFontFamily"],
                FontSize = 11,
            },
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 2, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = name,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => run();
        return button;
    }

    private void ScrollIfPinned()
    {
        if (_pinned)
        {
            _scroll.ScrollToEnd();
        }
    }
}
