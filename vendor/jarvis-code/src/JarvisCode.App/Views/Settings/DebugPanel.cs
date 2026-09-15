using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Settings;

/// <summary>
/// Settings › Debug: the calls the model providers actually made, newest first, with the
/// selected one opened up into request and response. It reads
/// <see cref="ModelTrafficLog"/> snapshots only, so nothing here touches state a network
/// thread is still writing.
/// </summary>
public sealed class DebugPanel : StackPanel
{
    private const double BodyMaxHeight = 190;

    /// <summary>
    /// How often a call that is still streaming is re-read. The log itself signals only
    /// lifecycle changes — a per-byte event would repaint thousands of times per answer —
    /// so a slow tick is what makes a live response visible.
    /// </summary>
    private static readonly TimeSpan LiveRefresh = TimeSpan.FromMilliseconds(500);

    private readonly ModelTrafficLog _log;
    private readonly StackPanel _listHost = new();
    private readonly ContentControl _detailHost = new() { Focusable = false };
    private readonly DispatcherTimer _timer;

    private IReadOnlyList<ModelTrafficSnapshot> _calls = [];
    private int? _selectedId;
    /// <summary>
    /// The signature of what the detail pane currently shows. Empty until the first
    /// render, which no real signature can be — otherwise the very first Refresh would
    /// mistake "nothing rendered yet" for "already up to date" and leave the pane blank.
    /// </summary>
    private string _renderedDetail = "";
    private bool _requestHeadersOpen;
    private bool _responseHeadersOpen;

    /// <summary>Null until the user picks a half for the selected call; see ShowResponse.</summary>
    private bool? _showResponse;

    public DebugPanel(ModelTrafficLog log)
    {
        _log = log;
        Margin = new Thickness(0, 4, 0, 0);

        Children.Add(SettingsUi.PanelTitle("Debug"));
        Children.Add(SettingsUi.PanelSubtitle(
            "Every call the model providers made, exactly as it went out and came back."));
        Children.Add(_listHost);
        Children.Add(_detailHost);

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = LiveRefresh };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            _log.Changed += OnLogChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _log.Changed -= OnLogChanged;
            _timer.Stop();
        };
    }

    private void OnLogChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);

    private void Refresh()
    {
        if (!IsLoaded)
        {
            return;
        }

        _calls = _log.Snapshot();
        var selected = Select(_calls, _selectedId);

        RenderList(selected);

        // Rebuilt only when something about the selected call actually moved, so reading a
        // finished response is not interrupted every time a new call starts.
        var signature = Signature(selected);
        if (signature != _renderedDetail)
        {
            _renderedDetail = signature;
            _detailHost.Content = selected is null ? EmptyState() : Detail(selected);
        }

        // The timer runs only while something is still arriving.
        if (selected?.IsRunning == true)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>
    /// Which call the detail shows: the newest until the user picks one, then theirs for as
    /// long as it is still in the log. Following the newest is the point of the pane — a
    /// selection that pinned itself to whatever happened to be first would stop tracking
    /// the call the user is waiting on.
    /// </summary>
    internal static ModelTrafficSnapshot? Select(IReadOnlyList<ModelTrafficSnapshot> calls, int? pinned)
        => pinned is { } id
            ? calls.FirstOrDefault(call => call.Id == id) ?? calls.FirstOrDefault()
            : calls.FirstOrDefault();

    private string Signature(ModelTrafficSnapshot? call) => call is null
        ? "(none)"
        : string.Join('|',
            call.Id, call.State, call.StatusCode, call.RequestBytes, call.ResponseBytes,
            _requestHeadersOpen, _responseHeadersOpen, ShowResponse(call));

    // ---------------- list ----------------

    private void RenderList(ModelTrafficSnapshot? selected)
    {
        _listHost.Children.Clear();
        if (_calls.Count == 0)
        {
            return;
        }

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var header = SettingsUi.SectionHeader($"Recent calls ({_calls.Count})");
        header.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(header);

        var clear = SettingsUi.GhostButton("Clear", () =>
        {
            _selectedId = null;
            _log.Clear();
        });
        clear.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(clear, "Clear captured calls");
        Grid.SetColumn(clear, 1);
        head.Children.Add(clear);
        _listHost.Children.Add(head);

        foreach (var call in _calls)
        {
            _listHost.Children.Add(ListRow(call, isSelected: call.Id == selected?.Id));
        }
    }

    private Button ListRow(ModelTrafficSnapshot call, bool isSelected)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var status = StatusChip(call);
        status.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(status);

        var target = Mono($"{call.Method} {call.Host}{call.Path}", 11.5, "Text200Brush");
        target.Margin = new Thickness(10, 0, 10, 0);
        target.VerticalAlignment = VerticalAlignment.Center;
        target.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(target, 1);
        row.Children.Add(target);

        var meta = Text(
            string.Join("  ·  ", new[] { call.ModelId, Duration(call), call.StartedAt.ToString("HH:mm:ss") }
                .Where(static part => !string.IsNullOrEmpty(part))),
            11,
            "Text500Brush");
        meta.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(meta, 2);
        row.Children.Add(meta);

        var button = new Button
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 2, 0, 0),
        };
        button.SetResourceReference(StyleProperty, "RowButton");
        if (isSelected)
        {
            button.SetResourceReference(BackgroundProperty, "SelectedOverlayBrush");
        }

        AutomationProperties.SetName(
            button, $"{StatusLabel(call)} {call.Method} {call.Host}{call.Path} at {call.StartedAt:HH:mm:ss}");
        button.Click += (_, _) =>
        {
            _selectedId = call.Id;
            _requestHeadersOpen = false;
            _responseHeadersOpen = false;
            _showResponse = null;
            Refresh();
        };
        return button;
    }

    // ---------------- detail ----------------

    private FrameworkElement EmptyState()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var title = Text("No model calls yet", 13, "Text200Brush", semibold: true);
        stack.Children.Add(title);
        var hint = Text(
            "Send a message on either surface and the request that went out — and the response " +
            "that came back — will appear here.",
            12.5,
            "Text400Brush");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(hint);
        stack.Children.Add(Footer());
        return stack;
    }

    private FrameworkElement Detail(ModelTrafficSnapshot call)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };

        var rule = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 14) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        stack.Children.Add(rule);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var url = Mono($"{call.Method}  {call.Url}", 11.5, "Text300Brush");
        url.TextWrapping = TextWrapping.Wrap;
        url.Margin = new Thickness(0, 0, 12, 0);
        header.Children.Add(url);
        var chip = StatusChip(call);
        chip.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(chip, 1);
        header.Children.Add(chip);
        stack.Children.Add(header);

        var timing = new List<string>();
        if (call.TimeToHeaders is { } head)
        {
            timing.Add($"headers in {Format(head)}");
        }

        if (call.Elapsed is { } total)
        {
            timing.Add(call.IsRunning ? $"{Format(total)} so far" : $"{Format(total)} total");
        }

        if (timing.Count > 0)
        {
            var line = Text(string.Join("  ·  ", timing), 11.5, "Text500Brush");
            line.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(line);
        }

        if (call.Error is { } error)
        {
            var line = Text(error, 12, "Danger000Brush");
            line.TextWrapping = TextWrapping.Wrap;
            line.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(line);
        }

        // One half at a time: the settings dialog is 620px tall, and stacking two body
        // views put the response entirely below the fold.
        bool response = ShowResponse(call);
        stack.Children.Add(Tabs(call, response));
        stack.Children.Add(response
            ? Section(
                "Response",
                ResponseMeta(call),
                call.ResponseHeaders,
                _responseHeadersOpen,
                open =>
                {
                    _responseHeadersOpen = open;
                    Refresh();
                },
                call.ResponseIsBinary ? call.ResponseBody : Pretty(call.ResponseBody),
                call.State switch
                {
                    ModelTrafficState.InFlight => "Waiting for the response…",
                    ModelTrafficState.Failed => "No response — the request never completed.",
                    _ => "The response had no body.",
                },
                call.ResponseIsBinary)
            : Section(
                "Request",
                $"{Size(call.RequestBytes)}{(call.RequestTruncated ? " · truncated" : "")}",
                call.RequestHeaders,
                _requestHeadersOpen,
                open =>
                {
                    _requestHeadersOpen = open;
                    Refresh();
                },
                Pretty(call.RequestBody),
                "The request body was not captured.",
                isBinary: false));

        stack.Children.Add(Footer());
        return stack;
    }

    /// <summary>
    /// Which half to open on. Once the user has picked for this call their pick stands;
    /// until then a finished call opens on what came back and a call still in flight — which
    /// has no response yet — opens on what went out.
    /// </summary>
    private bool ShowResponse(ModelTrafficSnapshot call)
        => _showResponse ?? call.ResponseBytes > 0;

    private FrameworkElement Tabs(ModelTrafficSnapshot call, bool response)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        strip.Children.Add(Tab("Request", !response, () => _showResponse = false));
        strip.Children.Add(Tab("Response", response, () => _showResponse = true));

        var shell = new Border
        {
            Child = strip,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        shell.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");

        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(shell);

        var body = response
            ? call.ResponseIsBinary ? call.ResponseBody : Pretty(call.ResponseBody)
            : Pretty(call.RequestBody);
        var copy = new Button { Content = "Copy", IsEnabled = body.Length > 0 };
        copy.SetResourceReference(StyleProperty, "GhostButton");
        AutomationProperties.SetName(copy, response ? "Copy the response body" : "Copy the request body");
        copy.Click += (_, _) => CopyToClipboard(body, copy);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        return row;
    }

    private Button Tab(string label, bool selected, Action select)
    {
        var button = new Button
        {
            Content = Text(label, 12, selected ? "Text100Brush" : "Text400Brush", semibold: selected),
            Padding = new Thickness(14, 4, 14, 5),
        };
        button.SetResourceReference(StyleProperty, "RowButton");
        if (selected)
        {
            button.SetResourceReference(BackgroundProperty, "Bg000Brush");
        }

        AutomationProperties.SetName(button, $"Show the {label.ToLowerInvariant()}");
        button.Click += (_, _) =>
        {
            select();
            Refresh();
        };
        return button;
    }

    private static string ResponseMeta(ModelTrafficSnapshot call)
    {
        var parts = new List<string> { Size(call.ResponseBytes) };
        if (call.ResponseTruncated)
        {
            parts.Add("truncated");
        }

        if (!string.IsNullOrEmpty(call.ContentType))
        {
            parts.Add(call.ContentType);
        }

        return string.Join(" · ", parts);
    }

    private FrameworkElement Section(
        string title,
        string meta,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        bool headersOpen,
        Action<bool> toggleHeaders,
        string body,
        string emptyMessage,
        bool isBinary)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var metaText = Text(meta, 11.5, "Text500Brush");
        metaText.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(metaText);
        stack.Children.Add(row);

        stack.Children.Add(HeadersToggle(headers, headersOpen, toggleHeaders));

        if (body.Length == 0)
        {
            var empty = Text(emptyMessage, 12, "Text400Brush");
            empty.Margin = new Thickness(0, 6, 0, 0);
            empty.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(empty);
            return stack;
        }

        if (isBinary)
        {
            var note = Text(
                "Binary body — shown as hex. This provider answers in a framed event stream, not text.",
                11.5,
                "Text500Brush");
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(note);
        }

        var view = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            AcceptsReturn = true,
            // Wrapped rather than editor-style: a request body is one JSON string tens of
            // thousands of characters wide, which WPF force-breaks and then clips.
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = BodyMaxHeight,
            FontSize = 11.5,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 0, 0),
        };
        view.SetResourceReference(StyleProperty, "CodeEditorTextBox");
        view.SetResourceReference(Control.FontFamilyProperty, "MonoFontFamily");
        AutomationProperties.SetName(view, $"{title} body");
        stack.Children.Add(view);
        return stack;
    }

    private static FrameworkElement HeadersToggle(
        IReadOnlyList<KeyValuePair<string, string>> headers, bool open, Action<bool> toggle)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Content = Text($"{(open ? "⌄" : "›")}  Headers ({headers.Count})", 12, "Text400Brush"),
        };
        button.SetResourceReference(StyleProperty, "IconButton");
        AutomationProperties.SetName(button, open ? "Hide headers" : "Show headers");
        button.Click += (_, _) => toggle(!open);
        stack.Children.Add(button);

        if (!open)
        {
            return stack;
        }

        var rows = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
        foreach (var (name, value) in headers)
        {
            var line = Mono($"{name}: {value}", 11.5, "Text500Brush");
            line.TextWrapping = TextWrapping.Wrap;
            line.Margin = new Thickness(0, 1, 0, 1);
            rows.Children.Add(line);
        }

        if (headers.Count == 0)
        {
            rows.Children.Add(Text("None.", 11.5, "Text500Brush"));
        }

        stack.Children.Add(rows);
        return stack;
    }

    private static FrameworkElement Footer()
    {
        var caption = SettingsUi.Caption(
            $"The last {ModelTrafficLog.Capacity} calls, kept in memory only and gone when the app closes — " +
            "these bodies hold the whole conversation. API keys are blanked before capture, so what you copy " +
            "carries none. Retries and key rotations appear as the separate calls they are. The ChatGPT web " +
            "session drives a page instead of an API and so never appears here.");
        caption.Margin = new Thickness(0, 20, 0, 0);
        return caption;
    }

    // ---------------- pieces ----------------

    private static void CopyToClipboard(string text, Button button)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process holds the clipboard; say so rather than pretending it worked.
            button.Content = "Clipboard busy";
            return;
        }

        button.Content = "Copied";
        var reset = new DispatcherTimer(DispatcherPriority.Background, button.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        reset.Tick += (_, _) =>
        {
            reset.Stop();
            button.Content = "Copy";
        };
        reset.Start();
    }

    private static Border StatusChip(ModelTrafficSnapshot call)
    {
        var label = Text(StatusLabel(call), 11, StatusBrush(call), semibold: true);
        var chip = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 3),
            BorderThickness = new Thickness(1),
        };
        chip.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        chip.SetResourceReference(Border.BackgroundProperty, "Bg200Brush");
        return chip;
    }

    private static string StatusLabel(ModelTrafficSnapshot call) => call.State switch
    {
        ModelTrafficState.InFlight => "In flight",
        ModelTrafficState.Streaming => call.StatusCode is { } code ? $"{code} streaming" : "Streaming",
        ModelTrafficState.Interrupted => call.StatusCode is { } code ? $"{code} stopped" : "Stopped",
        ModelTrafficState.Failed => "Failed",
        _ => call.StatusCode is { } status ? $"{status} {call.ReasonPhrase}".TrimEnd() : "Done",
    };

    private static string StatusBrush(ModelTrafficSnapshot call) => call switch
    {
        { State: ModelTrafficState.Failed } => "Danger000Brush",
        { StatusCode: >= 400 } => "Danger000Brush",
        { State: ModelTrafficState.Interrupted } => "Warning000Brush",
        { IsRunning: true } => "Accent000Brush",
        _ => "Success000Brush",
    };

    private static string Duration(ModelTrafficSnapshot call)
        => call.Elapsed is { } elapsed ? Format(elapsed) : "";

    private static string Format(TimeSpan span) => span.TotalSeconds >= 1
        ? $"{span.TotalSeconds:0.0}s"
        : $"{span.TotalMilliseconds:0}ms";

    internal static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        1 => "1 byte",
        _ => $"{bytes} bytes",
    };

    /// <summary>
    /// Pretty-prints a body that is one JSON document; SSE streams and truncated bodies
    /// are left exactly as they arrived.
    /// </summary>
    internal static string Pretty(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
        {
            return body;
        }

        try
        {
            return JsonNode.Parse(body) is { } node ? RequestPreviewBuilder.Format(node) : body;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            return body;
        }
    }

    private static TextBlock Text(string text, double size, string brushKey, bool semibold = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    private static TextBlock Mono(string text, double size, string brushKey)
    {
        var block = Text(text, size, brushKey);
        block.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
        return block;
    }
}
