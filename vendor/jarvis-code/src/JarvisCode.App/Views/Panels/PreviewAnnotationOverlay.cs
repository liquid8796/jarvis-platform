using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace JarvisCode.App.Views.Panels;

/// <summary>The tools the reference's drawing surface offers, in its own order.</summary>
public enum AnnotationTool
{
    Pen,
    Line,
    Rect,
    Ellipse,
    Text,
    Arrow,
}

/// <summary>
/// The pencil on the preview toolbar: the page as it stands, drawn on, and handed to the
/// composer as an image. Ported from the reference's annotation editor (ion-dist chunks
/// c467aa787-BHotK1Ul.js for the surface and toolbar, cd089cf92-CPpbZ5h_.js for the host):
/// its six tools, four ink colours, 4px stroke, undo/redo, responsive toolbar and the
/// "Discard your annotations?" question a close with strokes on it raises.
/// </summary>
public sealed partial class PreviewAnnotationOverlay : Grid
{
    /// <summary>The reference's ink palette, verbatim.</summary>
    public static readonly IReadOnlyList<string> InkColors = ["#E03131", "#1971C2", "#2F9E44", "#1F1E1D"];

    /// <summary>The reference's stroke width for every shape.</summary>
    public const double StrokeWidth = 4;

    private readonly Image _page = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _ink = new() { Background = Brushes.Transparent };
    private readonly Button _save;
    private AnnotationTool _tool = AnnotationTool.Pen;
    private Brush _color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(InkColors[0]));
    private Point _start;
    private Shape? _drawing;
    private Polyline? _stroke;

    public PreviewAnnotationOverlay()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        AutomationProperties.SetName(this, "Annotate image");

        var stage = new Grid { Margin = new Thickness(24, 24, 24, 76) };
        stage.Children.Add(_page);
        stage.Children.Add(_ink);
        Children.Add(stage);

        _ink.MouseLeftButtonDown += OnInkDown;
        _ink.MouseMove += OnInkMove;
        _ink.MouseLeftButtonUp += OnInkUp;

        var toolbar = BuildAnnotationToolbar();
        _save = toolbar.Save;
        Children.Add(toolbar.Card);
        Focusable = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (e.Key == Key.Z && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { UndoInk(); e.Handled = true; }
                else if (e.Key == Key.Y || e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { RedoInk(); e.Handled = true; }
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RequestClose();
            }
        };
    }

    /// <summary>The annotated page, written to a PNG the composer can attach.</summary>
    public event EventHandler<string>? Annotated;

    /// <summary>The overlay is done — the host takes it off the pane.</summary>
    public event EventHandler? Closed;

    /// <summary>Shows the captured page under the ink layer.</summary>
    public void Load(BitmapSource capture)
    {
        _page.Source = capture;
        _ink.Children.Clear();
        _undo.Clear();
        _redo.Clear();
        UpdateHistoryActions();
    }

    private bool HasStrokes => _ink.Children.Count > 0;

    private void Clear()
    {
        if (!HasStrokes) return;
        SaveUndo();
        _ink.Children.Clear();
        UpdateHistoryActions();
    }

    /// <summary>The reference asks before throwing annotated work away.</summary>
    private void RequestClose()
    {
        if (HasStrokes && !ConfirmDialog.Ask(
                Window.GetWindow(this),
                "Discard your annotations?",
                "You have unsaved marks on this image. Discarding removes them.",
                "Discard",
                focusCancel: true, cancelLabel: "Keep editing"))
        {
            return;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Commit()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jarvis-annotation-{Guid.NewGuid():N}.png");
        try
        {
            var width = (int)Math.Max(1, _ink.ActualWidth);
            var height = (int)Math.Max(1, _ink.ActualHeight);
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                if (_page.Source is { } source)
                {
                    var scale = Math.Min(width / source.Width, height / source.Height);
                    var imageWidth = source.Width * scale;
                    var imageHeight = source.Height * scale;
                    context.DrawImage(source, new Rect((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight));
                }
            }

            target.Render(visual);
            target.Render(_ink);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Annotated?.Invoke(this, path);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    // ---- drawing ----

    private void OnInkDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        SaveUndo();
        _start = e.GetPosition(_ink);
        _ink.CaptureMouse();
        switch (_tool)
        {
            case AnnotationTool.Pen:
                _stroke = new Polyline { Stroke = _color, StrokeThickness = StrokeWidth, StrokeLineJoin = PenLineJoin.Round };
                _stroke.Points.Add(_start);
                _ink.Children.Add(_stroke);
                break;
            case AnnotationTool.Line:
                var line = new Line { Stroke = _color, StrokeThickness = StrokeWidth, X1 = _start.X, Y1 = _start.Y, X2 = _start.X, Y2 = _start.Y };
                _drawing = line;
                _ink.Children.Add(line);
                break;
            case AnnotationTool.Arrow:
                _drawing = new System.Windows.Shapes.Path { Stroke = _color, StrokeThickness = StrokeWidth };
                UpdateArrow((System.Windows.Shapes.Path)_drawing, _start, _start);
                _ink.Children.Add(_drawing);
                break;
            case AnnotationTool.Rect:
                _drawing = new Rectangle { Stroke = _color, StrokeThickness = StrokeWidth };
                Place(_drawing, _start, _start);
                _ink.Children.Add(_drawing);
                break;
            case AnnotationTool.Ellipse:
                _drawing = new Ellipse { Stroke = _color, StrokeThickness = StrokeWidth };
                Place(_drawing, _start, _start);
                _ink.Children.Add(_drawing);
                break;
            case AnnotationTool.Text:
                AddTextLabel(_start);
                break;
        }

        UpdateHistoryActions();
    }

    private void OnInkMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(_ink);
        if (_stroke is not null)
        {
            _stroke.Points.Add(point);
            return;
        }

        switch (_drawing)
        {
            case System.Windows.Shapes.Path arrow:
                UpdateArrow(arrow, _start, point);
                break;
            case Line line:
                line.X2 = point.X;
                line.Y2 = point.Y;
                break;
            case { } shape:
                Place(shape, _start, point);
                break;
        }
    }

    private void OnInkUp(object sender, MouseButtonEventArgs e)
    {
        _ink.ReleaseMouseCapture();
        _stroke = null;
        _drawing = null;
        UpdateHistoryActions();
    }

    private static void Place(Shape shape, Point a, Point b)
    {
        Canvas.SetLeft(shape, Math.Min(a.X, b.X));
        Canvas.SetTop(shape, Math.Min(a.Y, b.Y));
        shape.Width = Math.Abs(a.X - b.X);
        shape.Height = Math.Abs(a.Y - b.Y);
    }

    private void AddTextLabel(Point at)
    {
        var box = new TextBox
        {
            MinWidth = 60,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _color,
            FontSize = 16,
        };
        AutomationProperties.SetName(box, "Text label");
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        _ink.Children.Add(box);
        WireTextHistory(box, newlyCreated: true);
        box.Focus();
        box.LostFocus += (_, _) =>
        {
            if (box.Text.Trim().Length == 0)
            {
                _ink.Children.Remove(box);
                UpdateHistoryActions();
            }
        };
    }

    // ---- toolbar pieces ----

    private ToggleButton ToolButton(AnnotationTool tool, string glyph, string label)
    {
        var button = new ToggleButton
        {
            Content = glyph,
            Width = 28,
            Height = 28,
            IsChecked = tool == _tool,
            ToolTip = label,
        };
        button.SetResourceReference(StyleProperty, "PanelTab");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) =>
        {
            SelectDrawingTool(tool);
        };
        _toolButtons[tool] = button;
        return button;
    }

    private static UIElement Divider()
    {
        var line = new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0) };
        line.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        return line;
    }

    private static Button TextButton(string label, Action click)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 3, 8, 3),
        };
        button.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }
}
