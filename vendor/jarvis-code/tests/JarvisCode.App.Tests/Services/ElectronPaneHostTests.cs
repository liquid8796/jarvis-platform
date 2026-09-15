using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// A fact that needs the pinned Electron runtime on this machine. It skips
/// rather than fails where the runtime has not been fetched, because the
/// download is 149 MB and a clean checkout should not pay for it to run the
/// suite — but it never skips silently: the reason names what is missing.
/// </summary>
internal sealed class ElectronFactAttribute : NativeUiFactAttribute
{
    public ElectronFactAttribute()
    {
        if (Skip is null && !new ElectronRuntime().IsInstalled)
        {
            Skip = $"Electron {ElectronRuntime.Version} is not installed at {ElectronRuntime.DefaultRoot}; " +
                   "open the Browser pane once, or run the app, to fetch it.";
        }
    }
}

/// <summary>
/// Drives the real engine process over its real pipe. These are integration
/// tests on purpose: the whole point of the class is the transport, and a
/// mocked pipe would have passed just as happily against the stdio transport
/// that could never have worked on Windows.
/// </summary>
public sealed partial class ElectronPaneHostTests
{
    private static string AppDirectory
    {
        get
        {
            // The engine app is copied beside the test binary by the same
            // Content entry that ships it with the app.
            var beside = Path.Combine(AppContext.BaseDirectory, "Assets", "ElectronHost");
            if (File.Exists(Path.Combine(beside, "main.js")))
            {
                return beside;
            }

            // Falling back to the source tree keeps the test meaningful when the
            // test project does not copy the app's content.
            var here = new DirectoryInfo(AppContext.BaseDirectory);
            while (here is not null && !Directory.Exists(Path.Combine(here.FullName, "src")))
            {
                here = here.Parent;
            }

            return Path.Combine(
                here?.FullName ?? ".", "src", "JarvisCode.App", "Assets", "ElectronHost");
        }
    }

    /// <summary>A hung engine must fail the test rather than block the suite.</summary>
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(90));

    private static ElectronPaneHost NewHost(out string userData)
    {
        userData = Path.Combine(Path.GetTempPath(), "jarvis-pane-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(userData);
        return new ElectronPaneHost(new ElectronRuntime(), AppDirectory, userData);
    }

    [ElectronFact]
    public async Task TheEngineIsTheBuildTheReferenceDesktopShips()
    {
        await using var host = NewHost(out _);

        var versions = await host.RequestAsync("app.versions", null, Deadline().Token);

        Assert.Equal(ElectronRuntime.Version, versions["electron"]?.GetValue<string>());
        Assert.Equal(ElectronRuntime.ChromiumVersion, versions["chrome"]?.GetValue<string>());
        Assert.True(host.IsRunning);
    }

    [ElectronFact]
    public async Task TheHostWindowHasARealNativeHandleToReparent()
    {
        await using var host = NewHost(out _);

        var created = await host.RequestAsync(
            "host.create", new JsonObject { ["show"] = false }, Deadline().Token);

        var hwnd = created["hwnd"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(hwnd));
        Assert.True(ulong.TryParse(hwnd, out var handle) && handle != 0, $"hwnd was '{hwnd}'");
    }

    [ElectronFact]
    public async Task ATabRendersAndAnswersTheCdpDomainsTheReferenceUses()
    {
        using var cts = Deadline();
        var token = cts.Token;
        await using var host = NewHost(out _);
        await host.RequestAsync("host.create", new JsonObject { ["show"] = false }, token);

        var tab = (await host.RequestAsync(
            "tab.create", new JsonObject { ["foreground"] = true }, token))["tabId"]!.GetValue<string>();

        await host.RequestAsync("tab.navigate", new JsonObject
        {
            ["tabId"] = tab,
            ["url"] = "data:text/html,<title>T</title><h1>hello</h1><button aria-label=\"Go\">B</button>",
        }, token);

        // These four are exactly what the WebView2 pane could not do, and are
        // the reason the engine was swapped: the accessibility tree, the DOM
        // domain, the native element-picker overlay, and object-group release.
        var ax = await Cdp(host, tab, "Accessibility.getFullAXTree", token);
        Assert.True((ax["nodes"] as JsonArray)?.Count > 0);

        var dom = await Cdp(host, tab, "DOM.getDocument", token);
        Assert.NotNull(dom["root"]);

        await Cdp(host, tab, "Overlay.enable", token);
        await Cdp(host, tab, "Overlay.setInspectMode", token, new JsonObject
        {
            ["mode"] = "searchForNode",
            ["highlightConfig"] = new JsonObject { ["showInfo"] = true },
        });

        await Cdp(host, tab, "Runtime.releaseObjectGroup", token, new JsonObject { ["objectGroup"] = "probe" });
    }

    [ElectronFact]
    public async Task ABackgroundTabKeepsItsPageWhileOnlyTheActiveOneIsShown()
    {
        using var cts = Deadline();
        var token = cts.Token;
        await using var host = NewHost(out _);
        await host.RequestAsync("host.create", new JsonObject { ["show"] = false }, token);

        var first = (await host.RequestAsync("tab.create", new JsonObject { ["foreground"] = true }, token))
            ["tabId"]!.GetValue<string>();
        var second = (await host.RequestAsync("tab.create", new JsonObject { ["foreground"] = false }, token))
            ["tabId"]!.GetValue<string>();

        var listed = (await host.RequestAsync("tab.list", null, token))["tabs"] as JsonArray;
        Assert.Equal(2, listed!.Count);
        Assert.Equal(first, (await host.RequestAsync("tab.list", null, token))["activeTabId"]?.GetValue<string>());

        // The background tab still answers CDP, which is what "keeps running"
        // means for a pane tab the user has switched away from.
        var dom = await Cdp(host, second, "DOM.getDocument", token);
        Assert.NotNull(dom["root"]);

        var closed = await host.RequestAsync("tab.close", new JsonObject { ["tabId"] = second }, token);
        Assert.True(closed["found"]!.GetValue<bool>());
        Assert.False(closed["wasLast"]!.GetValue<bool>());

        var last = await host.RequestAsync("tab.close", new JsonObject { ["tabId"] = first }, token);
        Assert.True(last["wasLast"]!.GetValue<bool>());
    }

    [ElectronFact]
    public async Task AnUnknownTabAndAnUnknownCommandComeBackAsErrorsRatherThanHangs()
    {
        using var cts = Deadline();
        var token = cts.Token;
        await using var host = NewHost(out _);
        await host.RequestAsync("host.create", new JsonObject { ["show"] = false }, token);

        var noTab = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Cdp(host, "tab-999", "DOM.getDocument", token));
        Assert.Contains("no tab tab-999", noTab.Message, StringComparison.Ordinal);

        var noCommand = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.RequestAsync("bogus.command", null, token));
        Assert.Contains("unknown command", noCommand.Message, StringComparison.Ordinal);
    }

    [ElectronFact]
    public async Task TwoSurfacesGetTheirOwnWindowsAndTabsInOneEngine()
    {
        // The pane, the artifact tile, a question preview and the diagram
        // renderer are separate windows over one process. Each must get its own
        // window and its own tabs, and one closing must not take the other's.
        using var cts = Deadline();
        var token = cts.Token;
        await using var host = NewHost(out _);

        var first = await host.RequestAsync("host.create", new JsonObject { ["show"] = false }, token);
        var second = await host.RequestAsync("host.create", new JsonObject { ["show"] = false }, token);

        var firstWindow = first["windowId"]!.GetValue<string>();
        var secondWindow = second["windowId"]!.GetValue<string>();
        Assert.NotEqual(firstWindow, secondWindow);
        Assert.NotEqual(first["hwnd"]!.GetValue<string>(), second["hwnd"]!.GetValue<string>());

        var firstTab = (await host.RequestAsync(
            "tab.create", new JsonObject { ["windowId"] = firstWindow }, token))["tabId"]!.GetValue<string>();
        var secondTab = (await host.RequestAsync(
            "tab.create", new JsonObject { ["windowId"] = secondWindow }, token))["tabId"]!.GetValue<string>();

        var listed = await host.RequestAsync(
            "tab.list", new JsonObject { ["windowId"] = firstWindow }, token);
        var rows = (listed["tabs"] as JsonArray)!;
        Assert.Single(rows);
        Assert.Equal(firstTab, rows[0]!["tabId"]!.GetValue<string>());

        // Closing one surface's window leaves the other's tab answering.
        await host.RequestAsync("host.close", new JsonObject { ["windowId"] = firstWindow }, token);
        var survivor = await Cdp(host, secondTab, "DOM.getDocument", token);
        Assert.NotNull(survivor["root"]);
    }

    [ElectronFact]
    public async Task TheCommandsTheOtherSurfacesNeedAllAnswer()
    {
        // Each of these backs a surface that used to be its own browser control,
        // and each is a call the engine either has or does not - Page.printToPDF
        // looked right and is headless-only, which only running it showed.
        using var cts = Deadline();
        var token = cts.Token;
        await using var host = NewHost(out _);
        await host.RequestAsync("host.create", new JsonObject { ["show"] = false }, token);
        var tab = (await host.RequestAsync("tab.create", null, token))["tabId"]!.GetValue<string>();

        await host.RequestAsync(
            "session.setUserAgent", new JsonObject { ["userAgent"] = "Mozilla/5.0 JarvisTest" }, token);

        var cookies = await host.RequestAsync("session.setCookies", new JsonObject
        {
            ["cookies"] = new JsonArray(new JsonObject
            {
                ["url"] = "https://example.invalid/",
                ["name"] = "probe",
                ["value"] = "1",
            }),
        }, token);
        Assert.Equal(1, cookies["set"]!.GetValue<int>());

        var served = await host.RequestAsync("assets.serve", new JsonObject
        {
            ["host"] = "probe",
            ["directory"] = AppDirectory,
        }, token);
        Assert.Equal("jarvis-asset", served["scheme"]!.GetValue<string>());

        // The asset origin has to read as secure, or a page that sandboxes an
        // iframe (the diagram renderer does) cannot run in it.
        await host.RequestAsync(
            "tab.navigate", new JsonObject { ["tabId"] = tab, ["url"] = "jarvis-asset://probe/package.json" }, token);
        var secure = await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = "JSON.stringify({ ua: navigator.userAgent, secure: window.isSecureContext })",
            ["returnByValue"] = true,
        });
        var reported = secure["result"]!["value"]!.GetValue<string>();
        Assert.Contains("JarvisTest", reported, StringComparison.Ordinal);
        Assert.Contains("\"secure\":true", reported, StringComparison.Ordinal);

        var pdf = await host.RequestAsync("tab.printToPDF", new JsonObject { ["tabId"] = tab }, token);
        Assert.True(Convert.FromBase64String(pdf["data"]!.GetValue<string>()).Length > 1000);

        var found = await host.RequestAsync(
            "tab.find", new JsonObject { ["tabId"] = tab, ["query"] = "jarvis" }, token);
        Assert.True(found["requestId"]!.GetValue<int>() > 0);
    }

    [ElectronFact]
    public async Task DisposingTheHostTakesTheEngineProcessWithIt()
    {
        using var cts = Deadline();
        var token = cts.Token;
        var host = NewHost(out _);
        await host.RequestAsync("app.versions", null, token);

        // The one this host started, not every engine on the machine: other
        // tests run their own, and asking about theirs made this fail for a
        // process it was never responsible for.
        var engine = host.ProcessId;
        Assert.NotNull(engine);
        Assert.False(Process.GetProcessById(engine!.Value).HasExited);

        await host.DisposeAsync();

        // The engine treats the closed pipe as its shutdown signal; the kill is
        // only the fallback. Either way nothing of ours may outlive the host.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Process.GetProcessById(engine.Value).HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                // The process is gone entirely, which is the same answer.
                return;
            }

            await Task.Delay(250, token);
        }

        Assert.Fail("the engine process outlived the host that owned it");
    }

    private static async Task<JsonObject> Cdp(
        ElectronPaneHost host, string tabId, string method, CancellationToken token, JsonObject? parameters = null)
    {
        var answer = await host.RequestAsync("cdp.send", new JsonObject
        {
            ["tabId"] = tabId,
            ["method"] = method,
            ["params"] = parameters ?? [],
        }, token);

        return answer["result"] as JsonObject ?? [];
    }

    [ElectronFact]
    public async Task BrowserPartitionsSeparateAccountsWhileSharedWindowsSeeTheSameData()
    {
        using var cts = Deadline();
        await using var host = NewHost(out _);
        var token = cts.Token;
        var first = await StorageTab(host, "persist:launch-preview-session-first", token);
        var second = await StorageTab(host, "persist:launch-preview-session-second", token);
        var sharedA = await StorageTab(host, "persist:launch-preview-cowork-shared", token);
        var sharedB = await StorageTab(host, "persist:launch-preview-cowork-shared", token, popup: true);
        var legacy = await StorageTab(host, "", token);

        await PutStorage(host, first, "first-account", token);
        await PutStorage(host, sharedA, "shared-account", token);
        await PutStorage(host, legacy, "existing-account", token);

        Assert.Equal(("first-account", "first-account"), await ReadStorage(host, first.Tab, token));
        Assert.Equal((null, null), await ReadStorage(host, second.Tab, token));
        Assert.Equal(("shared-account", "shared-account"), await ReadStorage(host, sharedB.Tab, token));

        // Clearing one pane can clear its shared siblings, but no unrelated
        // session and never the legacy default profile used by existing logins.
        await host.RequestAsync("session.clear", new JsonObject { ["windowId"] = sharedB.Window }, token);
        Assert.Equal((null, null), await ReadStorage(host, sharedA.Tab, token));
        Assert.Equal(("first-account", "first-account"), await ReadStorage(host, first.Tab, token));
        Assert.Equal(("existing-account", "existing-account"), await ReadStorage(host, legacy.Tab, token));
    }

    [ElectronFact]
    public async Task BrowserPersistenceSurvivesRestartButDontKeepLeavesSavedAccountsUntouched()
    {
        using var cts = Deadline();
        var token = cts.Token;
        string userData;
        await using (var host = NewHost(out userData))
        {
            var persistent = await StorageTab(host, "persist:launch-preview-session-one", token);
            var temporary = await StorageTab(host, "launch-preview-temporary", token);
            var legacy = await StorageTab(host, "", token);
            await PutStorage(host, persistent, "saved-account", token);
            await PutStorage(host, temporary, "temporary-account", token);
            await PutStorage(host, legacy, "legacy-account", token);
        }

        await using (var host = new ElectronPaneHost(new ElectronRuntime(), AppDirectory, userData))
        {
            var persistent = await StorageTab(host, "persist:launch-preview-session-one", token);
            var temporary = await StorageTab(host, "launch-preview-temporary", token);
            var legacy = await StorageTab(host, "", token);
            Assert.Equal(("saved-account", "saved-account"), await ReadStorage(host, persistent.Tab, token));
            Assert.Equal((null, null), await ReadStorage(host, temporary.Tab, token));
            Assert.Equal(("legacy-account", "legacy-account"), await ReadStorage(host, legacy.Tab, token));
        }
    }

    [ElectronFact]
    public async Task OpeningATransientSurfaceNeverClearsTheDefaultProfilesAccount()
    {
        using var cts = Deadline();
        await using var host = NewHost(out _);
        var token = cts.Token;
        var legacy = await StorageTab(host, "", token);
        await PutStorage(host, legacy, "keep-me", token);

        await using var surface = new ElectronPaneSession(
            host, System.Windows.Threading.Dispatcher.CurrentDispatcher, ownsHost: false);
        await surface.EnsureHostWindowAsync(persistSessions: false, cancellationToken: token);
        var transientTab = await surface.CreateTabAsync(foreground: true, cancellationToken: token);
        await host.RequestAsync("tab.navigate", new JsonObject
        {
            ["tabId"] = transientTab, ["url"] = "jarvis-asset://storage-probe/package.json",
        }, token);
        Assert.Equal((null, null), await ReadStorage(host, transientTab, token));
        Assert.Equal(("keep-me", "keep-me"), await ReadStorage(host, legacy.Tab, token));
    }

    private static async Task<(string Window, string Tab)> StorageTab(
        ElectronPaneHost host, string partition, CancellationToken token, bool popup = false)
    {
        await host.RequestAsync("assets.serve", new JsonObject
        {
            ["host"] = "storage-probe", ["directory"] = AppDirectory,
        }, token);
        var window = (await host.RequestAsync("host.create", new JsonObject
        {
            ["show"] = false, ["partition"] = partition,
        }, token))["windowId"]!.GetValue<string>();
        var tab = (await host.RequestAsync("tab.create", new JsonObject
        {
            ["windowId"] = window, ["foreground"] = true, ["popup"] = popup,
        }, token))["tabId"]!.GetValue<string>();
        await host.RequestAsync("tab.navigate", new JsonObject
        {
            ["tabId"] = tab, ["url"] = "jarvis-asset://storage-probe/package.json",
        }, token);
        return (window, tab);
    }

    private static async Task PutStorage(
        ElectronPaneHost host, (string Window, string Tab) target, string value, CancellationToken token)
    {
        var cookie = await host.RequestAsync("session.setCookies", new JsonObject
        {
            ["windowId"] = target.Window,
            ["cookies"] = new JsonArray(new JsonObject
            {
                ["url"] = "https://profile-test.invalid/", ["name"] = "account", ["value"] = value,
                ["expirationDate"] = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            }),
        }, token);
        Assert.Equal(1, cookie["set"]!.GetValue<int>());
        await Cdp(host, target.Tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = "localStorage.setItem('account', " + System.Text.Json.JsonSerializer.Serialize(value) + ")",
            ["returnByValue"] = true,
        });
    }

    private static async Task<(string? LocalStorage, string? Cookie)> ReadStorage(
        ElectronPaneHost host, string tab, CancellationToken token)
    {
        var local = await Cdp(host, tab, "Runtime.evaluate", token, new JsonObject
        {
            ["expression"] = "localStorage.getItem('account')", ["returnByValue"] = true,
        });
        Assert.Null(local["exceptionDetails"]);
        var cookies = await Cdp(host, tab, "Network.getCookies", token, new JsonObject
        {
            ["urls"] = new JsonArray("https://profile-test.invalid/"),
        });
        return (local["result"]?["value"]?.GetValue<string>(),
            (cookies["cookies"] as JsonArray)?.FirstOrDefault(c => c?["name"]?.GetValue<string>() == "account")?["value"]?.GetValue<string>());
    }
}
