using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>Check the real Windows shell styles, not merely the constructor option's spelling.</summary>
[Collection("Native window tests")]
public sealed class ElectronTaskbarTests
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsChild = 0x40000000;
    private const int WsExToolWindow = 0x80;
    private const int WsExAppWindow = 0x40000;
    private const int WsExNoActivate = 0x08000000;

    [ElectronFact]
    public async Task AnOffscreenHostStaysOutOfTaskbarBeforeAndAfterNativeShowHide()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var host = NewHost();
        var created = await host.RequestAsync("host.create", new JsonObject
        {
            ["show"] = false, ["x"] = -10000, ["y"] = -10000,
        }, deadline.Token);
        var hwnd = Handle(created);
        var window = created["windowId"]!.GetValue<string>();
        AssertTaskbarExcludedAndFocusable(hwnd);
        Assert.False(IsWindowVisible(hwnd));

        for (var cycle = 0; cycle < 2; cycle++)
        {
            await host.RequestAsync("host.show", Window(window), deadline.Token);
            Assert.True(IsWindowVisible(hwnd));
            AssertTaskbarExcludedAndFocusable(hwnd);
            await host.RequestAsync("host.hide", Window(window), deadline.Token);
            Assert.False(IsWindowVisible(hwnd));
            AssertTaskbarExcludedAndFocusable(hwnd);
        }

        // Some callers opt into visibility directly at creation instead.
        var visible = await host.RequestAsync("host.create", new JsonObject
        {
            ["show"] = true, ["x"] = -10000, ["y"] = -10000,
        }, deadline.Token);
        Assert.True(IsWindowVisible(Handle(visible)));
        AssertTaskbarExcludedAndFocusable(Handle(visible));
    }

    [ElectronFact]
    public async Task AnEmbeddedHostRemainsVisibleAndInteractiveWithoutItsOwnTaskbarIdentity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var host = NewHost();
        var created = await host.RequestAsync("host.create", new JsonObject
        {
            ["show"] = false, ["x"] = -10000, ["y"] = -10000,
        }, deadline.Token);
        var hwnd = Handle(created);
        var window = created["windowId"]!.GetValue<string>();
        var tab = (await host.RequestAsync("tab.create", Window(window), deadline.Token))
            ["tabId"]!.GetValue<string>();
        await host.RequestAsync("tab.navigate", new JsonObject
        {
            ["tabId"] = tab,
            ["url"] = "data:text/html,<button id='probe' onclick='this.textContent=\"clicked\"'>ready</button>",
        }, deadline.Token);

        WpfTestThread.Run(() =>
        {
            using var view = new ElectronPaneView();
            var wrapper = new Window
            {
                Width = 500, Height = 320, Left = -10000, Top = -10000,
                ShowInTaskbar = false, ShowActivated = false, Content = view,
            };
            view.Attach(hwnd);
            wrapper.Show();
            wrapper.UpdateLayout();
            Assert.Equal(view.ContainerWindow, GetParent(hwnd));
            Assert.NotEqual(IntPtr.Zero, view.ContainerWindow);
            Assert.NotEqual(0, GetWindowLong(hwnd, GwlStyle) & WsChild);
            Assert.True(IsWindowVisible(hwnd));
            AssertTaskbarExcludedAndFocusable(hwnd);

            host.RequestAsync("host.hide", Window(window), deadline.Token).GetAwaiter().GetResult();
            Assert.False(IsWindowVisible(hwnd));
            host.RequestAsync("host.show", Window(window), deadline.Token).GetAwaiter().GetResult();
            Assert.True(IsWindowVisible(hwnd));
            Assert.Equal(view.ContainerWindow, GetParent(hwnd));
            AssertTaskbarExcludedAndFocusable(hwnd);

            // Detaching while rebuilding a pane must not turn its live browser
            // into an independent application window, even before reattachment.
            host.RequestAsync("host.hide", Window(window), deadline.Token).GetAwaiter().GetResult();
            wrapper.Content = null;
            view.Dispose();
            Assert.Equal(GetDesktopWindow(), GetParent(hwnd));
            AssertTaskbarExcludedAndFocusable(hwnd);
            using var replacement = new ElectronPaneView();
            try
            {
                wrapper.Content = replacement;
                replacement.Attach(hwnd);
                wrapper.UpdateLayout();
                host.RequestAsync("host.show", Window(window), deadline.Token).GetAwaiter().GetResult();
                Assert.True(IsWindowVisible(hwnd));
                Assert.Equal(replacement.ContainerWindow, GetParent(hwnd));
                AssertTaskbarExcludedAndFocusable(hwnd);

                var evaluated = host.RequestAsync("cdp.send", new JsonObject
                {
                    ["tabId"] = tab, ["method"] = "Runtime.evaluate",
                    ["params"] = new JsonObject
                    {
                        ["expression"] = "document.getElementById('probe').focus(); document.activeElement.click(); document.activeElement.textContent",
                        ["returnByValue"] = true,
                    },
                }, deadline.Token).GetAwaiter().GetResult();
                Assert.Equal("clicked", evaluated["result"]?["result"]?["value"]?.GetValue<string>());
                Assert.Null(evaluated["result"]?["exceptionDetails"]);
            }
            finally
            {
                // HwndHost releases its child to the desktop on disposal.
                // Hide first so this offscreen fixture cannot flash there.
                ShowWindow(hwnd, 0);
            }
        });
    }

    private static ElectronPaneHost NewHost()
    {
        var appDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "ElectronHost");
        Assert.True(File.Exists(Path.Combine(appDirectory, "main.js")), "The shipping Electron host must accompany the fixture.");
        var userData = Path.Combine(Path.GetTempPath(), "jarvis-taskbar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userData);
        return new ElectronPaneHost(new ElectronRuntime(), appDirectory, userData);
    }

    private static JsonObject Window(string id) => new() { ["windowId"] = id };
    private static IntPtr Handle(JsonNode created) =>
        (IntPtr)long.Parse(created["hwnd"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    private static void AssertTaskbarExcludedAndFocusable(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GwlExStyle);
        Assert.NotEqual(0, style & WsExToolWindow);
        Assert.Equal(0, style & WsExAppWindow);
        Assert.Equal(0, style & WsExNoActivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
