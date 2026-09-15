namespace JarvisCode.App.Services;

/// <summary>
/// The reference desktop's notification preferences as one testable table,
/// ported from app.asar 1.40609.1.0 (<c>CB</c>/<c>TB</c>/<c>requestUserAttention</c> in
/// <c>index.chunk-DnlgCaT3.js</c>) and the Claude Code settings page that edits them
/// (<c>c71860c77-D1Dqt2F3.js</c>, its <c>ya</c>/<c>ka</c>/<c>P</c> tables).
/// </summary>
public static class NotificationPolicy
{
    /// <summary>The three notification types, in the order the page lists them (the reference's <c>ya</c>).</summary>
    public static readonly IReadOnlyList<string> Types = ["permission", "question", "idle"];

    /// <summary>The types whose level decides whether "Draw attention" may be on (the reference's <c>ka</c>).</summary>
    public static readonly IReadOnlyList<string> AttentionTypes = ["permission", "question"];

    /// <summary>The three type keys, as the reference stores them.</summary>
    public const string Permission = "permission";

    public const string Question = "question";
    public const string Idle = "idle";

    public const string Off = "off";
    public const string Badge = "badge";
    public const string Banner = "banner";

    public const string SoundSystem = "system";
    public const string SoundNone = "none";

    /// <summary>What a level means for delivery (the reference's <c>yln</c>).</summary>
    public readonly record struct Delivery(bool Banner, bool Badge);

    /// <summary>
    /// The levels a type offers: "Task complete" never has "Badge only" — a badge
    /// counts work that is blocked on the user, and an idle session is not (the
    /// reference's <c>P</c> table: permission and question get all three, idle two).
    /// </summary>
    public static IReadOnlyList<string> LevelsFor(string type) =>
        type == "idle" ? [Off, Banner] : [Off, Badge, Banner];

    /// <summary>The level in force for a type; an absent entry is "banner", as the page shows it.</summary>
    public static string LevelFor(IReadOnlyDictionary<string, string> levels, string type) =>
        levels.TryGetValue(type, out var level) && level is Off or Badge or Banner ? level : Banner;

    /// <summary>The reference's <c>yln</c> map: off shows nothing, badge counts only, banner does both.</summary>
    public static Delivery DeliveryFor(string level) => level switch
    {
        Off => new Delivery(false, false),
        Badge => new Delivery(false, true),
        _ => new Delivery(true, true),
    };

    /// <summary>Whether a delivery for this type shows an OS banner.</summary>
    public static bool ShowsBanner(IReadOnlyDictionary<string, string> levels, string type) =>
        DeliveryFor(LevelFor(levels, type)).Banner;

    /// <summary>Whether a delivery for this type counts toward the badge (the taskbar overlay).</summary>
    public static bool CountsForBadge(IReadOnlyDictionary<string, string> levels, string type) =>
        DeliveryFor(LevelFor(levels, type)).Badge;

    /// <summary>
    /// "Draw attention" is disabled while both attention types are off — there
    /// is nothing left that could draw it (the reference's <c>q</c>: every
    /// <c>ka</c> type at "off").
    /// </summary>
    public static bool AttentionSwitchDisabled(IReadOnlyDictionary<string, string> levels) =>
        AttentionTypes.All(type => LevelFor(levels, type) == Off);

    /// <summary>
    /// Whether the window should flash: the reference's <c>requestUserAttention</c>
    /// flashes only when the app is not focused and visible, and only with the
    /// preference on.
    /// </summary>
    public static bool ShouldFlash(bool drawAttentionEnabled, bool appFocusedAndVisible) =>
        drawAttentionEnabled && !appFocusedAndVisible;

    /// <summary>The reference's <c>TB</c>: a banner is silent when the sound is "none".</summary>
    public static bool IsSilent(string sound) => sound == SoundNone;

    /// <summary>The page's own label for a type.</summary>
    public static string Label(string type) => type switch
    {
        "permission" => "Permission requests",
        "question" => "Questions",
        _ => "Task complete",
    };

    /// <summary>The page's own description for a type.</summary>
    public static string Description(string type) => type switch
    {
        "permission" => "Jarvis needs your approval to use a tool.",
        "question" => "Jarvis is asking you to choose between options.",
        _ => "Jarvis finished and is waiting for your input.",
    };

    /// <summary>The label a level shows in its select.</summary>
    public static string LevelLabel(string level) => level switch
    {
        Off => "Off",
        Badge => "Badge only",
        _ => "Banners",
    };
}
