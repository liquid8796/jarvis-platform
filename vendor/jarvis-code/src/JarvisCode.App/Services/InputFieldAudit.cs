using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace JarvisCode.App.Services;

/// <summary>
/// What a screenshot cannot settle about a text field: where its caret really is, what
/// colour that caret really is, and whether the hint standing in for the text starts at
/// the same point the text will. All three are laid-out facts rather than declared ones —
/// a hint's offset is right or wrong only against the padding, border and font the field
/// ended up with — so they are measured on the built tree and reported per field.
/// </summary>
internal static class InputFieldAudit
{
    /// <summary>
    /// How far a placeholder may sit from the caret before it reads as misaligned. A
    /// device-independent pixel each way absorbs layout rounding and nothing else.
    /// </summary>
    private const double OffsetTolerance = 1.0;

    /// <summary>
    /// The relative-luminance gap below which a caret is invisible against the field it
    /// blinks in. WPF derives an unset caret from the inverse of the Background colour,
    /// which lands on near-black for a transparent or alpha-composited field.
    /// </summary>
    private const double CaretContrast = 0.10;

    internal sealed record Finding(string Field, string Problem);

    /// <summary>Every visible, editable field under <paramref name="root"/>, audited.</summary>
    internal static List<Finding> Audit(Visual root, string surface)
    {
        var findings = new List<Finding>();
        foreach (var box in Descendants<TextBox>(root))
        {
            if (box.IsReadOnly || !box.IsVisible || box.ActualWidth < 1 || box.ActualHeight < 1)
            {
                continue;
            }

            var name = Describe(box, surface);
            var caret = Caret(box);
            var behind = Behind(box);
            if (Math.Abs(Luminance(caret) - Luminance(behind)) < CaretContrast)
            {
                findings.Add(new Finding(name,
                    $"the caret is invisible: {Hex(caret)} on {Hex(behind)}"));
            }

            // The caret at index 0 is where the first character will be drawn, which is
            // the one point a placeholder has to agree with.
            Rect start;
            try
            {
                start = box.GetRectFromCharacterIndex(0);
            }
            catch (Exception)
            {
                continue;
            }

            if (double.IsInfinity(start.X) || double.IsInfinity(start.Y))
            {
                continue;
            }

            var content = new Rect(0, 0, box.ActualWidth, box.ActualHeight);
            if (start.Left < content.Left - OffsetTolerance ||
                start.Top < content.Top - OffsetTolerance ||
                start.Bottom > content.Bottom + OffsetTolerance)
            {
                findings.Add(new Finding(name,
                    $"the first line does not fit the field: line {Show(start)} in {Show(content)}"));
            }

            // A template positions its placeholder by binding, and a binding that failed
            // leaves the label where the field's own edge is rather than raising anything:
            // nothing on screen says why, so the status is read rather than the pixels.
            if (box.Template?.FindName("Placeholder", box) is TextBlock templated &&
                BindingOperations.GetBindingExpression(templated, FrameworkElement.MarginProperty)
                    is { Status: not (BindingStatus.Active or BindingStatus.Unattached) } expression)
            {
                findings.Add(new Finding(name, $"the placeholder's inset binding is {expression.Status}"));
            }

            var hints = Hints(box).ToList();
            // A hint that never draws is the same bug worn differently: the field
            // advertises one and the reader sees an empty box.
            if (hints.Count == 0 && box.Text.Length == 0 &&
                Controls.PlaceholderText.GetText(box) is { Length: > 0 } declared)
            {
                findings.Add(new Finding(name, $"the placeholder \"{declared}\" is never drawn"));
            }

            foreach (var hint in hints)
            {
                // TransformToVisual, not TransformToAncestor: a hint laid over the field
                // is its sibling rather than its descendant, and only a common ancestor
                // is guaranteed.
                Point origin;
                try
                {
                    origin = hint.TransformToVisual(box).Transform(new Point(0, 0));
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (Math.Abs(origin.X - start.X) > OffsetTolerance ||
                    Math.Abs(origin.Y - start.Y) > OffsetTolerance)
                {
                    findings.Add(new Finding(name,
                        $"the placeholder starts at ({Round(origin.X)}, {Round(origin.Y)}) " +
                        $"where the caret starts at ({Round(start.X)}, {Round(start.Y)})"));
                }

                if (hint.ActualWidth > box.ActualWidth + OffsetTolerance ||
                    origin.Y + hint.ActualHeight > box.ActualHeight + OffsetTolerance)
                {
                    findings.Add(new Finding(name,
                        $"the placeholder overflows the field: {Round(hint.ActualWidth)}x" +
                        $"{Round(hint.ActualHeight)} at y={Round(origin.Y)} in a field " +
                        $"{Round(box.ActualWidth)}x{Round(box.ActualHeight)}"));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// The labels standing in for a field's text: the placeholder its own template draws,
    /// and any click-through label a caller laid over it.
    /// </summary>
    private static IEnumerable<TextBlock> Hints(TextBox box)
    {
        if (box.Template?.FindName("Placeholder", box) is TextBlock part &&
            part.IsVisible && part.Text.Length > 0)
        {
            yield return part;
        }

        if (VisualTreeHelper.GetParent(box) is not Panel parent)
        {
            yield break;
        }

        foreach (var sibling in parent.Children.OfType<TextBlock>())
        {
            if (!sibling.IsHitTestVisible && sibling.IsVisible && sibling.Text.Length > 0)
            {
                yield return sibling;
            }
        }
    }

    /// <summary>
    /// The colour the caret is actually drawn in. WPF uses <c>CaretBrush</c> when it is
    /// set and otherwise inverts the field's Background — dropping its alpha, which is
    /// what turns a 5%-ink fill into a near-black caret.
    /// </summary>
    private static Color Caret(TextBox box)
    {
        if (box.CaretBrush is SolidColorBrush caret)
        {
            return Over(caret.Color, Behind(box));
        }

        if (box.Background is SolidColorBrush fill)
        {
            return Color.FromRgb((byte)~fill.Color.R, (byte)~fill.Color.G, (byte)~fill.Color.B);
        }

        return Colors.Black;
    }

    /// <summary>What the field is painted on, composited down to the first opaque surface.</summary>
    private static Color Behind(TextBox box)
    {
        var layers = new List<Color>();
        if (box.Background is SolidColorBrush own)
        {
            layers.Add(own.Color);
        }

        DependencyObject? node = VisualTreeHelper.GetParent(box);
        while (node is not null)
        {
            var brush = node switch
            {
                Panel panel => panel.Background,
                Border border => border.Background,
                Control control => control.Background,
                _ => null,
            };

            if (brush is SolidColorBrush solid && solid.Color.A > 0)
            {
                layers.Add(solid.Color);
                if (solid.Color.A == 255)
                {
                    break;
                }
            }

            node = VisualTreeHelper.GetParent(node);
        }

        // Composited from the farthest surface inwards, over a black floor for the case
        // where nothing opaque was found before the tree ran out.
        var result = Colors.Black;
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            result = Over(layers[i], result);
        }

        return result;
    }

    /// <summary>Source-over compositing, which is what an alpha fill really shows.</summary>
    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round((top.R * a) + (under.R * (1 - a))),
            (byte)Math.Round((top.G * a) + (under.G * (1 - a))),
            (byte)Math.Round((top.B * a) + (under.B * (1 - a))));
    }

    /// <summary>Relative luminance, WCAG's own weighting.</summary>
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static string Describe(TextBox box, string surface)
    {
        var name = System.Windows.Automation.AutomationProperties.GetName(box);
        if (string.IsNullOrEmpty(name))
        {
            name = Controls.PlaceholderText.GetText(box) ?? box.Name;
        }

        return $"{surface}: {(string.IsNullOrEmpty(name) ? "unnamed field" : name)}";
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static double Round(double value) => Math.Round(value, 1);

    private static string Show(Rect rect) =>
        $"({Round(rect.X)}, {Round(rect.Y)}, {Round(rect.Width)}x{Round(rect.Height)})";

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
