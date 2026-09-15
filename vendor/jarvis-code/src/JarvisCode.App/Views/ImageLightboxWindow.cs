using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JarvisCode.App.Views;

/// <summary>
/// The viewer a tool result's image opens in. The reference's transcript makes
/// every returned screenshot a <c>cursor-zoom-in</c> button that raises its media
/// lightbox over a dim backdrop, with the row's other images alongside it — a
/// screenshot is the one tool result you cannot read at 360px, so the row has to
/// have somewhere to send it.
///
/// The media toolbar controls fit/actual size, zoom, copy and download. Zoomed
/// images remain pannable and every action is keyboard and automation accessible.
/// </summary>
public sealed class ImageLightboxWindow : Window
{
    private readonly IReadOnlyList<JarvisCode.Core.Models.ImageBlock> _images;
    private readonly Image _view = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private int _index;
    private readonly ScrollViewer _viewport = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _zoomLabel = new() { Foreground = Brushes.White, Width = 58, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _positionLabel = new() { Foreground = Brushes.White, Margin = new Thickness(8, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _error = new() { Foreground = Brushes.White, Text = "Image preview unavailable", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private double _zoom = 1;
    private bool _fit = true;
    private Point? _panStart;
    private Point _panOffset;

    internal ImageLightboxWindow(IReadOnlyList<JarvisCode.Core.Models.ImageBlock> images, int index)
    {
        _images = images;
        _index = index;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowState = WindowState.Maximized;
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0));
        Title = "Screenshot";

        var previous = Arrow("", -1);
        var next = Arrow("", 1);
        previous.HorizontalAlignment = HorizontalAlignment.Left;
        next.HorizontalAlignment = HorizontalAlignment.Right;
        previous.Visibility = next.Visibility = images.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var root = new Grid { Margin = new Thickness(48) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _viewport.Content = _view;
        root.Children.Add(_viewport);
        root.Children.Add(_error);
        root.Children.Add(previous);
        root.Children.Add(next);
        _toolbar.Children.Add(_positionLabel);
        _toolbar.Children.Add(ActionButton("−", "Zoom out", () => ZoomBy(1 / 1.25)));
        _toolbar.Children.Add(_zoomLabel);
        _toolbar.Children.Add(ActionButton("+", "Zoom in", () => ZoomBy(1.25)));
        _toolbar.Children.Add(ActionButton("Fit", "Fit image", Fit));
        _toolbar.Children.Add(ActionButton("100%", "Actual size", () => SetZoom(1)));
        _toolbar.Children.Add(ActionButton("Copy", "Copy image", Copy));
        _toolbar.Children.Add(ActionButton("Save", "Save image", Save));
        _toolbar.Children.Add(ActionButton("×", "Close image viewer", Close));
        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xe8, 24, 24, 24)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Child = _toolbar,
        };
        Grid.SetRow(bar, 1); root.Children.Add(bar);
        Content = root;

        // Clicking the backdrop dismisses, the way its own backdrop does; a click
        // that landed on the image itself is not a dismissal.
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_viewport.IsMouseOver && !_toolbar.IsMouseOver && !previous.IsMouseOver && !next.IsMouseOver)
            {
                e.Handled = true;
                Close();
            }
        };

        _viewport.SizeChanged += (_, _) => { if (_fit) Fit(); };
        Loaded += (_, _) => Fit();
        _viewport.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            ZoomBy(e.Delta > 0 ? 1.25 : 1 / 1.25); e.Handled = true;
        };
        _view.MouseLeftButtonDown += (_, e) =>
        {
            _panStart = e.GetPosition(_viewport);
            _panOffset = new Point(_viewport.HorizontalOffset, _viewport.VerticalOffset);
            _view.CaptureMouse(); _view.Cursor = Cursors.SizeAll; e.Handled = true;
        };
        _view.MouseMove += (_, e) =>
        {
            if (_panStart is not { } start) return;
            var position = e.GetPosition(_viewport);
            _viewport.ScrollToHorizontalOffset(_panOffset.X + start.X - position.X);
            _viewport.ScrollToVerticalOffset(_panOffset.Y + start.Y - position.Y);
        };
        _view.MouseLeftButtonUp += (_, e) => { _panStart = null; _view.ReleaseMouseCapture(); _view.Cursor = Cursors.Hand; e.Handled = true; };

        Show(_index);
    }

    internal double ZoomFactor => _zoom;
    internal int ImageIndex => _index;

    private static Button ActionButton(string text, string label, Action action)
    {
        var button = new Button { Content = text, ToolTip = label, Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(2), Foreground = Brushes.White, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), MinWidth = 34, Cursor = Cursors.Hand };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }

    internal void ZoomBy(double factor) => SetZoom(_zoom * factor);
    internal void SetZoom(double zoom)
    {
        _fit = false; _zoom = Math.Clamp(zoom, 0.05, 8); ApplyZoom();
    }
    internal void Fit()
    {
        _fit = true;
        if (_view.Source is BitmapSource bitmap && _viewport.ActualWidth > 0 && _viewport.ActualHeight > 0)
            _zoom = Math.Min(1, Math.Min(Math.Max(1, (_viewport.ViewportWidth > 0 ? _viewport.ViewportWidth : _viewport.ActualWidth) - 4) / bitmap.PixelWidth,
                Math.Max(1, (_viewport.ViewportHeight > 0 ? _viewport.ViewportHeight : _viewport.ActualHeight) - 4) / bitmap.PixelHeight));
        ApplyZoom();
    }
    private void ApplyZoom()
    {
        if (_view.Source is BitmapSource bitmap)
        { _view.Width = bitmap.PixelWidth * _zoom; _view.Height = bitmap.PixelHeight * _zoom; }
        _zoomLabel.Text = $"{_zoom:P0}";
    }
    private void Copy()
    {
        if (_view.Source is not BitmapSource bitmap) return;
        try { Clipboard.SetImage(bitmap); }
        catch (System.Runtime.InteropServices.ExternalException) { Services.ToastQueue.Current?.AddError("The clipboard is busy. Try again."); }
    }
    private void Save()
    {
        var image = _images[_index];
        var extension = image.MediaType switch { "image/png" => ".png", "image/jpeg" or "image/jpg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/bmp" => ".bmp", "image/tiff" => ".tiff", "image/svg+xml" => ".svg", "image/avif" => ".avif", _ => ".img" };
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = $"Screenshot-{_index + 1}{extension}", DefaultExt = extension, Filter = "Image|*" + extension };
        if (dialog.ShowDialog(this) != true) return;
        try { System.IO.File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(image.Base64Data)); }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or UnauthorizedAccessException)
        { Services.ToastQueue.Current?.AddError("Could not save the image: " + ex.Message); }
    }

    private Button Arrow(string glyph, int delta)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = Brushes.White,
            },
            Width = 44,
            Height = 44,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };

        button.Click += (_, _) => Show(_index + delta);
        AutomationProperties.SetName(button, delta < 0 ? "Previous image" : "Next image");
        return button;
    }

    private void Show(int index)
    {
        if (_images.Count == 0)
        {
            return;
        }

        _index = (index % _images.Count + _images.Count) % _images.Count;
        _view.Source = Decode(_images[_index]);
        _positionLabel.Text = $"{_index + 1} / {_images.Count}";
        _error.Visibility = _view.Source is null ? Visibility.Visible : Visibility.Collapsed;
        Fit();
        AutomationProperties.SetName(_view, "Screenshot");
    }

    private static BitmapImage? Decode(JarvisCode.Core.Models.ImageBlock image)
    {
        if (image.Base64Data.Length == 0)
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(image.Base64Data));
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or System.IO.IOException)
        {
            return null;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                return;
            case Key.Left:
                e.Handled = true;
                Show(_index - 1);
                return;
            case Key.Right:
                e.Handled = true;
                Show(_index + 1);
                return;
            case Key.Add:
            case Key.OemPlus:
                ZoomBy(1.25); e.Handled = true; return;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomBy(1 / 1.25); e.Handled = true; return;
            case Key.D0:
                Fit(); e.Handled = true; return;
            case Key.D1:
                SetZoom(1); e.Handled = true; return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Opens the row's images at the one that was clicked.</summary>
    public static void OpenFile(Window? owner, string path)
    {
        var mime = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", _ => "image/png" };
        Open(owner, [new JarvisCode.Core.Models.ImageBlock(mime, Convert.ToBase64String(System.IO.File.ReadAllBytes(path)))], 0);
    }

    /// <summary>Opens the row's images at the one that was clicked.</summary>
    public static void Open(
        Window? owner, IReadOnlyList<JarvisCode.Core.Models.ImageBlock> images, int index)
    {
        if (images.Count == 0)
        {
            return;
        }

        var window = new ImageLightboxWindow(images, index);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
    }
}
