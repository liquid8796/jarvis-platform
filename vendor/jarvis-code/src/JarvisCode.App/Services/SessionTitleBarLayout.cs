namespace JarvisCode.App.Services;

/// <summary>
/// The session titlebar's fitting rules, ported constant for constant from the
/// reference desktop 1.44121.4.0 (ion-dist chunk <c>c66fe388e-DOFZnzRG.js</c>: the
/// hook <c>Oh</c> and the three pure functions <c>Th</c>, <c>Eh</c> and <c>Dh</c>
/// it drives, over <c>yh/bh/xh/Sh/Ch/wh</c>).
///
/// The header has two things it can give up when the window narrows, and it gives
/// them up in a fixed order: first the label pill collapses to its folder icon, then
/// pane toggles fold into the overflow menu. Both decisions hold hysteresis, so a
/// header sitting exactly at the boundary does not flicker between the two states.
/// </summary>
public static class SessionTitleBarLayout
{
    /// <summary>The reference's <c>yh</c>: at most five toggles ever fold away.</summary>
    public const int MaxHiddenToggles = 5;

    /// <summary>
    /// The reference's <c>bh</c>: however long the title really is, the fit is computed
    /// against at most this much of it, so one enormous title cannot fold the whole rail.
    /// </summary>
    public const double TitleNaturalCap = 100;

    /// <summary>The reference's <c>xh</c>: slack a toggle needs beyond its own width before it comes back.</summary>
    public const double RestoreHysteresisPx = 24;

    /// <summary>The reference's <c>Sh</c>: the assumed toggle width until one has been measured.</summary>
    public const double DefaultToggleWidth = 32;

    /// <summary>The reference's <c>Ch</c>: free space the header needs before the pill may expand again.</summary>
    public const double UncompactFreePx = 96;

    /// <summary>The reference's <c>wh</c>: the pill budget must beat its collapse-time value by this much.</summary>
    public const double PillBudgetHysteresisPx = 1;

    /// <summary>
    /// The reference's <c>Th</c>: how much room the header has spare once the title has
    /// been given as much as it wants, up to <see cref="TitleNaturalCap"/>. Negative means
    /// the title is being squeezed by that many pixels.
    /// </summary>
    public static double Slack(double freePx, double titleClientPx, double titleNaturalPx)
        => freePx + titleClientPx - Math.Min(titleNaturalPx, TitleNaturalCap);

    /// <summary>
    /// The reference's <c>Eh</c>: by how much the hidden-toggle count should move. Negative
    /// slack folds toggles away, slack past one toggle plus the hysteresis brings them back,
    /// and anything between leaves the count where it is.
    /// </summary>
    public static int HiddenDelta(double slack, double toggleWidth, int hidden, int max = MaxHiddenToggles)
    {
        if (toggleWidth <= 0)
        {
            return 0;
        }

        if (slack < -1 && hidden < max)
        {
            return Math.Min((int)Math.Ceiling(-slack / toggleWidth), max - hidden);
        }

        if (slack >= toggleWidth + RestoreHysteresisPx && hidden > 0)
        {
            return -Math.Min((int)Math.Floor((slack - RestoreHysteresisPx) / toggleWidth), hidden);
        }

        return 0;
    }

    /// <summary>
    /// The reference's <c>Dh</c>: whether the label pill should be showing only its folder
    /// icon. It collapses once the header has run out of free space *and* the title is
    /// already being clipped; it expands again only once every toggle is back, the budget
    /// has genuinely grown since the collapse, and there is a comfortable amount of room.
    /// </summary>
    public static bool PillsCompact(
        bool compact,
        double freePx,
        double titleClientPx,
        double titleNaturalPx,
        int hidden,
        double pillBudget,
        double pillBudgetAtCollapse)
        => compact
            ? !(hidden == 0 && pillBudget > pillBudgetAtCollapse + PillBudgetHysteresisPx && freePx >= UncompactFreePx)
            : freePx <= 0 && titleClientPx < titleNaturalPx;
}
