using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.App.Infrastructure;
using JarvisCode.App.Services;
using JarvisCode.Core.Utilities;

namespace JarvisCode.App.ViewModels;

public abstract class TranscriptItem : ObservableObject
{
    private static long _issued;

    /// <summary>
    /// What the virtualizer files this row's measured height under, and what a
    /// stored viewport snapshot names its anchor by. The reference keys a row by
    /// the entry uuid its server assigned; nothing here carries one, so a row
    /// rebuilt from the session file takes a key derived from its position in that
    /// file (<see cref="Services.TranscriptKeys"/>) and stays the same row across a
    /// reopen, while a row a live turn produced takes a fresh one — it has no
    /// stored height to find anyway.
    /// </summary>
    public string RowKey { get; internal set; } =
        "l" + System.Threading.Interlocked.Increment(ref _issued)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class UserMessageItem : TranscriptItem
{
    private bool _isCopied;

    public required string Text { get; init; }
    public IReadOnlyList<string> AttachedPaths { get; init; } = [];
    public bool HasAttachments => AttachedPaths.Count > 0;
    public string AttachmentsSummary => string.Join(", ", AttachedPaths.Select(System.IO.Path.GetFileName));

    /// <summary>When the prompt was sent; drives the relative time in the hover action bar.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Where this prompt sits in Session.Messages — the cut point for rewind and branch.</summary>
    public int MessageIndex { get; init; }

    /// <summary>The checkpoint turn this prompt started (code surface); 0 when there is none.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Set briefly after Copy so the button can show a tick instead.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set => SetProperty(ref _isCopied, value);
    }

    public string RelativeTime => TranscriptTime.Stamp(CreatedAt);

    /// <summary>Re-reads the clock; the surface ticks this while the session is open.</summary>
    public void RefreshRelativeTime() => OnPropertyChanged(nameof(RelativeTime));
}

/// <summary>The transcript's one wording for "how long ago" — bubbles and footers share it.</summary>
public static class TranscriptTime
{
    /// <summary>
    /// The reference's <c>timeFormat</c> / <c>timeZone</c> settings, read live:
    /// "auto" (the default) keeps the relative clock; "12-hour", "24-hour",
    /// "24-hour-utc" or a strftime pattern (containing %) show a wall clock in
    /// the configured zone instead.
    /// </summary>
    public static Func<(string? Format, string? Zone)>? Settings { get; set; }

    /// <summary>A transcript timestamp for <paramref name="at"/>, per the settings.</summary>
    public static string Stamp(DateTimeOffset at)
    {
        var (format, zone) = Settings?.Invoke() ?? (null, null);
        return Clock(at, format, zone) ?? Relative(DateTimeOffset.Now - at);
    }

    /// <summary>The wall clock for a non-auto format, or null when the format is auto.</summary>
    public static string? Clock(DateTimeOffset at, string? format, string? zone)
    {
        var trimmed = format?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var local = trimmed.Equals("24-hour-utc", StringComparison.OrdinalIgnoreCase)
            ? at.ToUniversalTime()
            : InZone(at, zone);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return trimmed.ToLowerInvariant() switch
        {
            "12-hour" => local.ToString("h:mm tt", culture),
            "24-hour" => local.ToString("HH:mm", culture),
            "24-hour-utc" => local.ToString("HH:mm", culture) + " UTC",
            _ when trimmed.Contains('%') => Strftime(local, trimmed, culture),
            _ => null,
        };
    }

    private static DateTimeOffset InZone(DateTimeOffset at, string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
        {
            return at.ToLocalTime();
        }

        try
        {
            return TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(zone.Trim()));
        }
        catch (TimeZoneNotFoundException)
        {
            return at.ToLocalTime();
        }
        catch (InvalidTimeZoneException)
        {
            return at.ToLocalTime();
        }
    }

    /// <summary>The strftime directives a clock pattern is likely to use.</summary>
    internal static string Strftime(DateTimeOffset t, string pattern, IFormatProvider culture)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '%' || i + 1 >= pattern.Length)
            {
                builder.Append(pattern[i]);
                continue;
            }

            i++;
            builder.Append(pattern[i] switch
            {
                'H' => t.ToString("HH", culture),
                'I' => t.ToString("hh", culture),
                'M' => t.ToString("mm", culture),
                'S' => t.ToString("ss", culture),
                'p' => t.ToString("tt", culture),
                'P' => t.ToString("tt", culture).ToLowerInvariant(),
                'Y' => t.ToString("yyyy", culture),
                'y' => t.ToString("yy", culture),
                'm' => t.ToString("MM", culture),
                'd' => t.ToString("dd", culture),
                'e' => t.Day.ToString(culture).PadLeft(2),
                'b' or 'h' => t.ToString("MMM", culture),
                'B' => t.ToString("MMMM", culture),
                'a' => t.ToString("ddd", culture),
                'A' => t.ToString("dddd", culture),
                'j' => t.DayOfYear.ToString("000", culture),
                'Z' => t.Offset == TimeSpan.Zero ? "UTC" : t.ToString("zzz", culture),
                'z' => t.ToString("zzz", culture).Replace(":", ""),
                'F' => t.ToString("yyyy-MM-dd", culture),
                'T' => t.ToString("HH:mm:ss", culture),
                'R' => t.ToString("HH:mm", culture),
                'D' => t.ToString("MM/dd/yy", culture),
                'n' => "\n",
                't' => "\t",
                '%' => "%",
                var other => "%" + other,
            });
        }

        return builder.ToString();
    }

    public static string Relative(TimeSpan age) => age switch
    {
        { TotalSeconds: < 45 } => "just now",
        { TotalMinutes: < 2 } => "1 minute ago",
        { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} minutes ago",
        { TotalHours: < 2 } => "1 hour ago",
        { TotalHours: < 24 } => $"{(int)age.TotalHours} hours ago",
        { TotalDays: < 2 } => "yesterday",
        { TotalDays: < 7 } => $"{(int)age.TotalDays} days ago",
        _ => (DateTimeOffset.Now - age).ToString("MMM d"),
    };
}

public sealed class AssistantTextItem : TranscriptItem
{
    private string _markdown = "";
    private bool _isStreaming = true;
    private bool _isCopied;
    private int _feedback;

    public string Markdown
    {
        get => _markdown;
        set => SetProperty(ref _markdown, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>Index of the assistant message in Session.Messages — the retry cut point.</summary>
    public int MessageIndex { get; set; } = -1;

    private bool _isReading;

    /// <summary>Whether this answer is the one being read aloud right now.</summary>
    public bool IsReading
    {
        get => _isReading;
        set
        {
            if (SetProperty(ref _isReading, value))
            {
                OnPropertyChanged(nameof(ReadAloudLabel));
            }
        }
    }

    /// <summary>The read-aloud button's label, which the reference swaps while it speaks.</summary>
    public string ReadAloudLabel =>
        _isReading ? Services.ReadAloud.Pause : Services.ReadAloud.Read;

    /// <summary>Set briefly after Copy so the button can show a tick instead.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set => SetProperty(ref _isCopied, value);
    }

    /// <summary>1 = good response, -1 = bad response, 0 = none. Local acknowledgement only.</summary>
    public int Feedback
    {
        get => _feedback;
        set
        {
            if (SetProperty(ref _feedback, value))
            {
                OnPropertyChanged(nameof(IsGood));
                OnPropertyChanged(nameof(IsBad));
            }
        }
    }

    public bool IsGood => _feedback > 0;

    public bool IsBad => _feedback < 0;

    public void Append(string delta) => Markdown += delta;
}

public sealed class ThinkingItem : TranscriptItem
{
    private readonly List<TimeSpan> _spans = [];
    private string _text = "";
    private bool _isStreaming = true;
    private bool _isCopied;
    private bool _isExpanded;
    private bool _showsFullText;
    private TimeSpan? _thought;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(HasText));
            }
        }
    }

    /// <summary>
    /// True on the Chat surface, where the reference renders thinking as a
    /// conversation cell — timeline row, markdown body, a "Thought for …" header
    /// and a 200px clamp. The Code surface keeps the reference's own plain italic
    /// block with a copy button, which is a different component there.
    /// </summary>
    public bool IsChatCell { get; init; }

    /// <summary>The reference renders no cell at all for an empty thinking block.</summary>
    public bool HasText => _text.Length > 0;

    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>Set briefly after the hover copy so the button shows a tick.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set
        {
            if (SetProperty(ref _isCopied, value))
            {
                OnPropertyChanged(nameof(CopyActionName));
            }
        }
    }

    /// <summary>
    /// The copy button's accessible name, which the reference swaps for the
    /// confirmation the moment the text is on the clipboard.
    /// </summary>
    public string CopyActionName => _isCopied ? "Copied" : "Copy as quote";   // p556q3uvbn / GWEGVdQi8W

    /// <summary>
    /// The chat header's disclosure state. The reference collapses a message's
    /// thinking as soon as the turn is over, and starts every message collapsed.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// "Show more" state inside the cell — the reference's clamp, which is a
    /// second, independent disclosure below <see cref="IsExpanded"/>.
    /// </summary>
    public bool ShowsFullText
    {
        get => _showsFullText;
        set => SetProperty(ref _showsFullText, value);
    }

    /// <summary>
    /// Summed duration of this message's thinking blocks, or null when none was
    /// timed. Individual block durations live in <see cref="Spans"/>.
    /// </summary>
    public TimeSpan? Thought => _thought;

    /// <summary>Per-block durations, in the order the blocks streamed.</summary>
    public IReadOnlyList<TimeSpan> Spans => _spans;

    /// <summary>The reference's header for this message's thinking.</summary>
    public string Label => ThinkingLabels.ForTotal(_thought);

    /// <summary>
    /// The reference's copy format: a markdown blockquote headed by a rough
    /// token count, so pasted thinking is visibly quoted.
    /// </summary>
    public string CopyText =>
        $"> **Thinking** (~{ThinkingLabels.RoundHalfUp(Text.Length / 4.0)} tok)\n>\n" +
        string.Join("\n", Text.Split('\n').Select(l => l.Length > 0 ? $"> {l}" : ">"));

    public void Append(string delta) => Text += delta;

    /// <summary>
    /// Records one thinking block's measured duration. Like the reference, a span
    /// that did not advance is not a measurement and leaves the label unmeasured.
    /// </summary>
    public void AddThinkingSpan(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return;
        }

        _spans.Add(span);
        _thought = (_thought ?? TimeSpan.Zero) + span;
        OnPropertyChanged(nameof(Thought));
        OnPropertyChanged(nameof(Label));
    }

    /// <summary>Restores durations stored with a session's thinking blocks.</summary>
    public void RestoreThinkingSpans(IEnumerable<TimeSpan> spans)
    {
        _spans.Clear();
        _thought = null;
        foreach (var span in spans)
        {
            if (span > TimeSpan.Zero)
            {
                _spans.Add(span);
                _thought = (_thought ?? TimeSpan.Zero) + span;
            }
        }

        OnPropertyChanged(nameof(Thought));
        OnPropertyChanged(nameof(Label));
    }
}

/// <summary>One line of a todo_write call's checklist rendering.</summary>
public sealed record TodoRowItem(string Text, string Status)
{
    public string Glyph => Status switch
    {
        "completed" => "☑",
        "in_progress" => "◐",
        _ => "☐",
    };

    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
}

public sealed class ToolCallItem : TranscriptItem, Services.IToolSummaryCall
{
    private string _result = "";
    private bool _isRunning = true;
    private bool _isError;
    private bool _isDenied;
    private bool _isInterrupted;
    private bool _isAwaitingApproval;
    private bool _isExpanded;
    private string _progress = "";
    private string? _inputText;
    private IReadOnlyList<Services.ToolInputRow>? _inputRows;
    private bool _isBodyCopied;
    private bool _inCard = true;
    private bool _inGroup = true;
    private string? _verbOverride;
    private ToolRowInfo? _info;
    private JsonObject? _args;
    private bool _argsParsed;
    private IReadOnlyList<DiffLine>? _diffLines;
    private bool _diffComputed;
    private IReadOnlyList<TodoRowItem>? _todoRows;
    private IReadOnlyList<JarvisCode.Core.Models.ImageBlock> _images = [];
    private string? _subagentReport;

    public required string CallId { get; init; }
    public required string ToolName { get; init; }
    public required string Description { get; init; }

    /// <summary>The call's raw arguments, as the orchestrator sent them.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>
    /// Whether the chip a task id names started a session, carried so an agent's
    /// own nested runs read the same as the ones around them.
    /// </summary>
    public Func<string, bool>? TaskStarted { get; init; }

    /// <summary>When the call started; agents show "took {elapsed}" from it.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Follow-on calls merged into this row (consecutive reads/edits of one file).</summary>
    public ObservableCollection<ToolCallItem> Continuations { get; } = [];

    /// <summary>
    /// Whether this row sits inside the group's outline card. The reference
    /// passes <c>inCard</c> only from the card's own child list; a run that
    /// renders as one bare row passes neither it nor <c>inGroup</c>, and its
    /// bodies take their standalone shapes — the shell card grows a header bar
    /// naming the shell, and a diff or a file is wrapped in an outlined card of
    /// its own rather than sitting under a plain path line.
    /// </summary>
    public bool InCard
    {
        get => _inCard;
        set
        {
            if (SetProperty(ref _inCard, value))
            {
                OnPropertyChanged(nameof(IsBare));
            }
        }
    }

    /// <summary>
    /// The reference's <c>inGroup</c>: false only for a run of exactly one call.
    /// A coalesced run is still a group of calls even when it draws as one row,
    /// which is why its images stay inside the disclosure while a lone call's
    /// ride outside it.
    /// </summary>
    public bool InGroup
    {
        get => _inGroup;
        set
        {
            if (SetProperty(ref _inGroup, value))
            {
                OnPropertyChanged(nameof(ImagesRideTheRow));
                OnPropertyChanged(nameof(ImagesRideTheBody));
            }
        }
    }

    public bool IsBare => !InCard;

    /// <summary>A lone call's images sit outside the disclosure, always visible.</summary>
    public bool ImagesRideTheRow => !InGroup && HasImages;

    /// <summary>Inside a group they sit in the body, with the rest of it.</summary>
    public bool ImagesRideTheBody => InGroup && HasImages;

    /// <summary>This call plus everything coalesced into it, in order.</summary>
    public IEnumerable<ToolCallItem> AllCalls => Continuations.Count == 0
        ? [this]
        : new[] { this }.Concat(Continuations);

    /// <summary>Shell input reads as a command line; everything else as its arguments.</summary>
    public bool IsCommand => ToolName is "PowerShell" or "Bash";

    /// <summary>What the input box is written in, for the highlighter.</summary>
    public string SyntaxLanguage => ToolName == "PowerShell" ? "powershell" : IsCommand ? "bash" : "json";

    /// <summary>
    /// The reference's command gutter: its shell block prints a prompt glyph
    /// ahead of the command, and picks it from the shell rather than always
    /// writing a bash prompt in front of a PowerShell line.
    /// </summary>
    public string CommandPrompt => ToolName == "PowerShell" ? ">" : "$";

    /// <summary>
    /// The shell the reference's standalone card is titled with — the only place
    /// it names one, since inside a group the row above already says what ran.
    /// </summary>
    public string ShellName => ToolName == "PowerShell" ? "PowerShell" : "Bash";

    /// <summary>Parsed arguments (null when absent or malformed).</summary>
    public JsonObject? Args
    {
        get
        {
            if (!_argsParsed)
            {
                _argsParsed = true;
                try
                {
                    _args = string.IsNullOrWhiteSpace(ArgumentsJson) ? null : JsonNode.Parse(ArgumentsJson) as JsonObject;
                }
                catch (JsonException)
                {
                    _args = null;
                }
            }

            return _args;
        }
    }

    /// <summary>Reference-style row wording, refreshed once the result is known.</summary>
    public ToolRowInfo Info => _info ??= ToolRowPresentation.Describe(ToolName, Args, null, Description);

    /// <summary>How the expanded body renders (diff, checklist, terminal, file, JSON).</summary>
    public ToolBodyKind BodyKind => Info.Kind;

    /// <summary>
    /// Which body the row opens on, in the reference's own order: the checklist,
    /// the shell card, the diff, the file, the error report, and finally the
    /// argument list. Each is exclusive of the ones above it, which is why a
    /// failed edit shows its error rather than a diff of a change that never
    /// landed, and a Read that answered with an image falls through to the
    /// argument list — the branch that prints "file_path:".
    /// </summary>
    public bool BodyIsTodos => BodyKind == ToolBodyKind.Todos && TodoRows.Count > 0;

    public bool BodyIsCommand => !BodyIsTodos && BodyKind == ToolBodyKind.Command;

    public bool BodyIsDiff =>
        !BodyIsTodos && !BodyIsCommand && BodyKind == ToolBodyKind.Diff && !IsError && DiffLines.Count > 0;

    public bool BodyIsFile =>
        !BodyIsTodos && !BodyIsCommand && !BodyIsDiff &&
        BodyKind == ToolBodyKind.FileView && HasResult && !IsError;

    private bool BodyIsGeneric => !BodyIsTodos && !BodyIsCommand && !BodyIsDiff && !BodyIsFile;

    /// <summary>An errored call reports the error first and its arguments under it.</summary>
    public bool BodyIsErrorDetails => BodyIsGeneric && IsError && HasResult;

    /// <summary>Anything else with something to say: the arguments, then the output.</summary>
    public bool BodyIsDetails => BodyIsGeneric && !BodyIsErrorDetails && (HasInputRows || HasResult);

    /// <summary>The argument list rides both generic bodies, and only those.</summary>
    public bool ShowParams => (BodyIsErrorDetails || BodyIsDetails) && HasInputRows;

    /// <summary>An ordinary call lists its arguments above its output…</summary>
    public bool ShowParamsFirst => BodyIsDetails && HasInputRows;

    /// <summary>…and an errored one reports the error first and its arguments under it.</summary>
    public bool ShowParamsLast => BodyIsErrorDetails && HasInputRows;

    /// <summary>The scrolling region the reference caps at 400px, holding both of those.</summary>
    public bool HasOutputRegion => ShowParams || ShowTextResult;

    /// <summary>
    /// The shell's output belongs to the shell card, which is what puts it inside
    /// the outlined card a standalone row draws; every other body reaches its
    /// output through the generic region.
    /// </summary>
    public bool ShowCommandOutput => BodyIsCommand && ShowTextResult;

    public bool ShowGenericRegion => HasOutputRegion && !BodyIsCommand;

    /// <summary>
    /// The shell card's header-bar copy, which the reference offers exactly when
    /// there is no command line for the copy to hang off.
    /// </summary>
    public bool ShowShellHeaderCopy => BodyIsCommand && !ShowInput && ShowTextResult;

    /// <summary>The command line of a shell call.</summary>
    public bool ShowInput => BodyIsCommand && HasInput;

    /// <summary>File content renders through the syntax highlighter (Read).</summary>
    public bool ShowFileResult => BodyIsFile;

    /// <summary>The raw-result box: the shell's output, and either generic body's.</summary>
    public bool ShowTextResult => HasResult && (BodyIsCommand || BodyIsErrorDetails || BodyIsDetails);

    /// <summary>A failed call's output is the reference's danger-coloured report.</summary>
    public bool ResultIsError => IsError;

    /// <summary>Whether the expanded row has anything at all to draw.</summary>
    public bool HasBody =>
        BodyIsTodos || BodyIsCommand || BodyIsDiff || BodyIsFile || BodyIsErrorDetails || BodyIsDetails ||
        HasImages || IsPreviewCard;

    /// <summary>Highlight language for a read file, from its extension.</summary>
    public string? ResultLanguage
    {
        get
        {
            if (ToolName != "Read")
            {
                return null;
            }

            var path = GetString("file_path");
            var dot = path?.LastIndexOf('.') ?? -1;
            return dot >= 0 && dot < path!.Length - 1 ? path[(dot + 1)..].ToLowerInvariant() : null;
        }
    }

    /// <summary>
    /// The reference's awaiting_approval row state: the call exists (the permission
    /// card is up) but nothing runs yet — running wording, no spinner, dimmed.
    /// </summary>
    public bool IsAwaitingApproval
    {
        get => _isAwaitingApproval;
        set
        {
            if (SetProperty(ref _isAwaitingApproval, value))
            {
                RefreshDisplay();
            }
        }
    }

    /// <summary>The verb-or-label half of the row ("Read", "Committed", "Installed deps").</summary>
    public string PrimaryText
    {
        get
        {
            var info = Info;
            if (IsRunning || IsAwaitingApproval)
            {
                return info.RunningLabel ?? info.RunningVerb;
            }

            if (IsDenied || (IsError && !IsInterrupted))
            {
                return info.FailedLabel ?? info.FailedVerb ?? info.Verb;
            }

            return info.DoneLabel ?? VerbOverride ?? info.Verb;
        }
    }

    /// <summary>
    /// The verb the run puts on this row in place of the tool's own — the
    /// reference's <c>verbOverride</c>, which a spawn_task row wears once the
    /// chip it queued has started a session. It stands behind a done label and
    /// in front of the tool's plain verb, exactly where the reference reads it.
    /// </summary>
    public string? VerbOverride
    {
        get => _verbOverride;
        set
        {
            if (SetProperty(ref _verbOverride, value))
            {
                OnPropertyChanged(nameof(PrimaryText));
            }
        }
    }

    /// <summary>The meta half ("Base.xaml", the pattern, "#12"); hidden when the label already carries it.</summary>
    public string? MetaText
    {
        get
        {
            var info = Info;
            var usingLabel = IsRunning || IsAwaitingApproval ? info.RunningLabel is not null
                : IsFailed && !IsInterrupted ? info.FailedLabel is not null
                : info.DoneLabel is not null;
            return usingLabel ? null : info.Meta;
        }
    }

    /// <summary>The meta stands down while the permission card is up, which is where its own words go.</summary>
    public bool HasMeta => !IsAwaitingApproval && !string.IsNullOrEmpty(MetaText);

    public bool MetaIsCode => Info.MetaIsCode;
    public string? MetaHref => Info.MetaHref;
    public bool MetaIsLink => HasMeta && !string.IsNullOrEmpty(Info.MetaHref);

    /// <summary>
    /// The reference's <c>k</c>: a meta that names a file or is written rather
    /// than said is drawn in the primary colour instead of the secondary one.
    /// It is not drawn in the code face — the reference sets no mono font there.
    /// </summary>
    public bool MetaIsProminent =>
        BodyKind is ToolBodyKind.Diff or ToolBodyKind.FileView || MetaIsCode;

    /// <summary>"(919–1058, 1058–1177)" over this row's coalesced reads; null unless every call has one.</summary>
    public string? RangeText
    {
        get
        {
            if (ToolName != "Read")
            {
                return null;
            }

            var ranges = AllCalls.Select(c => ToolRowPresentation.ReadRange(c.Args)).ToList();
            return ranges.All(r => r is not null) && ranges.Count > 0
                ? $"({string.Join(", ", ranges)})"
                : null;
        }
    }

    public bool HasRange => RangeText is not null;

    /// <summary>Lines this row's calls added / removed (edits and writes).</summary>
    public int Added { get; private set; }
    public int Removed { get; private set; }

    /// <summary>
    /// Whether the ± badge arrived while this session was watching. The reference
    /// captures that once, at mount, so a reopened transcript blinks nothing.
    /// </summary>
    public bool FlashDiff { get; private set; }

    public string? AddedText
    {
        get
        {
            var total = AllCalls.Sum(c => c.Added);
            return total > 0 ? $"+{total}" : null;
        }
    }

    public string? RemovedText
    {
        get
        {
            var total = AllCalls.Sum(c => c.Removed);
            return total > 0 ? $"-{total}" : null;
        }
    }

    /// <summary>
    /// The model an agent row ran on, drawn plain after the label — the
    /// reference's own <c>se</c>. Its "took {elapsed}" belongs to the background
    /// task chip rather than to this row, so no elapsed time rides here.
    /// </summary>
    public string? ModelText
    {
        get
        {
            if (ToolName != "Agent")
            {
                return null;
            }

            var model = GetString("model");
            return string.IsNullOrWhiteSpace(model) ? null : model;
        }
    }

    /// <summary>
    /// The prompt a Agent call was given, which the subagent view shows above
    /// the agent's own steps the way the reference shows it as the first entry.
    /// </summary>
    public string? SubagentPrompt => ToolName == "Agent" ? GetString("prompt") : null;

    /// <summary>
    /// A subagent's row. Clicking it opens that agent's transcript in the
    /// Background tasks pane, which is why it never expands in place.
    /// </summary>
    public bool IsSubagentRow => ToolName == "Agent";

    /// <summary>
    /// An agent row's trailing caret. The reference gives those rows a caret
    /// that always points right — they navigate rather than expand — and shows
    /// it only once the agent has settled.
    /// </summary>
    public bool ShowsOpenCaret => IsSubagentRow && !IsRunning;

    /// <summary>
    /// What the subagent view is titled when opened from this row: the call's
    /// own `description`, or the tool's name when it carried none — the
    /// reference's fallback.
    /// </summary>
    public string SubagentTitle =>
        GetString("description") is { Length: > 0 } described ? described : ToolName;

    /// <summary>
    /// The agent's own report, once it is known. A background call is answered
    /// with a launch acknowledgement rather than a report, so the worker hands
    /// its report over when it finishes; null means not known yet, and an empty
    /// string means known to be nothing.
    /// </summary>
    public string? SubagentReport
    {
        get => _subagentReport;
        set => SetProperty(ref _subagentReport, value);
    }

    /// <summary>
    /// What the subagent view shows as the agent's report. A foreground call's
    /// tool result is the report; a background call's is not, so it shows only
    /// what the worker handed back.
    /// </summary>
    public string SubagentReportText => SubagentReport ?? (OpensRunsPanel ? "" : Result);

    /// <summary>
    /// Whether the agent is still working. A background call's row settles the
    /// moment the agent is launched, so it counts as working until its report
    /// lands or the session is reopened without one.
    /// </summary>
    public bool SubagentIsWorking => IsRunning || (OpensRunsPanel && SubagentReport is null);

    /// <summary>A background agent lives in the runs panel; clicking its row opens it there.</summary>
    public bool OpensRunsPanel =>
        ToolName == "Agent" &&
        Args?["run_in_background"] is JsonValue bg && bg.TryGetValue<bool>(out var flag) && flag;

    /// <summary>
    /// The worker this row started, read back from its result — the row the
    /// Background tasks panel opens at when this one is clicked.
    /// </summary>
    public string? BackgroundAgentId
    {
        get
        {
            if (!OpensRunsPanel)
            {
                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(Result, @"background agent (agent-\d+)");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    // ---- preview card (preview_start), the reference's inline dev-server card ----

    private string? _previewThumbnailPath;

    /// <summary>preview_start rows render the reference's inline card: name · URL · logs · stop.</summary>
    public bool IsPreviewCard => ToolName == "preview_start" && !IsRunning && !IsFailed && PreviewServerId is not null;

    public string? PreviewServerId
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(Result, @"serverId ([\w-]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    public string? PreviewUrl
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(Result, @"open at (\S+)");
            return match.Success ? match.Groups[1].Value.TrimEnd('.', ',', ')', ';') : null;
        }
    }

    /// <summary>A captured screenshot of the previewed page, when the Browser panel could take one.</summary>
    public string? PreviewThumbnailPath
    {
        get => _previewThumbnailPath;
        set
        {
            if (SetProperty(ref _previewThumbnailPath, value))
            {
                OnPropertyChanged(nameof(HasPreviewThumbnail));
            }
        }
    }

    public bool HasPreviewThumbnail => _previewThumbnailPath is not null;

    // ---- background shell task (run_in_background), the reference's live row ----

    /// <summary>
    /// The task id a backgrounded shell call started ("Started background task
    /// task-3…"). The row stays running until the task itself exits.
    /// </summary>
    public string? BackgroundTaskId { get; private set; }

    /// <summary>The background task exited; settle the row from its real outcome.</summary>
    public void FinishBackground(bool killed, int? exitCode)
    {
        BackgroundTaskId = null;
        if (killed)
        {
            IsInterrupted = true;
        }
        else if (exitCode is not (null or 0))
        {
            Result = $"{Result}\n[exited with code {exitCode}]".TrimStart('\n');
            IsError = true;
        }

        IsRunning = false;
    }

    // ---- nested subagent transcript (Agent), the reference's inner blocks ----

    private ToolGroupItem? _subGroup;
    private Dictionary<string, ToolCallItem>? _subCalls;

    /// <summary>The subagent's own transcript — its text and tool groups, nested under this row.</summary>
    public ObservableCollection<TranscriptItem> SubItems { get; } = [];

    public bool HasSubItems => SubItems.Count > 0;

    /// <summary>
    /// Folds one inner event of this row's subagent into the nested transcript.
    /// Must be called on the UI thread.
    /// </summary>
    public void AppendSubagentEvent(JarvisCode.Core.Agent.AgentEvent agentEvent)
    {
        var hadItems = SubItems.Count > 0;
        switch (agentEvent)
        {
            case JarvisCode.Core.Agent.ToolExecutionStarted started:
            {
                // The nested transcript runs the same machinery, so its runs split
                // on the same bucket: a subagent's spawn_task is counted by what
                // became of its chips, not alongside the calls around it.
                if (_subGroup is { Calls.Count: > 0 } open &&
                    open.IsSpawnTaskRun != Services.ToolGroupSummary.IsSpawnTask(started.ToolName))
                {
                    open.RefreshTitle();
                    _subGroup = null;
                }

                if (_subGroup is null)
                {
                    _subGroup = new ToolGroupItem { TaskStarted = TaskStarted };
                    SubItems.Add(_subGroup);
                }

                var item = new ToolCallItem
                {
                    CallId = started.CallId,
                    ToolName = started.ToolName,
                    Description = started.CallDescription,
                    ArgumentsJson = started.ArgumentsJson,
                };
                _subGroup.AddCall(item);
                (_subCalls ??= new(StringComparer.Ordinal))[started.CallId] = item;
                _subGroup.RefreshTitle();
                break;
            }

            case JarvisCode.Core.Agent.ToolExecutionCompleted completed:
                if (_subCalls?.TryGetValue(completed.CallId, out var done) == true)
                {
                    var text = completed.Result.Length <= 20_000
                        ? completed.Result
                        : completed.Result[..20_000] + "\n… (truncated in view)";
                    done.Complete(text, completed.IsError, completed.Images);
                    _subGroup?.RefreshTitle();
                }

                break;

            case JarvisCode.Core.Agent.ToolExecutionDenied denied:
                if (_subCalls?.TryGetValue(denied.CallId, out var deniedCall) == true)
                {
                    deniedCall.IsDenied = true;
                    deniedCall.IsRunning = false;
                    _subGroup?.RefreshTitle();
                }

                break;

            case JarvisCode.Core.Agent.AssistantMessageCompleted message:
            {
                var text = message.Message.GetDisplayText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _subGroup = null;
                    SubItems.Add(new SubagentTextItem { Text = text.Trim() });
                }

                break;
            }
        }

        if (!hadItems && SubItems.Count > 0)
        {
            OnPropertyChanged(nameof(HasSubItems));
        }
    }

    /// <summary>The diff body for edit/write calls (empty when not applicable).</summary>
    public IReadOnlyList<DiffLine> DiffLines
    {
        get
        {
            if (!_diffComputed)
            {
                _diffComputed = true;
                _diffLines = ComputeDiff();
            }

            return _diffLines ?? [];
        }
    }

    /// <summary>The checklist body for todo_write calls.</summary>
    public IReadOnlyList<TodoRowItem> TodoRows => _todoRows ??= ParseTodos();

    /// <summary>Images the tool returned (screenshots); shown inline like the reference.</summary>
    public IReadOnlyList<JarvisCode.Core.Models.ImageBlock> Images
    {
        get => _images;
        set
        {
            if (SetProperty(ref _images, value))
            {
                OnPropertyChanged(nameof(HasImages));
                OnPropertyChanged(nameof(ImagesRideTheRow));
                OnPropertyChanged(nameof(ImagesRideTheBody));
            }
        }
    }

    public bool HasImages => _images.Count > 0;

    /// <summary>
    /// The input as the expanded row shows it: the bare command for the PowerShell tool,
    /// indented JSON otherwise, and empty when the call carried no arguments.
    /// </summary>
    public string InputText => _inputText ??= Cap(BuildInputText(ToolName, ArgumentsJson));

    public bool HasInput => InputText.Length > 0;

    /// <summary>
    /// The call's arguments as the reference prints them — one "key: value" line
    /// each, with paths as chips that open the file and commands in the code face.
    /// </summary>
    public IReadOnlyList<Services.ToolInputRow> InputRows =>
        _inputRows ??= Services.ToolInputRows.Build(Args);

    public bool HasInputRows => InputRows.Count > 0;

    /// <summary>
    /// What the body's Copy hands over: the argument list, then the output, one
    /// blank line apart — the reference's <c>yC(gC(e), e.output)</c>. The shell
    /// body copies the prompted command line instead, as its own copy does.
    /// </summary>
    /// <summary>Set for the reference's own 1200ms while the body's Copy shows its tick.</summary>
    public bool IsBodyCopied
    {
        get => _isBodyCopied;
        set => SetProperty(ref _isBodyCopied, value);
    }

    public string CopyBodyText => BodyIsCommand
        ? Services.ToolInputRows.Join(
            InputText.Length > 0 ? $"{CommandPrompt} {InputText}" : null, Result)
        : Services.ToolInputRows.Join(Services.ToolInputRows.CopyText(Args), Result);

    /// <summary>
    /// A row waiting on the permission card says so where its meta would be —
    /// the reference replaces the meta rather than adding to it, so the row does
    /// not read as though the call had already touched the file it names.
    /// </summary>
    public string? ApprovalChipText => IsAwaitingApproval ? "Needs approval" : null;

    public bool HasApprovalChip => ApprovalChipText is not null;

    /// <summary>
    /// The trailing word the reference puts after the meta on a row that did not
    /// simply finish: "Stopped" for one the user interrupted, "Denied" for one
    /// they refused, and "Failed" on an agent row whose own label does not
    /// already say so.
    /// </summary>
    public string? StatusChipText
    {
        get
        {
            if (IsRunning || IsAwaitingApproval)
            {
                return null;
            }

            if (IsInterrupted)
            {
                return "Stopped";
            }

            if (IsDenied)
            {
                return "Denied";
            }

            return IsSubagentRow && IsError && Info.FailedLabel is null && Info.FailedVerb is null
                ? "Failed"
                : null;
        }
    }

    public bool HasStatusChip => StatusChipText is not null;

    /// <summary>"Failed" is the reference's one danger-coloured trailing word.</summary>
    public bool StatusChipIsDanger => StatusChipText == "Failed";

    /// <summary>
    /// The reference makes the meta of a file row a link that opens the file —
    /// a diff for an edit, the file itself for a read — rather than dead text.
    /// </summary>
    public bool MetaIsFileRef =>
        !MetaIsLink && HasMeta && MetaFilePath is not null &&
        BodyKind is ToolBodyKind.Diff or ToolBodyKind.FileView;

    /// <summary>The path the meta names, when it names one.</summary>
    public string? MetaFilePath => GetString("file_path") ?? GetString("notebook_path");

    /// <summary>Meta drawn as plain text: everything a link or a file ref does not take.</summary>
    public bool MetaIsPlain => HasMeta && !MetaIsLink && !MetaIsFileRef;

    public bool HasResult => Result.Length > 0;

    /// <summary>Denied and errored calls share the failed treatment in the transcript.</summary>
    public bool IsFailed => (IsError && !IsInterrupted) || IsDenied;

    public string Result
    {
        get => _result;
        set
        {
            if (SetProperty(ref _result, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RefreshDisplay();
            }
        }
    }

    public bool IsError
    {
        get => _isError;
        set
        {
            if (SetProperty(ref _isError, value))
            {
                OnPropertyChanged(nameof(IsFailed));
                RefreshDisplay();
            }
        }
    }

    public bool IsDenied
    {
        get => _isDenied;
        set
        {
            if (SetProperty(ref _isDenied, value))
            {
                OnPropertyChanged(nameof(IsFailed));
                RefreshDisplay();
            }
        }
    }

    /// <summary>
    /// The user stopped the turn while this call ran. Reference-style: the row
    /// settles without the failed treatment ("[Request interrupted by user]").
    /// </summary>
    public bool IsInterrupted
    {
        get => _isInterrupted;
        set
        {
            if (SetProperty(ref _isInterrupted, value))
            {
                OnPropertyChanged(nameof(IsFailed));
                RefreshDisplay();
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Live one-line activity for Agent subagents.</summary>
    public string Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    /// <summary>
    /// The consecutive-same-file merge key, reference-style: reads coalesce with
    /// reads of the same file, edits with edits of it. Null never merges.
    /// </summary>
    public string? CoalesceKey => ToolName switch
    {
        "Read" => Key("Read", "file_path"),
        "Edit" => Key("Edit", "file_path"),
        "NotebookEdit" => Key("Edit", "notebook_path"),
        "Write" => Key("Write", "file_path"),
        _ => null,
    };

    private string? Key(string verb, string pathArg)
    {
        var path = Args?[pathArg] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s : null;
        return path is null ? null : $"{verb} {path}";
    }

    /// <summary>Merges a follow-on call into this row; the group calls it for adjacent same-file calls.</summary>
    public void Absorb(ToolCallItem continuation)
    {
        Continuations.Add(continuation);
        continuation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsRunning) or nameof(IsError) or nameof(Result))
            {
                RefreshAggregates();
            }
        };
        OnPropertyChanged(nameof(AllCalls));
        RefreshAggregates();
    }

    /// <summary>The call finished; refresh result-dependent wording (git verbs, Created vs Updated) and counts.</summary>
    public void Complete(string result, bool isError, IReadOnlyList<JarvisCode.Core.Models.ImageBlock>? images = null, bool interrupted = false)
    {
        Result = result;
        _info = ToolRowPresentation.Describe(ToolName, Args, result, Description);
        _diffComputed = false;
        _diffLines = null;
        IsInterrupted = interrupted;
        IsError = isError;
        IsRunning = false;
        Progress = "";
        if (images is { Count: > 0 })
        {
            Images = images;
        }

        if (!isError)
        {
            (Added, Removed) = CountDiff();
            FlashDiff = Added > 0 || Removed > 0;
            OnPropertyChanged(nameof(FlashDiff));
        }

        RefreshBody();
        OnPropertyChanged(nameof(DiffLines));
        OnPropertyChanged(nameof(IsPreviewCard));
        OnPropertyChanged(nameof(PreviewUrl));

        // A backgrounded shell call is not done — the row follows the task's own
        // life; the view model flips it back to running and resolves it on exit.
        if (ToolName == "PowerShell" && !isError &&
            Args?["run_in_background"] is JsonValue bg && bg.TryGetValue<bool>(out var flag) && flag)
        {
            var task = System.Text.RegularExpressions.Regex.Match(result, @"Started background task (task-\d+)");
            if (task.Success)
            {
                BackgroundTaskId = task.Groups[1].Value;
            }
        }

        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(MetaIsCode));
        OnPropertyChanged(nameof(MetaHref));
        OnPropertyChanged(nameof(MetaIsLink));
        OnPropertyChanged(nameof(MetaIsFileRef));
        OnPropertyChanged(nameof(MetaIsPlain));
        OnPropertyChanged(nameof(MetaIsProminent));
        OnPropertyChanged(nameof(ShowsOpenCaret));
        OnPropertyChanged(nameof(ApprovalChipText));
        OnPropertyChanged(nameof(HasApprovalChip));
        OnPropertyChanged(nameof(StatusChipText));
        OnPropertyChanged(nameof(HasStatusChip));
        OnPropertyChanged(nameof(StatusChipIsDanger));
        RefreshAggregates();
    }

    /// <summary>Which body the row would open on, once the result is known.</summary>
    private void RefreshBody()
    {
        OnPropertyChanged(nameof(BodyIsTodos));
        OnPropertyChanged(nameof(BodyIsCommand));
        OnPropertyChanged(nameof(BodyIsDiff));
        OnPropertyChanged(nameof(BodyIsFile));
        OnPropertyChanged(nameof(BodyIsErrorDetails));
        OnPropertyChanged(nameof(BodyIsDetails));
        OnPropertyChanged(nameof(ShowParams));
        OnPropertyChanged(nameof(ShowParamsFirst));
        OnPropertyChanged(nameof(ShowParamsLast));
        OnPropertyChanged(nameof(HasOutputRegion));
        OnPropertyChanged(nameof(ShowCommandOutput));
        OnPropertyChanged(nameof(ShowGenericRegion));
        OnPropertyChanged(nameof(ShowShellHeaderCopy));
        OnPropertyChanged(nameof(ShowInput));
        OnPropertyChanged(nameof(ShowFileResult));
        OnPropertyChanged(nameof(ShowTextResult));
        OnPropertyChanged(nameof(ResultIsError));
        OnPropertyChanged(nameof(HasBody));
        OnPropertyChanged(nameof(CopyBodyText));
    }

    private void RefreshAggregates()
    {
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(RemovedText));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(HasRange));
    }

    private (int Added, int Removed) CountDiff()
    {
        if (Info.Kind != ToolBodyKind.Diff)
        {
            return (0, 0);
        }

        var lines = DiffLines;
        if (lines.Count > 0)
        {
            return (lines.Count(l => l.Kind == DiffKind.Added), lines.Count(l => l.Kind == DiffKind.Removed));
        }

        // Diff unavailable (huge edit): fall back to raw line counts.
        return ToolName switch
        {
            "Edit" => (LineCount(GetString("new_string")), LineCount(GetString("old_string"))),
            "Write" => (LineCount(GetString("content")), 0),
            "NotebookEdit" => (LineCount(GetString("new_source")), 0),
            _ => (0, 0),
        };
    }

    private IReadOnlyList<DiffLine>? ComputeDiff()
    {
        if (Info.Kind != ToolBodyKind.Diff || IsFailed)
        {
            return null;
        }

        var (oldText, newText) = ToolName switch
        {
            "Edit" => (GetString("old_string"), GetString("new_string")),
            "Write" => ("", GetString("content")),
            "NotebookEdit" => ("", GetString("new_source")),
            _ => (null, null),
        };
        if (oldText is null || newText is null || (oldText.Length == 0 && newText.Length == 0))
        {
            return null;
        }

        if (oldText.Length + newText.Length > 400_000)
        {
            return null;
        }

        // A brand-new file is all additions — diffing against "" would count a
        // phantom removed empty line; the reference shows +N with nothing removed.
        if (oldText.Length == 0)
        {
            var added = newText.Replace("\r\n", "\n").Split('\n');
            return added.Length > 2000
                ? null
                : added.Select(l => new DiffLine(DiffKind.Added, l)).ToList();
        }

        try
        {
            return LineDiff.Compute(oldText, newText);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IReadOnlyList<TodoRowItem> ParseTodos()
    {
        if (ToolName != "todo_write" || Args?["todos"] is not JsonArray todos)
        {
            return [];
        }

        var rows = new List<TodoRowItem>();
        foreach (var node in todos)
        {
            if (node is not JsonObject todo)
            {
                continue;
            }

            var text = (todo["content"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c
                : (todo["text"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var status = (todo["status"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : "pending";
            rows.Add(new TodoRowItem(text.Trim(), status is "completed" or "in_progress" ? status : "pending"));
        }

        return rows;
    }

    private string? GetString(string name) =>
        Args?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int LineCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + 1;

    private static string BuildInputText(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "";
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return argumentsJson.Trim();
        }

        if (node is not JsonObject arguments)
        {
            return argumentsJson.Trim();
        }

        // A tool that takes no arguments arrives as {}, which is not worth a block.
        if (arguments.Count == 0)
        {
            return "";
        }

        // Only the shell card prints an input at all — every other body reaches
        // the arguments through InputRows, one key at a time.
        return toolName is "PowerShell" or "Bash" &&
            arguments["command"] is JsonValue value &&
            value.TryGetValue<string>(out var command)
            ? command.Trim()
            : "";
    }

    /// <summary>
    /// Same ceiling the transcript puts on tool output. Without it a Write call
    /// would hold a second copy of the whole file for as long as the session is open.
    /// </summary>
    private static string Cap(string text)
        => text.Length <= 20_000 ? text : text[..20_000] + "\n… (truncated in view)";
}

public sealed class ToolGroupItem : TranscriptItem
{
    private bool _isExpanded;
    private bool _forceExpanded;
    private bool _isRunning = true;
    private string _title = "";
    private string _addedText = "";
    private string _removedText = "";
    private int _failedCount;
    private string? _thinkingRecap;
    private bool _showRecap;
    private IReadOnlyList<Services.ToolSummarySegment> _segments = [];
    private bool _rendersBareRow;

    public ObservableCollection<ToolCallItem> Calls { get; } = [];

    /// <summary>The header's ± badge arrived while this session was watching.</summary>
    public bool FlashDiff { get; private set; }

    /// <summary>
    /// Whether the chip a task id names went on to start a session, which is what
    /// separates the run's "started" clause from its "suggested" one.
    /// </summary>
    public Func<string, bool>? TaskStarted { get; init; }

    /// <summary>
    /// A run of spawn_task calls and nothing else — the reference's spawnTask
    /// bucket. It is summarised by what became of the chips rather than by the
    /// tool that queued them, so it is never mixed with calls of another kind.
    /// </summary>
    public bool IsSpawnTaskRun =>
        Calls.Count > 0 && AllCalls.All(static c => Services.ToolGroupSummary.IsSpawnTask(c.ToolName));

    /// <summary>
    /// The reference's <c>uQ</c>: a run whose calls coalesce into one row and
    /// that has nothing else to say — no memory operations, and here no task
    /// events or session events to thread through it — is drawn as that row
    /// alone. No summary sentence, no outline card: a sentence reading "Read a
    /// file" over a card holding one row that reads "Read Base.xaml" says the
    /// same thing twice.
    /// </summary>
    public bool RendersBareRow
    {
        get => _rendersBareRow;
        private set
        {
            if (SetProperty(ref _rendersBareRow, value))
            {
                OnPropertyChanged(nameof(RendersCard));
            }
        }
    }

    public bool RendersCard => !RendersBareRow;

    /// <summary>The one row a bare run draws, or null while it draws a card.</summary>
    public ToolCallItem? BareRow => RendersBareRow ? Calls.FirstOrDefault() : null;

    /// <summary>
    /// Where this session keeps its memory files. A write into it reads as
    /// "saved a memory" in the header rather than "created a file", which is the
    /// reference lifting its memory operations out in front of the rest.
    /// </summary>
    public string? MemoryDirectory { get; init; }

    /// <summary>
    /// The header sentence as its own clauses, because the reference colours a
    /// clause whose calls all failed rather than counting them in a parenthetical.
    /// The first clause is capitalised; the rest are joined with ", ".
    /// </summary>
    public IReadOnlyList<Services.ToolSummarySegment> Segments
    {
        get => _segments;
        private set
        {
            _segments = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// A call in this group is waiting on the permission card. The reference puts
    /// its own "Needs approval" on the group header as well as on the row.
    /// </summary>
    public bool IsAwaiting => AllCalls.Any(static c => c.IsAwaitingApproval);

    /// <summary>Every call in the group, coalesced rows flattened back out.</summary>
    public IEnumerable<ToolCallItem> AllCalls => Calls.SelectMany(c => c.AllCalls);

    /// <summary>One-line recap of the thinking that led to this tool group.</summary>
    public string? ThinkingRecap
    {
        get => _thinkingRecap;
        set
        {
            if (SetProperty(ref _thinkingRecap, value))
            {
                OnPropertyChanged(nameof(RecapVisible));
            }
        }
    }

    /// <summary>True only in the thinking transcript view (the surface flips it).</summary>
    public bool ShowRecap
    {
        get => _showRecap;
        set
        {
            if (SetProperty(ref _showRecap, value))
            {
                OnPropertyChanged(nameof(RecapVisible));
            }
        }
    }

    public bool RecapVisible => _showRecap && !string.IsNullOrEmpty(_thinkingRecap);

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>"+39" once any call in the group added lines; empty otherwise.</summary>
    public string AddedText
    {
        get => _addedText;
        private set
        {
            if (SetProperty(ref _addedText, value))
            {
                OnPropertyChanged(nameof(HasDiffStat));
            }
        }
    }

    /// <summary>"-6" once any call removed lines; empty otherwise.</summary>
    public string RemovedText
    {
        get => _removedText;
        private set
        {
            if (SetProperty(ref _removedText, value))
            {
                OnPropertyChanged(nameof(HasDiffStat));
            }
        }
    }

    public bool HasDiffStat => _addedText.Length > 0 || _removedText.Length > 0;

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    /// <summary>
    /// Whether the run's card is open: the reference's <c>expanded = verbose ||
    /// local</c>. A run the verbose view holds open is frozen there - its own
    /// handler returns before touching state (<c>if(p||!K)return</c>) - so a
    /// click does nothing and the disclosure is not drawn at all. The run's own
    /// expanded state is left untouched meanwhile, which is what makes leaving
    /// verbose land back on whatever the reader had.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded || _forceExpanded;
        set
        {
            if (_forceExpanded)
            {
                return;
            }

            SetProperty(ref _isExpanded, value);
        }
    }

    /// <summary>
    /// Whether the run draws a disclosure at all. The reference renders none
    /// while the verbose view is forcing it open, because there is nothing a
    /// click could do.
    /// </summary>
    public bool ShowsDisclosure => !_forceExpanded;

    /// <summary>
    /// Verbose view holds every group open without touching the row's own
    /// expanded state, so leaving it restores what the reader had.
    /// </summary>
    public bool ForceExpanded
    {
        get => _forceExpanded;
        set
        {
            if (SetProperty(ref _forceExpanded, value))
            {
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(ShowsDisclosure));
            }
        }
    }

    /// <summary>Calls that errored or were denied, across the whole group.</summary>
    public int FailedCount
    {
        get => _failedCount;
        private set
        {
            if (SetProperty(ref _failedCount, value))
            {
                OnPropertyChanged(nameof(HasFailures));
            }
        }
    }

    public bool HasFailures => FailedCount > 0;

    /// <summary>
    /// Which shape this run draws in, and what that makes of its rows. A second
    /// row arriving mid-turn promotes a bare run into a card, which is why this
    /// is decided on every refresh rather than once when the run opens.
    /// </summary>
    private void RefreshShape()
    {
        RendersBareRow = Calls.Count == 1 &&
            !Services.ToolGroupSummary.AnyMemoryOperation([.. AllCalls], MemoryDirectory);
        foreach (var row in Calls)
        {
            row.InCard = !RendersBareRow;
            row.InGroup = !RendersBareRow || row.Continuations.Count > 0;
            row.VerbOverride = StartedSessionVerb(row);
        }

        OnPropertyChanged(nameof(IsSpawnTaskRun));
        OnPropertyChanged(nameof(BareRow));
    }

    /// <summary>
    /// The reference's <c>P</c>: a spawn_task row whose chip started a session
    /// says so, in place of the verb the tool would otherwise wear.
    /// </summary>
    private string? StartedSessionVerb(ToolCallItem row) =>
        Services.ToolGroupSummary.IsSpawnTask(row.ToolName) &&
        Services.ToolGroupSummary.TaskIdOf(row) is { } id &&
        TaskStarted?.Invoke(id) == true
            ? "Started session"
            : null;

    /// <summary>
    /// Adds a call, merging it into the previous row when both are consecutive
    /// reads/edits of the same file — the reference's coalescing.
    /// </summary>
    public ToolCallItem AddCall(ToolCallItem call)
    {
        var last = Calls.LastOrDefault();
        if (last is not null && last.CoalesceKey is { } key && key == call.CoalesceKey)
        {
            last.Absorb(call);
        }
        else
        {
            Calls.Add(call);
        }

        return call;
    }

    /// <summary>
    /// The running calls in this group that can carry on off the turn — the
    /// reference's "Run in background", which moves all of a message's at once.
    /// </summary>
    public IEnumerable<ToolCallItem> MovableCalls =>
        AllCalls.Where(static c => c.IsRunning && c.ToolName == "PowerShell");

    public bool CanMoveToBackground => MovableCalls.Any();

    public void RefreshTitle()
    {
        OnPropertyChanged(nameof(CanMoveToBackground));
        OnPropertyChanged(nameof(IsAwaiting));
        RefreshShape();
        var all = AllCalls.ToList();
        FailedCount = all.Count(static c => c.IsFailed);

        // Awaiting-approval rows count as live too — the reference gives
        // awaiting_approval priority over running.
        var runningRow = Calls.FirstOrDefault(static row =>
            row.AllCalls.Any(static c => c.IsRunning || c.IsAwaitingApproval));
        if (runningRow is not null)
        {
            // The reference shows the live call's own label ("Reading Base.xaml",
            // "Installing deps") while the group runs.
            var live = runningRow.AllCalls.First(static c => c.IsRunning || c.IsAwaitingApproval);
            var meta = live.MetaText;
            Title = string.IsNullOrEmpty(meta) ? live.PrimaryText : $"{live.PrimaryText} {meta}";
            Segments = [new Services.ToolSummarySegment(Title, null, false)];
            IsRunning = true;
            AddedText = "";
            RemovedText = "";
            return;
        }

        IsRunning = false;
        Segments = IsSpawnTaskRun
            ? Services.ToolGroupSummary.BuildSpawnTask(all, TaskStarted)
            : Services.ToolGroupSummary.Build(all, MemoryDirectory);
        Title = Services.ToolGroupSummary.Sentence(Segments);

        var added = all.Sum(static c => c.Added);
        var removed = all.Sum(static c => c.Removed);
        AddedText = added > 0 ? $"+{added}" : "";
        RemovedText = removed > 0 ? $"-{removed}" : "";
        if ((added > 0 || removed > 0) && all.Any(static c => c.FlashDiff))
        {
            FlashDiff = true;
            OnPropertyChanged(nameof(FlashDiff));
        }
    }
}

/// <summary>
/// A subagent's own interim/final text inside its parent row's nested transcript —
/// plain, secondary, deliberately without the assistant bubble's action bar.
/// </summary>
public sealed class SubagentTextItem : TranscriptItem
{
    public required string Text { get; init; }
}

/// <summary>
/// The action bar under one assistant turn on the Code surface — the reference's
/// message footer, in its order: Copy · Fork from here · 👍 · 👎 · Run in
/// background · model · timestamp. It is the turn's last transcript item, so
/// everything the turn produces is inserted above it.
/// </summary>
public sealed class AssistantFooterItem : TranscriptItem
{
    private readonly List<ToolGroupItem> _groups = [];
    private string? _text;
    private bool _isCopied;
    private int _feedback;
    private DateTimeOffset? _completedAt;

    /// <summary>
    /// How many of Session.Messages this turn covers — the exclusive cut a fork
    /// takes, so the new session holds everything through this answer.
    /// </summary>
    public int MessageIndex { get; set; }

    /// <summary>The model the turn ran on, shown dim at the end of the bar.</summary>
    public string? ModelLabel { get; init; }

    /// <summary>The turn's final answer, for Copy; null hides the action, as in the reference.</summary>
    public string? Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(HasText));
            }
        }
    }

    public bool HasText => !string.IsNullOrWhiteSpace(_text);

    /// <summary>When the turn finished; null while it is still running.</summary>
    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set
        {
            if (SetProperty(ref _completedAt, value))
            {
                OnPropertyChanged(nameof(RelativeTime));
                OnPropertyChanged(nameof(HasTimestamp));
                OnPropertyChanged(nameof(HasSeparator));
            }
        }
    }

    public bool HasTimestamp => _completedAt is not null;

    /// <summary>The reference puts " · " between the model label and the timestamp.</summary>
    public bool HasSeparator => ModelLabel is not null && _completedAt is not null;

    public string RelativeTime =>
        _completedAt is { } at ? TranscriptTime.Stamp(at) : "";

    public void RefreshRelativeTime() => OnPropertyChanged(nameof(RelativeTime));

    /// <summary>Set briefly after Copy so the button can show a tick instead.</summary>
    public bool IsCopied
    {
        get => _isCopied;
        set => SetProperty(ref _isCopied, value);
    }

    /// <summary>+1 good, -1 bad, 0 none — a local toggle, like the Chat surface's.</summary>
    public int Feedback
    {
        get => _feedback;
        set
        {
            if (SetProperty(ref _feedback, value))
            {
                OnPropertyChanged(nameof(IsGood));
                OnPropertyChanged(nameof(IsBad));
            }
        }
    }

    public bool IsGood => _feedback > 0;

    public bool IsBad => _feedback < 0;

    /// <summary>Follows one of the turn's tool groups so the action can come and go live.</summary>
    public void Track(ToolGroupItem group)
    {
        _groups.Add(group);
        group.PropertyChanged += OnGroupChanged;
        OnPropertyChanged(nameof(CanMoveToBackground));
    }

    private void OnGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolGroupItem.CanMoveToBackground))
        {
            OnPropertyChanged(nameof(CanMoveToBackground));
        }
    }

    /// <summary>The turn's running calls that can carry on in the background.</summary>
    public IEnumerable<ToolCallItem> MovableCalls => _groups.SelectMany(static g => g.MovableCalls);

    public bool CanMoveToBackground => MovableCalls.Any();
}

public sealed class NoticeItem : TranscriptItem
{
    public required string Text { get; init; }
    public bool IsError { get; init; }
}

/// <summary>
/// The card the Chat surface ends an unfinished turn with, ported from the
/// reference's own (<c>c3e2391e3-3lB_ip9x.js</c>, its <c>Xu</c>): one sentence
/// saying what happened, and the two things that can be done about it — "Edit
/// prompt", which the reference offers only for a turn the user stopped, and
/// "Try again".
/// </summary>
public sealed class ChatErrorItem : TranscriptItem
{
    public required string Text { get; init; }

    /// <summary>A turn the user stopped, which is the only one that offers Edit prompt.</summary>
    public bool Interrupted { get; init; }

    /// <summary>Whether the card is a warning rather than the neutral variant.</summary>
    public bool Warning { get; init; }
}

/// <summary>
/// The divider mcp__ccd_session__mark_chapter draws: a titled rule across the
/// transcript marking where a new phase of the work began. The optional summary
/// is what the table of contents shows on hover.
/// </summary>
public sealed class ChapterItem : TranscriptItem
{
    /// <summary>The id the rename and hide are filed under, per session.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Summary { get; init; }

    private string? _displayTitle;

    /// <summary>The renamed title, when the session carries one.</summary>
    public string DisplayTitle
    {
        get => _displayTitle ?? Title;
        set => SetProperty(ref _displayTitle, value);
    }

    private bool _isEditing;

    /// <summary>True while the double-clicked title is an editable field.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }
}

/// <summary>
/// A widget the model rendered with <c>mcp__visualize__show_widget</c>, in the
/// transcript where the reference puts it: the tool's own row, replaced by the
/// widget rather than sitting beside it (the reference's <c>tO</c> in
/// <c>c360a9e1c-DUoNQd2W.js</c> is the MCP-app tool row).
/// </summary>
public sealed class WidgetItem : TranscriptItem
{
    private readonly System.Text.StringBuilder _streamedInput = new();
    public int InputRevision { get; private set; }
    public bool IsInputComplete { get; private set; } = true;
    public bool IsInputStreaming { get; private set; }

    public void AppendInput(string delta)
    {
        _streamedInput.Append(delta);
        ApplyInput(Services.VisualizeWidgetCalls.ParsePartial(_streamedInput.ToString()), complete: false);
    }

    public void CompleteInput(string? argumentsJson) =>
        ApplyInput(Services.VisualizeWidgetCalls.Parse(argumentsJson), complete: true);

    private void ApplyInput(Services.VisualizeWidgetCall input, bool complete)
    {
        Title = input.Title;
        WidgetCode = input.WidgetCode;
        LoadingMessages = input.LoadingMessages;
        IsInputComplete = complete;
        IsInputStreaming = !complete;
        InputRevision++;
        OnPropertyChanged(nameof(InputRevision));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsRendering));
    }

    public void StopInput()
    {
        IsInputStreaming = false;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsRendering));
    }
    /// <summary>The tool call this row stands for; nothing else keys off it.</summary>
    public required string CallId { get; init; }

    /// <summary>The server the widget came from — the label names it.</summary>
    public required string ServerName { get; init; }

    /// <summary>The tool's short name, shown in the code face beside the label.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The wire name the call arrived under. read_widget_context is asked for a
    /// widget by tool name, and this is the name the model saw.
    /// </summary>
    public required string WireName { get; init; }

    /// <summary>The call's <c>title</c>: names the widget, and is its download filename.</summary>
    public string? Title { get; set; }

    /// <summary>The call's <c>widget_code</c>, handed to the runtime as its tool input.</summary>
    public string WidgetCode { get; set; } = "";

    /// <summary>The call's <c>loading_messages</c>, carried so the page receives the whole input.</summary>
    public IReadOnlyList<string> LoadingMessages { get; set; } = [];

    /// <summary>The <c>ui://</c> resource the runtime page is served from.</summary>
    public string ResourceUri { get; init; } = Services.VisualizeTools.RuntimeUri;

    /// <summary>Reads that resource out of the window's in-process MCP shell.</summary>
    public Func<string, Services.InternalMcpResourceContents?>? ResourceReader { get; init; }

    private bool _isExpanded = true;

    /// <summary>The reference opens the row expanded and lets the user fold it.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isInitialized;

    /// <summary>
    /// True once the widget page has answered the host handshake. Until then the
    /// row reads "Rendering widget" and the body is not shown, which is the
    /// reference's own <c>ge = me || !U</c>.
    /// </summary>
    public bool IsInitialized
    {
        get => _isInitialized;
        set
        {
            if (SetProperty(ref _isInitialized, value))
            {
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(IsRendering));
            }
        }
    }

    public bool IsRendering => !IsInitialized || IsInputStreaming;

    /// <summary>The row's label, and its accessible name once it settles.</summary>
    public string Label => IsRendering ? Services.VisualizeWidgetStrings.Rendering
        : IsInputComplete ? Services.VisualizeWidgetStrings.WidgetFrom(ServerName) : "Stopped";

    /// <summary>
    /// What the widget last reported through <c>ui/update-model-context</c>.
    /// This is what read_widget_context reads back.
    /// </summary>
    public IReadOnlyList<JarvisCode.Core.Models.ContentBlock> ModelContext { get; set; } = [];
}

/// <summary>
/// The reference's "Session not found on disk" card (its <c>wN</c> in the ccd
/// chunk <c>c11959232-DM8o5ho4.js</c>): a title, a body, and Import CLI sessions
/// / Archive / Delete.
/// </summary>
public sealed class SessionNotFoundItem : TranscriptItem
{
    public required string SessionId { get; init; }

    public string Title => Services.SessionNotFound.Title;

    public string Body => Services.SessionNotFound.Body;

    public bool CanImport { get; init; }

    public string ImportLabel => Services.SessionNotFound.Label(Services.SessionNotFoundAction.ImportCliSessions);

    public string ArchiveLabel => Services.SessionNotFound.Label(Services.SessionNotFoundAction.Archive);

    public string DeleteLabel => Services.SessionNotFound.Label(Services.SessionNotFoundAction.Delete);

    public string ArchiveName => Services.SessionNotFound.AccessibleName(Services.SessionNotFoundAction.Archive);

    public string DeleteName => Services.SessionNotFound.AccessibleName(Services.SessionNotFoundAction.Delete);
}

/// <summary>
/// The card a failed request leaves in the transcript, in the reference's shape
/// (<c>tR</c> in <c>c360a9e1c-DUoNQd2W.js</c>): a headline, a hint, the request
/// id when the provider named one, collapsible details, and the actions the
/// error's own category says would help.
/// </summary>
public sealed class ErrorCardItem : TranscriptItem
{
    public required string Headline { get; init; }

    public required string Hint { get; init; }

    /// <summary>The provider's own message, behind "View details".</summary>
    public string? Details { get; init; }

    public string? RequestId { get; init; }

    public Services.ApiErrorCategory Category { get; init; }

    /// <summary>Whether sending the message again would plausibly work.</summary>
    public bool RetryHelps { get; init; }

    public bool RewindHelps { get; init; }

    public bool CompactHelps { get; init; }

    private bool _detailsExpanded;

    public bool DetailsExpanded
    {
        get => _detailsExpanded;
        set => SetProperty(ref _detailsExpanded, value);
    }

    private bool _copied;

    /// <summary>"Copy error details" becomes "Copied" for the reference's 1500ms.</summary>
    public bool Copied
    {
        get => _copied;
        set => SetProperty(ref _copied, value);
    }

    public bool HasDetails => Details is { Length: > 0 };

    public bool HasRequestId => RequestId is { Length: > 0 };

    /// <summary>"Request ID: {requestId}", the line the card prints under the hint.</summary>
    public string RequestIdText => $"Request ID: {RequestId}";
}

/// <summary>
/// The row a compaction leaves behind, in the reference's three forms
/// (<c>CL</c> in <c>c360a9e1c-DUoNQd2W.js</c>): what it saved when both sides
/// are known, what it started from when only that is, and the bare sentence
/// otherwise. "Context cleared" is its sibling <c>SL</c>.
/// </summary>
public sealed class CompactionItem : TranscriptItem
{
    public long PreTokens { get; init; }

    public long PostTokens { get; init; }

    /// <summary>A /clear rather than a compaction, which the reference draws as its own row.</summary>
    public bool ContextCleared { get; init; }

    public string Text => ContextCleared
        ? "Context cleared"
        : PreTokens > 0 && PostTokens > 0 && PreTokens > PostTokens
            ? $"Compacted session · saved {FormatTokens(PreTokens - PostTokens)} tokens"
            : PreTokens > 0
                ? $"Compacted session · from {FormatTokens(PreTokens)} tokens"
                : "Compacted session";

    /// <summary>The transcript's own thousands form, which the context pill also uses.</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k",
        _ => tokens.ToString(),
    };
}
