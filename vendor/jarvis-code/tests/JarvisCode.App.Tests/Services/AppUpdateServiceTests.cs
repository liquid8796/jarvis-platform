using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("release-2.0", "2.0")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("nightly", null)]
    [InlineData(null, null)]
    public void ReadsTheNumberOutOfATag(string? tag, string? expected) =>
        Assert.Equal(expected, ReleaseVersion.FromTag(tag));

    [Theory]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("1.9.3", "1.10.0", false)]
    [InlineData("2.0", "1.99.99", true)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.1", "1.2", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    public void ComparesNumericallyPartByPart(string candidate, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.IsNewerThan(candidate, current));
}

public class UpdateCheckFailureTests
{
    [Theory]
    [InlineData("<!DOCTYPE html>", true)]
    [InlineData("Unexpected token < in JSON at position 0 is not valid JSON", true)]
    [InlineData("The server sent an invalid response", true)]
    [InlineData("net::ERR_CONNECTION_REFUSED", true)]
    [InlineData("net::ERR_CERT_AUTHORITY_INVALID", true)]
    [InlineData("The remote name could not be resolved", false)]
    [InlineData("403 Forbidden", false)]
    public void ClassifiesAnInterceptedNetworkTheReferenceWay(string message, bool intercepted) =>
        Assert.Equal(intercepted, UpdateCheckFailure.LooksIntercepted(message));

    [Fact]
    public void TheInterceptedBodyNamesTheHostToUnblock()
    {
        var detail = UpdateCheckFailure.Detail("net::ERR_CONNECTION_REFUSED", "api.github.com");

        Assert.Contains("api.github.com", detail, StringComparison.Ordinal);
        Assert.StartsWith("Jarvis couldn't reach the update server.", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyOtherFailureIsReported() =>
        Assert.Equal(
            "Failed to check for updates: 403 Forbidden",
            UpdateCheckFailure.Detail("403 Forbidden", "api.github.com"));
}

public class ReleaseParsingTests
{
    [Fact]
    public void PrefersAWindowsInstallerOverAnArchive()
    {
        var release = AppUpdateService.ParseRelease("""
            {
              "tag_name": "v1.4.0",
              "assets": [
                {"name": "JarvisCode-1.4.0.zip", "browser_download_url": "https://x/zip"},
                {"name": "JarvisCodeSetup.exe", "browser_download_url": "https://x/exe"},
                {"name": "checksums.txt", "browser_download_url": "https://x/txt"}
              ]
            }
            """);

        Assert.Equal("v1.4.0", release!.Tag);
        Assert.Equal("JarvisCodeSetup.exe", release.AssetName);
    }

    [Fact]
    public void FallsBackToTheArchiveAndIgnoresUnusableAssets()
    {
        var release = AppUpdateService.ParseRelease("""
            {
              "tag_name": "v1.4.0",
              "assets": [
                {"name": "notes.txt", "browser_download_url": "https://x/txt"},
                {"name": "JarvisCode-1.4.0.zip", "browser_download_url": "https://x/zip"}
              ]
            }
            """);

        Assert.Equal("JarvisCode-1.4.0.zip", release!.AssetName);
    }

    [Fact]
    public void ARelaseWithNoUsableAssetStillReportsItsTag()
    {
        var release = AppUpdateService.ParseRelease("""{"tag_name": "v2.0.0", "assets": []}""");

        Assert.Equal("v2.0.0", release!.Tag);
        Assert.Null(release.AssetUrl);
    }

    [Fact]
    public void HtmlWhereJsonWasExpectedReadsAsAnInterceptedNetwork()
    {
        var error = Assert.Throws<InvalidOperationException>(
            static () => AppUpdateService.ParseRelease("<!DOCTYPE html><html>Sign in</html>"));

        Assert.True(UpdateCheckFailure.LooksIntercepted(error.Message));
    }
}
