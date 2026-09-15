using System.IO;
using System.Reflection;

namespace JarvisCode.App.Services;

/// <summary>
/// The corpus <c>mcp__visualize__read_me</c> answers from, and the composition
/// rule it answers by.
///
/// The reference keeps every section as a string literal inside its Electron
/// main bundle and joins them in one pure function (desktop 1.40609.1.0,
/// <c>.vite/build/index2.chunk-CBBJKSmo.js</c>, <c>getImagineServerDef</c>).
/// <c>Captures/Mcp/gen-visualize-corpus.js</c> runs that function's own code and
/// writes the sections out under <c>Assets/Visualize/</c>, so what is embedded
/// here is the reference's bytes rather than a paraphrase; the same recording
/// carries a sha256 of all 24 answers the reference can give, which
/// <c>VisualizeCorpusTests</c> reproduces through this class.
///
/// The rule is: <c>base</c>, then the sections of every requested module
/// **deduped in first-seen order**, then <c>footer</c>, joined by a blank line.
/// The platform picks a width first and the width picks three of the sections —
/// a narrow widget is told different things, not the same things with a
/// different number in them — so <c>svg</c>, <c>layout</c> and <c>html</c> are
/// recorded once per width.
/// </summary>
public static class VisualizeCorpus
{
    /// <summary>The reference joins its sections with one blank line.</summary>
    public const string Separator = "\n\n";

    /// <summary>The widget container's width on a mobile client.</summary>
    public const int MobileWidth = 380;

    /// <summary>The width every other client gets — "unknown" means desktop sizing.</summary>
    public const int DesktopWidth = 680;

    /// <summary>The modules read_me offers, in the order its enum declares them.</summary>
    public static readonly IReadOnlyList<string> Modules =
        ["diagram", "mockup", "interactive", "data_viz", "art", "chart", "elicitation"];

    /// <summary>The platforms read_me accepts, in the order its enum declares them.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["mobile", "desktop", "unknown"];

    /// <summary>
    /// The reference's <c>n(platform)</c>: mobile is 380, and everything else —
    /// including a platform it does not recognise — is 680.
    /// </summary>
    public static int WidthFor(string? platform) =>
        string.Equals(platform, "mobile", StringComparison.Ordinal) ? MobileWidth : DesktopWidth;

    /// <summary>
    /// The reference's <c>g(width)</c>: which sections each module contributes.
    /// Section names are this recording's, taken from what each section is
    /// about; the chunk's own are single letters.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string[]>> Composition =
        new Dictionary<int, IReadOnlyDictionary<string, string[]>>
        {
            [MobileWidth] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["diagram"] = ["palette", "w380/svg", "w380/layout"],
                ["mockup"] = ["w380/html", "palette", "cds-tokens"],
                ["interactive"] = ["w380/html", "palette", "cds-tokens"],
                ["data_viz"] = ["w380/html", "palette", "charts", "data-viz"],
                ["art"] = ["w380/svg", "art"],
                ["chart"] = ["w380/html", "palette", "charts", "data-viz"],
                ["elicitation"] = ["elicitation"],
            },
            [DesktopWidth] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["diagram"] = ["palette", "w680/svg", "w680/layout"],
                ["mockup"] = ["w680/html", "palette", "cds-tokens"],
                ["interactive"] = ["w680/html", "palette", "cds-tokens"],
                ["data_viz"] = ["w680/html", "palette", "charts", "data-viz"],
                ["art"] = ["w680/svg", "art"],
                ["chart"] = ["w680/html", "palette", "charts", "data-viz"],
                ["elicitation"] = ["elicitation"],
            },
        };

    /// <summary>The sections one module contributes at one width, or nothing for a name the reference has no module for.</summary>
    public static IReadOnlyList<string> SectionsFor(int width, string module) =>
        Composition.TryGetValue(width, out var byModule) &&
        byModule.TryGetValue(module, out var sections)
            ? sections
            : [];

    /// <summary>
    /// What read_me answers. Duplicate sections are dropped in first-seen order,
    /// so asking for chart and data_viz together does not send the palette twice.
    /// </summary>
    public static string ReadMe(IEnumerable<string>? modules, string? platform)
    {
        var width = WidthFor(platform);
        List<string> parts = ["base"];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var module in modules ?? [])
        {
            foreach (var section in SectionsFor(width, module))
            {
                if (seen.Add(section))
                {
                    parts.Add(section);
                }
            }
        }

        parts.Add("footer");
        return string.Join(Separator, parts.Select(Section));
    }

    /// <summary>One recorded section, by the name the recording filed it under.</summary>
    public static string Section(string name) => Resource($"{name.Replace('/', '.')}.md");

    /// <summary>
    /// The page the <c>ui://imagine/show-widget.html</c> resource serves: the
    /// reference's own widget runtime, byte for byte.
    /// </summary>
    public static string WidgetRuntimeHtml => Resource("widget-runtime.html");

    private const string ResourcePrefix = "JarvisCode.App.Assets.Visualize.";

    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    private static string Resource(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var cached))
            {
                return cached;
            }

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new FileNotFoundException(
                    $"The visualize corpus is missing {file}. Regenerate Assets/Visualize with " +
                    "Captures/Mcp/gen-visualize-corpus.js.", file);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            Cache[file] = text;
            return text;
        }
    }
}
