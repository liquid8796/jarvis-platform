using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>Which part of the titlebar a child is, so the panel need not read its own child order.</summary>
public enum SessionTitleBarSlot
{
    /// <summary>The 32×24 gutter square holding the origin glyph.</summary>
    Origin,

    /// <summary>The title, its rename editor, or its loading skeleton.</summary>
    Title,

    /// <summary>The origin label pill.</summary>
    Pill,

    /// <summary>Everything pushed to the right edge: the agent badge, the rail and the pane controls.</summary>
    Trailing,
}

/// <summary>
/// The Code session's titlebar, ported from the reference desktop 1.44121.4.0 (ion-dist
/// chunk <c>c66fe388e-DOFZnzRG.js</c>: <c>Nh</c>, class <c>epitaxy-titlebar relative flex
/// items-center h-[32px] pl-0 pr-[var(--epitaxy-titlebar-pr,12px)]</c>, over the slot
/// classes <c>yb</c>/<c>bb</c>/<c>xb</c> it imports from <c>shared-17</c>).
///
/// It is a panel rather than a stack because the reference's fitting rules
/// (<see cref="SessionTitleBarLayout"/>) are stated in terms the layout has to supply —
/// how much room is spare, and how wide the title wanted to be against how wide it got —
/// and a StackPanel hands its children infinite width, so it can answer neither.
///
/// The bar is also the window's drag region: the reference lays a full-bleed
/// <c>draggable</c> layer behind it and marks both content clusters <c>draggable-none</c>,
/// so pressing the bar itself moves the window while pressing anything in it does not.
/// </summary>
public sealed class SessionTitleBarPanel : System.Windows.Controls.Panel
{
    /// <summary>The reference's <c>h-[32px]</c>.</summary>
    public const double BarHeight = 32;

    /// <summary>The reference's <c>pr-[var(--epitaxy-titlebar-pr,12px)]</c>; its <c>pl-0</c> is why there is no left pad.</summary>
    public const double BarPaddingRight = 12;

    /// <summary>The reference's <c>w-[var(--chat-gutter-start,32px)]</c>.</summary>
    public const double OriginSlotWidth = 32;

    /// <summary>The reference's <c>h-6</c> on the origin slot.</summary>
    public const double OriginSlotHeight = 24;

    /// <summary>The reference's <c>gap-xs</c> between title and pill, at the desktop's comfortable density.</summary>
    public const double TitleGap = 8;

    public static readonly DependencyProperty SlotProperty = DependencyProperty.RegisterAttached(
        "Slot",
        typeof(SessionTitleBarSlot),
        typeof(SessionTitleBarPanel),
        new FrameworkPropertyMetadata(SessionTitleBarSlot.Title, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static void SetSlot(UIElement element, SessionTitleBarSlot value) => element.SetValue(SlotProperty, value);

    public static SessionTitleBarSlot GetSlot(UIElement element) => (SessionTitleBarSlot)element.GetValue(SlotProperty);

    private double _originWidth;
    private double _titleWidth;
    private double _pillWidth;
    private double _trailingWidth;
    private double _gap;

    private bool _compact;
    private int _hidden;
    private double _budgetAtCollapse = double.NegativeInfinity;

    public SessionTitleBarPanel()
    {
        Height = BarHeight;
        // A panel with no brush is not hit-testable, and the bar's own background is
        // exactly the part that must answer the mouse: it is the drag region.
        Background = Brushes.Transparent;
    }

    /// <summary>The rail whose toggles fold away when the bar runs out of room.</summary>
    public Views.Panels.PanelRail? Rail { get; set; }

    /// <summary>
    /// Drops the fitting state. The hysteresis in <see cref="SessionTitleBarLayout"/> is
    /// about one bar narrowing and widening again; carried across a session switch it
    /// would let the previous session's title decide this one's first layout.
    /// </summary>
    public void ResetFit()
    {
        _compact = false;
        _hidden = 0;
        _budgetAtCollapse = double.NegativeInfinity;
        if (Child(SessionTitleBarSlot.Pill) is SessionOriginPill pill)
        {
            pill.Compact = false;
        }

        Rail?.SetHiddenCount(0);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var origin = Child(SessionTitleBarSlot.Origin);
        var title = Child(SessionTitleBarSlot.Title);
        var pill = Child(SessionTitleBarSlot.Pill) as SessionOriginPill;
        var trailing = Child(SessionTitleBarSlot.Trailing);

        origin?.Measure(new Size(OriginSlotWidth, OriginSlotHeight));
        _originWidth = origin is null ? 0 : OriginSlotWidth;

        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var room = 0.0;

        // The reference re-runs its measurement whenever the flags it sets change what
        // fits. Two passes is what that converges to: the hysteresis in PillsCompact and
        // HiddenDelta forbids a state from immediately undoing itself, so a second pass
        // can settle but never oscillate. The trailing cluster is measured inside the loop
        // because folding a toggle is one of the things that changes, and the room it gives
        // back belongs to the title in the same pass rather than the next one.
        for (var pass = 0; pass < 2; pass++)
        {
            trailing?.Measure(new Size(double.PositiveInfinity, BarHeight));
            _trailingWidth = trailing?.DesiredSize.Width ?? 0;
            room = Math.Max(0, width - BarPaddingRight - _originWidth - _trailingWidth);

            title?.Measure(new Size(double.PositiveInfinity, BarHeight));
            var titleNatural = title?.DesiredSize.Width ?? 0;

            pill?.Measure(new Size(double.PositiveInfinity, BarHeight));
            var pillNatural = pill is { Visibility: Visibility.Visible } ? pill.DesiredSize.Width : 0;
            _gap = pillNatural > 0 ? TitleGap : 0;

            Allocate(room, titleNatural, pillNatural, pill?.Compact ?? false);

            var freePx = Math.Max(0, room - (_titleWidth + _gap + _pillWidth));
            var pillBudget = width - _trailingWidth - titleNatural;

            var moved = ApplyFit(pill, freePx, titleNatural, pillBudget);
            if (!moved || pass == 1)
            {
                break;
            }
        }

        title?.Measure(new Size(_titleWidth, BarHeight));
        pill?.Measure(new Size(_pillWidth, BarHeight));

        return new Size(
            double.IsInfinity(availableSize.Width)
                ? _originWidth + _titleWidth + _gap + _pillWidth + _trailingWidth + BarPaddingRight
                : availableSize.Width,
            BarHeight);
    }

    /// <summary>
    /// Shares the room out the way the reference's flex row does: the origin gutter and the
    /// trailing cluster are <c>shrink-0</c>, while the title and the pill both carry
    /// <c>min-w-0</c> and give up width in proportion to how much they asked for — except a
    /// compacted pill, which the reference pins at <c>shrink-0 min-w-fit</c>.
    /// </summary>
    private void Allocate(double room, double titleNatural, double pillNatural, bool pillCompact)
    {
        var need = titleNatural + _gap + pillNatural;
        if (need <= room)
        {
            _titleWidth = titleNatural;
            _pillWidth = pillNatural;
            return;
        }

        var overflow = need - room;
        var shrinkable = titleNatural + (pillCompact ? 0 : pillNatural);
        if (shrinkable <= 0)
        {
            _titleWidth = 0;
            _pillWidth = Math.Min(pillNatural, Math.Max(0, room - _gap));
            return;
        }

        _titleWidth = Math.Max(0, titleNatural - (overflow * titleNatural / shrinkable));
        _pillWidth = pillCompact
            ? Math.Min(pillNatural, Math.Max(0, room - _gap - _titleWidth))
            : Math.Max(0, pillNatural - (overflow * pillNatural / shrinkable));
    }

    /// <summary>
    /// Applies the reference's two fitting decisions, and answers whether either of them
    /// moved — which is what tells the measure pass it has to run again.
    /// </summary>
    private bool ApplyFit(SessionOriginPill? pill, double freePx, double titleNatural, double pillBudget)
    {
        var moved = false;

        var compact = SessionTitleBarLayout.PillsCompact(
            _compact, freePx, _titleWidth, titleNatural, _hidden, pillBudget, _budgetAtCollapse);
        if (compact != _compact)
        {
            // Its `g.current`: the budget the pill collapsed at is the mark the budget has
            // to beat before it may expand again, and expanding forgets the mark.
            _budgetAtCollapse = compact ? pillBudget : double.NegativeInfinity;
            _compact = compact;
            if (pill is not null)
            {
                pill.Compact = compact;
            }

            moved = true;
        }

        var toggleWidth = Rail?.ToggleWidth ?? SessionTitleBarLayout.DefaultToggleWidth;
        var slack = SessionTitleBarLayout.Slack(freePx, _titleWidth, titleNatural);
        var hidden = _hidden + SessionTitleBarLayout.HiddenDelta(slack, toggleWidth, _hidden);
        if (hidden != _hidden)
        {
            _hidden = hidden;
            Rail?.SetHiddenCount(hidden);
            moved = true;
        }

        return moved;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;

        if (Child(SessionTitleBarSlot.Origin) is { } origin)
        {
            origin.Arrange(new Rect(x, (finalSize.Height - OriginSlotHeight) / 2, OriginSlotWidth, OriginSlotHeight));
            x += OriginSlotWidth;
        }

        Place(Child(SessionTitleBarSlot.Title), x, _titleWidth, finalSize);
        x += _titleWidth + _gap;

        Place(Child(SessionTitleBarSlot.Pill), x, _pillWidth, finalSize);
        x += _pillWidth;

        if (Child(SessionTitleBarSlot.Trailing) is { } trailing)
        {
            // The reference's `ml-auto`: the cluster sits against the right edge, and only
            // gives that up when the lead has already taken the room.
            Place(trailing, Math.Max(x, finalSize.Width - BarPaddingRight - _trailingWidth), _trailingWidth, finalSize);
        }

        return finalSize;
    }

    private static void Place(UIElement? element, double x, double width, Size finalSize)
    {
        if (element is null)
        {
            return;
        }

        var height = Math.Min(element.DesiredSize.Height, finalSize.Height);
        element.Arrange(new Rect(x, (finalSize.Height - height) / 2, width, height));
    }

    private UIElement? Child(SessionTitleBarSlot slot)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (GetSlot(child) == slot)
            {
                return child;
            }
        }

        return null;
    }

    // ---- the drag layer ----

    /// <summary>
    /// The reference's <c>draggable absolute inset-0</c> behind the bar, with
    /// <c>draggable-none</c> on both content clusters: a press that lands on the bar itself
    /// moves the window and a double-click maximizes it, which is what
    /// <c>-webkit-app-region: drag</c> does on Windows; a press on any child does neither.
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!ReferenceEquals(e.OriginalSource, this) || Window.GetWindow(this) is not { } window)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            window.DragMove();
            e.Handled = true;
        }
    }
}
