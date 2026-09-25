using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
using JarvisCode.App.Services;

namespace Jarvis.Agent.Windows;

/// <summary>
/// Codex Desktop's unified Windows computer-use surface: window discovery, accessibility state,
/// activation, application launch, and window-relative mouse/keyboard/accessibility actions.
/// </summary>
public sealed class CodexComputerUseTool(
    UiSettingsStore settings,
    ComputerUseService service,
    IAgentTool requestAccess,
    IAgentTool openApplication) : IAgentTool
{
    private readonly ComputerUseObservationStore<AutomationElement> _observations = new();

    public ToolDescriptor Descriptor { get; } = new(
        "computer_use.computer_use",
        "computer_use",
        "computer",
        "Control Windows applications through observation-bound window handles. get_window_state returns an observation_id and optional screenshot_id; every input action must consume the current observation, and coordinate actions must also present its matching screenshot_id. Optional post-action state refresh prevents blind retries. Supported actions: list_windows, get_window, list_apps, launch_app, get_window_state, click, press_key, type_text, scroll, set_value, drag, perform_secondary_action, and activate_window. Local app grants, denied-app policy, Arm/Pause, approval and Full permission remain enforced.",
        Schema(),
        ReadOnly: false,
        Sensitive: true);

    public async Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken ct)
    {
        using var consent = ToolConsentScope.Enter(context.FullPermission, ct);
        try
        {
            var action = arguments.GetProperty("action").GetString() ?? "";
            return action switch
            {
                "list_windows" => Reply(new { windows = EnumerateWindows() }),
                "get_window" => Reply(GetWindow(ResolveWindow(arguments))),
                "list_apps" => Reply(new { apps = ListApps() }),
                "launch_app" => await LaunchAppAsync(arguments, context, ct).ConfigureAwait(false),
                "get_window_state" => await GetWindowStateAsync(arguments, context, ct).ConfigureAwait(false),
                "activate_window" => await ActivateAsync(arguments, context, ct).ConfigureAwait(false),
                "click" => await ClickAsync(arguments, context, ct).ConfigureAwait(false),
                "press_key" => await PressKeyAsync(arguments, context, ct).ConfigureAwait(false),
                "type_text" => await TypeTextAsync(arguments, context, ct).ConfigureAwait(false),
                "scroll" => await ScrollAsync(arguments, context, ct).ConfigureAwait(false),
                "set_value" => await SetValueAsync(arguments, context, ct).ConfigureAwait(false),
                "drag" => await DragAsync(arguments, context, ct).ConfigureAwait(false),
                "perform_secondary_action" => await SecondaryActionAsync(arguments, context, ct).ConfigureAwait(false),
                _ => ToolReply.Error("Unknown computer_use action.")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or COMException)
        {
            return ToolReply.Error(ex.Message);
        }
    }

    private async Task<ToolReply> LaunchAppAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var app = RequiredString(args, "app_identifier");
        var access = await EnsureAccessAsync(app, InputNeed.Keyboard, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        var reply = await openApplication.ExecuteAsync(WireJson.Element(new { app }), context, ct).ConfigureAwait(false);
        if (reply.IsError) return reply;
        for (var i = 0; i < 40; i++)
        {
            var window = EnumerateWindows().FirstOrDefault(w => MatchesApp(w.App, app));
            if (window is not null) return Reply(new { window, launch = JsonDocument.Parse(reply.Text).RootElement.Clone() });
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return new ToolReply(JsonSerializer.Serialize(new { launched = app, window = (object?)null, message = reply.Text }, WireJson.Options));
    }

    private async Task<ToolReply> ActivateAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Pointer, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        _ = BeginAction(args, context, window, requireScreenshot: false);
        Activate(window.Handle);
        return await FinishActionAsync(new { activated = GetWindow(window) }, args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> GetWindowStateAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Pointer, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        var includeScreenshot = !args.TryGetProperty("include_screenshot", out var screenshotNode) || screenshotNode.GetBoolean();
        var includeText = args.TryGetProperty("include_text", out var textNode) && textNode.GetBoolean();
        var maxNodes = args.TryGetProperty("max_nodes", out var maxNodesNode) ? maxNodesNode.GetInt32() : 500;
        var capture = await CaptureObservationAsync(window, context, includeScreenshot, includeText, maxNodes, ct).ConfigureAwait(false);
        return new ToolReply(capture.Body.GetRawText(), Images: capture.Image is null ? null : [capture.Image]);
    }

    private async Task<ObservationCapture> CaptureObservationAsync(WindowRef window, AgentExecutionContext context,
        bool includeScreenshot, bool includeText, int maxNodes, CancellationToken ct)
    {
        if (maxNodes is < 1 or > 1000) throw new ArgumentException("max_nodes must be 1..1000.");
        Activate(window.Handle);
        await Task.Delay(100, ct).ConfigureAwait(false);
        var rect = RectFor(window.Handle);
        IReadOnlyList<ElementRow> rows = [];
        bool truncated = false;
        AutomationElement? root = null;
        if (includeText)
        {
            (root, rows, truncated) = ReadElements(window.Handle, rect, maxNodes);
        }

        WireImage? image = null;
        string? mime = null;
        string? base64 = null;
        string? captureBackend = null;
        if (includeScreenshot)
        {
            (mime, base64, captureBackend) = Capture(window.Handle, rect);
            image = new WireImage(mime, base64);
        }

        var observation = _observations.Capture(context.IsolationScopeId, window.Handle, rect,
            rows.Select(row => row.Element).ToArray(), includeScreenshot);
        var tree = includeText ? rows.Select(row => new
        {
            element_index = row.Index,
            role = row.Role,
            name = row.Name,
            value = row.Value,
            bounds = row.Bounds,
            enabled = row.Enabled,
            offscreen = row.Offscreen,
            focused = row.Focused,
            selected = row.Selected
        }).ToArray() : null;
        var focused = includeText ? FocusedElement(rows) : null;
        var selectedText = includeText && root is not null ? ReadSelectedText(root, rows) : null;
        var documentText = includeText && root is not null ? ReadDocumentText(root, rows) : null;
        var selectedElements = includeText ? rows.Where(row => row.Selected).Select(row => row.Index).ToArray() : null;
        var body = WireJson.Element(new
        {
            observation_id = observation.ObservationId,
            screenshot_id = observation.ScreenshotId,
            generation = observation.Generation,
            observed_at = observation.ObservedAt,
            window = GetWindow(window),
            bounds = new[] { rect.Left, rect.Top, rect.Width, rect.Height },
            screenshot = includeScreenshot ? new
            {
                screenshot_id = observation.ScreenshotId,
                mime_type = mime,
                width = rect.Width,
                height = rect.Height,
                capture_backend = captureBackend,
                image_url = $"data:{mime};base64,{base64}"
            } : null,
            accessibility = includeText ? new
            {
                element_tree = tree,
                focused_element = focused,
                selected_text = selectedText,
                selected_elements = selectedElements,
                document_text = documentText
            } : null,
            truncated
        });
        return new(observation, body, image);
    }

    private async Task<ToolReply> ClickAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Click, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        var coordinate = args.TryGetProperty("coordinate", out _);
        var observation = BeginAction(args, context, window, requireScreenshot: coordinate);
        Activate(window.Handle);
        var point = PointFor(args, observation, required: true);
        service.Click(point, "left", 1, []);
        return await FinishActionAsync(new { clicked = new[] { point.X, point.Y }, window = GetWindow(window) },
            args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> PressKeyAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Keyboard, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        _ = BeginAction(args, context, window, requireScreenshot: false);
        Activate(window.Handle);
        var key = RequiredString(args, "key");
        var modifiers = args.TryGetProperty("modifiers", out var modifierNode)
            ? modifierNode.EnumerateArray().Select(node => node.GetString() ?? "").Where(text => text.Length > 0).ToArray()
            : [];
        var chord = string.Join('+', modifiers.Append(key));
        if (service.PressChord(chord) is { } error) return ToolReply.Error(error);
        return await FinishActionAsync(new { pressed = chord, window = GetWindow(window) },
            args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> TypeTextAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Keyboard, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        _ = BeginAction(args, context, window, requireScreenshot: false);
        Activate(window.Handle);
        var text = RequiredString(args, "text", allowEmpty: true);
        if (text.Length > 65536) throw new ArgumentException("text exceeds 65536 characters.");
        service.TypeText(text);
        return await FinishActionAsync(new { typed = text.Length, window = GetWindow(window) },
            args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> ScrollAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Click, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        if (!args.TryGetProperty("coordinate", out _))
            throw new ArgumentException("scroll requires coordinate and the matching screenshot_id from get_window_state.");
        var observation = BeginAction(args, context, window, requireScreenshot: true);
        Activate(window.Handle);
        var point = PointFor(args, observation, required: true);
        var dx = args.TryGetProperty("delta_x", out var x) ? x.GetDouble() : 0;
        var dy = args.TryGetProperty("delta_y", out var y) ? y.GetDouble() : 0;
        if (dx == 0 && dy == 0) throw new ArgumentException("scroll requires delta_x or delta_y.");
        static int Wheel(double delta) => delta == 0 ? 0 : Math.Sign(delta) * Math.Max(1, (int)Math.Round(Math.Abs(delta) / 120d));
        service.Scroll(point, -Wheel(dx), -Wheel(dy));
        return await FinishActionAsync(new { scrolled = new { delta_x = dx, delta_y = dy }, window = GetWindow(window) },
            args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> SetValueAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Keyboard, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        var observation = BeginAction(args, context, window, requireScreenshot: false);
        Activate(window.Handle);
        var element = ElementFor(args, observation);
        var value = RequiredString(args, "value", allowEmpty: true);
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern setter)
            return ToolReply.Error("The selected element does not support ValuePattern. Refresh window state or use click plus type_text.");
        setter.SetValue(value);
        return await FinishActionAsync(new { element_index = args.GetProperty("element_index").GetInt32(), value, window = GetWindow(window) },
            args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> DragAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.FullMouse, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        var observation = BeginAction(args, context, window, requireScreenshot: true);
        Activate(window.Handle);
        if (!args.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("drag requires path with at least two [x,y] points.");
        var points = pathNode.EnumerateArray().Select(ReadCoordinate).ToArray();
        if (points.Length < 2 || points.Length > 256) throw new ArgumentException("drag path must contain 2..256 points.");
        foreach (var point in points)
            if (point.X < 0 || point.Y < 0 || point.X >= observation.Bounds.Width || point.Y >= observation.Bounds.Height)
                throw new ArgumentException("drag path contains a point outside the observed window bounds.");
        var from = new System.Drawing.Point(observation.Bounds.Left + points[0].X, observation.Bounds.Top + points[0].Y);
        var to = new System.Drawing.Point(observation.Bounds.Left + points[^1].X, observation.Bounds.Top + points[^1].Y);
        service.Drag(from, to);
        return await FinishActionAsync(new { dragged = points.Length, window = GetWindow(window) },
            args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply> SecondaryActionAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.FullMouse, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        var observation = BeginAction(args, context, window, requireScreenshot: false);
        Activate(window.Handle);
        var element = ElementFor(args, observation);
        var action = RequiredString(args, "secondary_action").ToLowerInvariant();
        switch (action)
        {
            case "invoke" when element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke): ((InvokePattern)invoke).Invoke(); break;
            case "expand" when element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand): ((ExpandCollapsePattern)expand).Expand(); break;
            case "collapse" when element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var collapse): ((ExpandCollapsePattern)collapse).Collapse(); break;
            case "toggle" when element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle): ((TogglePattern)toggle).Toggle(); break;
            case "select" when element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select): ((SelectionItemPattern)select).Select(); break;
            case "focus": element.SetFocus(); break;
            case "scroll_into_view" when element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll): ((ScrollItemPattern)scroll).ScrollIntoView(); break;
            default: return ToolReply.Error("The selected element does not support secondary_action '" + action + "'.");
        }
        return await FinishActionAsync(new { secondary_action = action, element_index = args.GetProperty("element_index").GetInt32(), window = GetWindow(window) },
            args, context, window, ct).ConfigureAwait(false);
    }

    private async Task<ToolReply?> EnsureAccessAsync(string app, InputNeed need, AgentExecutionContext context, CancellationToken ct)
    {
        if (ComputerUseGrants.RefusalFor(settings.Current, context.SessionId, app, need) is null) return null;
        var request = await requestAccess.ExecuteAsync(WireJson.Element(new
        {
            apps = new[] { app },
            reason = "Use computer_use to inspect and control " + app
        }), context, ct).ConfigureAwait(false);
        if (request.IsError) return request;
        var refusal = ComputerUseGrants.RefusalFor(settings.Current, context.SessionId, app, need);
        return refusal is null ? null : ToolReply.Error(refusal);
    }

    private ComputerUseObservation<AutomationElement> BeginAction(JsonElement args, AgentExecutionContext context,
        WindowRef window, bool requireScreenshot)
    {
        var observationId = RequiredString(args, "observation_id");
        var screenshotId = args.TryGetProperty("screenshot_id", out var screenshotNode) &&
                           screenshotNode.ValueKind == JsonValueKind.String
            ? screenshotNode.GetString()
            : null;
        var observation = _observations.RequireCurrent(context.IsolationScopeId, window.Handle, observationId,
            RectFor(window.Handle), screenshotId, requireScreenshot);
        // Consume before dispatch. Even a later input or refresh failure leaves the outcome uncertain,
        // so callers must observe again rather than replaying against stale pixels/elements.
        _observations.Consume(observation);
        return observation;
    }

    private async Task<ToolReply> FinishActionAsync(object actionResult, JsonElement args,
        AgentExecutionContext context, WindowRef window, CancellationToken ct)
    {
        var returnState = args.TryGetProperty("return_state", out var returnNode)
            ? returnNode.GetString() ?? "none"
            : "none";
        if (returnState == "none") return Reply(actionResult);
        var (includeScreenshot, includeText) = returnState switch
        {
            "accessibility" => (false, true),
            "screenshot" => (true, false),
            "full" => (true, true),
            _ => throw new ArgumentException("return_state must be none, accessibility, screenshot, or full.")
        };
        var maxNodes = args.TryGetProperty("max_nodes", out var maxNodesNode) ? maxNodesNode.GetInt32() : 500;
        var capture = await CaptureObservationAsync(window, context, includeScreenshot, includeText, maxNodes, ct)
            .ConfigureAwait(false);
        var body = WireJson.Element(new { action_result = actionResult, state = capture.Body });
        return new ToolReply(body.GetRawText(), Images: capture.Image is null ? null : [capture.Image]);
    }

    private static System.Drawing.Point PointFor(JsonElement args,
        ComputerUseObservation<AutomationElement> observation, bool required)
    {
        if (args.TryGetProperty("element_index", out _))
        {
            var element = ElementFor(args, observation);
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty) throw new InvalidOperationException("The selected element has no clickable bounds. Refresh window state.");
            return new((int)Math.Round(bounds.Left + bounds.Width / 2), (int)Math.Round(bounds.Top + bounds.Height / 2));
        }
        if (args.TryGetProperty("coordinate", out var coordinate))
        {
            var relative = ReadCoordinate(coordinate);
            var rect = observation.Bounds;
            if (relative.X < 0 || relative.Y < 0 || relative.X >= rect.Width || relative.Y >= rect.Height)
                throw new ArgumentException("coordinate is outside the observed window bounds.");
            return new(rect.Left + relative.X, rect.Top + relative.Y);
        }
        if (required) throw new ArgumentException("Provide element_index or coordinate.");
        return new(observation.Bounds.Left + observation.Bounds.Width / 2,
            observation.Bounds.Top + observation.Bounds.Height / 2);
    }

    private static AutomationElement ElementFor(JsonElement args,
        ComputerUseObservation<AutomationElement> observation)
    {
        if (!args.TryGetProperty("element_index", out var indexNode)) throw new ArgumentException("element_index is required.");
        var index = indexNode.GetInt32();
        if (index < 0 || index >= observation.Elements.Count)
            throw new ArgumentException("element_index is outside the supplied observation's element tree.");
        var element = observation.Elements[index];
        try { _ = element.Current.ProcessId; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new AgentRequestException("STALE_OBSERVATION", "The selected element no longer exists. Call get_window_state again.");
        }
        return element;
    }

    private static (AutomationElement Root, IReadOnlyList<ElementRow> Rows, bool Truncated) ReadElements(
        nint handle, System.Drawing.Rectangle window, int maxNodes)
    {
        var root = AutomationElement.FromHandle(handle) ?? throw new InvalidOperationException("Windows UI Automation cannot inspect this window.");
        var result = new List<ElementRow>();
        var queue = new Queue<AutomationElement>();
        var focusedKey = FocusedRuntimeKey();
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (root.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection) && selection is SelectionPattern selected)
                foreach (var element in selected.Current.GetSelection())
                    if (RuntimeKey(element) is { } key) selectedKeys.Add(key);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        queue.Enqueue(root);
        while (queue.Count > 0 && result.Count < maxNodes)
        {
            var element = queue.Dequeue();
            try
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                string? value = null;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern values) value = values.Current.Value;
                var runtimeKey = RuntimeKey(element);
                var selected = runtimeKey is not null && selectedKeys.Contains(runtimeKey);
                if (!selected && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionItem) &&
                    selectionItem is SelectionItemPattern item)
                    selected = item.Current.IsSelected;
                result.Add(new(result.Count, element, current.ControlType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal) ?? "Unknown",
                    current.Name ?? "", value,
                    bounds.IsEmpty ? null : new[] { (int)Math.Round(bounds.Left - window.Left), (int)Math.Round(bounds.Top - window.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height) },
                    current.IsEnabled, current.IsOffscreen,
                    runtimeKey is not null && StringComparer.Ordinal.Equals(runtimeKey, focusedKey), selected));
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                while (child is not null)
                {
                    queue.Enqueue(child);
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        }
        return (root, result, queue.Count > 0);
    }

    private static object? FocusedElement(IReadOnlyList<ElementRow> rows)
    {
        var row = rows.FirstOrDefault(candidate => candidate.Focused);
        return row is null ? null : new
        {
            element_index = row.Index,
            role = row.Role,
            name = row.Name,
            value = row.Value,
            bounds = row.Bounds
        };
    }

    private static string? ReadSelectedText(AutomationElement root, IReadOnlyList<ElementRow> rows)
    {
        var candidates = rows.Where(row => row.Focused).Select(row => row.Element).Append(root);
        foreach (var element in candidates)
        {
            try
            {
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern text) continue;
                var selected = string.Join(Environment.NewLine, text.GetSelection().Select(range => range.GetText(16_384)));
                if (!string.IsNullOrEmpty(selected)) return Clip(selected, 16_384);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        }
        return null;
    }

    private static string? ReadDocumentText(AutomationElement root, IReadOnlyList<ElementRow> rows)
    {
        foreach (var element in new[] { root }.Concat(rows.Where(row => row.Focused).Select(row => row.Element)))
        {
            try
            {
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern text) continue;
                var value = text.DocumentRange.GetText(50_000);
                if (!string.IsNullOrWhiteSpace(value)) return Clip(value, 50_000);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { }
        }
        var fallback = string.Join(Environment.NewLine, rows.Select(row =>
            string.IsNullOrWhiteSpace(row.Value) ? row.Name : row.Name + ": " + row.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(fallback) ? null : Clip(fallback, 50_000);
    }

    private static string? RuntimeKey(AutomationElement? element)
    {
        if (element is null) return null;
        try { return string.Join('.', element.GetRuntimeId()); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return null; }
    }

    private static string? FocusedRuntimeKey()
    {
        try { return RuntimeKey(AutomationElement.FocusedElement); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException) { return null; }
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private static (string Mime, string Base64, string Backend) Capture(nint handle, System.Drawing.Rectangle rect)
    {
        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var backend = "print_window";
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            bool printed;
            try { printed = PrintWindow(handle, hdc, 2); } // PW_RENDERFULLCONTENT
            finally { graphics.ReleaseHdc(hdc); }
            if (!printed || IsBlankCapture(bitmap))
            {
                backend = "desktop_copy";
                graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, rect.Size,
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }
        }
        using var output = new MemoryStream();
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return ("image/png", Convert.ToBase64String(output.ToArray()), backend);
    }

    private static bool IsBlankCapture(System.Drawing.Bitmap bitmap)
    {
        var points = new[]
        {
            new System.Drawing.Point(0, 0),
            new System.Drawing.Point(Math.Max(0, bitmap.Width - 1), 0),
            new System.Drawing.Point(0, Math.Max(0, bitmap.Height - 1)),
            new System.Drawing.Point(Math.Max(0, bitmap.Width - 1), Math.Max(0, bitmap.Height - 1)),
            new System.Drawing.Point(bitmap.Width / 2, bitmap.Height / 2)
        };
        return points.Select(point => bitmap.GetPixel(point.X, point.Y).ToArgb()).All(argb => argb is 0 or -16777216);
    }

    private static IReadOnlyList<WindowInfo> EnumerateWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0) return true;
            try
            {
                var window = GetWindow(new WindowRef(AppFor(handle), handle, TitleFor(handle)));
                if (window.Bounds[2] > 0 && window.Bounds[3] > 0) result.Add(window);
            }
            catch (Exception) { }
            return true;
        }, nint.Zero);
        return result.OrderBy(window => window.App, StringComparer.OrdinalIgnoreCase).ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<object> ListApps()
    {
        var running = EnumerateWindows().GroupBy(window => window.App, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { identifier = group.Key, running = true, windows = group.Count() });
        var shortcuts = StartMenuRoots().Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new { identifier = name, running = false, windows = 0 });
        return running.Cast<object>().Concat(shortcuts).GroupBy(item => JsonSerializer.Serialize(item), StringComparer.Ordinal).Select(group => group.First()).Take(1000).ToArray();
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    }

    private static WindowRef ResolveWindow(JsonElement args)
    {
        if (args.TryGetProperty("window", out var windowNode) && windowNode.ValueKind == JsonValueKind.Object)
        {
            var id = windowNode.GetProperty("id").GetInt64();
            var app = windowNode.TryGetProperty("app", out var appNode) ? appNode.GetString() ?? "" : "";
            var title = windowNode.TryGetProperty("title", out var titleNode) ? titleNode.GetString() : null;
            var reference = new WindowRef(app, (nint)id, title);
            ValidateWindow(reference);
            return reference with { App = AppFor(reference.Handle), Title = TitleFor(reference.Handle) };
        }
        if (args.TryGetProperty("id", out var idNode))
        {
            var handle = (nint)idNode.GetInt64();
            var reference = new WindowRef(AppFor(handle), handle, TitleFor(handle));
            ValidateWindow(reference);
            return reference;
        }
        throw new ArgumentException("window or id is required for this action.");
    }

    private static WindowInfo GetWindow(WindowRef reference)
    {
        ValidateWindow(reference);
        var rect = RectFor(reference.Handle);
        return new(reference.App, reference.Handle.ToInt64(), TitleFor(reference.Handle), new[] { rect.Left, rect.Top, rect.Width, rect.Height }, IsIconic(reference.Handle));
    }

    private static void ValidateWindow(WindowRef window)
    {
        if (window.Handle == nint.Zero || !IsWindow(window.Handle) || !IsWindowVisible(window.Handle))
            throw new ArgumentException("Window handle is no longer valid. Call list_windows again.");
        if (window.App.Length > 0 && !MatchesApp(AppFor(window.Handle), window.App))
            throw new ArgumentException("Window handle does not belong to the requested application.");
    }

    private static bool MatchesApp(string actual, string requested) =>
        actual.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileNameWithoutExtension(actual).Equals(Path.GetFileNameWithoutExtension(requested), StringComparison.OrdinalIgnoreCase);

    private static string AppFor(nint handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        using var process = Process.GetProcessById((int)processId);
        return process.ProcessName;
    }

    private static string TitleFor(nint handle)
    {
        var length = GetWindowTextLength(handle);
        var text = new StringBuilder(length + 1);
        _ = GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private static System.Drawing.Rectangle RectFor(nint handle)
    {
        if (!GetWindowRect(handle, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            throw new InvalidOperationException("Window bounds are unavailable.");
        return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static void Activate(nint handle)
    {
        if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
        if (!SetForegroundWindow(handle)) throw new InvalidOperationException("Windows refused to activate the requested window.");
    }

    private static System.Drawing.Point ReadCoordinate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("Coordinates must be [x,y].");
        var items = value.EnumerateArray().ToArray();
        if (items.Length != 2) throw new ArgumentException("Coordinates must contain exactly two numbers.");
        return new(items[0].GetInt32(), items[1].GetInt32());
    }

    private static string RequiredString(JsonElement args, string name, bool allowEmpty = false)
    {
        if (!args.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            throw new ArgumentException(name + " is required.");
        var text = node.GetString() ?? "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw new ArgumentException(name + " is required.");
        return text;
    }

    private static ToolReply Reply(object value) => new(JsonSerializer.Serialize(value, WireJson.Options));

    private static JsonElement Schema() => WireJson.Element(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["action"] = new { type = "string", @enum = new[] { "list_windows", "get_window", "list_apps", "launch_app", "get_window_state", "click", "press_key", "type_text", "scroll", "set_value", "drag", "perform_secondary_action", "activate_window" } },
            ["window"] = new { type = "object", properties = new { app = new { type = "string" }, id = new { type = "integer" }, title = new { type = "string" } }, required = new[] { "app", "id" }, additionalProperties = false },
            ["id"] = new { type = "integer", description = "Native window ID from list_windows." },
            ["app_identifier"] = new { type = "string", minLength = 1, maxLength = 260 },
            ["observation_id"] = new { type = "string", pattern = "^obs_[a-f0-9]{32}$", description = "Current observation ID returned by get_window_state. Required for every input action and consumed on the first action attempt." },
            ["screenshot_id"] = new { type = "string", pattern = "^shot_[a-f0-9]{32}$", description = "Screenshot ID from the same observation. Required for coordinate, scroll, and drag actions." },
            ["include_screenshot"] = new { type = "boolean", @default = true, description = "get_window_state only: include and display a fresh screenshot." },
            ["include_text"] = new { type = "boolean", @default = false, description = "get_window_state only: include accessibility element tree, focused/selected state, and document text." },
            ["max_nodes"] = new { type = "integer", minimum = 1, maximum = 1000, @default = 500, description = "Maximum accessibility nodes for get_window_state or post-action state." },
            ["return_state"] = new { type = "string", @enum = new[] { "none", "accessibility", "screenshot", "full" }, @default = "none", description = "Input actions only: return a fresh observation after the action. The previous observation is always consumed before dispatch." },
            ["element_index"] = new { type = "integer", minimum = 0, description = "Element index from the supplied observation_id. The observation must include_text=true." },
            ["coordinate"] = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "integer" }, description = "Window-relative [x,y] coordinate bound to observation_id and screenshot_id." },
            ["key"] = new { type = "string", minLength = 1, maxLength = 64 },
            ["modifiers"] = new { type = "array", maxItems = 8, items = new { type = "string", @enum = new[] { "ctrl", "alt", "shift", "win" } } },
            ["text"] = new { type = "string", maxLength = 65536 },
            ["delta_x"] = new { type = "number" },
            ["delta_y"] = new { type = "number" },
            ["value"] = new { type = "string", maxLength = 65536 },
            ["path"] = new { type = "array", minItems = 2, maxItems = 256, items = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "integer" } } },
            ["secondary_action"] = new { type = "string", @enum = new[] { "invoke", "expand", "collapse", "toggle", "select", "focus", "scroll_into_view" } }
        },
        required = new[] { "action" },
        additionalProperties = false
    });

    private sealed record WindowRef(string App, nint Handle, string? Title);
    private sealed record WindowInfo(string App, long Id, string Title, int[] Bounds, bool Minimized);
    private sealed record ObservationCapture(ComputerUseObservation<AutomationElement> Observation, JsonElement Body, WireImage? Image);
    private sealed record ElementRow(int Index, AutomationElement Element, string Role, string Name, string? Value,
        int[]? Bounds, bool Enabled, bool Offscreen, bool Focused, bool Selected);

    private delegate bool EnumWindowsProc(nint handle, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint handle, nint deviceContext, uint flags);
}
