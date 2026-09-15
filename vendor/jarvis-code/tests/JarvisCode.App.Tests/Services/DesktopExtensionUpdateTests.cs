using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class DesktopExtensionUpdateTests
{
    [Fact]
    public async Task LocalFilesAreIneligibleAndTheOffTogglePerformsNoDirectoryRequest()
    {
        using var profile = new TestProfile();
        profile.Install("1.0.0");
        var directory = new FakeDirectory(Bundle("2.0.0"));
        using (var updater = new DesktopExtensionUpdates(profile.Paths, () => true, directory))
            Assert.Equal(0, (await updater.CheckNowAsync()).Checked);
        Assert.Equal(0, directory.Requests);
        profile.Install("1.0.0", "catalog.sample");
        using (var updater = new DesktopExtensionUpdates(profile.Paths, () => false, directory))
            Assert.Equal(0, (await updater.CheckNowAsync()).Checked);
        Assert.Equal(0, directory.Requests);
        Assert.Equal(TimeSpan.FromHours(6), DesktopExtensionUpdates.CheckInterval);
    }

    [Fact]
    public async Task UpdateIsCachedThenAppliedAtNextStartupKeepingConfigurationAndDisabledState()
    {
        using var profile = new TestProfile();
        profile.Install("1.0.0", "catalog.sample");
        profile.PreserveSettings();
        var directory = new FakeDirectory(Bundle("2.0.0"));
        using (var updater = new DesktopExtensionUpdates(profile.Paths, () => true, directory))
        {
            Assert.Equal(1, (await updater.CheckNowAsync()).Staged);
            Assert.Equal("1.0.0", profile.Installed.Manifest.Version);
            Assert.Single(updater.Pending());
            Assert.Equal(0, (await updater.CheckNowAsync()).Staged);
        }
        using (var restarted = new DesktopExtensionUpdates(profile.Paths, () => false, directory))
        {
            // Like the reference, a cached update applies on startup even if
            // future downloads were subsequently switched off.
            restarted.Start();
            Assert.Empty(restarted.Pending());
        }
        Assert.Equal("2.0.0", profile.Installed.Manifest.Version);
        Assert.False(profile.Installed.Enabled);
        Assert.Equal("keep-secret", profile.Installed.UserConfig["token"]?.GetValue<string>());
        Assert.Equal("catalog.sample", profile.Installed.DirectoryId);
        Assert.Empty((JsonNode.Parse(File.ReadAllText(DesktopExtensions.McpFile(profile.Paths)))!["mcpServers"] as JsonObject)!);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("identity")]
    [InlineData("signature")]
    [InlineData("policy")]
    [InlineData("zip-path")]
    public async Task AnUnverifiedOrWrongBundleNeverReplacesTheWorkingInstallation(string failure)
    {
        using var profile = new TestProfile();
        profile.Install("1.0.0", "catalog.sample");
        var bytes = failure == "identity" ? Bundle("2.0.0", "other")
            : Bundle("2.0.0", unsafeEntry: failure == "zip-path");
        var directory = new FakeDirectory(bytes)
        {
            BadHash = failure == "hash", SignatureVerified = failure != "signature", PolicyAllowed = failure != "policy",
        };
        using var updater = new DesktopExtensionUpdates(profile.Paths, () => true, directory);
        var result = await updater.CheckNowAsync();
        Assert.Equal(0, result.Staged);
        Assert.Single(result.Errors);
        Assert.Empty(updater.Pending());
        Assert.Equal("1.0.0", profile.Installed.Manifest.Version);
        Assert.Equal("payload-1.0.0", File.ReadAllText(Path.Combine(profile.Installed.Directory, "server.js")));
    }

    [Fact]
    public async Task ACommitFailureRollsBackTheFilesAndKeepsTheUsersConfigForRetry()
    {
        using var profile = new TestProfile();
        profile.Install("1.0.0", "catalog.sample");
        profile.PreserveSettings();
        using var updater = new DesktopExtensionUpdates(profile.Paths, () => true, new FakeDirectory(Bundle("2.0.0")));
        Assert.Equal(1, (await updater.CheckNowAsync()).Staged);
        var mcp = DesktopExtensions.McpFile(profile.Paths);
        var before = File.ReadAllBytes(mcp);
        File.SetAttributes(mcp, File.GetAttributes(mcp) | FileAttributes.ReadOnly);
        try
        {
            var failed = updater.ApplyPending();
            Assert.Equal(0, failed.Applied);
            Assert.Single(failed.Errors);
            Assert.Equal("1.0.0", profile.Installed.Manifest.Version);
            Assert.Equal("keep-secret", profile.Installed.UserConfig["token"]?.GetValue<string>());
            Assert.False(profile.Installed.Enabled);
            Assert.Equal(before, File.ReadAllBytes(mcp));
        }
        finally { File.SetAttributes(mcp, File.GetAttributes(mcp) & ~FileAttributes.ReadOnly); }
        Assert.Equal(1, updater.ApplyPending().Applied);
        Assert.Equal("2.0.0", profile.Installed.Manifest.Version);
    }

    [Fact]
    public async Task CachedBytesAreVerifiedAgainAndAnUninstalledExtensionIsNotResurrected()
    {
        using var profile = new TestProfile();
        profile.Install("1.0.0", "catalog.sample");
        using var updater = new DesktopExtensionUpdates(profile.Paths, () => true, new FakeDirectory(Bundle("2.0.0")));
        Assert.Equal(1, (await updater.CheckNowAsync()).Staged);
        var cache = Path.Combine(DesktopExtensions.UpdateRoot(profile.Paths), "pending", "sample", "bundle.mcpb");
        File.AppendAllText(cache, "tampered");
        Assert.Single(updater.ApplyPending().Errors);
        Assert.Equal("1.0.0", profile.Installed.Manifest.Version);
        DesktopExtensions.Uninstall(profile.Paths, profile.Installed);
        Assert.Equal(0, updater.ApplyPending().Applied);
        Assert.Empty(DesktopExtensions.List(profile.Paths));
        Assert.Empty(updater.Pending());
    }

    [Fact]
    public async Task OrganizationScopedUpdatesRemainPendingUntilReviewed()
    {
        using var profile = new TestProfile();
        profile.Install("1.0.0", "catalog.sample");
        using var updater = new DesktopExtensionUpdates(profile.Paths, () => true,
            new FakeDirectory(Bundle("2.0.0")) { IsInternal = true });
        Assert.Equal(1, (await updater.CheckNowAsync()).Staged);
        Assert.Equal(0, updater.ApplyPending().Applied);
        Assert.Single(updater.Pending());
        updater.ApproveInternalUpdate("sample");
        Assert.Equal(1, updater.ApplyPending().Applied);
    }

    [Theory]
    [InlineData("1.2.0", "1.1.9", true)]
    [InlineData("1.2.0", "1.2.0-beta.10", true)]
    [InlineData("1.2.0-beta.10", "1.2.0-beta.2", true)]
    [InlineData("1.2.0-beta.2", "1.2.0", false)]
    [InlineData("1.2.0+build.2", "1.2.0+build.1", false)]
    [InlineData("invalid", "1.2.0", false)]
    public void VersionOrderingUsesReleaseAndPrereleaseSemantics(string candidate, string current, bool expected) =>
        Assert.Equal(expected, ExtensionVersions.IsNewer(candidate, current));

    private static byte[] Bundle(string version, string name = "sample", bool unsafeEntry = false)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write(new JsonObject
                {
                    ["manifest_version"] = "0.3", ["name"] = name, ["version"] = version, ["description"] = "test extension",
                    ["server"] = new JsonObject { ["type"] = "node", ["entry_point"] = "server.js", ["mcp_config"] = new JsonObject
                    { ["command"] = "node", ["args"] = new JsonArray("${__dirname}/server.js"), ["env"] = new JsonObject { ["TOKEN"] = "${user_config.token}" } } },
                    ["user_config"] = new JsonObject { ["token"] = new JsonObject { ["type"] = "string", ["required"] = true, ["sensitive"] = true } },
                }.ToJsonString());
            using (var writer = new StreamWriter(zip.CreateEntry(unsafeEntry ? "../outside.js" : "server.js").Open())) writer.Write("payload-" + version);
        }
        return bytes.ToArray();
    }

    private sealed class FakeDirectory(byte[] bundle) : IDesktopExtensionDirectory
    {
        public bool IsAvailable => true;
        public int Requests { get; private set; }
        public bool BadHash { get; init; }
        public bool SignatureVerified { get; init; } = true;
        public bool PolicyAllowed { get; init; } = true;
        public bool IsInternal { get; init; }
        public Task<DesktopExtensionRelease?> FindUpdateAsync(string id, string current, CancellationToken token)
        {
            Requests++;
            return Task.FromResult<DesktopExtensionRelease?>(new("2.0.0", BadHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bundle)),
                PolicyAllowed, SignatureRequired: true, SignatureVerified, IsInternal));
        }
        public Task<Stream> OpenBundleAsync(string id, DesktopExtensionRelease release, CancellationToken token) => Task.FromResult<Stream>(new MemoryStream(bundle));
    }

    private sealed class TestProfile : IDisposable
    {
        public ProfilePaths Paths { get; } = ProfilePaths.Create("extension-update-test-" + Guid.NewGuid().ToString("N"));
        public InstalledExtension Installed => Assert.Single(DesktopExtensions.List(Paths));
        public void Install(string version, string? directoryId = null)
        {
            Directory.CreateDirectory(Paths.Root);
            var file = Path.Combine(Paths.Root, "test.mcpb");
            File.WriteAllBytes(file, Bundle(version));
            var result = DesktopExtensions.Install(Paths, file, directoryId);
            Assert.Null(result.Error);
            Assert.NotNull(result.Extension);
        }
        public void PreserveSettings() => DesktopExtensions.Save(Paths,
            [Installed with { Enabled = false, UserConfig = new Dictionary<string, JsonNode?> { ["token"] = JsonValue.Create("keep-secret") } }]);
        public void Dispose()
        {
            var prefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarvisCode-extension-update-test-");
            Assert.StartsWith(prefix, Path.GetFullPath(Paths.Root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, recursive: true);
        }
    }
}
