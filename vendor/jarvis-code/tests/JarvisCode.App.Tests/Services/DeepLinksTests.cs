using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class DeepLinksTests
{
    [Theory]
    [InlineData("jarvis-code://code/new", true)]
    [InlineData("JARVIS-CODE://code/new", true)]
    [InlineData("https://example.com", false)]
    [InlineData("--profile=x", false)]
    public void RecognizesItsOwnScheme(string argument, bool expected) =>
        Assert.Equal(expected, DeepLinks.IsDeepLink(argument));

    [Fact]
    public void NewChatCarriesTheSurfaceTheJumpListSends()
    {
        var link = DeepLinks.Parse("jarvis-code://claude.ai/new?surface=chat&source=desktop_action");

        Assert.Equal(DeepLinkKind.NewChat, link.Kind);
        Assert.Equal("desktop_action", link.Source);
    }

    [Fact]
    public void NewChatWithAnotherSurfaceIsNotRouted() =>
        Assert.Equal(
            DeepLinkKind.Unrecognized,
            DeepLinks.Parse("jarvis-code://claude.ai/new?surface=cowork").Kind);

    [Fact]
    public void NewCodeSessionTakesFoldersAndAPrompt()
    {
        var link = DeepLinks.Parse(
            "jarvis-code://code/new?folder=D%3A%5Crepo&folder=D%3A%5Cother&q=fix%20the%20build&source=jump_list");

        Assert.Equal(DeepLinkKind.NewCodeSession, link.Kind);
        Assert.Equal(["D:\\repo", "D:\\other"], link.Folders);
        Assert.Equal("fix the build", link.Prompt);
        Assert.Equal("jump_list", link.Source);
    }

    [Fact]
    public void NewCodeSessionAlsoReadsThePromptParameter() =>
        Assert.Equal("hello", DeepLinks.Parse("jarvis-code://code/new?prompt=hello").Prompt);

    [Fact]
    public void NewCodeSessionWithNothingElseStillRoutes()
    {
        var link = DeepLinks.Parse("jarvis-code://code/new");

        Assert.Equal(DeepLinkKind.NewCodeSession, link.Kind);
        Assert.Null(link.Prompt);
        Assert.Empty(link.Folders!);
    }

    [Fact]
    public void APromptIsCutToTheReferenceCap()
    {
        var link = DeepLinks.Parse("jarvis-code://code/new?q=" + new string('a', DeepLinks.MaxPromptLength + 500));

        Assert.Equal(DeepLinks.MaxPromptLength, link.Prompt!.Length);
    }

    [Theory]
    [InlineData("jarvis-code://code/continue?session=last", "last")]
    [InlineData("jarvis-code://code/continue?session=local_abc-123", "local_abc-123")]
    public void ContinueTakesLastOrALocalSessionId(string url, string expected)
    {
        var link = DeepLinks.Parse(url);

        Assert.Equal(DeepLinkKind.ContinueCodeSession, link.Kind);
        Assert.Equal(expected, link.Session);
    }

    [Theory]
    [InlineData("jarvis-code://code/continue")]
    [InlineData("jarvis-code://code/continue?session=")]
    [InlineData("jarvis-code://code/continue?session=../../etc")]
    [InlineData("jarvis-code://code/continue?session=cse_abc")]
    public void ContinueRefusesAnythingElse(string url) =>
        Assert.Equal(DeepLinkKind.Unrecognized, DeepLinks.Parse(url).Kind);

    [Fact]
    public void NeedsInputTakesAnOptionalSession()
    {
        Assert.Equal(DeepLinkKind.NeedsInput, DeepLinks.Parse("jarvis-code://code/needs-input").Kind);
        Assert.Null(DeepLinks.Parse("jarvis-code://code/needs-input").Session);

        var named = DeepLinks.Parse("jarvis-code://code/needs-input?session=local_9");
        Assert.Equal(DeepLinkKind.NeedsInput, named.Kind);
        Assert.Equal("local_9", named.Session);
    }

    [Fact]
    public void NeedsInputRefusesASessionThatIsNotOneOfOurs() =>
        Assert.Equal(
            DeepLinkKind.Unrecognized,
            DeepLinks.Parse("jarvis-code://code/needs-input?session=%2E%2E%2Fetc").Kind);

    [Fact]
    public void ResumeTakesAUuid()
    {
        var link = DeepLinks.Parse(
            "jarvis-code://resume?session=8f14e45f-ceea-467a-9a3b-6a1f3e0c5d21");

        Assert.Equal(DeepLinkKind.ResumeCliSession, link.Kind);
        Assert.Equal("8f14e45f-ceea-467a-9a3b-6a1f3e0c5d21", link.Session);
    }

    [Theory]
    [InlineData("jarvis-code://resume")]
    [InlineData("jarvis-code://resume?session=not-a-uuid")]
    public void ResumeRefusesAnythingThatIsNotAUuid(string url) =>
        Assert.Equal(DeepLinkKind.Unrecognized, DeepLinks.Parse(url).Kind);

    [Theory]
    [InlineData("jarvis-code://cowork/new")]
    [InlineData("jarvis-code://login/google-auth")]
    [InlineData("jarvis-code://code/settings")]
    [InlineData("jarvis://code/new")]
    [InlineData("not a url at all")]
    public void EverythingElseIsUnrecognizedRatherThanAnError(string url) =>
        Assert.Equal(DeepLinkKind.Unrecognized, DeepLinks.Parse(url).Kind);

    [Fact]
    public void UrlBuildsTheShapeTheJumpListSends() =>
        Assert.Equal(
            "jarvis-code://code/continue?session=local_7&source=jump_list",
            DeepLinks.Url("code", "/continue", "jump_list", ("session", "local_7")));

    [Fact]
    public void UrlEscapesAFolderPath() =>
        Assert.Equal(
            "jarvis-code://code/new?folder=D%3A%5Cwork%5Cmy%20repo&source=jump_list",
            DeepLinks.Url("code", "/new", "jump_list", ("folder", "D:\\work\\my repo")));

    [Fact]
    public void EveryUrlItBuildsParsesBack()
    {
        var link = DeepLinks.Parse(
            DeepLinks.Url("code", "/new", "jump_list", ("folder", "D:\\work\\my repo")));

        Assert.Equal(DeepLinkKind.NewCodeSession, link.Kind);
        Assert.Equal(["D:\\work\\my repo"], link.Folders);
    }
}

public class ResumeFailureMessagesTests
{
    [Fact]
    public void EachCategoryHasItsOwnAdvice()
    {
        Assert.Equal(
            "Couldn't open that session. Sign in to the desktop app and try again.",
            ResumeFailureMessages.For(ResumeFailure.AuthExpired));
        Assert.Equal(
            "Couldn't open that session. Check your network connection and try again.",
            ResumeFailureMessages.For(ResumeFailure.Network));
        Assert.Equal(
            "Couldn't open that session. Its transcript may have been removed.",
            ResumeFailureMessages.For(ResumeFailure.TranscriptMissing));
        Assert.Equal(
            "Couldn't open that session from Jarvis Code.",
            ResumeFailureMessages.For(ResumeFailure.Other));
    }
}
