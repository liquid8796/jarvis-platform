using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The session titlebar's origin glyph — the reference's <c>pp</c> and <c>mp</c>. Only the
/// local arm is reachable in this build, and the rest is carried so the slot cannot quietly
/// acquire a different answer than the reference's for a kind this app later grows.
/// </summary>
public sealed class SessionOriginIconTests
{
    [Theory]
    [InlineData(SessionOriginKind.Local, "LaptopGlyph")]
    [InlineData(SessionOriginKind.Cli, "LaptopGlyph")]
    [InlineData(SessionOriginKind.Bridge, "LaptopGlyph")]
    [InlineData(SessionOriginKind.Ssh, "CommandLineGlyph")]
    public void Each_kind_takes_the_reference_glyph(SessionOriginKind kind, string glyph)
        => Assert.Equal(glyph, SessionOriginIcon.Glyph(kind, SessionOriginConnection.None));

    [Fact]
    public void Only_the_cloud_glyph_reads_the_connection()
    {
        Assert.Equal(
            "CloudSlashGlyph",
            SessionOriginIcon.Glyph(SessionOriginKind.Remote, SessionOriginConnection.Disconnected));
        Assert.Equal(
            "CloudGlyph",
            SessionOriginIcon.Glyph(SessionOriginKind.Remote, SessionOriginConnection.Connected));

        // A dropped connection recolours every kind but only restrikes this one.
        Assert.Equal(
            "LaptopGlyph",
            SessionOriginIcon.Glyph(SessionOriginKind.Local, SessionOriginConnection.Disconnected));
    }

    [Fact]
    public void A_dropped_transport_paints_the_glyph_danger()
        => Assert.Equal("Danger100Brush", SessionOriginIcon.BrushKey(SessionOriginConnection.Disconnected));

    [Theory]
    [InlineData(SessionOriginConnection.Connecting)]
    [InlineData(SessionOriginConnection.Reconnecting)]
    public void A_transport_coming_up_is_accent_and_pulses(SessionOriginConnection connection)
    {
        Assert.Equal("Accent100Brush", SessionOriginIcon.BrushKey(connection));
        Assert.True(SessionOriginIcon.Pulses(connection));
    }

    [Theory]
    [InlineData(SessionOriginConnection.None)]
    [InlineData(SessionOriginConnection.Connected)]
    [InlineData(SessionOriginConnection.Idle)]
    public void A_settled_transport_leaves_the_bar_its_own_colour(SessionOriginConnection connection)
    {
        // Null rather than a brush key, so the caller keeps `text-primary` instead of being
        // handed back the colour it already had.
        Assert.Null(SessionOriginIcon.BrushKey(connection));
        Assert.False(SessionOriginIcon.Pulses(connection));
    }
}
