namespace JarvisCode.App.Services;

/// <summary>
/// The sidebar's row geometry, ported constant for constant from the reference
/// desktop's own stylesheet (1.44121.2.0, <c>shared-styles-DHnDvusg.css</c>): the
/// <c>--df-*</c> custom properties <c>.dframe-root</c> declares and the two blocks
/// that override them, <c>.dframe-sidebar[data-density=comfortable]</c> and
/// <c>.dframe-sidebar[data-variant=desktop][data-density=comfortable]</c>.
///
/// A CSS pixel and a WPF device-independent pixel are the same unit at 96 DPI, so
/// these ride straight into the layout rather than being re-derived. This build is
/// the desktop variant at the reference's comfortable density, which is why
/// <see cref="Current"/> is <see cref="ComfortableDesktop"/>; the compact set is the
/// base declaration and is kept so the pair is checkable.
/// </summary>
public sealed record SidebarMetrics
{
    /// <summary>Row height — the reference's <c>--df-row-h</c>.</summary>
    public required double RowHeight { get; init; }

    /// <summary>Horizontal padding inside a row — <c>--df-row-px</c>.</summary>
    public required double RowPaddingX { get; init; }

    /// <summary>Gap between a row's slots — <c>--df-row-gap</c>.</summary>
    public required double RowGap { get; init; }

    /// <summary>Row text size — <c>--df-row-font</c>.</summary>
    public required double RowFontSize { get; init; }

    /// <summary>Section-header text size — <c>--df-group-font</c>.</summary>
    public required double GroupFontSize { get; init; }

    /// <summary>Icon side — <c>--df-icon-size</c>.</summary>
    public required double IconSize { get; init; }

    /// <summary>The leading slot a row's icon sits in — <c>--df-leading-slot</c>.</summary>
    public required double LeadingSlot { get; init; }

    /// <summary>A row control (the ⋮ button, the status mark) — <c>--df-row-ctl</c>.</summary>
    public required double RowControl { get; init; }

    /// <summary>Row corner radius — <c>--df-radius-pill</c>.</summary>
    public required double RadiusPill { get; init; }

    /// <summary>Space above a section header — <c>--df-group-pt</c>.</summary>
    public required double GroupPaddingTop { get; init; }

    /// <summary>
    /// The pad that stands in for a missing icon, so an iconless row's label lines up
    /// with the labels of rows that have one — the reference's <c>--df-iconless-pad</c>
    /// default, <c>(leading-slot − icon) / 2</c>.
    /// </summary>
    public double IconlessPad => (LeadingSlot - IconSize) / 2;

    /// <summary>
    /// Where a row's text begins: the reference's <c>.df-label-inset</c>,
    /// <c>row-px + iconless-pad</c>. Section headers and iconless rows share it.
    /// </summary>
    public double LabelInset => RowPaddingX + IconlessPad;

    /// <summary>
    /// How far a nested child row is pushed in — the reference's
    /// <c>--df-row-indent: calc(var(--df-icon-size) + var(--df-row-gap))</c>.
    /// </summary>
    public double RowIndent => IconSize + RowGap;

    /// <summary>
    /// Where the accordion guide's hairline sits under a parent row — the reference's
    /// <c>.df-accordion-guide:before</c>, <c>row-px + leading-slot / 2 − 0.5px</c>.
    /// </summary>
    public double AccordionGuideOffset => RowPaddingX + (LeadingSlot / 2) - 0.5;

    /// <summary>
    /// The inset of a row's trailing controls — the reference's
    /// <c>right: calc((var(--df-row-h) - var(--df-row-ctl)) / 2)</c>.
    /// </summary>
    public double RowControlInset => (RowHeight - RowControl) / 2;

    /// <summary>
    /// A section header's minimum height — the reference's
    /// <c>calc(var(--df-group-pt) + (var(--df-row-h) - 8px) + 4px)</c>.
    /// </summary>
    public double GroupHeaderMinHeight => GroupPaddingTop + (RowHeight - 8) + 4;

    /// <summary>The trailing padding of a section header — its <c>pr-[calc((var(--df-row-h)-24px)/2)]</c>.</summary>
    public double GroupHeaderPaddingRight => Math.Max(0, (RowHeight - 24) / 2);

    /// <summary>
    /// How much of a row's trailing edge the label fades out over at rest — the
    /// reference's <c>mask-image</c> stop at <c>calc(100% - 24px)</c>.
    /// </summary>
    public const double LabelFadeWidth = 24;

    /// <summary>
    /// The same fade once the row's controls are showing (hover, focus, menu open) —
    /// its second mask, transparent from <c>calc(100% - 20px)</c> and starting to
    /// fade at <c>calc(100% - 44px)</c>.
    /// </summary>
    public const double LabelFadeWidthActive = 44;

    /// <summary>Where the active fade reaches full transparency, measured from the right edge.</summary>
    public const double LabelFadeClearActive = 20;

    /// <summary>
    /// The reference's <c>zE</c>: a title overflows when its natural width runs past
    /// the room it has, less the 44px a decorated row's trailing controls take.
    /// </summary>
    public static bool LabelOverflows(double naturalWidth, double availableWidth, bool decorated, bool suffixed) =>
        naturalWidth - (availableWidth - (decorated && !suffixed ? LabelFadeWidthActive : 0)) > 0;

    /// <summary>The base declaration on <c>.dframe-root</c>.</summary>
    public static SidebarMetrics Compact { get; } = new()
    {
        RowHeight = 26,
        RowPaddingX = 2,
        RowGap = 4,
        RowFontSize = 13,
        GroupFontSize = 12,
        IconSize = 16,
        LeadingSlot = 24,
        RowControl = 20,
        RadiusPill = 6,
        GroupPaddingTop = 12,
    };

    /// <summary><c>.dframe-sidebar[data-density=comfortable]</c>.</summary>
    public static SidebarMetrics Comfortable { get; } = new()
    {
        RowHeight = 32,
        RowPaddingX = 2,
        RowGap = 8,
        RowFontSize = 14,
        GroupFontSize = 13,
        IconSize = 20,
        LeadingSlot = 28,
        RowControl = 24,
        RadiusPill = 8,
        GroupPaddingTop = 16,
    };

    /// <summary>
    /// <c>.dframe-sidebar[data-variant=desktop][data-density=comfortable]</c>, which
    /// overrides exactly two values on top of <see cref="Comfortable"/>.
    /// </summary>
    public static SidebarMetrics ComfortableDesktop { get; } =
        Comfortable with { RowHeight = 30, GroupPaddingTop = 14 };

    /// <summary>What this build lays the sidebar out at.</summary>
    public static SidebarMetrics Current => ComfortableDesktop;
}
