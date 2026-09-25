using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
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
    private readonly ConcurrentDictionary<(string Scope, nint Window), WindowState> _states = new();

    public ToolDescriptor Descriptor { get; } = new(
        "computer_use.computer_use",
        "computer_use",
        "computer",
        "Control Windows applications through Codex-compatible window handles and window-relative accessibility state. Supported actions: list_windows, get_window, list_apps, launch_app, get_window_state, click, press_key, type_text, scroll, set_value, drag, perform_secondary_action, and activate_window. Local app grants, denied-app policy, Arm/Pause, approval and Full permission remain enforced.",
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
                "get_window_state" => await GetWindowStateAsync(ResolveWindow(arguments), context, ct).ConfigureAwait(false),
                "activate_window" => await ActivateAsync(ResolveWindow(arguments), context, ct).ConfigureAwait(false),
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

    private async Task<ToolReply> ActivateAsync(WindowRef window, AgentExecutionContext context, CancellationToken ct)
    {
        var access = await EnsureAccessAsync(window.App, InputNeed.Pointer, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        return Reply(new { activated = GetWindow(window) });
    }

    private async Task<ToolReply> GetWindowStateAsync(WindowRef window, AgentExecutionContext context, CancellationToken ct)
    {
        var access = await EnsureAccessAsync(window.App, InputNeed.Pointer, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        await Task.Delay(100, ct).ConfigureAwait(false);
        var rect = RectFor(window.Handle);
        var elements = ReadElements(window.Handle, rect);
        _states[(context.IsolationScopeId, window.Handle)] = new(elements.Select(e => e.Element).ToArray(), rect, DateTimeOffset.UtcNow);
        var (mime, base64) = Capture(rect);
        var tree = elements.Select(e => new
        {
            element_index = e.Index,
            role = e.Role,
            name = e.Name,
            value = e.Value,
            bounds = e.Bounds,
            enabled = e.Enabled,
            offscreen = e.Offscreen
        }).ToArray();
        var body = JsonSerializer.Serialize(new
        {
            window = GetWindow(window),
            screenshot = new { mime_type = mime, width = rect.Width, height = rect.Height, image_url = $"data:{mime};base64,{base64}" },
            element_tree = tree
        }, WireJson.Options);
        return new ToolReply(body, Images: [new WireImage(mime, base64)]);
    }

    private async Task<ToolReply> ClickAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Click, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        var point = PointFor(args, context, window, required: true);
        service.Click(point, "left", 1, []);
        return Reply(new { clicked = new[] { point.X, point.Y }, window = GetWindow(window) });
    }

    private async Task<ToolReply> PressKeyAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Keyboard, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        var key = RequiredString(args, "key");
        var modifiers = args.TryGetProperty("modifiers", out var modifierNode)
            ? modifierNode.EnumerateArray().Select(node => node.GetString() ?? "").Where(text => text.Length > 0).ToArray()
            : [];
        var chord = string.Join('+', modifiers.Append(key));
        if (service.PressChord(chord) is { } error) return ToolReply.Error(error);
        return Reply(new { pressed = chord, window = GetWindow(window) });
    }

    private async Task<ToolReply> TypeTextAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Keyboard, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        var text = RequiredString(args, "text", allowEmpty: true);
        if (text.Length > 65536) throw new ArgumentException("text exceeds 65536 characters.");
        service.TypeText(text);
        return Reply(new { typed = text.Length, window = GetWindow(window) });
    }

    private async Task<ToolReply> ScrollAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Click, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        var point = PointFor(args, context, window, required: false);
        var dx = args.TryGetProperty("delta_x", out var x) ? x.GetDouble() : 0;
        var dy = args.TryGetProperty("delta_y", out var y) ? y.GetDouble() : 0;
        if (dx == 0 && dy == 0) throw new ArgumentException("scroll requires delta_x or delta_y.");
        static int Wheel(double delta) => delta == 0 ? 0 : Math.Sign(delta) * Math.Max(1, (int)Math.Round(Math.Abs(delta) / 120d));
        service.Scroll(point, -Wheel(dx), -Wheel(dy));
        return Reply(new { scrolled = new { delta_x = dx, delta_y = dy }, window = GetWindow(window) });
    }

    private async Task<ToolReply> SetValueAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.Keyboard, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        var element = ElementFor(args, context, window);
        var value = RequiredString(args, "value", allowEmpty: true);
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern setter)
            return ToolReply.Error("The selected element does not support ValuePattern. Refresh window state or use click plus type_text.");
        setter.SetValue(value);
        return Reply(new { element_index = args.GetProperty("element_index").GetInt32(), value, window = GetWindow(window) });
    }

    private async Task<ToolReply> DragAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.FullMouse, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        if (!args.TryGetProperty("path", out var pathNode) || pathNode.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("drag requires path with at least two [x,y] points.");
        var points = pathNode.EnumerateArray().Select(ReadCoordinate).ToArray();
        if (points.Length < 2 || points.Length > 256) throw new ArgumentException("drag path must contain 2..256 points.");
        var rect = RectFor(window.Handle);
        var from = new System.Drawing.Point(rect.Left + points[0].X, rect.Top + points[0].Y);
        var to = new System.Drawing.Point(rect.Left + points[^1].X, rect.Top + points[^1].Y);
        service.Drag(from, to);
        return Reply(new { dragged = points.Length, window = GetWindow(window) });
    }

    private async Task<ToolReply> SecondaryActionAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
    {
        var window = ResolveWindow(args);
        var access = await EnsureAccessAsync(window.App, InputNeed.FullMouse, context, ct).ConfigureAwait(false);
        if (access is not null) return access;
        Activate(window.Handle);
        var element = ElementFor(args, context, window);
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
        return Reply(new { secondary_action = action, element_index = args.GetProperty("element_index").GetInt32(), window = GetWindow(window) });
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

    private System.Drawing.Point PointFor(JsonElement args, AgentExecutionContext context, WindowRef window, bool required)
    {
        if (args.TryGetProperty("element_index", out _))
        {
            var element = ElementFor(args, context, window);
            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty) throw new InvalidOperationException("The selected element has no clickable bounds. Refresh window state.");
            return new((int)Math.Round(bounds.Left + bounds.Width / 2), (int)Math.Round(bounds.Top + bounds.Height / 2));
        }
        if (args.TryGetProperty("coordinate", out var coordinate))
        {
            var relative = ReadCoordinate(coordinate);
            var rect = RectFor(window.Handle);
            if (relative.X < 0 || relative.Y < 0 || relative.X >= rect.Width || relative.Y >= rect.Height)
                throw new ArgumentException("coordinate is outside the current window bounds.");
            return new(rect.Left + relative.X, rect.Top + relative.Y);
        }
        if (required) throw new ArgumentException("Provide element_index or coordinate.");
        var current = ComputerUseService.CursorPosition();
        var windowRect = RectFor(window.Handle);
        return windowRect.Contains(current) ? current : new(windowRect.Left + windowRect.Width / 2, windowRect.Top + windowRect.Height / 2);
    }

    private AutomationElement ElementFor(JsonElement args, AgentExecutionContext context, WindowRef window)
    {
        if (!args.TryGetProperty("element_index", out var indexNode)) throw new ArgumentException("element_index is required.");
        var index = indexNode.GetInt32();
        if (!_states.TryGetValue((context.IsolationScopeId, window.Handle), out var state))
            throw new InvalidOperationException("Call get_window_state before using element_index.");
        if (DateTimeOffset.UtcNow - state.CapturedAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("The cached element tree is stale. Call get_window_state again.");
        if (index < 0 || index >= state.Elements.Count) throw new ArgumentException("element_index is outside the latest element tree.");
        var element = state.Elements[index];
        try { _ = element.Current.ProcessId; }
        catch (ElementNotAvailableException) { throw new InvalidOperationException("The element is stale. Call get_window_state again."); }
        return element;
    }

    private static IReadOnlyList<ElementRow> ReadElements(nint handle, System.Drawing.Rectangle window)
    {
        var root = AutomationElement.FromHandle(handle) ?? throw new InvalidOperationException("Windows UI Automation cannot inspect this window.");
        var result = new List<ElementRow>();
        var queue = new Queue<AutomationElement>();
        queue.Enqueue(root);
        while (queue.Count > 0 && result.Count < 500)
        {
            var element = queue.Dequeue();
            try
            {
                var current = element.Current;
                var bounds = current.BoundingRectangle;
                string? value = null;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern values) value = values.Current.Value;
                result.Add(new(result.Count, element, current.ControlType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal) ?? "Unknown",
                    current.Name ?? "", value,
                    bounds.IsEmpty ? null : new[] { (int)Math.Round(bounds.Left - window.Left), (int)Math.Round(bounds.Top - window.Top), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height) },
                    current.IsEnabled, current.IsOffscreen));
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                while (child is not null)
                {
                    queue.Enqueue(child);
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException) { }
        }
        return result;
    }

    private static (string Mime, string Base64) Capture(System.Drawing.Rectangle rect)
    {
        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, rect.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        using var output = new MemoryStream();
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return ("image/png", Convert.ToBase64String(output.ToArray()));
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
            ["element_index"] = new { type = "integer", minimum = 0, description = "Element index from the latest get_window_state for this window." },
            ["coordinate"] = new { type = "array", minItems = 2, maxItems = 2, items = new { type = "integer" }, description = "Window-relative [x,y] coordinate." },
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
    private sealed record WindowState(IReadOnlyList<AutomationElement> Elements, System.Drawing.Rectangle Bounds, DateTimeOffset CapturedAt);
    private sealed record ElementRow(int Index, AutomationElement Element, string Role, string Name, string? Value, int[]? Bounds, bool Enabled, bool Offscreen);

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
}
