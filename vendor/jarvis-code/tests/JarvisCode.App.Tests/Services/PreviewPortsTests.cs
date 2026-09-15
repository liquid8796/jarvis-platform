using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The reference's autoPort ladder (desktop 1.44121.2.0, its <c>Jor</c>/<c>qor</c>).
/// Each arm is a different sentence, so each arm is asserted by what it says.
/// </summary>
public class PreviewPortsTests
{
    private const string Launch = ".jarvis/launch.json";

    private static readonly PreviewServerRef Mine = new("preview-1", "web", 3000, "session-a");
    private static readonly PreviewServerRef Theirs = new("preview-9", "web", 3000, "session-b");

    private static Func<int, PortBindResult> Binder(params (int Port, PortBindResult Result)[] answers) =>
        port => answers.FirstOrDefault(a => a.Port == port).Result ?? PortBindResult.Bound(port);

    private static PreviewPortResult Resolve(
        bool? autoPort,
        IReadOnlyList<PreviewServerRef>? running = null,
        Func<int, PortBindResult>? bind = null,
        Func<int, string?>? occupant = null,
        int port = 3000,
        string? sessionId = "session-a") =>
        PreviewPorts.Resolve(
            port, autoPort, running ?? [], sessionId, Launch,
            bind ?? Binder(), occupant ?? (_ => null));

    [Fact]
    public void PortlessConfigurationIsLeftAlone()
    {
        var probed = false;
        var result = PreviewPorts.Resolve(
            0, null, [], "session-a", Launch,
            _ => { probed = true; return PortBindResult.Bound(1); }, _ => null);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Port);
        Assert.False(probed);
    }

    [Fact]
    public void FreePortIsTakenAsConfigured()
    {
        var result = Resolve(autoPort: null);

        Assert.True(result.Ok);
        Assert.Equal(3000, result.Port);
    }

    [Fact]
    public void AutoPortTakesAFreshPortWhenAPreviewServerHoldsIt()
    {
        var result = Resolve(
            autoPort: true, running: [Mine], bind: Binder((0, PortBindResult.Bound(51234))));

        Assert.True(result.Ok);
        Assert.Equal(51234, result.Port);
    }

    [Fact]
    public void AutoPortTakesAFreshPortWhenAnotherProcessHoldsIt()
    {
        var result = Resolve(
            autoPort: true,
            bind: Binder((3000, PortBindResult.InUse), (0, PortBindResult.Bound(51235))));

        Assert.True(result.Ok);
        Assert.Equal(51235, result.Port);
    }

    [Fact]
    public void AutoPortReassignsPastAReservedPortToo()
    {
        var result = Resolve(
            autoPort: true,
            bind: Binder((3000, PortBindResult.Reserved), (0, PortBindResult.Bound(51236))));

        Assert.True(result.Ok);
        Assert.Equal(51236, result.Port);
    }

    [Fact]
    public void RequiredPortNamesThePreviewServerToStop()
    {
        var result = Resolve(autoPort: false, running: [Mine]);

        Assert.False(result.Ok);
        Assert.Contains("(autoPort is false)", result.Error);
        Assert.Contains("preview_stop with serverId \"preview-1\"", result.Error);
    }

    [Fact]
    public void AbsentFieldAsksTheUserWhichTheyMeant()
    {
        var result = Resolve(autoPort: null, running: [Mine]);

        Assert.False(result.Ok);
        Assert.Contains("in use by preview server \"web\" (preview-1)", result.Error);
        Assert.Contains("does this server need port 3000 specifically", result.Error);
        Assert.Contains("PORT environment variable", result.Error);
        // The advice names the file that was actually read.
        Assert.Contains(Launch, result.Error);
    }

    [Fact]
    public void AnotherSessionsServerSaysPreviewStopWillNotReachIt()
    {
        var result = Resolve(autoPort: null, running: [Theirs]);

        Assert.False(result.Ok);
        Assert.Contains("another chat's dev server \"web\"", result.Error);
        Assert.Contains("preview_stop won't stop another chat's server.", result.Error);
        Assert.DoesNotContain("preview_stop with serverId", result.Error);
    }

    [Fact]
    public void AnotherSessionsServerWithAutoPortFalseAsksForTheOtherChat()
    {
        var result = Resolve(autoPort: false, running: [Theirs]);

        Assert.False(result.Ok);
        Assert.Contains("Ask the user to stop it from that chat", result.Error);
        Assert.DoesNotContain("does this server need port", result.Error);
    }

    [Fact]
    public void ReservedPortIsReportedAsTheOsRefusingIt()
    {
        var result = Resolve(autoPort: null, bind: Binder((3000, PortBindResult.Reserved)));

        Assert.False(result.Ok);
        Assert.Contains("reserved by the OS", result.Error);
        Assert.Contains("set \"autoPort\": true", result.Error);
    }

    [Fact]
    public void ExternalHolderIsNamedWhenItCanBe()
    {
        var result = Resolve(
            autoPort: false,
            bind: Binder((3000, PortBindResult.InUse)),
            occupant: _ => "\"node\" (PID 42)");

        Assert.False(result.Ok);
        Assert.Contains("in use by \"node\" (PID 42). Stop that process", result.Error);
    }

    [Fact]
    public void UnnamedExternalHolderFallsBackToTheReferencesOwnBranch()
    {
        var result = Resolve(autoPort: null, bind: Binder((3000, PortBindResult.InUse)));

        Assert.False(result.Ok);
        Assert.Contains("in use by another process (not a preview server)", result.Error);
        Assert.Contains("lsof -i :3000", result.Error);
    }

    [Fact]
    public void FailedReassignmentOffersTheOccupantItCanStop()
    {
        var result = Resolve(
            autoPort: true, running: [Mine],
            bind: Binder((0, PortBindResult.InUse), (3000, PortBindResult.InUse)));

        Assert.False(result.Ok);
        Assert.Contains("automatic reassignment to a fresh port failed", result.Error);
        Assert.Contains("preview_stop with serverId \"preview-1\"", result.Error);
    }

    [Fact]
    public void FailedReassignmentDoesNotOfferAnotherSessionsServer()
    {
        var result = Resolve(
            autoPort: true, running: [Theirs],
            bind: Binder((0, PortBindResult.InUse), (3000, PortBindResult.InUse)));

        Assert.False(result.Ok);
        Assert.Contains("automatic port reassignment failed", result.Error);
        Assert.DoesNotContain("preview_stop with serverId", result.Error);
    }

    [Fact]
    public void AServerWithNoSessionIsNotTreatedAsAnotherChats()
    {
        var anonymous = new PreviewServerRef("preview-2", "web", 3000, null);
        var result = Resolve(autoPort: false, running: [anonymous]);

        Assert.Contains("preview_stop with serverId \"preview-2\"", result.Error);
        Assert.DoesNotContain("another chat", result.Error);
    }

    [Theory]
    [InlineData("web", "web")]
    [InlineData("we\"b", "we'b…")]
    [InlineData("web\nsecond line", "web…")]
    [InlineData("a\u0001b", "a\uFFFDb…")]
    public void NameIsSanitizedBeforeItIsQuotedBack(string input, string expected) =>
        Assert.Equal(expected, PreviewPorts.SanitizeName(input));

    [Fact]
    public void AstralCharactersSurviveSanitizing()
    {
        // .NET's \p{Cs} matches each half of a surrogate pair; a regex port of the
        // reference's class would delete this.
        Assert.Equal("web \U0001F680", PreviewPorts.SanitizeName("web \U0001F680"));
    }

    [Fact]
    public void LongNameIsCutAtTheReferencesLimit()
    {
        var sanitized = PreviewPorts.SanitizeName(new string('a', 200));

        Assert.Equal(new string('a', 120) + "…", sanitized);
    }

    [Fact]
    public void SmartQuotesBecomePlainApostrophes() =>
        Assert.Equal("'web'…", PreviewPorts.SanitizeName("“web”"));
}
