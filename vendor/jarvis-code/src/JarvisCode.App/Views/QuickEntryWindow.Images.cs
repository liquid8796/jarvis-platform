using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace JarvisCode.App.Views;

/// <summary>What Quick Entry sends: the typed text and any images attached to it.</summary>
/// <param name="Text">The prompt.</param>
/// <param name="ImagePaths">Files on disk, in the order they were attached.</param>
public sealed record QuickEntrySubmission(string Text, IReadOnlyList<string> ImagePaths);

/// <summary>
/// Quick Entry's attachments. The reference's quick-entry payload carries an
/// <c>images</c> array of <c>{base64, mimeType, filename}</c> beside its text
/// (ion-dist <c>index-DEczO-db.js</c>, the handler whose failure raises "Failed to
/// process quick entry"), which is what its tutorial means by "send messages and
/// screenshots from any app": a screenshot reaches the pill by paste or by drop.
/// </summary>
public partial class QuickEntryWindow
{
    /// <summary>The reference accepts these four image kinds; anything else is not an image.</summary>
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    private readonly List<string> _images = [];

    /// <summary>Where a pasted screenshot is written so it has a path to attach.</summary>
    private static string PastedImageDirectory =>
        Path.Combine(Path.GetTempPath(), "jarvis-quick-entry");

    private void InitializeImages()
    {
        AllowDrop = true;
        Drop += OnDropFiles;
        DragOver += OnDragOverFiles;
    }

    private void OnDragOverFiles(object sender, DragEventArgs e)
    {
        e.Effects = HasImageFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths.Where(IsImage))
            {
                Attach(path);
            }
        }

        e.Handled = true;
    }

    private static bool HasImageFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(IsImage);

    private static bool IsImage(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ctrl+V with a bitmap on the clipboard — a screenshot taken with the OS's own
    /// capture — attaches it. Text keeps the box's ordinary paste.
    /// </summary>
    private bool TryPasteImage()
    {
        BitmapSource? bitmap;
        try
        {
            if (!Clipboard.ContainsImage())
            {
                return false;
            }

            bitmap = Clipboard.GetImage();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }

        if (bitmap is null)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(PastedImageDirectory);
            var path = Path.Combine(
                PastedImageDirectory,
                $"pasted-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            Attach(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            return false;
        }
    }

    private void Attach(string path)
    {
        if (_images.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _images.Add(path);
        RenderChips();
    }

    private void Remove(string path)
    {
        _images.Remove(path);
        RenderChips();
    }

    private void RenderChips()
    {
        ImageChips.Children.Clear();
        ImageChips.Visibility = _images.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in _images)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 4, 2),
                Margin = new Thickness(0, 0, 6, 0),
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Bg300Brush");

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock
            {
                Text = Path.GetFileName(path),
                FontSize = 11.5,
                MaxWidth = 160,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text300Brush");
            row.Children.Add(label);

            var remove = new Button
            {
                Content = "✕",
                FontSize = 9,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(2, 0, 2, 0),
            };
            remove.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
            System.Windows.Automation.AutomationProperties.SetName(
                remove, $"Remove {Path.GetFileName(path)}");
            var attached = path;
            remove.Click += (_, _) => Remove(attached);
            row.Children.Add(remove);

            chip.Child = row;
            ImageChips.Children.Add(chip);
        }
    }
}
