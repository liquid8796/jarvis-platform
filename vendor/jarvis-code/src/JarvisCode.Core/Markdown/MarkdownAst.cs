namespace JarvisCode.Core.Markdown;

/// <summary>Block-level markdown element produced by <see cref="MarkdownParser"/>.</summary>
public abstract record MarkdownBlock;

public sealed record HeadingBlock(int Level, IReadOnlyList<InlineRun> Inlines) : MarkdownBlock;

public sealed record ParagraphBlock(IReadOnlyList<InlineRun> Inlines) : MarkdownBlock;

public sealed record CodeFenceBlock(string? Language, string Code) : MarkdownBlock;

/// <summary>
/// A block quote. <paramref name="Inlines"/> is the first paragraph, kept so a
/// caller that only renders a line of text still works; <paramref name="Blocks"/>
/// carries the full nested content the reference renderers show.
/// </summary>
public sealed record QuoteBlock(
    IReadOnlyList<InlineRun> Inlines,
    IReadOnlyList<MarkdownBlock>? Blocks = null) : MarkdownBlock;

/// <summary>
/// A GFM footnote definition. The reference prints these below the prose at one
/// pixel under body size in the secondary colour (its <c>.footnote-def</c>).
/// </summary>
public sealed record FootnoteDefinitionBlock(
    string Label, IReadOnlyList<InlineRun> Inlines) : MarkdownBlock;

public sealed record HorizontalRuleBlock : MarkdownBlock;

/// <summary>Display math: a <c>$$…$$</c> or <c>\[…\]</c> block of LaTeX.</summary>
public sealed record MathBlock(string Latex) : MarkdownBlock;

/// <summary>
/// A list. <paramref name="Start"/> is the first item's own number, which an
/// ordered list renders from — <c>3.</c> starts at three, as CommonMark says.
/// </summary>
public sealed record ListBlock(bool Ordered, IReadOnlyList<ListItem> Items, int Start = 1) : MarkdownBlock;

/// <summary>
/// One list item. <paramref name="Inlines"/> is its first paragraph and
/// <paramref name="ChildBlocks"/> everything nested under it — further
/// paragraphs, fenced code, quotes and sub-lists, in source order.
/// <paramref name="IndentLevel"/> is kept for callers that lay items out flat.
/// </summary>
public sealed record ListItem(
    int IndentLevel,
    string Marker,
    IReadOnlyList<InlineRun> Inlines,
    bool IsTask = false,
    bool IsChecked = false,
    IReadOnlyList<MarkdownBlock>? ChildBlocks = null);

/// <summary>Column alignment from a pipe table's delimiter row.</summary>
public enum ColumnAlignment
{
    None,
    Left,
    Center,
    Right,
}

public sealed record TableBlock(
    IReadOnlyList<IReadOnlyList<InlineRun>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> Rows,
    IReadOnlyList<ColumnAlignment>? Alignments = null) : MarkdownBlock;

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strikethrough = 8,
}

/// <summary>
/// A styled run of inline text. <paramref name="LinkUrl"/> is set for links,
/// <paramref name="Math"/> marks <paramref name="Text"/> as inline LaTeX,
/// <paramref name="ImageUrl"/> makes the run an image whose
/// <paramref name="Text"/> is the alt text, and <paramref name="HardBreak"/> is
/// a line break inside the paragraph rather than any text at all.
/// </summary>
public sealed record InlineRun(
    string Text,
    InlineStyle Style = InlineStyle.None,
    string? LinkUrl = null,
    bool Math = false,
    string? ImageUrl = null,
    bool HardBreak = false,
    bool Kbd = false,
    string? FootnoteLabel = null,
    bool Incomplete = false);

/// <summary>
/// The dialect knobs the two reference renderers differ on. <see cref="Default"/>
/// is the document dialect; <see cref="Conversation"/> is what both of the
/// reference's chat renderers use — they add remark-breaks, so a single newline
/// inside a paragraph is a line break rather than a space.
/// </summary>
public sealed record MarkdownOptions
{
    /// <summary>Render a still-open inline construct without its raw opening delimiters. Opt-in display state only.</summary>
    public bool Streaming { get; init; }
    /// <summary>remark-breaks: every newline inside a paragraph is a hard break.</summary>
    public bool SoftBreaks { get; init; }

    /// <summary>Accept <c>~text~</c> as strikethrough, not only <c>~~text~~</c>.</summary>
    public bool SingleTildeStrikethrough { get; init; }

    /// <summary>
    /// remark-gfm: tables, strikethrough, task items, literal autolinks and
    /// footnotes. Both reference renderers drop the plugin while a turn streams
    /// and add it back once the message settles, so a table appears whole rather
    /// than one malformed row at a time.
    /// </summary>
    public bool GithubFlavored { get; init; } = true;

    /// <summary>
    /// The transcript renderer's own inline scanner reads a literal
    /// <c>&lt;kbd&gt;</c> tag, capped at 64 characters of content. The chat
    /// renderer has no such element, so its dialect leaves the tag as text.
    /// </summary>
    public bool KbdTag { get; init; }

    public static MarkdownOptions Default { get; } = new();

    public static MarkdownOptions Conversation { get; } = new() { SoftBreaks = true };

    /// <summary>The content cap the reference puts on a &lt;kbd&gt; tag.</summary>
    public const int MaxKbdLength = 64;
}
