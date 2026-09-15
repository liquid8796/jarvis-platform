using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class SessionNotFoundTests
{
    [Fact]
    public void CopyIsTheReferences()
    {
        Assert.Equal("Session not found on disk", SessionNotFound.Title);
        Assert.Equal("Send a message to start fresh in this directory.", SessionNotFound.Body);
    }

    [Fact]
    public void ActionsAreTheReferencesThree()
    {
        Assert.Equal(
            new[]
            {
                SessionNotFoundAction.ImportCliSessions,
                SessionNotFoundAction.Archive,
                SessionNotFoundAction.Delete,
            },
            SessionNotFound.Actions(canImportCliSessions: true));
    }

    [Fact]
    public void ImportIsDroppedWithNothingToImportFrom()
    {
        Assert.Equal(
            new[] { SessionNotFoundAction.Archive, SessionNotFoundAction.Delete },
            SessionNotFound.Actions(canImportCliSessions: false));
    }

    [Fact]
    public void LabelsAndAccessibleNamesDiffer()
    {
        Assert.Equal("Archive", SessionNotFound.Label(SessionNotFoundAction.Archive));
        Assert.Equal("Archive session", SessionNotFound.AccessibleName(SessionNotFoundAction.Archive));
        Assert.Equal("Delete", SessionNotFound.Label(SessionNotFoundAction.Delete));
        Assert.Equal("Delete session", SessionNotFound.AccessibleName(SessionNotFoundAction.Delete));
        Assert.Equal(
            "Import CLI sessions", SessionNotFound.Label(SessionNotFoundAction.ImportCliSessions));
    }

    [Fact]
    public void OnlyALiveRowWithNoStoredSessionRaisesTheCard()
    {
        Assert.True(SessionNotFound.ShouldShow(listed: true, loaded: false));
        Assert.False(SessionNotFound.ShouldShow(listed: true, loaded: true));
        Assert.False(SessionNotFound.ShouldShow(listed: false, loaded: false));
    }
}
