using System.Windows;
using System.Windows.Media.Animation;

namespace JarvisCode.App.Controls;

/// <summary>
/// A CSS <c>cubic-bezier(x1, y1, x2, y2)</c> as a WPF easing function: the
/// curve's x is solved for the elapsed fraction, then its y is the eased
/// progress. Newton-Raphson with a bisection fallback, which is how browsers
/// evaluate the same curve.
/// </summary>
internal sealed class CubicBezierEase(double x1, double y1, double x2, double y2) : EasingFunctionBase
{
    private const int NewtonIterations = 8;
    private const double NewtonMinSlope = 0.001;
    private const double SubdivisionPrecision = 1e-7;
    private const int SubdivisionMaxIterations = 20;

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime <= 0 || normalizedTime >= 1)
        {
            return Math.Clamp(normalizedTime, 0, 1);
        }

        return Curve(SolveForX(normalizedTime), y1, y2);
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(x1, y1, x2, y2);

    /// <summary>The 1-D cubic Bézier with endpoints pinned at 0 and 1.</summary>
    private static double Curve(double t, double a, double b)
    {
        var inverse = 1 - t;
        return (3 * inverse * inverse * t * a) + (3 * inverse * t * t * b) + (t * t * t);
    }

    private static double Slope(double t, double a, double b)
    {
        var inverse = 1 - t;
        return (3 * inverse * inverse * (a - 0)) +
               (6 * inverse * t * (b - a)) +
               (3 * t * t * (1 - b));
    }

    private double SolveForX(double x)
    {
        var guess = x;
        for (var i = 0; i < NewtonIterations; i++)
        {
            var slope = Slope(guess, x1, x2);
            if (Math.Abs(slope) < NewtonMinSlope)
            {
                break;
            }

            guess -= (Curve(guess, x1, x2) - x) / slope;
        }

        if (guess is >= 0 and <= 1 && Math.Abs(Curve(guess, x1, x2) - x) < SubdivisionPrecision)
        {
            return guess;
        }

        double low = 0, high = 1;
        guess = x;
        for (var i = 0; i < SubdivisionMaxIterations; i++)
        {
            var current = Curve(guess, x1, x2);
            if (Math.Abs(current - x) < SubdivisionPrecision)
            {
                break;
            }

            if (current > x)
            {
                high = guess;
            }
            else
            {
                low = guess;
            }

            guess = ((high - low) / 2) + low;
        }

        return guess;
    }
}
