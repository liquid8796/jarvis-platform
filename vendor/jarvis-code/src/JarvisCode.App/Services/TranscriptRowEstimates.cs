using JarvisCode.App.ViewModels;

namespace JarvisCode.App.Services;

/// <summary>
/// How tall a transcript row is before anybody has looked at it. A virtualized
/// list has to answer that for every row it is not building, because the scrollbar
/// and every offset below the viewport are computed from the answer.
///
/// Ported from the reference desktop's own estimator (1.44121.4.0, ion-dist chunk
/// <c>cd5a31703-Bbz821zb.js</c>: its <c>J1</c> text measure, its <c>X1</c> per-item
/// switch and the <c>i0</c> memo over them), constant for constant. Its kinds are
/// not this port's — a claude.ai channel row has no counterpart here and this port
/// has rows it never had — so a kind maps onto the reference's nearest counterpart
/// and anything text-bearing is measured its way rather than given a constant.
/// </summary>
public static class TranscriptRowEstimates
{
    /// <summary>The reference's <c>p1</c>: one wrapped line of prose.</summary>
    public const double LineHeight = 20;

    /// <summary>
    /// The reference's <c>S1</c>: a blank line, a fence marker, and the gap under a
    /// row that is not the last of its turn.
    /// </summary>
    public const double BlankLine = 10;

    /// <summary>The reference's <c>q1</c>: what a table row costs over a prose line.</summary>
    public const double TableRow = 8;

    /// <summary>The reference's <c>m1</c>: pixels per character at the default text size.</summary>
    public const double PixelsPerChar = 7.5;

    /// <summary>The reference's <c>E1</c>: a user bubble is clamped here however long it is.</summary>
    public const double UserTextClamp = 256;

    /// <summary>The reference's <c>D1</c>: a thinking cell, which is a header until it is opened.</summary>
    public const double ThinkingRow = 24;

    /// <summary>The reference's <c>O1</c>: one row of a tool run.</summary>
    public const double ToolRow = 40;

    /// <summary>The reference's <c>P1</c>: what a row it has no better answer for costs.</summary>
    public const double DefaultRow = 48;

    /// <summary>The reference's <c>k1</c>: an attached image.</summary>
    public const double ImageRow = 330;

    /// <summary>The reference's <c>A1</c>: an image a tool returned.</summary>
    public const double OutputImage = 370;

    /// <summary>The reference's <c>y1</c>: the chrome under an assistant turn's last row.</summary>
    public const double AssistantChrome = 57;

    /// <summary>The reference's <c>x1</c>: a user bubble's own chrome.</summary>
    public const double UserChrome = 49;

    /// <summary>The reference's <c>_1</c>: a user bubble is narrower than the column.</summary>
    public const double UserWidthFactor = 0.85;

    /// <summary>The reference's <c>V1</c>: the action bar under a user bubble.</summary>
    public const double UserActions = 20;

    /// <summary>The reference's clamp on one entry, however much it holds.</summary>
    public const double EntryClamp = 4000;

    /// <summary>The reference's <c>staleCompact</c>.</summary>
    public const double CompactionRow = 40;

    /// <summary>The reference's <c>chapter</c>.</summary>
    public const double ChapterRow = 48;

    /// <summary>The reference's <c>init</c>, which is the nearest thing it has to a card.</summary>
    public const double CardRow = 200;

    /// <summary>
    /// The line break the reference splits on. Its transcript normalizes CRLF away
    /// before an estimate is taken, so a stray carriage return rides its line
    /// rather than opening a second one.
    /// </summary>
    private const char LineBreak = '\n';

    /// <summary>
    /// How many characters fit on a line. The reference divides the transcript
    /// column by <see cref="PixelsPerChar"/> and again by the text-size scale, so a
    /// reader at Large wraps sooner and every estimate follows.
    /// </summary>
    public static double CharsPerLine(double columnWidth, double textScale)
    {
        double scale = textScale <= 0 ? 1 : textScale;
        return Math.Max(1, columnWidth / PixelsPerChar / scale);
    }

    /// <summary>
    /// The reference's <c>J1</c>: wrap the text by character count rather than
    /// laying it out. A body past two hundred lines' worth of characters is not
    /// measured at all — it answers the clamp, because the difference between
    /// "very tall" and "very tall" does not move a scrollbar anybody is reading.
    /// </summary>
    public static double TextHeight(string? text, double charsPerLine, bool fenceAware)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LineHeight;
        }

        if (text.Length >= charsPerLine * 200)
        {
            return EntryClamp;
        }

        double total = 0;
        bool inFence = false;
        foreach (var line in text.Split(LineBreak))
        {
            if (fenceAware && line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                total += BlankLine;
                continue;
            }

            if (fenceAware && !inFence && line.Length == 0)
            {
                total += BlankLine;
                continue;
            }

            total += Math.Max(1, Math.Ceiling(line.Length / charsPerLine)) * LineHeight;
            if (line.Length > 0 && line[0] == '|')
            {
                total += TableRow;
            }
        }

        return Math.Max(LineHeight, total);
    }

    /// <summary>
    /// What a row is worth. <paramref name="columnWidth"/> is the transcript
    /// column and <paramref name="textScale"/> the reader's transcript text size
    /// over the 14px default, which is the pair the reference memoizes on.
    /// </summary>
    public static double For(TranscriptItem item, double columnWidth, double textScale)
    {
        double chars = CharsPerLine(columnWidth, textScale);
        return item switch
        {
            UserMessageItem user =>
                UserChrome
                + Math.Min(TextHeight(user.Text, chars * UserWidthFactor, false), UserTextClamp)
                + UserActions
                + (user.HasAttachments ? ImageRow : 0),

            AssistantTextItem assistant =>
                Math.Min(TextHeight(assistant.Markdown, chars, true) + BlankLine, EntryClamp),

            SubagentTextItem subagent =>
                Math.Min(TextHeight(subagent.Text, chars, false) + BlankLine, EntryClamp),

            // A thinking cell is its header until the reader opens it, on both
            // surfaces: the reference gives the kind one constant and lets the
            // measurement correct an opened one.
            ThinkingItem => ThinkingRow,

            ToolGroupItem group => ToolRunHeight(group),

            ToolCallItem call => ToolRow + call.Images.Count * OutputImage,

            AssistantFooterItem => AssistantChrome,

            ChapterItem => ChapterRow,

            CompactionItem => CompactionRow,

            NoticeItem notice => TextHeight(notice.Text, chars, false) + BlankLine,

            // The cards. The reference has no counterpart for any of them, so they
            // take the constant it gives the tallest row it does have.
            ErrorCardItem or SessionNotFoundItem or WidgetItem or ChatErrorItem => CardRow,

            _ => DefaultRow,
        };
    }

    /// <summary>
    /// The reference's <c>tools</c> arm: a run is worth its visible rows, never
    /// fewer than one, plus what each image its calls returned will take.
    /// </summary>
    private static double ToolRunHeight(ToolGroupItem group)
    {
        int rows = group.RendersBareRow ? 1 : Math.Max(1, group.Calls.Count) + 1;
        double height = rows * ToolRow;
        foreach (var call in group.AllCalls)
        {
            height += call.Images.Count * OutputImage;
        }

        return height;
    }
}
