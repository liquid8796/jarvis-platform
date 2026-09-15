using System.IO;
using JarvisCode.App.Services;
using JarvisCode.App.Theming;

namespace JarvisCode.App.Tests.Services;

public class UiSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jarvis-ui-test-" + Guid.NewGuid().ToString("N")[..8]);

    private string FilePath => Path.Combine(_dir, "ui-settings.json");

    [Fact]
    public void RoundTripsAllFields()
    {
        var store = new UiSettingsStore(FilePath);
        store.Current.ActiveTheme = "nord";
        store.Current.ThemeMode = ThemeMode.Dark;
        store.Current.ChatFont = "dyslexia";
        store.Current.WindowWidth = 1400;
        store.Current.SidebarCollapsed = true;
        store.Current.LastSurface = "code";
        store.Current.QuickEntryEnabled = false;
        store.Current.UserDisplayName = "Patrick";
        store.Current.ComputerUseEnabled = false;
        store.Current.ComputerUseInChat = true;
        store.Save();

        var reloaded = new UiSettingsStore(FilePath).Current;
        Assert.Equal("nord", reloaded.ActiveTheme);
        Assert.Equal(ThemeMode.Dark, reloaded.ThemeMode);
        Assert.Equal("dyslexia", reloaded.ChatFont);
        Assert.Equal(1400, reloaded.WindowWidth);
        Assert.True(reloaded.SidebarCollapsed);
        Assert.Equal("code", reloaded.LastSurface);
        Assert.False(reloaded.QuickEntryEnabled);
        Assert.Equal("Patrick", reloaded.UserDisplayName);
        Assert.False(reloaded.ComputerUseEnabled);
        Assert.True(reloaded.ComputerUseInChat);
    }

    [Fact]
    public void DesktopControlDefaultsToOnEverywhereButChat()
    {
        var defaults = new UiSettingsStore(FilePath).Current;

        Assert.True(defaults.ComputerUseEnabled);
        Assert.False(defaults.ComputerUseInChat);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{broken json!!");
        var store = new UiSettingsStore(FilePath);
        Assert.Equal("", store.Current.ActiveTheme);
        Assert.Equal(ThemeMode.System, store.Current.ThemeMode);
        Assert.True(store.Current.QuickEntryEnabled);
    }

    [Fact]
    public void MissingFileYieldsDefaults()
    {
        var store = new UiSettingsStore(FilePath);
        // The reference's own default is "Never": its ccAutoArchiveInactiveDays
        // reads `?? 0`, and 0 is the option its select labels "Never".
        Assert.Equal(0, store.Current.AutoArchiveDays);
        Assert.Equal("chat", store.Current.LastSurface);
    }

    /// <summary>
    /// Settings that have been removed still sit in every existing user's file
    /// (panelTabs opened the Terminal panel at startup until it was dropped), so
    /// an unknown key has to be skipped rather than fail the whole load.
    /// </summary>
    [Fact]
    public void UnknownKeysAreIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"panelTabs":true,"lastSurface":"code","sidebarWidth":320}""");

        var store = new UiSettingsStore(FilePath);

        Assert.Equal("code", store.Current.LastSurface);
        Assert.Equal(320, store.Current.SidebarWidth);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
