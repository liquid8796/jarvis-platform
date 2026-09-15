namespace JarvisCode.App.Services;

/// <summary>
/// What the Code transcript says about a rendered widget, and the geometry it
/// says it in — ported from the reference's own MCP-app tool row (its <c>tO</c>
/// in ion-dist <c>c360a9e1c-DUoNQd2W.js</c>, desktop 1.40609.1.0).
///
/// The row is one button: a label, the tool's short name in the code face, and a
/// disclosure caret. The label is <see cref="Rendering"/> with the shimmer while
/// the call is running or the widget has not reported itself initialized, and
/// <see cref="WidgetFrom"/> in the secondary colour afterwards — which is also
/// the row's accessible name. Under it sits the widget itself, expanded from the
/// start (the reference opens its <c>useState(true)</c>) and hidden until the
/// page has initialized, so nothing flashes.
///
/// The reference's <c>loading_messages</c> are deliberately not shown here: they
/// belong to its conversation status line (<c>Rw</c> in
/// <c>ca2ef848d-D8BWZk64.js</c>), which renders only while the session carries
/// no status message of its own — never on a Code session running a tool. See
/// the surface manifest.
/// </summary>
public static class VisualizeWidgetStrings
{
    /// <summary>The running label, shimmering, until the widget page initializes.</summary>
    public const string Rendering = "Rendering widget";

    private const string WidgetFromTemplate = "Widget from {server}";

    /// <summary>The settled label, and the row's accessible name.</summary>
    public static string WidgetFrom(string server) =>
        WidgetFromTemplate.Replace("{server}", server, StringComparison.Ordinal);

    /// <summary>The reference's <c>gap-g2</c> between the row's three parts.</summary>
    public const double HeaderGap = 3;

    /// <summary>Its <c>mt-p3</c>: the gap between the row and the widget under it.</summary>
    public const double BodyMarginTop = 4;

    /// <summary>Its <c>rounded-r6</c>.</summary>
    public const double BodyRadius = 8;

    /// <summary>
    /// Its <c>p-p6</c>, which the reference applies only to a built-in server's
    /// visualize widget (<c>isBuiltIn &amp;&amp; Il(name)</c>) — a connector's
    /// own app gets the border with no padding.
    /// </summary>
    public const double BodyPadding = 8;
}
