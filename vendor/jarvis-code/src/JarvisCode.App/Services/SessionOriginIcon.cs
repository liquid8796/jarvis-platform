namespace JarvisCode.App.Services;

/// <summary>Where a session runs, as the reference's titlebar classifies it.</summary>
public enum SessionOriginKind
{
    /// <summary>A session on this machine — the only kind this build produces.</summary>
    Local,

    /// <summary>A session the terminal front-end started.</summary>
    Cli,

    /// <summary>A session running over SSH.</summary>
    Ssh,

    /// <summary>A session bridged to another machine.</summary>
    Bridge,

    /// <summary>A session running in the cloud.</summary>
    Remote,
}

/// <summary>How the origin's transport is doing, which recolours the glyph.</summary>
public enum SessionOriginConnection
{
    /// <summary>Nothing to report — a local session is never anything else.</summary>
    None,

    /// <summary>The transport is up.</summary>
    Connected,

    /// <summary>The transport is being established.</summary>
    Connecting,

    /// <summary>The transport dropped and is being re-established.</summary>
    Reconnecting,

    /// <summary>The transport is idle.</summary>
    Idle,

    /// <summary>The transport is down.</summary>
    Disconnected,
}

/// <summary>
/// The glyph and colour the session titlebar's origin slot shows, ported from the
/// reference desktop 1.44121.4.0 (ion-dist chunk <c>c66fe388e-DOFZnzRG.js</c>: its
/// <c>pp(kind, connection)</c> picks the icon and its <c>mp(connection)</c> the class).
///
/// Only <see cref="SessionOriginKind.Local"/> is reachable here — this build has no
/// SSH, bridge or cloud session — but the rule is carried whole so the slot cannot
/// quietly acquire a different answer than the reference's for a kind it does have.
/// </summary>
public static class SessionOriginIcon
{
    /// <summary>The reference's <c>pp</c>: the icon name for a kind, with the cloud one reading its connection.</summary>
    public static string Glyph(SessionOriginKind kind, SessionOriginConnection connection) => kind switch
    {
        SessionOriginKind.Ssh => "CommandLineGlyph",
        SessionOriginKind.Bridge => "LaptopGlyph",
        SessionOriginKind.Remote => connection == SessionOriginConnection.Disconnected
            ? "CloudSlashGlyph"
            : "CloudGlyph",
        _ => "LaptopGlyph",
    };

    /// <summary>
    /// The reference's <c>mp</c>: a dropped transport paints the glyph danger and one being
    /// established paints it accent; every other state leaves it the titlebar's own colour,
    /// which this returns as null rather than as a brush key nobody asked for.
    /// </summary>
    public static string? BrushKey(SessionOriginConnection connection) => connection switch
    {
        SessionOriginConnection.Disconnected => "Danger100Brush",
        SessionOriginConnection.Connecting or SessionOriginConnection.Reconnecting => "Accent100Brush",
        _ => null,
    };

    /// <summary>The reference pulses the glyph while the transport is coming up (<c>animate-pulse</c>).</summary>
    public static bool Pulses(SessionOriginConnection connection)
        => connection is SessionOriginConnection.Connecting or SessionOriginConnection.Reconnecting;
}
