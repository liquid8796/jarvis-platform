using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class BrowserStorageProfileTests
{
    [Fact]
    public void ExistingBrowserAccountsRemainReachableAndNewInstallsPinTheirOwnSharedJar()
    {
        var legacyRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-profile-tests", Guid.NewGuid().ToString("N"));
        var oldProfile = System.IO.Path.Combine(legacyRoot, "webview2");
        System.IO.Directory.CreateDirectory(oldProfile);
        System.IO.File.WriteAllText(System.IO.Path.Combine(oldProfile, "Local State"), "untouched");
        Assert.Equal(oldProfile, BrowserStorageProfile.EngineDirectory(legacyRoot));
        Assert.Empty(BrowserStorageProfile.SharedPartition(oldProfile, legacyRoot));
        Assert.Equal("untouched", System.IO.File.ReadAllText(System.IO.Path.Combine(oldProfile, "Local State")));

        var freshRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jarvis-profile-tests", Guid.NewGuid().ToString("N"));
        var fresh = BrowserStorageProfile.EngineDirectory(freshRoot);
        Assert.Equal(System.IO.Path.Combine(freshRoot, "engine"), fresh);
        System.IO.Directory.CreateDirectory(fresh);
        System.IO.File.WriteAllText(System.IO.Path.Combine(fresh, "Local State"), "created by Chromium after first launch");
        Assert.Equal(fresh, BrowserStorageProfile.EngineDirectory(freshRoot));
        Assert.Equal(BrowserStorageProfile.Partition("shared", freshRoot, ""), BrowserStorageProfile.SharedPartition(fresh, freshRoot));
    }

    [Fact]
    public void SharedUsesTheWorkspaceUnlessTheMeasuredExternalBrowsingRolloutIsEnabled()
    {
        Assert.Equal(BrowserStorageProfile.Partition("shared", @"C:\one", "one"),
            BrowserStorageProfile.Partition("shared", @"C:\one", "two"));
        Assert.NotEqual(BrowserStorageProfile.Partition("shared", @"C:\one", "one"),
            BrowserStorageProfile.Partition("shared", @"D:\two", "two"));
        Assert.Equal("persist:launch-preview-cowork-shared",
            BrowserStorageProfile.Partition("shared", @"C:\one", "one", externalBrowsingEnabled: true));
    }

    [Fact]
    public void SeparateFollowsTheSessionAcrossDirectoryChangesWithoutTakingAnotherSessionsJar()
    {
        var first = BrowserStorageProfile.Partition("session", @"C:\one", "session-1");
        Assert.Equal("persist:launch-preview-session-session-1", first);
        Assert.Equal(first, BrowserStorageProfile.Partition("session", @"D:\worktree", "session-1"));
        Assert.NotEqual(first, BrowserStorageProfile.Partition("session", @"C:\one", "session-2"));
    }

    [Fact]
    public void DontKeepUsesAWorkspaceJarInMemoryAndDoesNotNameThePersistentJar()
    {
        var temporary = BrowserStorageProfile.Partition("none", @"C:\one", "session-1");
        Assert.StartsWith("launch-preview-", temporary, StringComparison.Ordinal);
        Assert.Equal(temporary, BrowserStorageProfile.Partition("none", @"C:\one\", "session-2"));
        Assert.NotEqual(temporary, BrowserStorageProfile.Partition("none", @"C:\two", "session-1"));
        Assert.NotEqual(temporary, BrowserStorageProfile.Partition("shared", @"C:\one", "session-1"));
    }
}
