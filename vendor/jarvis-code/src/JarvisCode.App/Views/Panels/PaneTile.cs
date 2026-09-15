using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JarvisCode.App.Views.Panels;

/// <summary>Which way a keyboard move would take the tile.</summary>
public enum TileMoveDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// One pane's card in the tile host: the panel's own content, the shared actions the
/// reference overlays at the top right (back · menu · pop out · expand · close), and the
/// drag handle it centres on the top edge.
///
/// The handle's geometry is the reference's own (ion-dist chunk c360a9e1c-DUoNQd2W.js,
/// its eq.dragHandle): a 44x16 hit area with a 32x3 rounded affordance that fades in on
/// hover or focus. Its tooltip is "Move" until an arrow key previews a side, when it
/// becomes the direction and asks for Enter.
/// </summary>
public sealed class PaneTile : Border
{
    private readonly Grid _root = new();
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly Border _handleAffordance;
    private readonly Button _handle;
    private readonly Button _backButton;
    private readonly Button _menuButton;
    private readonly Button _popoutButton;
    private readonly Button _expandButton;
    private readonly Button _closeButton;

    /// <summary>The instruction the reference attaches to the tiles container for a keyboard move.</summary>
    public const string ReorderInstructions =
        "Arrow keys move the tile. Perpendicular arrows preview a split; press Enter to commit or Escape to cancel.";

    public PaneTile(string paneKey)
    {
        PaneKey = paneKey;
        SetResourceReference(BackgroundProperty, "Bg200Brush");
        SetResourceReference(BorderBrushProperty, "BorderSoftBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;
        Child = _root;

        _root.Children.Add(_host);

        _handleAffordance = new Border
        {
            Width = 32,
            Height = 3,
            CornerRadius = new CornerRadius(999),
            Opacity = 0,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _handleAffordance.SetResourceReference(BackgroundProperty, "BorderStrongBrush");

        _handle = new Button
        {
            Width = 44,
            Height = 16,
            Padding = new Thickness(0, 6, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Content = _handleAffordance,
            Focusable = true,
            ToolTip = MoveLabel,
        };
        _handle.SetValue(AutomationProperties.NameProperty, MoveLabel);
        _handle.SetValue(AutomationProperties.HelpTextProperty, ReorderInstructions);
        _handle.MouseEnter += (_, _) => ShowAffordance(true);
        _handle.MouseLeave += (_, _) => ShowAffordance(_handle.IsKeyboardFocused);
        _handle.GotKeyboardFocus += (_, _) => ShowAffordance(true);
        _handle.LostKeyboardFocus += (_, _) =>
        {
            ShowAffordance(false);
            CancelPreview();
        };
        _handle.PreviewMouseLeftButtonDown += OnHandlePressed;
        _handle.PreviewKeyDown += OnHandleKey;
        _root.Children.Add(_handle);

        _backButton = Action("ArrowLeftGlyph", "Back", () => BackRequested?.Invoke(this, EventArgs.Empty));
        _backButton.Visibility = Visibility.Collapsed;
        _menuButton = Action("KebabGlyph", "Panel menu", () => MenuRequested?.Invoke(this, EventArgs.Empty), filled: true);
        _popoutButton = Action("ExternalLinkGlyph", "Open in a window", () => PopoutRequested?.Invoke(this, EventArgs.Empty));
        _expandButton = Action("ExpandGlyph", "Expand", () => ExpandRequested?.Invoke(this, EventArgs.Empty));
        _closeButton = Action("CloseGlyph", "Close", () => CloseRequested?.Invoke(this, EventArgs.Empty));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 6, 0),
        };
        actions.Children.Add(_backButton);
        actions.Children.Add(_menuButton);
        actions.Children.Add(_popoutButton);
        actions.Children.Add(_expandButton);
        actions.Children.Add(_closeButton);
        _root.Children.Add(actions);
    }

    private const string MoveLabel = "Move";

    /// <summary>The pane this tile draws.</summary>
    public string PaneKey { get; }

    /// <summary>The panel control the tile hosts.</summary>
    public object? PaneContent
    {
        get => _host.Content;
        set => _host.Content = value;
    }

    public bool CanPopOut
    {
        get => _popoutButton.IsEnabled;
        set => _popoutButton.IsEnabled = value;
    }

    public bool ShowBack
    {
        get => _backButton.Visibility == Visibility.Visible;
        set => _backButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Whether the tile can be dragged at all — the reference hides the handle when it is alone.</summary>
    public bool CanDrag
    {
        get => _handle.Visibility == Visibility.Visible;
        set => _handle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The expand action's tooltip, which flips to Restore while the pane is soloed.</summary>
    public void SetExpanded(bool expanded)
    {
        var label = expanded ? "Restore" : "Expand";
        _expandButton.ToolTip = label;
        _expandButton.SetValue(AutomationProperties.NameProperty, label);
    }

    /// <summary>The element a menu should be placed against.</summary>
    public UIElement MenuPlacementTarget => _menuButton;

    public event EventHandler? MenuRequested;
    public event EventHandler? PopoutRequested;
    public event EventHandler? ExpandRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? BackRequested;

    /// <summary>The handle was pressed — the host starts a drag from here.</summary>
    public event EventHandler<Point>? DragStarted;

    /// <summary>An arrow key asked to move the tile along the stack it is in.</summary>
    public event EventHandler<TileMoveDirection>? MoveRequested;

    /// <summary>Enter committed the previewed split, in the direction last previewed.</summary>
    public event EventHandler<TileMoveDirection>? SplitCommitted;

    private TileMoveDirection? _preview;

    private void ShowAffordance(bool visible) => _handleAffordance.Opacity = visible ? 1 : 0;

    private void OnHandlePressed(object sender, MouseButtonEventArgs e)
    {
        _handle.Focus();
        DragStarted?.Invoke(this, e.GetPosition(this));
    }

    /// <summary>
    /// The reference's keyboard move: an arrow along the stack moves the tile at once, an
    /// arrow across it previews a split that Enter commits and Escape cancels.
    /// </summary>
    private void OnHandleKey(object sender, KeyEventArgs e)
    {
        var direction = e.Key switch
        {
            Key.Left => TileMoveDirection.Left,
            Key.Right => TileMoveDirection.Right,
            Key.Up => TileMoveDirection.Up,
            Key.Down => TileMoveDirection.Down,
            _ => (TileMoveDirection?)null,
        };

        if (direction is { } moved)
        {
            e.Handled = true;
            MoveRequested?.Invoke(this, moved);
            return;
        }

        if (e.Key == Key.Enter && _preview is { } previewed)
        {
            e.Handled = true;
            SplitCommitted?.Invoke(this, previewed);
            CancelPreview();
            return;
        }

        if (e.Key == Key.Escape && _preview is not null)
        {
            e.Handled = true;
            CancelPreview();
        }
    }

    /// <summary>
    /// The host answers a cross-axis arrow by asking the tile to preview that side; the
    /// handle then reads as the direction and offers Enter, as the reference's tooltip does.
    /// </summary>
    public void PreviewSplit(TileMoveDirection direction)
    {
        _preview = direction;
        var label = direction switch
        {
            TileMoveDirection.Left => "Move left",
            TileMoveDirection.Right => "Move right",
            TileMoveDirection.Up => "Move up",
            _ => "Move down",
        };
        _handle.ToolTip = label;
        _handle.SetValue(AutomationProperties.NameProperty, label);
        _handle.SetValue(AutomationProperties.HelpTextProperty,
            direction switch
            {
                TileMoveDirection.Left => "Press Enter to move left, or Escape to cancel.",
                TileMoveDirection.Right => "Press Enter to move right, or Escape to cancel.",
                TileMoveDirection.Up => "Press Enter to move up, or Escape to cancel.",
                _ => "Press Enter to move down, or Escape to cancel.",
            });
    }

    public void CancelPreview()
    {
        if (_preview is null)
        {
            return;
        }

        _preview = null;
        _handle.ToolTip = MoveLabel;
        _handle.SetValue(AutomationProperties.NameProperty, MoveLabel);
        _handle.SetValue(AutomationProperties.HelpTextProperty, ReorderInstructions);
    }

    /// <summary>Puts keyboard focus on the drag handle, which is what a keyboard move drives.</summary>
    public void FocusHandle() => _handle.Focus();

    private Button Action(string glyph, string label, Action click, bool filled = false)
    {
        var path = new Path();
        path.SetResourceReference(StyleProperty, filled ? "PanelSmallGlyphFilled" : "PanelSmallGlyph");
        path.SetResourceReference(Path.DataProperty, glyph);
        var button = new Button { Content = path, ToolTip = label };
        button.SetResourceReference(StyleProperty, "PanelActionButton");
        button.SetValue(AutomationProperties.NameProperty, label);
        button.Click += (_, _) => click();
        return button;
    }
}
