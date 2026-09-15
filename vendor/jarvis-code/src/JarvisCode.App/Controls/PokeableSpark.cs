using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// The empty chat screen's mascot, which can be poked. Ported from the reference
/// desktop's <c>J_</c> (<c>ca2ef848d-C_oPm_EH.js</c>, desktop 1.44121.2.0), which
/// wraps the plain sprite in a right-side tooltip: a press bumps a counter and puts
/// the sprite into <see cref="SparkState.Tickle"/> until it reports back, and the
/// tooltip answers with <see cref="ChatWelcome.PokeReply"/> for the count so far.
///
/// Poking and the tooltip are both off while the mascot is busy — the reference's
/// <c>d = !isInteractive || state === "thinking" || state === "writing"</c> — so the
/// onboarding mascot, which is writing, is not pokeable until it settles.
/// </summary>
public sealed class PokeableSpark : Border
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(SparkState), typeof(PokeableSpark),
        new FrameworkPropertyMetadata(SparkState.Idle, OnStateChanged));

    public static readonly DependencyProperty IsInteractiveProperty = DependencyProperty.Register(
        nameof(IsInteractive), typeof(bool), typeof(PokeableSpark),
        new FrameworkPropertyMetadata(true, OnStateChanged));

    private readonly SparkGlyph _glyph = new();
    private int _pokes;
    private bool _tickling;

    public PokeableSpark()
    {
        Child = _glyph;
        Focusable = false;
        // A Decorator cannot be hit-tested over its empty area, and the poke is a
        // press anywhere on the mascot rather than on its ink.
        Background = System.Windows.Media.Brushes.Transparent;
        _glyph.Ended += OnTickleEnded;
        ApplyState();
        UpdateTooltip();
    }

    /// <summary>The state the mascot returns to once a poke has played out.</summary>
    public SparkState State
    {
        get => (SparkState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>Whether this mascot answers a press at all.</summary>
    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    /// <summary>The reference's <c>d</c>: a busy mascot neither tickles nor talks.</summary>
    private bool Busy => !IsInteractive || State is SparkState.Thinking or SparkState.Writing;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var spark = (PokeableSpark)d;
        spark.ApplyState();
        spark.UpdateTooltip();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (Busy || _tickling)
        {
            return;
        }

        _pokes++;
        _tickling = true;
        _glyph.State = SparkState.Tickle;
        UpdateTooltip();
    }

    private void OnTickleEnded(object? sender, EventArgs e)
    {
        if (!_tickling)
        {
            return;
        }

        _tickling = false;
        ApplyState();
    }

    private void ApplyState()
    {
        if (!_tickling)
        {
            _glyph.State = State;
        }

        IsHitTestVisible = !Busy;
    }

    private void UpdateTooltip()
    {
        if (Busy)
        {
            ToolTip = null;
            return;
        }

        ToolTip = new ToolTip
        {
            Content = ChatWelcome.PokeReply(_pokes),
            Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
        };
    }
}
