using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// The composer chin's context ring — the reference's `eI` (desktop 1.40609.1.0,
/// `shared-10-3`@310696): a 12px SVG with two circles of radius (size−2)/2 and stroke
/// 2, the track in `--t3` and the arc dash-offset to leave the used fraction visible,
/// rotated a quarter turn so it starts at twelve o'clock and capped round. Its colour
/// is the reference's `Wx` tier — accent under 75%, warning from 75%, danger from 90%.
/// </summary>
public sealed class ContextRingGlyph : Grid
{
    private readonly Ellipse _track;
    private readonly System.Windows.Shapes.Path _arc;

    public ContextRingGlyph()
    {
        Width = ContextRing.Size;
        Height = ContextRing.Size;

        _track = new Ellipse
        {
            Width = ContextRing.Size,
            Height = ContextRing.Size,
            StrokeThickness = ContextRing.StrokeWidth,
        };
        _track.SetResourceReference(Shape.StrokeProperty, "Tint3Brush");
        Children.Add(_track);

        _arc = new System.Windows.Shapes.Path
        {
            StrokeThickness = ContextRing.StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Children.Add(_arc);
        SetPercent(0);
    }

    /// <summary>Paints the ring at <paramref name="pct"/> percent full.</summary>
    public void SetPercent(double pct)
    {
        var clamped = Math.Max(0, Math.Min(100, pct));
        _arc.SetResourceReference(Shape.StrokeProperty,
            ContextRing.StrokeBrushKey(ContextRing.Tier(clamped)));

        if (clamped <= 0)
        {
            _arc.Data = null;
            return;
        }

        var r = ContextRing.Radius;
        var centre = ContextRing.Size / 2;
        var sweep = 2 * Math.PI * clamped / 100;
        if (clamped >= 100)
        {
            _arc.Data = new EllipseGeometry(new Point(centre, centre), r, r);
            return;
        }

        // Twelve o'clock is -90 degrees in screen coordinates, which is the reference's
        // `-rotate-90` on the whole svg.
        var start = new Point(centre, centre - r);
        var end = new Point(centre + r * Math.Sin(sweep), centre - r * Math.Cos(sweep));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0,
            sweep > Math.PI, SweepDirection.Clockwise, true));
        _arc.Data = new PathGeometry([figure]);
    }
}
