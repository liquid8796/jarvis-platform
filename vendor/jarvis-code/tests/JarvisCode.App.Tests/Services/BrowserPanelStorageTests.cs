using System.IO;
using System.Windows;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Panels;

namespace JarvisCode.App.Tests.Services;

[Collection("Native window tests")]
public sealed class BrowserPanelStorageTests
{
    [NativeUiFact]
    public void SwitchingSessionsRestoresTheirOwnTabsAndChangingStorageNeverReusesThePreviousContext()
    {
        WpfTestThread.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "jarvis-browser-context-tests", Guid.NewGuid().ToString("N"));
            var settings = new UiSettingsStore(Path.Combine(root, "ui-settings.json"));
            settings.Current.BrowserPreviewStorage = "session";
            var panel = new BrowserPanel();
            panel.Configure(root, Path.Combine(root, "engine"), settings, "first");
            var first = panel.ActiveTab!;
            first.Url = "https://first.invalid/";
            panel.NewTab();
            var firstActive = panel.ActiveTab;

            panel.Configure(root, Path.Combine(root, "engine"), settings, "second");
            Assert.Single(panel.ListTabs());
            Assert.Empty(panel.ActiveTab!.Url);
            Assert.NotSame(first, panel.ActiveTab);
            var second = panel.ActiveTab;

            panel.Configure(root, Path.Combine(root, "engine"), settings, "first");
            Assert.Equal(2, panel.ListTabs().Count);
            Assert.Same(firstActive, panel.ActiveTab);
            Assert.Equal("https://first.invalid/", panel.ListTabs()[0].Url);

            settings.Current.BrowserPreviewStorage = "shared";
            settings.Save();
            Assert.Single(panel.ListTabs());
            Assert.NotSame(firstActive, panel.ActiveTab);

            settings.Current.BrowserPreviewStorage = "session";
            settings.Save();
            Assert.Same(firstActive, panel.ActiveTab);
            panel.Configure(root, Path.Combine(root, "engine"), settings, "second");
            Assert.Same(second, panel.ActiveTab);
            panel.DisposeTabs();

            // A native inline document must be clipped by its scrolling
            // ancestor, including when it is only partly on screen.
            var native = new JarvisCode.App.Controls.ElectronPaneView { Height = 150 };
            var content = new System.Windows.Controls.StackPanel();
            content.Children.Add(new System.Windows.Controls.Border { Height = 80 });
            content.Children.Add(native);
            content.Children.Add(new System.Windows.Controls.Border { Height = 300 });
            var scroll = new System.Windows.Controls.ScrollViewer { Content = content, Height = 100, Width = 200 };
            var window = new Window { Content = scroll, SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None, ShowActivated = false, Left = -10000, Top = -10000 };
            window.Show();
            window.UpdateLayout();
            Assert.True(native.ViewportClip().Height < native.ActualHeight);
            scroll.ScrollToVerticalOffset(100);
            window.UpdateLayout();
            Assert.True(native.ViewportClip().Y > 0);
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(native.ContainerWindow, region));
                GetRgnBox(region, out var bounds);
                Assert.True(bounds.Top > 0);
            }
            finally { DeleteObject(region); }
            window.Close();

            var markdown = new JarvisCode.App.Controls.MarkdownView { IsStreaming = true, Markdown = "Stable paragraph.\n\nChanging tail" };
            var render = typeof(JarvisCode.App.Controls.MarkdownView).GetMethod("Render", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null)!;
            render.Invoke(markdown, null);
            var children = ((System.Windows.Controls.Panel)markdown.Content).Children;
            var stable = children[0];
            var changing = children[1];
            markdown.Markdown = "Stable paragraph.\n\nChanging tail continues";
            render.Invoke(markdown, null);
            Assert.Same(stable, children[0]);
            Assert.NotSame(changing, children[1]);
        });
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int GetRgnBox(IntPtr region, out NativeRect bounds);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr region);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);
}
