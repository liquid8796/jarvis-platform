using JarvisCode.Core.Utilities;

namespace JarvisCode.Core.Tests.Utilities;

/// <summary>
/// The reference refuses network paths before touching them (CLI 2.1.257: UNC
/// shares and /net automounts, for --add-dir, /add-dir, additionalDirectories
/// and attachments).
/// </summary>
public sealed class NetworkPathsTests
{
    [Theory]
    [InlineData(@"\\server\share\repo", true)]
    [InlineData("//server/share/repo", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData("/net/buildhost/exports/src", true)]
    [InlineData(@"C:\repo", false)]
    [InlineData(@"\\?\C:\very\long\path", false)]
    [InlineData("/home/user/repo", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Recognises_unc_shares_and_net_automounts(string? path, bool expected)
    {
        Assert.Equal(expected, NetworkPaths.IsNetworkPath(path));
    }

    [Fact]
    public void The_refusals_are_the_reference_sentences()
    {
        Assert.Equal(
            @"\\nas\code is a network path, which cannot be added as a working directory. On Windows, map the share " +
            "to a drive letter and pass it at launch with --add-dir (a drive letter added mid-session does not yet " +
            "carry remote-read trust)",
            NetworkPaths.AddDirectoryRefusal(@"\\nas\code"));
        Assert.Equal(
            "Attachment \"//nas/code/notes.md\" is a network path (UNC or /net autofs), which is not supported.",
            NetworkPaths.AttachmentRefusal("//nas/code/notes.md"));
    }

    [Fact]
    public void A_network_mention_is_never_read()
    {
        // The reference throws for a network attachment; here the mention stays
        // typed text and no file is read, whichever way the share is spelled.
        var result = FileMentions.BuildUserMessage(
            "please read //nas/code/notes.md and @//nas/code/other.md", workingDirectory: Path.GetTempPath());
        Assert.Empty(result.AttachedPaths);
        Assert.Single(result.Message.Content);
    }
}
