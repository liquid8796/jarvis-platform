using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views;

/// <summary>
/// The command palette, rebuilt against the reference desktop's own
/// <c>CommandPaletteBody</c> (<c>c2771e1f6-Czf-iSjS.js</c>): a Quick actions
/// group over the recent sessions by default, Tab into the grouped command list,
/// Backspace on an empty query back out of it, and a footer that shows only
/// while nothing has been typed.
/// </summary>
public sealed class CommandPaletteView : UserControl
{
    public event EventHandler? CloseRequested;

    private readonly TextBox _input;
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroll;
    private readonly Border _footer;
    private readonly TextBlock _placeholder;

    private IReadOnlyList<PaletteAction> _actions = [];
    private IReadOnlyList<PaletteRow> _quickActions = [];
    private IReadOnlyList<PaletteRow> _recents = [];
    private IReadOnlyList<PaletteGroupView> _groups = [];
    private IReadOnlyList<PaletteRow> _rows = [];
    private PaletteMode _mode = PaletteMode.Default;
    private int _selection;

    public CommandPaletteView()
    {
        Focusable = false;

        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) };
        backdrop.MouseLeftButtonDown += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _input = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 14.5,
            // The reference's own header padding: 24 left, 18 top, 14 bottom, 10 right.
            Padding = new Thickness(24, 18, 10, 14),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _input.SetResourceReference(ForegroundProperty, "Text100Brush");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "Text100Brush");
        _input.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "SelectionBrush");
        _input.TextChanged += (_, _) =>
        {
            _selection = 0;
            Rebuild();
        };
        _input.PreviewKeyDown += OnInputKey;

        var close = new Button
        {
            Content = new TextBlock { Text = "\uE8BB", FontSize = 10 },
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.SetResourceReference(StyleProperty, "IconButton");
        close.SetResourceReference(FontFamilyProperty, "IconFontFamily");
        close.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, CommandPalette.CloseLabel);
        close.ToolTip = CommandPalette.CloseLabel;
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        // WPF has no placeholder, so the reference's prompt is drawn over the field
        // and hidden the moment anything is typed. It takes its inset and its font
        // from the field itself, so it starts where the caret does.
        _placeholder = Controls.PlaceholderText.Overlay(_input, CommandPalette.DefaultPlaceholder);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_input);
        header.Children.Add(_placeholder);
        Grid.SetColumn(_placeholder, 0);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        // The reference's hairline under the header.
        var divider = new Border { Height = 0.5 };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderMidBrush");

        _list = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = CommandPalette.MaxResultsHeight,
            Padding = new Thickness(10),
        };

        _footer = BuildFooter();

        var cardContent = new StackPanel();
        cardContent.Children.Add(header);
        cardContent.Children.Add(divider);
        cardContent.Children.Add(_scroll);
        cardContent.Children.Add(_footer);

        var card = new Border
        {
            Child = cardContent,
            Width = 580,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 110, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 4,
                Opacity = 0.32,
                Color = Colors.Black,
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;

        var root = new Grid();
        root.Children.Add(backdrop);
        root.Children.Add(card);
        Content = root;
    }

    private static Border BuildFooter()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 10, 16, 10) };
        row.Children.Add(Hint(CommandPalette.FooterSelect, ["↑", "↓"]));
        row.Children.Add(Hint(CommandPalette.FooterActions, ["Tab"], 20));
        row.Children.Add(Hint(CommandPalette.FooterOpenMenu, ["Ctrl", "K"], 20));

        var footer = new Border { Child = row, BorderThickness = new Thickness(0, 0.5, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        footer.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
        return footer;

        static StackPanel Hint(string label, string[] keys, double leftMargin = 0)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(leftMargin, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            panel.Children.Add(text);
            var caps = KeyCaps.Build(keys, small: true, outlined: true);
            caps.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(caps);
            return panel;
        }
    }

    /// <summary>Opens the palette on the default mode with the rows this session offers.</summary>
    public void Show(
        IReadOnlyList<PaletteAction> actions,
        IReadOnlyList<PaletteRow> quickActions,
        IReadOnlyList<PaletteRow> recents)
    {
        _actions = actions;
        _quickActions = quickActions;
        _recents = recents;
        _mode = PaletteMode.Default;
        _selection = 0;
        _input.Text = "";
        Rebuild();
        Dispatcher.InvokeAsync(() => _input.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Rebuild()
    {
        var query = _input.Text;
        var matched = CommandPalette.Filter(_actions, query);

        _groups = _mode == PaletteMode.Actions
            ? CommandPalette.GroupActions(matched)
            : CommandPalette.GroupDefault(
                matched,
                _quickActions,
                [.. _recents.Take(CommandPalette.MaxOrganicItems)],
                query);

        _rows = CommandPalette.Flatten(_groups);
        _selection = Math.Clamp(_selection, 0, Math.Max(0, _rows.Count - 1));

        var placeholder = _mode == PaletteMode.Actions
            ? CommandPalette.ActionsPlaceholder
            : CommandPalette.DefaultPlaceholder;
        _input.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, placeholder);
        _placeholder.Text = placeholder;
        _placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            _mode == PaletteMode.Actions ? CommandPalette.ActionsListboxLabel : CommandPalette.DefaultListboxLabel);

        // The reference hides the footer the moment the palette leaves its
        // resting state — a query typed, or a mode entered.
        _footer.Visibility = query.Length == 0 && _mode == PaletteMode.Default
            ? Visibility.Visible
            : Visibility.Collapsed;

        Render();
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Tab when _mode != PaletteMode.Actions:
                e.Handled = true;
                EnterActionsMode();
                break;
            case Key.Back or Key.Delete when _input.Text.Length == 0 && _mode == PaletteMode.Actions:
                e.Handled = true;
                _mode = PaletteMode.Default;
                _selection = 0;
                Rebuild();
                break;
            case Key.Down:
                e.Handled = true;
                Move(1);
                break;
            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;
            case Key.Home when _input.Text.Length == 0:
                e.Handled = true;
                _selection = 0;
                Render();
                break;
            case Key.End when _input.Text.Length == 0:
                e.Handled = true;
                _selection = Math.Max(0, _rows.Count - 1);
                Render();
                break;
            case Key.Enter:
                e.Handled = true;
                if (_selection >= 0 && _selection < _rows.Count)
                {
                    Run(_rows[_selection]);
                }

                break;
        }
    }

    /// <summary>Arrow keys wrap at both ends, as the reference's do.</summary>
    private void Move(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        _selection = delta > 0
            ? (_selection < _rows.Count - 1 ? _selection + 1 : 0)
            : (_selection > 0 ? _selection - 1 : _rows.Count - 1);
        Render();
    }

    private void EnterActionsMode()
    {
        _mode = PaletteMode.Actions;
        _selection = 0;
        Rebuild();
    }

    private void Run(PaletteRow row)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        switch (row)
        {
            case PaletteActionRow action:
                action.Action.Execute();
                break;
            case PaletteSessionRow session:
                session.Open();
                break;
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        if (_rows.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = CommandPalette.EmptyLabel,
                FontSize = 12.5,
                Margin = new Thickness(14, 10, 14, 12),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
            _list.Children.Add(empty);
            return;
        }

        var index = 0;
        foreach (var group in _groups)
        {
            if (group.Title is { Length: > 0 } title)
            {
                _list.Children.Add(BuildGroupHeader(group.Id, title));
            }

            foreach (var row in group.Items)
            {
                var element = BuildRow(row, index == _selection, index);
                _list.Children.Add(element);
                if (index == _selection)
                {
                    element.BringIntoView();
                }

                index++;
            }
        }
    }

    private FrameworkElement BuildGroupHeader(string groupId, string title)
    {
        // "actions" is the one header the reference makes clickable: it is the
        // way into the grouped command list without reaching for Tab.
        var text = new TextBlock
        {
            Text = title,
            FontSize = 11.5,
            Margin = new Thickness(14, 12, 14, 8),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");

        if (groupId != "actions" || _mode == PaletteMode.Actions)
        {
            return text;
        }

        var button = new Button { Content = text, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(0) };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Click += (_, _) => EnterActionsMode();
        return button;
    }

    private FrameworkElement BuildRow(PaletteRow row, bool selected, int index)
    {
        var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            FontSize = 13.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Text100Brush" : "Text300Brush");
        grid.Children.Add(label);

        FrameworkElement? trailing = null;
        switch (row)
        {
            case PaletteActionRow action:
                label.Text = action.Action.Description;
                if (action.Action.Shortcut is { Length: > 0 } shortcut)
                {
                    trailing = KeyCaps.Build(ShortcutSheet.Keys(shortcut), small: true, outlined: true);
                }
                else if (action.Action.SecondaryText is { Length: > 0 } secondary)
                {
                    trailing = Muted(secondary);
                }

                break;
            case PaletteSessionRow session:
                label.Text = session.Title.Length > 0 ? session.Title : CommandPalette.UntitledSession;
                trailing = BuildSessionBadges(session);
                break;
        }

        // The reference swaps whatever sits on the right for a return arrow on
        // the row the keyboard is on.
        if (selected)
        {
            trailing = Muted("↵");
        }

        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 1);
            trailing.Margin = new Thickness(12, 0, 0, 0);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(trailing);
        }

        var border = new Border { Child = grid, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
        if (selected)
        {
            border.SetResourceReference(Border.BackgroundProperty, "SelectedOverlayBrush");
        }

        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Run(row);
        };
        border.MouseEnter += (_, _) =>
        {
            if (_selection != index)
            {
                _selection = index;
                Render();
            }
        };
        return border;
    }

    private static FrameworkElement? BuildSessionBadges(PaletteSessionRow session)
    {
        var parts = new List<string>();
        if (session.IsScheduled)
        {
            parts.Add(CommandPalette.ScheduledBadge);
            if (session.RunCount > 0)
            {
                parts.Add(CommandPalette.RunCountLabel(session.RunCount));
            }
        }

        if (session.PullRequestNumber is { } pr)
        {
            parts.Add(CommandPalette.PullRequestLabel(pr));
        }

        return parts.Count == 0 ? null : Muted(string.Join(" · ", parts));
    }

    private static TextBlock Muted(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
        return block;
    }
}
