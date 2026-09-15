namespace JarvisCode.App.Services;

/// <summary>
/// What the sidebar says about pinning, in the reference's own words (desktop
/// 1.44121.2.0, <c>shared-19-BctYnjt1.js</c>@180160 with the strings under their own
/// catalogue ids).
///
/// The reference declares the three drag labels together and does not say in the
/// bundle which drag state each belongs to; they are used here in the order a drag
/// goes through — the resting tip, the invitation once a row is in flight, and the
/// release prompt while the pointer is over the target. That mapping is this build's
/// reading and is declared as such.
/// </summary>
public static class PinHints
{
    /// <summary>The resting hint on the pin target (its <c>V39qk5WLAX</c>).</summary>
    public const string DragToPin = "Drag to pin";

    /// <summary>Shown while a row is being dragged (its <c>Au4AP9NTeL</c>).</summary>
    public const string DropHere = "Drop here";

    /// <summary>Shown while the pointer is over the target (its <c>9Z0I31VbJD</c>).</summary>
    public const string LetGo = "Let go";

    /// <summary>
    /// The one-time tip the reference raises the first time a session is pinned from a
    /// menu — its <c>maybeShowDragPinHint()</c>, fired on the pin arm of the row's pin
    /// action. The reference's string selects on the surface's kind; the Code sidebar
    /// is its <c>code</c> arm.
    /// </summary>
    public const string DragPinTip = "Tip: you can drag sessions here to pin them";
}
