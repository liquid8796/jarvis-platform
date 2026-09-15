using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using JarvisCode.App.Services;
using JarvisCode.Core.Providers;

namespace JarvisCode.App.Controls;

/// <summary>
/// The composer's status dot opens this: the exact call the session's next model
/// turn would make — method, URL, headers with the credential redacted, and the JSON
/// payload — with the payload editable. Applying an edit stores it as a merge patch
/// (<see cref="RequestBodyOverride"/>) that rides every request of that session until
/// it is reset, which is the only shape that survives a turn's many model calls.
/// </summary>
public sealed class RequestInspectorPopup
{
    private const double PopupWidth = 640;
    private const double EditorMaxHeight = 420;

    private readonly Popup _popup;
    private readonly Func<string?> _sessionId;
    private readonly Func<RequestPreview> _build;
    private readonly Action? _overrideChanged;

    private RequestPreview? _preview;
    private TextBox? _editor;
    private TextBlock? _status;
    private TextBlock? _size;
    private Button? _reset;
    private string _appliedText = "";
    private bool _headersExpanded;

    /// <param name="overrideChanged">
    /// Raised after an edit is applied or reset, so the composer's dot can show that
    /// this session's requests are being rewritten.
    /// </param>
    public RequestInspectorPopup(
        UIElement placementTarget,
        Func<string?> sessionId,
        Func<RequestPreview> build,
        Action? overrideChanged = null)
    {
        _sessionId = sessionId;
        _build = build;
        _overrideChanged = overrideChanged;
        _popup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Custom,
            StaysOpen = false,
            AllowsTransparency = true,
            // Right-aligned above the dot, like the context-window popup next to it.
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            [
                new CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 6),
                    PopupPrimaryAxis.Vertical),
            ],
        };
        _popup.Closed += (_, _) => StashDraft();
    }

    public bool IsOpen => _popup.IsOpen;

    public void Toggle()
    {
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
        }
        else
        {
            Show();
        }
    }

    public void Show()
    {
        // The previous open's controls are about to be replaced; dropping them now
        // keeps a stale editor from being read while the new preview is assembled.
        _editor = null;
        _status = null;
        _size = null;
        _reset = null;
        _popup.Child = Card(Message("Building the request…", "Text400Brush"));
        _popup.IsOpen = true;

        // Assembling the turn reads plugins, skills and hooks off disk; paint the
        // shell first so the popup never opens as a frozen rectangle.
        _ = _popup.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_popup.IsOpen)
            {
                return;
            }

            RequestPreview preview;
            try
            {
                preview = _build();
            }
            catch (Exception ex)
            {
                // Shown rather than swallowed: an inspector that fails silently is
                // worse than one that says why, and this must not take the app down.
                preview = RequestPreview.NotAvailable($"Could not build the request — {ex.Message}");
            }

            _preview = preview;
            _popup.Child = preview.Unavailable is { } reason
                ? Card(Message(reason, "Text300Brush"))
                : Card(BuildBody(preview));
            if (_editor is not null)
            {
                _ = Keyboard.Focus(_editor);
                _editor.CaretIndex = 0;
            }
        });
    }

    // ---------------- layout ----------------

    private FrameworkElement BuildBody(RequestPreview preview)
    {
        var stack = new StackPanel { Width = PopupWidth };
        stack.Children.Add(Header(preview));
        stack.Children.Add(UrlRow(preview));
        stack.Children.Add(HeadersSection(preview));
        stack.Children.Add(Separator());
        stack.Children.Add(PayloadHeader(preview));
        stack.Children.Add(Editor(preview));
        stack.Children.Add(StatusLine());
        stack.Children.Add(Footer());

        stack.PreviewKeyDown += OnKeyDown;
        RenderStatus();
        return stack;
    }

    private static FrameworkElement Header(RequestPreview preview)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Text("Request", 12.5, "Text300Brush"));
        var provider = Text(preview.ProviderName, 12.5, "Text100Brush", semibold: true);
        Grid.SetColumn(provider, 1);
        row.Children.Add(provider);
        return row;
    }

    private static FrameworkElement UrlRow(RequestPreview preview)
    {
        var text = Text($"{preview.Method}  {preview.Url}", 11.5, "Text400Brush");
        text.TextWrapping = TextWrapping.Wrap;
        text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
        text.Margin = new Thickness(0, 0, 0, 8);
        return text;
    }

    private FrameworkElement HeadersSection(RequestPreview preview)
    {
        var stack = new StackPanel();
        var toggle = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Content = Text(
                $"{(_headersExpanded ? "⌄" : "›")}  Headers ({preview.Headers.Count})", 12, "Text400Brush"),
        };
        toggle.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
        AutomationProperties.SetName(toggle, "Toggle request headers");
        toggle.Click += (_, _) =>
        {
            _headersExpanded = !_headersExpanded;
            if (_preview is { Unavailable: null } current)
            {
                var text = _editor?.Text;
                _popup.Child = Card(BuildBody(current));
                if (text is not null && _editor is not null)
                {
                    _editor.Text = text;
                }
            }
        };
        stack.Children.Add(toggle);

        if (_headersExpanded)
        {
            var rows = new StackPanel { Margin = new Thickness(4, 4, 0, 0) };
            foreach (var (name, value) in preview.Headers)
            {
                var line = Text($"{name}: {value}", 11.5, "Text500Brush");
                line.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFontFamily");
                line.TextWrapping = TextWrapping.Wrap;
                line.Margin = new Thickness(0, 1, 0, 1);
                rows.Children.Add(line);
            }

            stack.Children.Add(rows);
        }

        stack.Margin = new Thickness(0, 0, 0, 4);
        return stack;
    }

    private static FrameworkElement Separator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 10) };
        line.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        return line;
    }

    private FrameworkElement PayloadHeader(RequestPreview preview)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Text("Payload", 12.5, "Text200Brush", semibold: true));
        _size = Text(FormatSize(preview.EffectiveJson), 11.5, "Text500Brush");
        Grid.SetColumn(_size, 1);
        row.Children.Add(_size);
        return row;
    }

    private FrameworkElement Editor(RequestPreview preview)
    {
        var draft = _sessionId() is { } id ? RequestOverrides.Draft(id) : null;
        _appliedText = preview.EffectiveJson;
        _editor = new TextBox
        {
            Text = draft ?? preview.EffectiveJson,
            AcceptsReturn = true,
            AcceptsTab = true,
            // Wrapped, not editor-style: the system prompt is one JSON string tens of
            // thousands of characters long, and unwrapped WPF force-breaks such a line
            // into segments it then clips — the field becomes unreadable. Wrapping
            // costs the indentation of long lines and keeps every character reachable.
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 220,
            MaxHeight = EditorMaxHeight,
            FontSize = 11.5,
            Padding = new Thickness(8, 6, 8, 6),
        };
        _editor.SetResourceReference(FrameworkElement.StyleProperty, "CodeEditorTextBox");
        _editor.SetResourceReference(Control.FontFamilyProperty, "MonoFontFamily");
        AutomationProperties.SetName(_editor, "Request payload");
        _editor.TextChanged += (_, _) => RenderStatus();
        return _editor;
    }

    private FrameworkElement StatusLine()
    {
        _status = Text("", 11.5, "Text500Brush");
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        return _status;
    }

    private FrameworkElement Footer()
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _reset = new Button { Content = "Reset to default", HorizontalAlignment = HorizontalAlignment.Left };
        _reset.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        _reset.Click += (_, _) => OnReset();
        AutomationProperties.SetName(_reset, "Reset the payload to the default");
        row.Children.Add(_reset);

        var apply = new Button { Content = "Apply" };
        apply.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        apply.Click += (_, _) => OnApply();
        apply.ToolTip = "Ctrl+Enter";
        AutomationProperties.SetName(apply, "Apply the edited payload");
        Grid.SetColumn(apply, 1);
        row.Children.Add(apply);
        return row;
    }

    private static FrameworkElement Message(string text, string brushKey)
    {
        var block = Text(text, 12.5, brushKey);
        block.TextWrapping = TextWrapping.Wrap;
        block.Width = PopupWidth;
        return block;
    }

    private static Border Card(FrameworkElement child)
    {
        var card = new Border
        {
            Child = child,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg000Brush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderMidBrush");
        return card;
    }

    // ---------------- behavior ----------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _popup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Return &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            OnApply();
            e.Handled = true;
        }
    }

    private void OnApply()
    {
        if (_preview is not { Unavailable: null } preview || _editor is null || _sessionId() is not { } sessionId)
        {
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(_editor.Text);
        }
        catch (JsonException ex)
        {
            RenderStatus($"Invalid JSON — {ex.Message}", isError: true);
            return;
        }

        if (parsed is not JsonObject edited)
        {
            RenderStatus("The payload must be a JSON object.", isError: true);
            return;
        }

        var patch = RequestBodyOverride.Diff(preview.Baseline, edited);
        RequestOverrides.Set(sessionId, patch);
        RequestOverrides.SetDraft(sessionId, null);
        _appliedText = _editor.Text;
        _preview = preview with
        {
            EffectiveJson = _editor.Text,
            OverriddenKeys = RequestBodyOverride.TouchedKeys(patch),
        };
        RenderStatus();
        _overrideChanged?.Invoke();
    }

    private void OnReset()
    {
        if (_preview is not { Unavailable: null } preview || _editor is null || _sessionId() is not { } sessionId)
        {
            return;
        }

        RequestOverrides.Clear(sessionId);
        RequestOverrides.SetDraft(sessionId, null);
        _preview = preview with { EffectiveJson = preview.BaselineJson, OverriddenKeys = [] };
        _appliedText = preview.BaselineJson;
        _editor.Text = preview.BaselineJson;
        RenderStatus();
        _overrideChanged?.Invoke();
    }

    private void StashDraft()
    {
        if (_editor is null || _sessionId() is not { } sessionId)
        {
            return;
        }

        RequestOverrides.SetDraft(sessionId, _editor.Text == _appliedText ? null : _editor.Text);
    }

    private void RenderStatus() => RenderStatus(null, isError: false);

    private void RenderStatus(string? message, bool isError)
    {
        if (_status is null || _preview is not { Unavailable: null } preview)
        {
            return;
        }

        bool dirty = _editor is not null && _editor.Text != _appliedText;
        if (_reset is not null)
        {
            _reset.IsEnabled = preview.HasOverride || dirty;
        }

        if (_size is not null && _editor is not null)
        {
            // Follows the editor, not the last applied body: a size that disagrees
            // with what is on screen is just a small lie.
            _size.Text = FormatSize(_editor.Text);
        }

        if (message is not null)
        {
            _status.Text = message;
            _status.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Danger100Brush" : "Text400Brush");
            return;
        }

        if (dirty)
        {
            _status.Text = "Unsaved edits — Apply (Ctrl+Enter) to send them.";
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Text400Brush");
            return;
        }

        if (preview.HasOverride)
        {
            var keys = string.Join(", ", preview.OverriddenKeys);
            var critical = RequestPreviewBuilder.CriticalKeys(preview.OverriddenKeys);
            _status.Text = critical.Count > 0
                ? $"Override active on {keys} — every call this session sends your copy of " +
                  $"{string.Join(", ", critical)}, which the agent loop can no longer update."
                : $"Override active on {keys} — applied to every request this session until you reset it.";
            // Red is reserved for something to act on — a parse error or a pinned
            // key the loop can no longer update. A plain override is information.
            _status.SetResourceReference(TextBlock.ForegroundProperty,
                critical.Count > 0 ? "Danger100Brush" : "Text200Brush");
            return;
        }

        _status.Text = "Sent as shown; your next prompt is appended to messages. Edit any field, then Apply.";
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Text500Brush");
    }

    /// <summary>Wire size, so a payload of Vietnamese text is not reported as ASCII.</summary>
    private static string FormatSize(string json)
    {
        int bytes = System.Text.Encoding.UTF8.GetByteCount(json);
        return bytes < 1024 ? $"{bytes} B · JSON" : $"{bytes / 1024.0:0.#} KB · JSON";
    }

    private static TextBlock Text(string content, double size, string brushKey, bool semibold = false)
    {
        var text = new TextBlock
        {
            Text = content,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }
}
