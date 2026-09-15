using JarvisCode.Core.Permissions;

namespace JarvisCode.Cli.Repl.Render;

/// <summary>
/// The reference's terminal glyph table (CLI 2.1.257, the <c>Tr</c>…<c>$y</c>
/// block read out of the binary at the <c>Tr=M()==="macos"?"⏺":"●"</c>
/// site). The names are the reference's own roles so a renderer reads like the
/// component it was ported from.
/// </summary>
internal static class Glyphs
{
    /// <summary>The reference's <c>Tr</c>: ⏺ on macOS, ● everywhere else — so this build draws ●.</summary>
    public const string Bullet = "●";

    /// <summary>The reference's <c>bQ</c>.</summary>
    public const string Dot = "∙";

    /// <summary>The reference's <c>iv</c>: the star the welcome line and the retry rows carry.</summary>
    public const string Star = "✻";

    /// <summary>The reference's <c>PZe</c>: the connector under a tool row.</summary>
    public const string Connector = "↳";

    /// <summary>The reference's tree connectors (<c>$y</c>).</summary>
    public const string TreeBranch = "├";
    public const string TreeLast = "└";
    public const string TreePipe = "│";

    public const string ArrowUp = "↑";
    public const string ArrowDown = "↓";
    public const string ArrowLeft = "←";
    public const string ArrowRight = "→";
    public const string Return = "⏎";

    /// <summary>The reference's <c>Fkt</c> (⏸) and <c>FCe</c> (⏵⏵): the two mode symbols.</summary>
    public const string Pause = "⏸";
    public const string Play = "⏵⏵";

    /// <summary>The reference's <c>$kt</c>: the startup tips' leader.</summary>
    public const string Sparkle = "✦";

    /// <summary>The reference's <c>hRn</c>: the rotating-tip bullet.</summary>
    public const string TipMark = "※";

    public const string Warning = "⚠";
    public const string Tick = "✔";
    public const string Cross = "✕";
    public const string Pointer = "▸";

    /// <summary>The reference's <c>Nkt</c>: the four spinner frames of its default animation.</summary>
    public static readonly IReadOnlyList<string> SpinnerFrames = ["∴", "∷", "∵", "∷"];

    /// <summary>The reference's braille frames (its <c>l</c>), used where the theme asks for them.</summary>
    public static readonly IReadOnlyList<string> BrailleFrames =
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    /// <summary>The result connector the reference draws under a tool row.</summary>
    public const string ResultConnector = "⎿";
}

/// <summary>
/// The reference's permission-mode descriptor table (its <c>i</c>, beside the
/// glyphs): the title, the footer wording, the symbol and the colour token each
/// mode carries. Its <c>bubble</c> row has no counterpart here and its
/// <c>dontAsk</c> maps onto this engine's Auto-with-prompts-denied.
/// </summary>
internal sealed record ModeDescriptor(
    string Title, string ShortTitle, string Indicator, string Symbol, string Color, string External);

internal static class ModeDescriptors
{
    public static readonly ModeDescriptor Manual =
        new("Manual", "Manual", "manual mode", Glyphs.Pause, "inactive", "default");

    public static readonly ModeDescriptor Plan =
        new("Plan", "Plan", "plan mode", Glyphs.Pause, "planMode", "plan");

    public static readonly ModeDescriptor AcceptEdits =
        new("Accept edits", "Accept", "accept edits", Glyphs.Play, "autoAccept", "acceptEdits");

    public static readonly ModeDescriptor Bypass =
        new("Bypass Permissions", "Bypass", "bypass permissions", Glyphs.Play, "error", "bypassPermissions");

    public static readonly ModeDescriptor DontAsk =
        new("Don't Ask", "DontAsk", "don't ask", Glyphs.Play, "error", "dontAsk");

    public static readonly ModeDescriptor Auto =
        new("Auto", "Auto", "auto mode", Glyphs.Play, "warning", "auto");

    /// <summary>The reference's <c>s(e)</c>: an unknown mode falls back to the manual row.</summary>
    public static ModeDescriptor For(PermissionMode mode, bool dontAsk = false) => mode switch
    {
        PermissionMode.Plan => Plan,
        PermissionMode.AcceptEdits => AcceptEdits,
        PermissionMode.Bypass => Bypass,
        PermissionMode.Auto => dontAsk ? DontAsk : Auto,
        _ => Manual,
    };

    /// <summary>The reference's footer text for a mode: <c>{symbol} {indicator} on</c>.</summary>
    public static string FooterText(ModeDescriptor descriptor) =>
        $"{descriptor.Symbol} {descriptor.Indicator} on";

    /// <summary>The reference's ordering (its <c>t</c>), which is what shift+tab cycles through.</summary>
    public static readonly IReadOnlyList<PermissionMode> CycleOrder =
    [
        PermissionMode.Manual, PermissionMode.AcceptEdits, PermissionMode.Auto, PermissionMode.Plan,
    ];

    /// <summary>shift+tab: the next mode in the reference's order, skipping the ones this session may not enter.</summary>
    public static PermissionMode Next(PermissionMode current, bool bypassAvailable)
    {
        List<PermissionMode> order = bypassAvailable
            ? [.. CycleOrder, PermissionMode.Bypass]
            : [.. CycleOrder];
        int index = order.IndexOf(current);
        return order[(index < 0 ? 0 : index + 1) % order.Count];
    }
}
