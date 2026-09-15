using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JarvisCode.App.Views.Panels;

public sealed partial class PreviewAnnotationOverlay
{
    // Desktop 1.46388.3.0 c467aa787-DcyMw9VS.js, q: compact when the measured
    // full toolbar exceeds its container, not the historical fixed 420px rule.
    private static readonly (AnnotationTool Tool, string Glyph, string Label)[] DrawingTools =
    [
        (AnnotationTool.Pen, "✎", "Pen"), (AnnotationTool.Line, "╱", "Line"),
        (AnnotationTool.Arrow, "↗", "Arrow"), (AnnotationTool.Rect, "□", "Rectangle"),
        (AnnotationTool.Ellipse, "○", "Ellipse"), (AnnotationTool.Text, "T", "Text"),
    ];
    private static readonly string[] ColorNames = ["Red", "Blue", "Green", "Black"];
    private readonly Dictionary<AnnotationTool, ToggleButton> _toolButtons = [];
    private readonly List<ToggleButton> _colorButtons = [];
    private readonly List<UIElement[]> _undo = [];
    private readonly List<UIElement[]> _redo = [];
    private Button _undoButton = null!, _redoButton = null!, _clearButton = null!;
    private Button _compactTool = null!, _compactColor = null!;
    private int _colorIndex;
    private bool _restoringInk;

    internal bool IsCompactToolbar { get; private set; }
    internal AnnotationTool SelectedDrawingTool => _tool;
    internal string SelectedInkColor => InkColors[_colorIndex];

    private (Border Card, Button Save) BuildAnnotationToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        AutomationProperties.SetName(tools, "Drawing tool");
        foreach (var item in DrawingTools) tools.Children.Add(ToolButton(item.Tool, item.Glyph, item.Label));
        var colors = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(colors, "Ink color");
        for (var index = 0; index < InkColors.Count; index++)
        {
            var captured = index;
            var button = new ToggleButton { Content = ColorDot(index), Width = 22, Height = 28, ToolTip = ColorNames[index] };
            button.SetResourceReference(StyleProperty, "PanelTab");
            AutomationProperties.SetName(button, ColorNames[index]);
            button.Click += (_, _) => SelectInkColor(captured);
            _colorButtons.Add(button);
            colors.Children.Add(button);
        }
        _compactTool = ToolbarIcon("Drawing tool", "✎", OpenDrawingTools);
        _compactColor = ToolbarIcon("Ink color", ColorDot(0), OpenInkColors);
        var middleRule = Divider();
        bar.Children.Add(tools);
        bar.Children.Add(middleRule);
        bar.Children.Add(colors);
        bar.Children.Add(_compactTool);
        bar.Children.Add(_compactColor);
        bar.Children.Add(Divider());
        _undoButton = ToolbarIcon("Undo", "↶", UndoInk);
        _redoButton = ToolbarIcon("Redo", "↷", RedoInk);
        _clearButton = ToolbarIcon("Clear all", "⌫", Clear);
        bar.Children.Add(_undoButton);
        bar.Children.Add(_redoButton);
        bar.Children.Add(_clearButton);
        var close = TextButton("Close", RequestClose);
        bar.Children.Add(close);
        var save = new Button { Content = "Save", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), IsEnabled = false };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        AutomationProperties.SetName(save, "Save");
        save.Click += (_, _) => Commit();
        bar.Children.Add(save);
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(1),
            Child = bar, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 10, 24),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg100Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        var updating = false;
        void Layout()
        {
            if (updating || ActualWidth <= 0) return;
            updating = true;
            try
            {
                tools.Visibility = colors.Visibility = middleRule.Visibility = Visibility.Visible;
                _compactTool.Visibility = _compactColor.Visibility = Visibility.Collapsed;
                close.Content = "Close";
                bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                IsCompactToolbar = bar.DesiredSize.Width + 22 > ActualWidth - 20;
                if (IsCompactToolbar)
                {
                    tools.Visibility = colors.Visibility = middleRule.Visibility = Visibility.Collapsed;
                    _compactTool.Visibility = _compactColor.Visibility = Visibility.Visible;
                    close.Content = "×";
                }
            }
            finally { updating = false; }
        }
        Loaded += (_, _) => Layout();
        SizeChanged += (_, _) => Layout();
        SelectDrawingTool(_tool);
        SelectInkColor(0);
        _undoButton.IsEnabled = _redoButton.IsEnabled = _clearButton.IsEnabled = false;
        return (card, save);
    }

    private static Border ColorDot(int index) => new()
    {
        Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(InkColors[index])),
    };

    private Button ToolbarIcon(string name, object content, Action action)
    {
        var button = new Button { Content = content, Width = 28, Height = 28, ToolTip = name };
        button.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    private void SelectDrawingTool(AnnotationTool tool)
    {
        _tool = tool;
        foreach (var item in _toolButtons) item.Value.IsChecked = item.Key == tool;
        _compactTool.Content = DrawingTools.First(item => item.Tool == tool).Glyph;
    }

    private void SelectInkColor(int index)
    {
        _colorIndex = index;
        _color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(InkColors[index]));
        for (var i = 0; i < _colorButtons.Count; i++) _colorButtons[i].IsChecked = i == index;
        _compactColor.Content = ColorDot(index);
    }

    private void OpenDrawingTools()
    {
        var menu = new ContextMenu { PlacementTarget = _compactTool, Placement = PlacementMode.Top };
        foreach (var tool in DrawingTools)
        {
            var item = new MenuItem { Header = tool.Label, Icon = new TextBlock { Text = tool.Glyph }, IsCheckable = true, IsChecked = _tool == tool.Tool };
            item.Click += (_, _) => SelectDrawingTool(tool.Tool);
            menu.Items.Add(item);
        }
        _compactTool.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OpenInkColors()
    {
        var menu = new ContextMenu { PlacementTarget = _compactColor, Placement = PlacementMode.Top };
        for (var index = 0; index < InkColors.Count; index++)
        {
            var captured = index;
            var item = new MenuItem { Header = ColorNames[index], Icon = ColorDot(index), IsCheckable = true, IsChecked = index == _colorIndex };
            item.Click += (_, _) => SelectInkColor(captured);
            menu.Items.Add(item);
        }
        _compactColor.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static void UpdateArrow(System.Windows.Shapes.Path arrow, Point start, Point end)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        const double head = StrokeWidth * 3;
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(start, end));
        geometry.Children.Add(new LineGeometry(end, new Point(end.X - head * Math.Cos(angle - Math.PI / 6), end.Y - head * Math.Sin(angle - Math.PI / 6))));
        geometry.Children.Add(new LineGeometry(end, new Point(end.X - head * Math.Cos(angle + Math.PI / 6), end.Y - head * Math.Sin(angle + Math.PI / 6))));
        arrow.Data = geometry;
    }

    private UIElement[] SnapshotInk() => _ink.Children.Cast<UIElement>().Select(CloneInk).ToArray();

    private static UIElement CloneInk(UIElement element)
    {
        FrameworkElement copy = element switch
        {
            Polyline line => new Polyline { Points = line.Points.Clone() },
            Line line => new Line { X1 = line.X1, Y1 = line.Y1, X2 = line.X2, Y2 = line.Y2 },
            Rectangle => new Rectangle(),
            Ellipse => new Ellipse(),
            System.Windows.Shapes.Path path => new System.Windows.Shapes.Path { Data = path.Data.Clone() },
            TextBox text => new TextBox { Text = text.Text, MinWidth = text.MinWidth, Foreground = text.Foreground,
                FontSize = text.FontSize, Background = text.Background, BorderThickness = text.BorderThickness },
            _ => throw new InvalidOperationException("Unsupported annotation element."),
        };
        if (element is FrameworkElement source) { copy.Width = source.Width; copy.Height = source.Height; }
        if (element is Shape shape && copy is Shape result)
        {
            result.Stroke = shape.Stroke;
            result.StrokeThickness = shape.StrokeThickness;
            result.StrokeLineJoin = shape.StrokeLineJoin;
        }
        Canvas.SetLeft(copy, Canvas.GetLeft(element));
        Canvas.SetTop(copy, Canvas.GetTop(element));
        return copy;
    }

    private void SaveUndo()
    {
        if (_restoringInk) return;
        _undo.Add(SnapshotInk());
        _redo.Clear();
    }

    private void RestoreInk(UIElement[] snapshot)
    {
        _restoringInk = true;
        try
        {
            _ink.ReleaseMouseCapture();
            _stroke = null;
            _drawing = null;
            _ink.Children.Clear();
            foreach (var element in snapshot)
            {
                var copy = CloneInk(element);
                _ink.Children.Add(copy);
                if (copy is TextBox text) WireTextHistory(text);
            }
        }
        finally { _restoringInk = false; }
        UpdateHistoryActions();
    }

    private void UndoInk()
    {
        if (_undo.Count == 0) return;
        _redo.Add(SnapshotInk());
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        RestoreInk(snapshot);
    }

    private void RedoInk()
    {
        if (_redo.Count == 0) return;
        _undo.Add(SnapshotInk());
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        RestoreInk(snapshot);
    }

    private void WireTextHistory(TextBox text, bool newlyCreated = false)
    {
        AutomationProperties.SetName(text, "Text label");
        text.GotKeyboardFocus += (_, _) =>
        {
            if (newlyCreated) { newlyCreated = false; return; }
            if (!_restoringInk) SaveUndo();
        };
        text.LostKeyboardFocus += (_, _) =>
        {
            if (_restoringInk) return;
            if (string.IsNullOrWhiteSpace(text.Text)) _ink.Children.Remove(text);
            UpdateHistoryActions();
        };
    }

    private void UpdateHistoryActions()
    {
        _save.IsEnabled = _clearButton.IsEnabled = HasStrokes;
        _undoButton.IsEnabled = _undo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0;
        _undoButton.ToolTip = _undoButton.IsEnabled ? "Undo" : "Nothing to undo yet";
        _redoButton.ToolTip = _redoButton.IsEnabled ? "Redo" : "Nothing to redo yet";
        _clearButton.ToolTip = HasStrokes ? "Clear all" : "Nothing to clear yet";
    }
}
