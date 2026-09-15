using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using JarvisCode.App.Views;
using JarvisCode.App.Views.Panels;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class PreviewAnnotationToolbarTests
{
    [NativeUiFact]
    public void Saved_capture_preserves_the_visible_uniform_image_geometry()
    {
        WpfTestThread.Run(() =>
        {
            var overlay = new PreviewAnnotationOverlay();
            var pixels = new byte[100 * 50 * 4];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
            overlay.Load(BitmapSource.Create(100, 50, 96, 96, PixelFormats.Bgra32, null, pixels, 400));
            var window = new Window { Content = overlay, Width = 500, Height = 500, Left = -30000, Top = -30000, ShowActivated = false };
            window.Show(); window.UpdateLayout();
            var ink = Walk(overlay).OfType<Canvas>().Single();
            ink.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            ink.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            string? saved = null;
            overlay.Annotated += (_, path) => saved = path;
            FindButton(overlay, "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(saved);
            try
            {
                using var file = System.IO.File.OpenRead(saved);
                var image = BitmapFrame.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var result = new byte[image.PixelWidth * image.PixelHeight * 4];
                new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(result, image.PixelWidth * 4, 0);
                var nearTop = (5 * image.PixelWidth + image.PixelWidth / 2) * 4;
                var middle = (image.PixelHeight / 2 * image.PixelWidth + image.PixelWidth / 2) * 4;
                Assert.Equal(0, result[nearTop + 3]); // letterboxing seen under the overlay
                Assert.Equal(255, result[middle + 2]);
                Assert.Equal(255, result[middle + 3]);
            }
            finally { System.IO.File.Delete(saved); }
        });
    }

    [NativeUiFact]
    public void Loaded_toolbar_measures_available_width_and_preserves_tool_color_and_history_across_menus()
    {
        WpfTestThread.Run(() =>
        {
            var overlay = new PreviewAnnotationOverlay();
            var window = new Window { Content = overlay, Width = 900, Height = 600, Left = -30000, Top = -30000, ShowActivated = false };
            window.Show(); window.UpdateLayout();
            Assert.False(overlay.IsCompactToolbar);
            window.Width = 350; window.UpdateLayout();
            Assert.True(overlay.IsCompactToolbar);
            var tool = FindButton(overlay, "Drawing tool");
            tool.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { "Pen", "Line", "Arrow", "Rectangle", "Ellipse", "Text" }, tool.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header));
            var arrow = tool.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Arrow"));
            arrow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            tool.ContextMenu.IsOpen = false;
            var color = FindButton(overlay, "Ink color");
            color.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new[] { "Red", "Blue", "Green", "Black" }, color.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header));
            color.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Blue")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            color.ContextMenu.IsOpen = false;
            Assert.Equal(AnnotationTool.Arrow, overlay.SelectedDrawingTool);
            Assert.Equal("#1971C2", overlay.SelectedInkColor);
            var ink = Walk(overlay).OfType<Canvas>().Single();
            ink.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            ink.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            var shape = Assert.IsType<Path>(Assert.Single(ink.Children.Cast<UIElement>()));
            Assert.Equal(3, Assert.IsType<GeometryGroup>(shape.Data).Children.Count);
            Assert.True(FindButton(overlay, "Undo").IsEnabled);
            FindButton(overlay, "Undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(ink.Children);
            Assert.False(FindButton(overlay, "Save").IsEnabled);
            FindButton(overlay, "Redo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(ink.Children.Cast<UIElement>());
            FindButton(overlay, "Clear all").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(ink.Children);
            FindButton(overlay, "Undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(ink.Children.Cast<UIElement>());
            window.Width = 900; window.UpdateLayout();
            Assert.False(overlay.IsCompactToolbar);
            Assert.Equal(AnnotationTool.Arrow, overlay.SelectedDrawingTool);
            Assert.Equal("#1971C2", overlay.SelectedInkColor);
            Assert.True(Walk(overlay).OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(button => AutomationProperties.GetName(button) == "Arrow").IsChecked);
            Assert.True(Walk(overlay).OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(button => AutomationProperties.GetName(button) == "Blue").IsChecked);
        });
    }

    [NativeUiFact]
    public void Two_button_confirmation_keeps_the_requested_cancel_label_out_of_the_middle_slot()
    {
        WpfTestThread.Run(() =>
        {
            string? label = null;
            Visibility? middle = null;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<ConfirmDialog>().Single();
                label = ((Button)dialog.FindName("CancelButton")).Content?.ToString();
                middle = ((Button)dialog.FindName("MiddleButton")).Visibility;
                dialog.DialogResult = false;
            }));
            Assert.False(ConfirmDialog.Ask(null, "Discard your annotations?", "You have unsaved marks on this image. Discarding removes them.", "Discard", focusCancel: true, cancelLabel: "Keep editing"));
            Assert.Equal("Keep editing", label);
            Assert.Equal(Visibility.Collapsed, middle);
        });
    }

    private static Button FindButton(DependencyObject root, string name) => Walk(root).OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}
