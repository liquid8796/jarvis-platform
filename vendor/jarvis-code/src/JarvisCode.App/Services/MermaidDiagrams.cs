using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// Everything the reference decides about a <c>```mermaid</c> fence before the
/// diagram exists: which fence is one, what mermaid is initialized with, what is
/// stripped out of the source first, and the two refusals that stripping can end
/// in. Ported from the chat renderer's own mermaid module — <c>Ql</c> and the
/// sandbox render helper <c>zl</c> in ion-dist chunk
/// <c>shared-12-4ZL7iq1e.js</c>, desktop 1.40609.1.0 — where it sits behind the
/// <c>claude_ai_markdown_mermaid_render</c> gate.
///
/// This half is pure text, so it is a plain static class the tests drive without
/// an engine view; <see cref="MermaidRenderer"/> is the half that needs one.
/// </summary>
internal static class MermaidDiagrams
{
    /// <summary>
    /// The fence language the reference switches on: its check is
    /// <c>"mermaid" === language</c> against the lowercased first word of the
    /// <c>language-…</c> class, so the comparison here lowercases too.
    /// </summary>
    public static bool IsDiagramFence(string? language) =>
        !string.IsNullOrWhiteSpace(language)
        && string.Equals(language.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a rendered diagram announces itself as — the reference's
    /// <c>role="img"</c> label, catalogue id <c>xz/cIYRwG0</c>. It names the
    /// format, not the assistant, so it is not rebranded.
    /// </summary>
    public const string DiagramLabel = "Mermaid diagram";

    /// <summary>The reference races <c>mermaid.render</c> against this and gives up.</summary>
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromMilliseconds(5000);

    /// <summary>
    /// <c>maxTextSize</c>, <c>maxEdges</c> and the <c>space:</c> ceiling the
    /// preprocessor clamps a block diagram to — the reference's 2e4, 400, 100.
    /// </summary>
    public const int MaxTextSize = 20000;

    public const int MaxEdges = 400;

    public const int MaxBlockSpace = 100;

    /// <summary>
    /// The reference gives up after 32 passes of the shape-data strip; a source
    /// that still changes on the 32nd is refused rather than rendered.
    /// </summary>
    public const int StripPasses = 32;

    /// <summary>
    /// The two sentences a refusing strip throws with. Neither reaches the user —
    /// both land in the catch that falls the fence back to plain code — but they
    /// are what distinguishes a diagram this port declined to render from one
    /// mermaid itself could not parse.
    /// </summary>
    public const string StripDidNotConverge =
        "mermaid shape-data strip did not converge; refusing to render";

    public const string StripPostConditionViolated =
        "mermaid shape-data strip post-condition violated; refusing to render";

    /// <summary>Thrown by <see cref="Preprocess"/> for the two refusals above.</summary>
    internal sealed class RefusedException(string message) : Exception(message);

    // The reference's regexes, verbatim. Directive matches a %%{…}%% configuration
    // directive with or without its closing brace pair; ShapeData an @{…} shape-data
    // block, tolerating the ';' obfuscation a crafted diagram can hide one behind;
    // Space a block diagram's space:N.
    private static readonly Regex Directive = new(
        @"%{2}\{\s*(?:\w+\s*:|\w+)\s*(?:\w+|(?:(?!\}%{2}).|\r?\n)*)?\s*(?:\}%{2})?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShapeData = new(
        @"@;{0,2}\{(?:""[^""]*""|[^""}])*\}?", RegexOptions.Compiled);

    private static readonly Regex ShapeDataOpener = new(@"@;{0,2}\{", RegexOptions.Compiled);

    private static readonly Regex FrontMatter = new(
        @"^-{3}[^\S\n\r]*[\n\r]([\s\S]*?)[\n\r]-{3}[^\S\n\r]*[\n\r]+", RegexOptions.Compiled);

    private static readonly Regex Space = new(
        @"s;{0,2}p;{0,2}a;{0,2}c;{0,2}e;{0,2}:;{0,2}(\d(?:;{0,2}\d)*)", RegexOptions.Compiled);

    private static readonly Regex BlockOpener = new(
        @"^[^\S\n\r]*block", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The source mermaid is actually handed: line endings normalized, the front
    /// matter and the configuration directives lifted out, every <c>@{…}</c>
    /// shape-data block stripped, then the three put back in that order — and a
    /// block diagram's oversized <c>space:</c> clamped.
    /// </summary>
    /// <exception cref="RefusedException">The strip did not settle.</exception>
    public static string Preprocess(string code) => ClampBlockSpace(StripShapeData(code));

    private static string StripShapeData(string code)
    {
        code = code.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        var frontMatter = FrontMatter.Match(code) is { Success: true } m ? m.Value : "";
        var body = code[frontMatter.Length..];

        // A directive is kept — it is the diagram's own configuration — but is
        // taken out of the way first so the shape-data passes cannot chew on it,
        // and is closed if the author left it open.
        var directives = new List<string>();
        body = Directive.Replace(body, match =>
        {
            var text = match.Value;
            directives.Add(text.EndsWith("}%%", StringComparison.Ordinal) ? text : text + "}%%");
            return "";
        });

        var settled = false;
        for (var pass = 0; pass < StripPasses; pass++)
        {
            var before = body;
            body = Directive.Replace(ShapeData.Replace(body, ""), "");
            if (string.Equals(body, before, StringComparison.Ordinal))
            {
                settled = true;
                break;
            }
        }

        if (!settled)
        {
            throw new RefusedException(StripDidNotConverge);
        }

        if (ShapeDataOpener.IsMatch(body))
        {
            throw new RefusedException(StripPostConditionViolated);
        }

        var rebuilt = new StringBuilder(frontMatter);
        foreach (var directive in directives)
        {
            rebuilt.Append(directive).Append('\n');
        }

        return rebuilt.Append(body).ToString();
    }

    /// <summary>
    /// A block diagram's <c>space:N</c> above the ceiling becomes
    /// <c>space:100</c>: mermaid lays every column out, so a four-digit N is a
    /// hang rather than a diagram. Only a source that opens a line with
    /// <c>block</c> is touched, and a match that reads as the tail of an
    /// identifier is left alone — both the reference's own conditions.
    /// </summary>
    private static string ClampBlockSpace(string code)
    {
        if (!BlockOpener.IsMatch(code))
        {
            return code;
        }

        return Space.Replace(code, match =>
        {
            if (match.Index > 0
                && (char.IsAsciiLetter(code[match.Index - 1]) || code[match.Index - 1] == '_'))
            {
                return match.Value;
            }

            var digits = match.Groups[1].Value.Replace(";", "", StringComparison.Ordinal);
            return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && n > MaxBlockSpace
                    ? $"space:{MaxBlockSpace}"
                    : match.Value;
        });
    }

    /// <summary>
    /// <c>themeVariables</c> for the dark palette — the reference's <c>Xl</c>,
    /// hex for hex. The theme itself is mermaid's <c>base</c>, which is the one
    /// that takes these.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> DarkThemeVariables =
    [
        new("primaryTextColor", "#E5E5E5"),
        new("lineColor", "#A1A1A1"),
        new("primaryColor", "transparent"),
        new("primaryBorderColor", "#A1A1A1"),
        new("secondaryColor", "transparent"),
        new("tertiaryColor", "#CC785C"),
        new("actorTextColor", "#E5E5E5"),
        new("actorLineColor", "#A1A1A1"),
        new("signalColor", "#E5E5E5"),
        new("signalTextColor", "#E5E5E5"),
        new("noteBkgColor", "#2D2D2D"),
        new("noteTextColor", "#E5E5E5"),
        new("noteBorderColor", "#A1A1A1"),
    ];

    /// <summary>The light palette — the reference's <c>Jl</c>.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> LightThemeVariables =
    [
        new("primaryTextColor", "#191919"),
        new("lineColor", "#91918D"),
        new("primaryColor", "#F0F0EB"),
        new("primaryBorderColor", "#D9D8D5"),
        new("secondaryColor", "#F5E6D8"),
        new("tertiaryColor", "#CC785C"),
        new("actorTextColor", "#191919"),
        new("actorLineColor", "#91918D"),
        new("signalColor", "#191919"),
        new("signalTextColor", "#191919"),
        new("noteBkgColor", "#F0F0EB"),
        new("noteTextColor", "#191919"),
        new("noteBorderColor", "#D9D8D5"),
    ];

    public static IReadOnlyList<KeyValuePair<string, string>> ThemeVariables(bool dark) =>
        dark ? DarkThemeVariables : LightThemeVariables;

    /// <summary>
    /// The <c>secure</c> allowlist mermaid is initialized with: the keys a
    /// diagram's own <c>%%{init}%%</c> directive may not override. The
    /// reference's <c>$l</c>, in its order.
    /// </summary>
    public static readonly IReadOnlyList<string> SecureKeys =
    [
        "secure", "securityLevel", "startOnLoad", "maxTextSize", "maxEdges",
        "suppressErrorRendering", "htmlLabels", "themeCSS", "fontFamily",
        "altFontFamily", "themeVariables", "theme",
    ];

    /// <summary>
    /// The whole <c>mermaid.initialize</c> argument as JSON, for the render page.
    /// The reference builds the same object: its fixed config, then
    /// <c>securityLevel: "sandbox"</c> and the secure allowlist, and
    /// <c>fontFamily</c> resolved — it passes <c>"inherit"</c> and resolves that
    /// against <c>document.body</c>, which is the font this port hands in.
    /// </summary>
    public static string ConfigJson(bool dark, string fontFamily)
    {
        var variables = string.Join(",", ThemeVariables(dark).Select(
            v => JsonString(v.Key) + ":" + JsonString(v.Value)));
        var secure = string.Join(",", SecureKeys.Select(JsonString));
        return "{"
            + "\"startOnLoad\":false,"
            + "\"htmlLabels\":false,"
            + "\"maxTextSize\":" + MaxTextSize + ","
            + "\"maxEdges\":" + MaxEdges + ","
            + "\"fontFamily\":" + JsonString(fontFamily) + ","
            + "\"theme\":\"base\","
            + "\"themeVariables\":{" + variables + "},"
            + "\"securityLevel\":\"sandbox\","
            + "\"secure\":[" + secure + "]"
            + "}";
    }

    internal static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);
}
