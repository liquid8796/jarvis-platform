using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The reference's per-app tiers, grant flags and monitor switching. Everything
/// here is decided from settings and names, so none of it touches the desktop.
/// </summary>
public class ComputerUseGrantsTests
{
    private static readonly ToolExecutionContext Context = new() { WorkingDirectory = @"C:\" };

    private static UiSettings Granted(string app, AppTier tier)
    {
        var settings = new UiSettings { ComputerUseRequireGrants = true };
        settings.ComputerUseGrantedApps.Add(app);
        settings.ComputerUseGrantedTiers[app] = ComputerUseGrants.TierName(tier);
        return settings;
    }

    // ---- classification ----

    [Theory]
    [InlineData("chrome", "browser")]
    [InlineData("msedge.exe", "browser")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", "browser")]
    [InlineData("Code", "terminal")]
    [InlineData("powershell", "terminal")]
    [InlineData("devenv", "terminal")]
    [InlineData("explorer", "shell")]
    [InlineData("taskmgr", "shell")]
    [InlineData("binance", "trading")]
    [InlineData("notepad", null)]
    [InlineData("JarvisCode.App", null)]
    public void CategoriesFollowTheReferenceNameSets(string app, string? expected)
        => Assert.Equal(expected, ComputerUseGrants.Category(app));

    [Theory]
    [InlineData("chrome", AppTier.Read)]
    [InlineData("tradingview", AppTier.Read)]
    [InlineData("wt", AppTier.Click)]
    [InlineData("explorer", AppTier.Click)]
    [InlineData("notepad", AppTier.Full)]
    public void ProposedTiersMatchTheCategory(string app, AppTier expected)
        => Assert.Equal(expected, ComputerUseGrants.ProposedTier(app));

    [Fact]
    public void TheRestrictedTierNoteExplainsEveryRestrictedCategoryPresent()
    {
        var note = ComputerUseGrants.RestrictedTierNote(["chrome", "code", "notepad"])!;

        Assert.Contains("Terminals and IDEs can only be granted in 'click' mode", note);
        Assert.Contains("Browsers can only be granted in 'read' mode", note);
        Assert.Contains("the user approves once", note);
        Assert.DoesNotContain("Windows shell", note);
    }

    [Fact]
    public void OrdinaryApplicationsGetNoRestrictionNote()
        => Assert.Null(ComputerUseGrants.RestrictedTierNote(["notepad", "paint"]));

    // ---- tiers ----

    [Fact]
    public void AGrantStoredBeforeTiersExistedKeepsTheFullAccessItHad()
    {
        var settings = new UiSettings { ComputerUseRequireGrants = true };
        settings.ComputerUseGrantedApps.Add("notepad");

        Assert.Equal(AppTier.Full, ComputerUseGrants.GrantedTier(settings, null, "notepad"));
        Assert.Null(ComputerUseGrants.GrantedTier(settings, null, "paint"));
    }

    [Theory]
    [InlineData(AppTier.Read, InputNeed.Pointer, true)]
    [InlineData(AppTier.Read, InputNeed.Click, false)]
    [InlineData(AppTier.Read, InputNeed.FullMouse, false)]
    [InlineData(AppTier.Read, InputNeed.Keyboard, false)]
    [InlineData(AppTier.Click, InputNeed.Pointer, true)]
    [InlineData(AppTier.Click, InputNeed.Click, true)]
    [InlineData(AppTier.Click, InputNeed.FullMouse, false)]
    [InlineData(AppTier.Click, InputNeed.Keyboard, false)]
    [InlineData(AppTier.Full, InputNeed.FullMouse, true)]
    [InlineData(AppTier.Full, InputNeed.Keyboard, true)]
    public void EachTierAllowsExactlyItsOwnInput(AppTier tier, InputNeed need, bool allowed)
    {
        var refusal = ComputerUseGrants.RefusalFor(Granted("someapp", tier), null, "someapp", need);

        Assert.Equal(allowed, refusal is null);
    }

    [Fact]
    public void ARefusalNamesTheTierAndPointsAtRequestAccess()
    {
        var read = ComputerUseGrants.RefusalFor(Granted("chrome", AppTier.Read), null, "chrome", InputNeed.Keyboard)!;
        var click = ComputerUseGrants.RefusalFor(Granted("code", AppTier.Click), null, "code", InputNeed.Keyboard)!;
        var shell = ComputerUseGrants.RefusalFor(Granted("explorer", AppTier.Click), null, "explorer", InputNeed.Keyboard)!;

        Assert.Contains("tier \"read\"", read);
        Assert.Contains("request_access", read);
        Assert.Contains("tier \"click\"", click);
        Assert.Contains("Windows desktop shell", shell);
        Assert.Contains("use the PowerShell tool", shell);
    }

    [Fact]
    public void AnUngrantedApplicationIsNamedInTheRefusal()
    {
        var refusal = ComputerUseGrants.RefusalFor(Granted("notepad", AppTier.Full), null, "paint", InputNeed.Click)!;

        Assert.Contains("'paint' has no input grant", refusal);
        Assert.Contains("request_access", refusal);
    }

    [Fact]
    public void NothingIsCheckedWhileGrantsAreNotRequired()
    {
        var off = new UiSettings { ComputerUseRequireGrants = false };

        Assert.Null(ComputerUseGrants.CheckForeground(off, null, InputNeed.Keyboard));
        Assert.Null(ComputerUseGrants.CheckClickTarget(off, null, new System.Drawing.Point(0, 0), InputNeed.Keyboard));
        Assert.Null(ComputerUseGrants.ClipboardRefusal(off, null, read: true));
    }

    // ---- grant flags ----

    [Fact]
    public void ClipboardToolsNeedTheirOwnFlag()
    {
        var settings = new UiSettings { ComputerUseRequireGrants = true };

        Assert.Contains("`clipboardRead` grant", ComputerUseGrants.ClipboardRefusal(settings, null, read: true)!);
        Assert.Contains("`clipboardWrite` grant", ComputerUseGrants.ClipboardRefusal(settings, null, read: false)!);

        settings.ComputerUseClipboardRead = true;
        Assert.Null(ComputerUseGrants.ClipboardRefusal(settings, null, read: true));
        Assert.NotNull(ComputerUseGrants.ClipboardRefusal(settings, null, read: false));
    }

    [Theory]
    [InlineData("alt+tab", true)]
    [InlineData("Alt+Tab", true)]
    [InlineData("shift+alt+tab", true)]     // canonical order is ctrl, alt, shift, meta
    [InlineData("win+r", true)]
    [InlineData("cmd+e", true)]             // cmd/super/windows all normalise to meta
    [InlineData("ctrl+alt+delete", true)]
    [InlineData("ctrl+alt+del", true)]      // del is an alias for delete
    [InlineData("ctrl+escape", true)]
    [InlineData("ctrl+c", false)]
    [InlineData("win+p", false)]
    [InlineData("enter", false)]
    public void SystemShortcutsAreRecognisedInAnyOrderOrSpelling(string chord, bool expected)
        => Assert.Equal(expected, ComputerUseGrants.IsSystemShortcut(chord));

    [Fact]
    public async Task ASystemChordIsRefusedUntilTheFlagIsGranted()
    {
        // The foreground check runs first, so the app in front has to be granted
        // for the chord gate to be the thing under test — and it can change under
        // an interactive machine, so the assertion only holds when it did not.
        var foreground = ComputerUseExtras.ForegroundProcessName();
        var settings = Granted(foreground ?? "unknown", AppTier.Full);
        var tool = new ComputerBatchTool(new ComputerUseService(), () => settings);
        var batch = new JsonObject
        {
            ["actions"] = new JsonArray(new JsonObject { ["action"] = "key", ["text"] = "alt+f4" }),
        };

        var refused = await tool.ExecuteAsync(batch, Context, default);

        Assert.True(refused.IsError);
        if (foreground is not null && ComputerUseExtras.ForegroundProcessName() == foreground)
        {
            Assert.Contains("system-level shortcut", refused.Content);
            Assert.Contains("systemKeyCombos", refused.Content);
        }

        settings.ComputerUseSystemKeyCombos = true;
        var allowedNow = await tool.ExecuteAsync(
            new JsonObject
            {
                ["actions"] = new JsonArray(new JsonObject { ["action"] = "cursor_position" }),
            },
            Context,
            default);
        Assert.False(allowedNow.IsError);
    }

    // ---- names and paths ----

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", "notepad")]
    [InlineData("notepad.exe", "notepad")]
    [InlineData("\"chrome.exe\"", "chrome")]
    [InlineData("  paint  ", "paint")]
    public void GrantNamesAreNormalisedToAProcessName(string given, string expected)
        => Assert.Equal(expected, ComputerUseGrants.Normalize(given));

    // ---- switch_display / list_granted_applications ----

    private static ITool Tool(string name, UiSettingsStore store) =>
        ComputerUseTools.CreateIfEnabled(store).First(t => t.Name == name);

    private static UiSettingsStore Store()
    {
        var store = new UiSettingsStore(Path.Combine(
            Path.GetTempPath(), $"jarvis-grants-{Guid.NewGuid():N}.json"));
        store.Current.ComputerUseEnabled = true;
        return store;
    }

    [Fact]
    public async Task SwitchDisplayAcceptsAutoAndRejectsAnUnknownMonitor()
    {
        var store = Store();
        var tool = Tool("switch_display", store);

        var auto = await tool.ExecuteAsync(new JsonObject { ["display"] = "auto" }, Context, default);
        Assert.False(auto.IsError);
        Assert.Equal("Returned to automatic monitor selection. Call screenshot to continue.", auto.Content);

        var unknown = await tool.ExecuteAsync(new JsonObject { ["display"] = "Nonexistent 4K" }, Context, default);
        Assert.True(unknown.IsError);
        Assert.Contains(
            ComputerUseService.Displays().Count < 2
                ? "Only one monitor is connected. There is nothing to switch to."
                : "No monitor named \"Nonexistent 4K\" is connected. Available monitors:",
            unknown.Content);
    }

    [Fact]
    public async Task ListGrantedApplicationsReportsTiersAndFlags()
    {
        var store = Store();
        var tool = Tool("list_granted_applications", store);

        Assert.Contains("not required", (await tool.ExecuteAsync(new JsonObject(), Context, default)).Content);

        // The reference answers with JSON, so an empty allowlist is an empty
        // array rather than a sentence about there being none.
        store.Current.ComputerUseRequireGrants = true;
        Assert.Contains(
            "\"allowedApps\":[]",
            (await tool.ExecuteAsync(new JsonObject(), Context, default)).Content);

        store.Current.ComputerUseGrantedApps.Add("chrome");
        store.Current.ComputerUseGrantedTiers["chrome"] = "read";
        store.Current.ComputerUseClipboardRead = true;
        var listed = await tool.ExecuteAsync(new JsonObject(), Context, default);

        Assert.Contains(
            "{\"bundleId\":\"chrome\",\"displayName\":\"chrome\",\"tier\":\"read\"}",
            listed.Content);
        Assert.Contains("\"clipboardRead\":true", listed.Content);
        Assert.Contains("\"systemKeyCombos\":false", listed.Content);
    }

    [Fact]
    public void MonitorsAreNamedSoSwitchDisplayHasSomethingToTake()
    {
        foreach (var display in ComputerUseService.Displays())
        {
            Assert.False(string.IsNullOrWhiteSpace(display.Name));
            Assert.Contains($"\"{display.Name}\"", display.Describe());
        }
    }
}
