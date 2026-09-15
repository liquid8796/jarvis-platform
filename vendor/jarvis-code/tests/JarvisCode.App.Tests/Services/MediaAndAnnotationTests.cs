using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JarvisCode.App.Services;
using JarvisCode.App.Views;
using JarvisCode.App.Views.Panels;
using JarvisCode.Core.Models;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class MediaAndAnnotationTests
{
    [NativeUiFact]
    public void Media_toolbar_changes_the_real_image_layout_and_preserves_navigation()
    {
        WpfTestThread.Run(() =>
        {
            ImageLightboxWindow? window = null;
            try
            {
                var pixels = new byte[1600 * 1000 * 4];
                for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 180; pixels[i + 1] = 80; pixels[i + 2] = 20; pixels[i + 3] = 255; }
                var bitmap = BitmapSource.Create(1600, 1000, 96, 96, PixelFormats.Bgra32, null, pixels, 6400);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream(); encoder.Save(stream);
                var image = new ImageBlock("image/png", Convert.ToBase64String(stream.ToArray()));
                window = new ImageLightboxWindow([image, image], 0)
                { WindowState = WindowState.Normal, Width = 800, Height = 600, Left = -30000, Top = -30000, ShowActivated = false };
                window.Show(); window.UpdateLayout(); window.Fit(); window.UpdateLayout();
                Assert.InRange(window.ZoomFactor, 0.1, 0.6);
                var buttons = Descendants(window).OfType<Button>().ToArray();
                var before = window.ZoomFactor;
                Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Zoom in").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout(); Assert.True(window.ZoomFactor > before);
                Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Actual size").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, window.ZoomFactor);
                Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Next image").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, window.ImageIndex);
                Assert.Single(buttons, b => AutomationProperties.GetName(b) == "Fit image").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                if (Environment.GetEnvironmentVariable("JARVIS_MEDIA_TEST_IMAGE") is { Length: > 0 } file)
                {
                    var render = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
                    render.Render((Visual)window.Content);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(render));
                    using var output = File.Create(file); png.Save(output);
                }
            }
            finally { window?.Close(); }
        });
    }

    [NativeUiFact]
    public void Narrow_review_toolbar_keeps_apply_and_dismiss_available_in_its_menu()
    {
        WpfTestThread.Run(() =>
        {
            var session = "compact-review-" + Guid.NewGuid().ToString("N");
            var panel = new ChangesPanel();
            var window = new Window { Content = panel, Width = 900, Height = 600, Left = -30000, Top = -30000, ShowActivated = false };
            try
            {
                ReviewFindingsStore.Set(session, [new ReviewFinding { Id = "finding", File = "a.cs", Line = 7, Summary = "Handle the empty input." }]);
                panel.BindSession(session);
                window.Show(); window.UpdateLayout();
                var apply = (Button)panel.FindName("ApplyFixesButton");
                var more = (Button)panel.FindName("CompactReviewActionsButton");
                Assert.Equal(Visibility.Visible, apply.Visibility);
                Assert.Equal(Visibility.Collapsed, more.Visibility);
                window.Width = 420; window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, apply.Visibility);
                Assert.Equal(Visibility.Visible, more.Visibility);
                string? sent = null; panel.ReviewMessageRequested += (_, text) => sent = text;
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = more.ContextMenu!;
                Assert.Equal(new[] { "Apply fixes", "Dismiss" }, menu.Items.OfType<MenuItem>().Select(i => i.Header));
                ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Contains("Handle the empty input", sent);
                Assert.Equal(ReviewFindingState.Fixing, Assert.Single(ReviewFindingsStore.Get(session)).State);
                menu.IsOpen = false;
                window.Width = 900; window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, more.Visibility);
            }
            finally { window.Close(); ReviewFindingsStore.Clear(session); }
        });
    }

    [Fact]
    public void Stored_annotation_reopens_its_fold_and_does_not_mark_changed_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-review-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReviewAnnotationStore(Path.Combine(root, "annotations.json"));
            var annotation = new ReviewAnnotation("a.cs", "7", false, ReviewAnnotationStore.Hash("return value;"), "return value;", true, DateTimeOffset.Now);
            store.Add(annotation);
            var read = Assert.Single(store.Load());
            var line = new DiffLineRow { Number = "7", Marker = " ", Kind = DiffLineKind.Context, Text = "return value;" };
            var row = new ChangedFileRow("a.cs", 1, 0, false, _ => Task.CompletedTask);
            row.SetSegments([new DiffSegment { Lines = [line], IsCollapsible = true }]);
            Assert.IsType<DiffCollapseRow>(Assert.Single(row.Lines));
            row.MarkAnnotations(candidate => ReviewAnnotationStore.Matches(read, "a.cs", candidate.Number, false, candidate.Text));
            Assert.Same(line, Assert.Single(row.Lines)); Assert.True(line.IsAnnotated);
            Assert.True(Assert.IsType<DiffPairRow>(Assert.Single(row.Pairs)).RightSource!.IsAnnotated);
            Assert.False(ReviewAnnotationStore.Matches(read, "a.cs", "7", false, "return anotherValue;"));
            store.Remove(annotation); Assert.Empty(store.Load());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
