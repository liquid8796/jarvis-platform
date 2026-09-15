using System.Windows;

namespace JarvisCode.App.Controls;

/// <summary>
/// Which of the reference's two markdown renderers a <see cref="MarkdownView"/>
/// reproduces. The installed desktop ships both and uses them on different
/// surfaces: <c>standard-markdown</c> for a chat conversation and
/// <c>epitaxy-markdown</c> for a Claude Code transcript. They disagree on
/// almost every value, so a single renderer cannot be faithful to both.
/// </summary>
public enum MarkdownProfile
{
    /// <summary>The chat conversation renderer (<c>standard-markdown</c>).</summary>
    Chat,

    /// <summary>The Code transcript renderer (<c>epitaxy-markdown</c>).</summary>
    Code,
}

/// <summary>When a fenced block wraps rather than scrolls sideways.</summary>
public enum CodeWrapMode
{
    /// <summary>Never wrap; the block scrolls.</summary>
    Never,

    /// <summary>Wrap only a fence that declared no language (the chat renderer's rule).</summary>
    WhenUntagged,

    /// <summary>Always wrap (the transcript renderer's rule).</summary>
    Always,
}

/// <summary>How a table paints its cells.</summary>
public enum TableStyle
{
    /// <summary>Bottom rules only, no fills — the chat renderer.</summary>
    Rules,

    /// <summary>Rounded filled tiles separated by a gap — the transcript renderer.</summary>
    Tiles,
}

/// <summary>
/// Every geometry and colour constant one reference renderer uses, measured out
/// of the installed Claude Desktop 1.40609.0.0. Chat values come from the
/// element map in <c>shared-12-4ZL7iq1e.js</c> and the utility classes it
/// names; Code values from <c>c122c052f-54CudeeB.css</c> (the whole
/// <c>.epitaxy-markdown</c> stylesheet) and <c>c19e90103-CWkWZpKZ.js</c>.
/// Tailwind spacing is 4px per step and 0.5 border is half a pixel; rem is 16px.
/// </summary>
public sealed record MarkdownMetrics
{
    // ---- prose ----

    /// <summary>Resource key for the body typeface.</summary>
    public required string FontFamilyKey { get; init; }

    public required double BodySize { get; init; }

    public required double BodyLineHeight { get; init; }

    /// <summary>Gap between top-level blocks; the reference lays these out with flex/grid gap.</summary>
    public required double BlockGap { get; init; }

    /// <summary>Font size per heading level, indexed 1..6.</summary>
    public required IReadOnlyList<double> HeadingSizes { get; init; }

    public required FontWeight HeadingWeight { get; init; }

    /// <summary>h6 alone differs from the rest in the chat renderer.</summary>
    public required FontWeight LastHeadingWeight { get; init; }

    /// <summary>Space above each heading level, indexed 1..6.</summary>
    public required IReadOnlyList<double> HeadingSpaceAbove { get; init; }

    /// <summary>
    /// Space below a heading. Both renderers write a negative bottom margin that
    /// eats part of the block gap, so this is that margin, not the visible gap.
    /// </summary>
    public required double HeadingSpaceBelow { get; init; }

    /// <summary>The weight <c>**strong**</c> renders at.</summary>
    public required FontWeight StrongWeight { get; init; }

    // ---- inline code ----

    public required double CodeChipSize { get; init; }

    public required string CodeChipFillKey { get; init; }

    public required string CodeChipForegroundKey { get; init; }

    public required double CodeChipRadius { get; init; }

    public required Thickness CodeChipPadding { get; init; }

    /// <summary>Null where the chip has no border.</summary>
    public string? CodeChipBorderKey { get; init; }

    public double CodeChipBorderThickness { get; init; }

    // ---- fenced code ----

    public required double CodeSize { get; init; }

    public required double CodeLineHeight { get; init; }

    public required Thickness CodePadding { get; init; }

    public required double CodeRadius { get; init; }

    public required string CodeCardFillKey { get; init; }

    public string? CodeCardBorderKey { get; init; }

    public double CodeCardBorderThickness { get; init; }

    /// <summary>The chat renderer prints the fence's language above the code; the transcript never does.</summary>
    public required bool ShowsLanguageLabel { get; init; }

    /// <summary>The chat renderer's action cluster fades in on hover; the transcript's is always up.</summary>
    public required bool HoverRevealsActions { get; init; }

    /// <summary>The transcript's block shrinks to its content; the chat's fills the column.</summary>
    public required bool ShrinksToContent { get; init; }

    /// <summary>
    /// Whether a <c>```mermaid</c> fence becomes a diagram. Only the chat
    /// renderer's CodeBlock carries that branch in the reference; the transcript
    /// renderer has none, and neither does the document-thumbnail renderer,
    /// which passes <c>renderMermaidAsCode</c>.
    /// </summary>
    public bool RendersMermaid { get; init; }

    public required CodeWrapMode Wrap { get; init; }

    // ---- quote ----

    public required double QuoteRuleThickness { get; init; }

    public required string QuoteRuleKey { get; init; }

    /// <summary>Space between the rule and the quoted text.</summary>
    public required double QuoteTextInset { get; init; }

    /// <summary>Space to the left of the rule itself.</summary>
    public required double QuoteOuterInset { get; init; }

    // ---- rule ----

    public required double RuleThickness { get; init; }

    public required string RuleKey { get; init; }

    public required Thickness RuleMargin { get; init; }

    // ---- lists ----

    /// <summary>Left padding a list adds, which is where its markers sit.</summary>
    public required double ListInset { get; init; }

    /// <summary>Vertical gap between sibling items.</summary>
    public required double ListItemGap { get; init; }

    /// <summary>Space below a whole list, on top of the block gap.</summary>
    public required double ListSpaceBelow { get; init; }

    // ---- tables ----

    public required TableStyle TableStyle { get; init; }

    public required double TableCellPadding { get; init; }

    public required double TableSpaceBelow { get; init; }

    /// <summary>
    /// The parser dialect this renderer feeds. Both use remark-breaks; they differ
    /// on single-tilde strikethrough, because the chat renderer passes remark-gfm
    /// {singleTilde:false} and the transcript pushes the plugin with its defaults.
    /// </summary>
    public required Core.Markdown.MarkdownOptions ParseOptions { get; init; }

    /// <summary>Body text size inside a table, which the chat renderer shrinks.</summary>
    public required double TableFontSize { get; init; }

    public required double TableLineHeight { get; init; }

    /// <summary>
    /// Footnote text size. The transcript's --chat-footnote is
    /// max(12px, body - 2px); the reference styles its .footnote-def with it.
    /// </summary>
    public required double FootnoteSize { get; init; }

    /// <summary>Resource key for the kbd chip fill, or null where the renderer has no kbd element.</summary>
    public string? KbdFillKey { get; init; }

    /// <summary>Tiles only: the corner radius and the gap between cells.</summary>
    public double TableTileRadius { get; init; }

    public double TableTileGap { get; init; }

    /// <summary>
    /// The chat conversation renderer. Its prose is Anthropic's serif at 16/24
    /// and its code chip is the one visibly coloured thing in the message.
    /// </summary>
    public static MarkdownMetrics Chat { get; } = new()
    {
        FontFamilyKey = "ChatResponseFontFamily",
        BodySize = 16,
        BodyLineHeight = 24,
        BlockGap = 12,
        HeadingSizes = [22, 18, 16, 16, 14, 14],
        HeadingWeight = FontWeights.Bold,
        LastHeadingWeight = FontWeights.SemiBold,
        HeadingSpaceAbove = [12, 12, 8, 8, 8, 8],
        HeadingSpaceBelow = -4,
        StrongWeight = FontWeights.Bold,

        CodeChipSize = 14.4,
        CodeChipFillKey = "ChatCodeChipFillBrush",
        CodeChipForegroundKey = "Danger000Brush",
        CodeChipRadius = 6.4,
        CodeChipPadding = new Thickness(4, 1, 4, 1),
        CodeChipBorderKey = "Border300Brush",
        CodeChipBorderThickness = 0.5,

        CodeSize = 14,
        CodeLineHeight = 22.75,
        CodePadding = new Thickness(14),
        CodeRadius = 8,
        CodeCardFillKey = "ChatCodeCardBgBrush",
        CodeCardBorderKey = "Border400Brush",
        CodeCardBorderThickness = 0.5,
        ShowsLanguageLabel = true,
        HoverRevealsActions = true,
        ShrinksToContent = false,
        RendersMermaid = true,
        Wrap = CodeWrapMode.WhenUntagged,

        QuoteRuleThickness = 4,
        QuoteRuleKey = "ChatQuoteRuleBrush",
        QuoteTextInset = 16,
        QuoteOuterInset = 8,

        RuleThickness = 0.5,
        RuleKey = "Border200Brush",
        RuleMargin = new Thickness(6, 12, 6, 12),

        ListInset = 32,
        ListItemGap = 4,
        ListSpaceBelow = 12,

        ParseOptions = Core.Markdown.MarkdownOptions.Conversation,
        FootnoteSize = 14,
        KbdFillKey = null,
        TableStyle = TableStyle.Rules,
        TableCellPadding = 8,
        TableSpaceBelow = 24,
        TableFontSize = 14,
        TableLineHeight = 23.8,
    };

    /// <summary>
    /// The Code transcript renderer at the default body size. Everything it
    /// draws is derived from --chat-body and --chat-leading, which is why its
    /// headings are barely larger than its text.
    /// </summary>
    public static MarkdownMetrics Code { get; } = BuildCode(TranscriptTextSizes.Medium);

    /// <summary>
    /// --chat-leading. The stylesheet writes 1.42857, which is a truncation of
    /// 10/7: the ratio is used here so the default size still lands on exactly
    /// 20px rather than 19.99998, as the reference's own 14px body does.
    /// </summary>
    private const double TranscriptLeading = 10d / 7d;

    /// <summary>
    /// The transcript renderer at one of its three text sizes. The reference
    /// derives every value from the body size rather than storing them, so this
    /// does too: a size change moves headings, code, gaps and footnotes together.
    /// </summary>
    public static MarkdownMetrics CodeAt(double bodySize)
    {
        if (Math.Abs(bodySize - TranscriptTextSizes.Medium) < 0.01)
        {
            return Code;
        }

        // One reader, one size: caching the last build keeps streaming from
        // rebuilding the whole record on every frame.
        var cached = _sized;
        if (cached is not null && Math.Abs(cached.BodySize - bodySize) < 0.01)
        {
            return cached;
        }

        var built = BuildCode(bodySize);
        _sized = built;
        return built;
    }

    private static MarkdownMetrics? _sized;

    private static MarkdownMetrics BuildCode(double body) => new()
    {
        FontFamilyKey = "ChatFontFamily",
        BodySize = body,
        BodyLineHeight = body * TranscriptLeading,
        BlockGap = body * TranscriptLeading * 0.5,
        // 1.14286em and 1.07143em, likewise truncations of 8/7 and 15/14;
        // h3 through h6 sit at the body size.
        HeadingSizes = [body * 8 / 7, body * 15 / 14, body, body, body, body],
        HeadingWeight = FontWeights.SemiBold,
        LastHeadingWeight = FontWeights.Medium,
        HeadingSpaceAbove = [12, 10, 7, 7, 7, 7],
        HeadingSpaceBelow = -4,
        StrongWeight = FontWeights.Medium,

        CodeChipSize = body - 1,
        CodeChipFillKey = "AssistantCodeFillBrush",
        CodeChipForegroundKey = "Text100Brush",
        CodeChipRadius = 4,
        CodeChipPadding = new Thickness(2, 1, 2, 1),
        CodeChipBorderKey = null,
        CodeChipBorderThickness = 0,

        // --text-code and --leading-code, both scaled off the body size.
        CodeSize = body * 13 / 14,
        CodeLineHeight = body * 20 / 14,
        CodePadding = new Thickness(10, 8, 10, 8),
        CodeRadius = 8,
        CodeCardFillKey = "Tint1Brush",
        CodeCardBorderKey = null,
        CodeCardBorderThickness = 0,
        ShowsLanguageLabel = false,
        HoverRevealsActions = false,
        ShrinksToContent = true,
        Wrap = CodeWrapMode.Always,

        QuoteRuleThickness = 2,
        QuoteRuleKey = "Tint3Brush",
        QuoteTextInset = 8,
        QuoteOuterInset = 0,

        RuleThickness = 0.5,
        RuleKey = "Tint4Brush",
        RuleMargin = new Thickness(0, body * TranscriptLeading * 0.5, 0, body * TranscriptLeading * 0.5),

        ListInset = 20,
        ListItemGap = 6,
        ListSpaceBelow = 0,

        ParseOptions = Core.Markdown.MarkdownOptions.Conversation with
        {
            SingleTildeStrikethrough = true,
            KbdTag = true,
        },
        // --chat-footnote: max(12px, body - 2px).
        FootnoteSize = Math.Max(12, body - 2),
        KbdFillKey = "AssistantCodeFillBrush",
        TableStyle = TableStyle.Tiles,
        TableCellPadding = 6,
        TableSpaceBelow = 0,
        TableFontSize = body,
        TableLineHeight = body * TranscriptLeading,
        TableTileRadius = 3,
        TableTileGap = 2,
    };

    public static MarkdownMetrics For(MarkdownProfile profile)
        => profile == MarkdownProfile.Chat ? Chat : Code;

    /// <summary>
    /// The renderer for a profile at the reader's transcript text size. The chat
    /// renderer has no such setting — only the transcript carries
    /// data-chat-text-size — so the size is ignored there.
    /// </summary>
    public static MarkdownMetrics For(MarkdownProfile profile, double transcriptBodySize)
        => profile == MarkdownProfile.Chat ? Chat : CodeAt(transcriptBodySize);

    /// <summary>Font size for a heading level, clamped into 1..6.</summary>
    public double HeadingSize(int level) => HeadingSizes[Math.Clamp(level, 1, 6) - 1];

    public double HeadingSpace(int level) => HeadingSpaceAbove[Math.Clamp(level, 1, 6) - 1];

    public FontWeight HeadingWeightFor(int level)
        => Math.Clamp(level, 1, 6) == 6 ? LastHeadingWeight : HeadingWeight;
}

/// <summary>
/// The transcript's three text sizes, which the reference exposes as
/// "Transcript text size" and applies with data-chat-text-size. Every other
/// value in the Code renderer is derived from the one the reader picked.
/// </summary>
public static class TranscriptTextSizes
{
    public const double Small = 13;

    public const double Medium = 14;

    public const double Large = 16;

    /// <summary>Reads a stored setting name, falling back to the default size.</summary>
    public static double FromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "small" => Small,
        "large" => Large,
        _ => Medium,
    };

    public static string NameFor(double size) => size switch
    {
        Small => "small",
        Large => "large",
        _ => "medium",
    };
}
