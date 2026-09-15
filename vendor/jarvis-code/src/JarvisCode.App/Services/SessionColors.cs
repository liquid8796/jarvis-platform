namespace JarvisCode.App.Services;

/// <summary>One of the reference's session colours: its key, its label and the hex it paints with.</summary>
public sealed record SessionColor(string Key, string Label, string Hex);

/// <summary>
/// The reference's session colour table (desktop 1.40609.1.0, `Bo`/`jo`/`Ko` in the
/// ccd chunk): eight named colours in a fixed order, each with the hex the sidebar
/// row and the header paint with, offered under a "Default" row that clears it.
/// The reference stores the choice as a `color:{key}` tag on the session; this
/// app keeps it in session-groups.json beside the other sidebar state.
/// </summary>
public static class SessionColors
{
    /// <summary>The picker's "Default" row — no colour.</summary>
    public const string DefaultLabel = "Default";

    public static IReadOnlyList<SessionColor> All { get; } =
    [
        new("red", "Red", "#dc2626"),
        new("blue", "Blue", "#6a9bcc"),
        new("green", "Green", "#16a34a"),
        new("yellow", "Yellow", "#ca8a04"),
        new("purple", "Purple", "#827dbd"),
        new("orange", "Orange", "#d97757"),
        new("pink", "Pink", "#c46686"),
        new("cyan", "Cyan", "#0891b2"),
    ];

    public static bool IsKnown(string? key) => key is not null && All.Any(c => c.Key == key);

    public static SessionColor? Find(string? key) => key is null ? null : All.FirstOrDefault(c => c.Key == key);

    /// <summary>
    /// The reference's glow: a 1px ring at 20% and a 24px halo at 40% of the colour
    /// (`Fo`: "0 0 0 1px {hex}33, 0 0 24px -4px {hex}66"). Returned as the two
    /// ARGB strings a WPF brush can be built from.
    /// </summary>
    public static (string Ring, string Glow) Glow(SessionColor color) =>
        ($"#33{color.Hex.TrimStart('#')}", $"#66{color.Hex.TrimStart('#')}");
}
