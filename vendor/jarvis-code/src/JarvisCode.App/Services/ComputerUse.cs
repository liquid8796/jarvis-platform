using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;
using Forms = System.Windows.Forms;

namespace JarvisCode.App.Services;

/// <summary>One attached display, numbered the way the screenshot tool exposes it.</summary>
public sealed record DisplayInfo(int Number, string Name, Rectangle Bounds, bool IsPrimary)
{
    public string Describe() =>
        $"{Number} \"{Name}\" ({Bounds.Width}x{Bounds.Height}{(IsPrimary ? ", primary" : "")})";
}

/// <summary>
/// A screenshot plus the sentence telling the model what it is looking at.
/// The frame is the region model coordinates address; the image may be smaller
/// when a scale was asked for, which never moves the coordinate frame.
/// </summary>
public sealed record ScreenCapture(
    ImageBlock Image,
    int FrameWidth,
    int FrameHeight,
    int ImageWidth,
    int ImageHeight,
    string Summary);

/// <summary>
/// Desktop automation for the agent: screenshots of any attached display plus
/// mouse/keyboard input via SendInput. Coordinates the model sends are pixels in
/// the last screenshot's coordinate frame — the captured region at full
/// resolution — so a scaled-down image never moves a click.
/// </summary>
public sealed class ComputerUseService(Func<UiSettings>? settings = null)
{
    /// <summary>The reference ships JPEG screenshots; PNG costs ~3.4x the bytes for the same pixels.</summary>
    private const long JpegQuality = 80;

    /// <summary>Milliseconds per keystroke, matching the reference's paced typing.</summary>
    public const int TypeDelayMs = 8;

    private Rectangle _frame;
    private int _frameDisplay;
    private bool _frameAllDisplays;

    /// <summary>
    /// The region the most recent capture framed, whichever service took it —
    /// what the on-screen indicator marks. The reference resolves the same thing
    /// from the lock holder's display.
    /// </summary>
    internal static Rectangle LastFrame { get; private set; }

    /// <summary>Display switch_display pinned, or null while selection is automatic.</summary>
    public int? SelectedDisplay { get; set; }

    /// <summary>The display automatic selection settled on, or null before it has.</summary>
    private int? _autoDisplay;

    /// <summary>The app set that display was resolved for, so an unchanged set is not re-resolved.</summary>
    private string? _displayResolvedForApps;

    /// <summary>
    /// The apps a resolution is keyed by: sorted and joined, so the same set in a
    /// different order is the same key.
    /// </summary>
    public static string AppsKey(IEnumerable<string> apps) =>
        string.Join(",", apps.Select(static a => a.ToLowerInvariant()).OrderBy(static a => a, StringComparer.Ordinal));

    /// <summary>
    /// Whether automatic selection may pick a display again. The reference
    /// re-resolves only when the model has not pinned one and the set of granted,
    /// running apps has changed since the last resolve — an unchanged set keeps
    /// the display it already chose.
    /// </summary>
    public static bool ShouldResolveDisplay(bool pinned, string appsKey, string? resolvedForApps) =>
        !pinned && appsKey.Length > 0 && appsKey != resolvedForApps;

    /// <summary>
    /// Points automatic selection at the display the granted apps are on. The
    /// reference resolves the capture display from where those apps actually are
    /// rather than always taking the primary one.
    /// </summary>
    public void ResolveDisplayForApps(IReadOnlyCollection<string> runningGrantedApps)
    {
        var key = AppsKey(runningGrantedApps);
        if (!ShouldResolveDisplay(SelectedDisplay is not null, key, _displayResolvedForApps))
        {
            return;
        }

        // A resolution that finds nothing still counts as done for this app set:
        // the reference remembers the set it resolved, not whether it succeeded.
        _autoDisplay = ComputerUseGrants.DisplayForApps(runningGrantedApps);
        _displayResolvedForApps = key;
    }

    /// <summary>Every attached display, in the order the screenshot tool numbers them.</summary>
    public static IReadOnlyList<DisplayInfo> Displays() =>
    [
        .. Forms.Screen.AllScreens.Select((screen, index) =>
            new DisplayInfo(index + 1, MonitorName(screen, index), screen.Bounds, screen.Primary)),
    ];

    /// <summary>
    /// The name switch_display accepts. Windows reports monitors as `\\.\DISPLAY1`;
    /// the adapter's own description ("Dell U2720Q") is what a person would type,
    /// so it wins when the driver supplies one.
    /// </summary>
    private static string MonitorName(Forms.Screen screen, int index)
    {
        var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        if (EnumDisplayDevices(screen.DeviceName, 0, ref device, 0)
            && !string.IsNullOrWhiteSpace(device.DeviceString))
        {
            return device.DeviceString.Trim();
        }

        return $"Display {index + 1}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice info, uint flags);

    /// <summary>
    /// The region model coordinates address. Before the first screenshot this is
    /// the primary display, whose frame is its own pixels — so a click that
    /// arrives without a screenshot still lands where the model aimed.
    /// </summary>
    public Rectangle Frame => _frame.IsEmpty ? PrimaryBounds() : _frame;

    /// <summary>Zoom regions are relative to a real screenshot, so it has to have happened.</summary>
    public bool HasCaptured => !_frame.IsEmpty;

    /// <summary>Captures a display and makes it the coordinate frame for later clicks.</summary>
    /// <param name="displayNumber">1-based display to capture; null takes the primary one.</param>
    /// <param name="allDisplays">Capture the whole virtual desktop instead of a single display.</param>
    /// <param name="scale">Shrinks the returned image only; coordinates stay in the full-resolution frame.</param>
    public ScreenCapture CaptureScreenshot(
        int? displayNumber = null, bool allDisplays = false, double scale = 1.0, string? sessionId = null)
    {
        var displays = Displays();
        var number = displayNumber
            ?? SelectedDisplay
            ?? _autoDisplay
            ?? displays.FirstOrDefault(d => d.IsPrimary)?.Number
            ?? 1;
        var region = allDisplays
            ? Forms.SystemInformation.VirtualScreen
            : displays.FirstOrDefault(d => d.Number == number)?.Bounds ?? PrimaryBounds();

        var (image, width, height) = Capture(region, scale, settings?.Invoke(), sessionId);
        _frame = region;
        LastFrame = region;
        _frameDisplay = number;
        _frameAllDisplays = allDisplays;
        return new ScreenCapture(
            image,
            region.Width,
            region.Height,
            width,
            height,
            Summarize(displays, number, allDisplays, region, width, height));
    }

    /// <summary>
    /// Re-captures whatever the coordinate frame currently shows — the batch's own
    /// screenshot action. It must not silently fall back to the primary display:
    /// the model may have aimed the frame at another monitor, and the frame that
    /// its coordinates mean has to survive a mid-batch capture.
    /// </summary>
    public ScreenCapture CaptureCurrentFrame(double scale, string? sessionId = null) => _frameDisplay == 0
        ? CaptureScreenshot(scale: scale, sessionId: sessionId)
        : CaptureScreenshot(_frameDisplay, _frameAllDisplays, scale, sessionId);

    /// <summary>
    /// Captures one rectangle of the given frame at native resolution — the zoom
    /// action. Deliberately does not move the coordinate frame: the reference keeps
    /// clicks addressed to the last full screenshot.
    /// </summary>
    public ImageBlock CaptureRegion(
        Rectangle frame, Rectangle regionInFrame, double scale, string? sessionId = null)
    {
        var onScreen = new Rectangle(
            frame.X + regionInFrame.X,
            frame.Y + regionInFrame.Y,
            regionInFrame.Width,
            regionInFrame.Height);
        return Capture(onScreen, scale, settings?.Invoke(), sessionId).Image;
    }

    private static (ImageBlock Image, int Width, int Height) Capture(
        Rectangle region,
        double scale,
        UiSettings? settings,
        string? sessionId)
    {
        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, region.Size);
        }

        if (settings is not null)
        {
            ComputerUseGrants.MaskUngranted(bitmap, region, settings, sessionId);
        }

        Bitmap final = bitmap;
        if (scale < 1.0)
        {
            var size = new Size(
                Math.Max(1, (int)Math.Round(region.Width * scale)),
                Math.Max(1, (int)Math.Round(region.Height * scale)));
            final = new Bitmap(bitmap, size);
        }

        try
        {
            using var stream = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            final.Save(stream, JpegEncoder, parameters);
            return (new ImageBlock("image/jpeg", Convert.ToBase64String(stream.ToArray())), final.Width, final.Height);
        }
        finally
        {
            if (!ReferenceEquals(final, bitmap))
            {
                final.Dispose();
            }
        }
    }

    private static ImageCodecInfo JpegEncoder { get; } =
        ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

    /// <summary>
    /// Tells the model which pixels it just received, that coordinates live in the
    /// frame rather than in a scaled image, and that more screens exist — without
    /// the last part a second monitor is invisible and never gets asked for.
    /// </summary>
    private static string Summarize(
        IReadOnlyList<DisplayInfo> displays,
        int number,
        bool allDisplays,
        Rectangle frame,
        int imageWidth,
        int imageHeight)
    {
        // With one monitor the whole virtual desktop *is* display 1; saying
        // "all 1 displays" would only make the model doubt what it is looking at.
        var target = allDisplays && displays.Count > 1 ? $"all {displays.Count} displays" : $"display {number}";
        var summary = $"Screenshot of {target}, coordinate frame {frame.Width}x{frame.Height} px.";
        summary += imageWidth == frame.Width && imageHeight == frame.Height
            ? " Coordinates for computer_batch are pixels in this frame."
            : $" Returned at {imageWidth}x{imageHeight} px — coordinates are in the {frame.Width}x{frame.Height} " +
              "coordinate frame, not the scaled screenshot image's own pixels.";

        if (allDisplays || displays.Count < 2)
        {
            return summary;
        }

        var rest = displays.Where(d => d.Number != number).ToList();
        var others = string.Join(", ", rest.Select(d => d.Describe()));
        return summary +
            $" Other monitors: {others}. Call switch_display with a monitor name to capture one of them, " +
            "or pass display=\"all\" for every display at once.";
    }

    private static Rectangle PrimaryBounds()
    {
        if (Forms.Screen.PrimaryScreen is { } primary)
        {
            return primary.Bounds;
        }

        var virtualScreen = Forms.SystemInformation.VirtualScreen;
        return virtualScreen.IsEmpty ? new Rectangle(0, 0, 1920, 1080) : virtualScreen;
    }

    // ---- input synthesis ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeyboardInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint MouseHWheel = 0x1000;
    private const uint MouseAbsolute = 0x8000;
    private const uint MouseVirtualDesk = 0x4000;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;
    private const int WheelDelta = 120;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    private static void Send(params Input[] inputs)
        => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    /// <summary>Frame pixels → absolute screen pixels.</summary>
    public static Point ToScreen(Rectangle frame, int x, int y) => new(frame.X + x, frame.Y + y);

    /// <summary>Absolute screen pixels → the 0..65535 space SendInput's absolute mode wants.</summary>
    public static Point ToAbsolute(Rectangle virtualScreen, Point screen) => new(
        (screen.X - virtualScreen.Left) * 65535 / Math.Max(1, virtualScreen.Width - 1),
        (screen.Y - virtualScreen.Top) * 65535 / Math.Max(1, virtualScreen.Height - 1));

    /// <summary>True when the point is inside the frame the last screenshot established.</summary>
    public static bool InFrame(Rectangle frame, int x, int y)
        => x >= 0 && y >= 0 && x < frame.Width && y < frame.Height;

    private static Input MouseAt(Point screen, uint flags)
    {
        // Display bounds, CopyFromScreen and SendInput all speak physical pixels;
        // WPF's SystemParameters would answer in DIPs and put the pointer in the
        // wrong place on any display that is not at 100% scaling.
        var absolute = ToAbsolute(Forms.SystemInformation.VirtualScreen, screen);
        return new Input
        {
            type = InputMouse,
            u = new InputUnion
            {
                mi = new MouseInput { dx = absolute.X, dy = absolute.Y, dwFlags = flags | MouseAbsolute | MouseVirtualDesk },
            },
        };
    }

    public void MoveMouse(Point screen) => Send(MouseAt(screen, MouseMove));

    /// <summary>Where the pointer is now, in absolute screen pixels.</summary>
    public static Point CursorPosition() => GetCursorPos(out var point) ? new Point(point.X, point.Y) : Point.Empty;

    /// <summary>Clicks, optionally with modifier keys held down for the whole click.</summary>
    public void Click(Point screen, string button, int times, IReadOnlyList<ushort> modifiers)
    {
        var (down, up) = button switch
        {
            "right" => (MouseRightDown, MouseRightUp),
            "middle" => (MouseMiddleDown, MouseMiddleUp),
            _ => (MouseLeftDown, MouseLeftUp),
        };

        Send(MouseAt(screen, MouseMove));
        Thread.Sleep(40);
        HoldDown(modifiers);
        try
        {
            for (var i = 0; i < times; i++)
            {
                Send(MouseAt(screen, down), MouseAt(screen, up));
                Thread.Sleep(60);
            }
        }
        finally
        {
            Release(modifiers);
        }
    }

    /// <summary>Wheel clicks: positive dy scrolls up, positive dx scrolls right.</summary>
    public void Scroll(Point screen, int dx, int dy)
    {
        Send(MouseAt(screen, MouseMove));
        if (dy != 0)
        {
            var wheel = MouseAt(screen, MouseWheel);
            wheel.u.mi.mouseData = unchecked((uint)(dy * WheelDelta));
            Send(wheel);
        }

        if (dx != 0)
        {
            var wheel = MouseAt(screen, MouseHWheel);
            wheel.u.mi.mouseData = unchecked((uint)(dx * WheelDelta));
            Send(wheel);
        }
    }

    public void MouseDown() => Send(MouseAt(CursorPosition(), MouseLeftDown));

    public void MouseUp() => Send(MouseAt(CursorPosition(), MouseLeftUp));

    /// <summary>
    /// Press, travel, release. The travel is interpolated because a drag delivered
    /// as one jump is ignored by anything that tracks pointer movement (canvases,
    /// sliders, drag-and-drop lists).
    /// </summary>
    public void Drag(Point from, Point to)
    {
        Send(MouseAt(from, MouseMove));
        Thread.Sleep(40);
        Send(MouseAt(from, MouseLeftDown));
        Thread.Sleep(40);

        const int steps = 24;
        for (var step = 1; step <= steps; step++)
        {
            var point = new Point(
                from.X + ((to.X - from.X) * step / steps),
                from.Y + ((to.Y - from.Y) * step / steps));
            Send(MouseAt(point, MouseMove));
            Thread.Sleep(8);
        }

        Thread.Sleep(40);
        Send(MouseAt(to, MouseLeftUp));
    }

    /// <summary>
    /// Puts the text on the clipboard and pastes it, which is how the reference
    /// delivers a long string on Windows rather than sending it a keystroke at a
    /// time. Returns false when the clipboard could not be set, so the caller
    /// falls back to typing rather than pasting whatever was there before.
    /// </summary>
    public bool TypeViaClipboard(string text)
    {
        var placed = false;
        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                placed = true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                or System.Runtime.InteropServices.ExternalException)
            {
                // Another process owns the clipboard; typing still works.
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5));
        if (!placed)
        {
            return false;
        }

        return PressChord("ctrl+v") is null;
    }

    public void TypeText(string text)
    {
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                PressKey(0x0D);
            }
            else if (ch == '\t')
            {
                PressKey(0x09);
            }
            else if (ch != '\r')
            {
                Send(
                    new Input { type = InputKeyboard, u = new InputUnion { ki = new KeyboardInput { wScan = ch, dwFlags = KeyUnicode } } },
                    new Input { type = InputKeyboard, u = new InputUnion { ki = new KeyboardInput { wScan = ch, dwFlags = KeyUnicode | KeyUp } } });
            }

            Thread.Sleep(TypeDelayMs);
        }
    }

    private static readonly Dictionary<string, ushort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["insert"] = 0x2D,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10,
        ["win"] = 0x5B, ["super"] = 0x5B, ["cmd"] = 0x5B, ["apps"] = 0x5D, ["printscreen"] = 0x2C,
    };

    /// <summary>Virtual-key codes for a chord like "ctrl+shift+t"; null message on success.</summary>
    public static string? ParseChord(string chord, out List<ushort> codes)
    {
        codes = [];
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "Empty key chord.";
        }

        foreach (var part in parts)
        {
            if (KeyMap.TryGetValue(part, out var vk))
            {
                codes.Add(vk);
            }
            else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
            {
                codes.Add((ushort)char.ToUpperInvariant(part[0]));
            }
            else if (part.Length is 2 or 3 && (part[0] is 'f' or 'F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
            {
                codes.Add((ushort)(0x70 + f - 1));
            }
            else
            {
                return $"Unknown key '{part}'.";
            }
        }

        return null;
    }

    /// <summary>Presses a chord like "ctrl+shift+t", "enter", "alt+F4".</summary>
    public string? PressChord(string chord)
    {
        if (ParseChord(chord, out var codes) is { } error)
        {
            return error;
        }

        HoldDown(codes);
        Release(codes);
        return null;
    }

    /// <summary>Holds a chord down for a while — the reference's hold_key.</summary>
    public async Task<string?> HoldChordAsync(string chord, int milliseconds, CancellationToken cancellationToken)
    {
        if (ParseChord(chord, out var codes) is { } error)
        {
            return error;
        }

        HoldDown(codes);
        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A cancelled hold must still let go of the keys, or the user's next
            // keystroke arrives with ctrl (or worse) silently pressed.
            Release(codes);
        }

        return null;
    }

    private static void HoldDown(IReadOnlyList<ushort> codes)
    {
        foreach (var vk in codes)
        {
            Send(new Input { type = InputKeyboard, u = new InputUnion { ki = new KeyboardInput { wVk = vk } } });
        }
    }

    private static void Release(IReadOnlyList<ushort> codes)
    {
        for (var i = codes.Count - 1; i >= 0; i--)
        {
            Send(new Input { type = InputKeyboard, u = new InputUnion { ki = new KeyboardInput { wVk = codes[i], dwFlags = KeyUp } } });
        }
    }

    private static void PressKey(ushort vk)
    {
        Send(
            new Input { type = InputKeyboard, u = new InputUnion { ki = new KeyboardInput { wVk = vk } } },
            new Input { type = InputKeyboard, u = new InputUnion { ki = new KeyboardInput { wVk = vk, dwFlags = KeyUp } } });
    }
}

/// <summary>Read-only screenshot tool — the model's eyes for computer use.</summary>
public sealed class ScreenshotTool(ComputerUseService service, Func<UiSettings>? settings = null) : ITool
{
    public string Name => "screenshot";

    public string Description =>
        "Takes a screenshot and returns it as an image. Use it to see the current state of the screen " +
        "before and after computer actions. Coordinates passed to computer_batch are pixels in this " +
        "screenshot's coordinate frame — the captured display at full resolution, which 'scale' does not " +
        "move. The primary display is captured unless 'display' names another one.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["display"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] =
                    "Which display to capture: a 1-based display number like \"2\", or \"all\" for every " +
                    "display in one image. Omit for the primary display.",
            },
            ["scale"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] =
                    "Scale factor in [0.1, 1] for the returned image; smaller images use fewer tokens. " +
                    "1 (default) returns the display at full resolution. Coordinates are ALWAYS in the " +
                    "full-resolution coordinate frame, never in the scaled image's own pixels.",
            },
        },
    };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        ReadDisplay(arguments) is { Length: > 0 } display ? $"Screenshot(display {display})" : "Screenshot()";

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var displays = ComputerUseService.Displays();
        int? displayNumber = null;
        var allDisplays = false;

        if (ReadDisplay(arguments) is { Length: > 0 } requested)
        {
            if (string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase))
            {
                allDisplays = true;
            }
            else if (int.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                && number >= 1 && number <= displays.Count)
            {
                displayNumber = number;
            }
            else
            {
                return Task.FromResult(ToolResult.Error(
                    $"There is no display '{requested}'. Attached displays: " +
                    $"{string.Join(", ", displays.Select(d => d.Describe()))}. Use a number or \"all\"."));
            }
        }

        if (ComputerBatchTool.ReadScale(arguments, out var scale) is { } scaleError)
        {
            return Task.FromResult(ToolResult.Error(scaleError));
        }

        // A screenshot the model did not aim at a display is the reference's
        // auto-target case: it captures where the granted apps are. Inside a
        // batch the frame is kept instead, which is why only this path resolves.
        if (displayNumber is null && !allDisplays && settings?.Invoke() is { } current)
        {
            service.ResolveDisplayForApps(
                ComputerUseGrants.RunningGrantedApps(current, context.SessionId));
        }

        // The reference hides before a screenshot as well as before a batch, and
        // the note names everything hidden since the last one.
        if (settings?.Invoke() is { } hideSettings)
        {
            _ = ComputerUseHide.HideUngranted(hideSettings, context.SessionId);
        }

        try
        {
            var capture = service.CaptureScreenshot(displayNumber, allDisplays, scale, context.SessionId);
            var note = ComputerUseHide.HiddenNote(
                ComputerUseHide.DrainPending(), InstalledApplications.Running().ToList());
            var summary = note is null ? capture.Summary : note + "\n\n" + capture.Summary;
            return Task.FromResult(ToolResult.WithImage(summary, capture.Image));
        }
        // ArgumentException covers a display that reports a zero-sized rectangle — a
        // locked or disconnected session — which Bitmap refuses before capture starts.
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException
            or IOException or ArgumentException)
        {
            return Task.FromResult(ToolResult.Error($"Could not capture the screen: {ex.Message}"));
        }
    }

    /// <summary>
    /// Models send the display as 2 or as "2". Numbers need both cases: arguments
    /// parsed from JSON come back as a double, while a JsonValue built in process
    /// keeps its int backing and matches neither the string nor the double case.
    /// </summary>
    private static string? ReadDisplay(JsonObject arguments) => arguments["display"] switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text.Trim(),
        JsonValue value when value.TryGetValue<int>(out var number) =>
            number.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<double>(out var number) =>
            ((int)number).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
}

/// <summary>One action's outcome inside a batch: a line of text, maybe an image.</summary>
internal sealed record ActionOutcome(string Text, bool IsError, ImageBlock? Image = null)
{
    public static ActionOutcome Ok(string text) => new(text, IsError: false);

    public static ActionOutcome Fail(string message) => new(message, IsError: true);
}

/// <summary>
/// State one batch carries across its actions. It lives per call rather than on
/// the tool because a tool instance is shared by every turn of a session, and a
/// held mouse button leaking into the next batch would wedge it.
/// </summary>
internal sealed class BatchState
{
    /// <summary>Coordinates are read against the frame as it was before the batch ran.</summary>
    public required Rectangle Frame { get; init; }

    /// <summary>Left button held by left_mouse_down, released by left_mouse_up or the next click.</summary>
    public bool MouseHeld { get; set; }
}

/// <summary>
/// Mouse/keyboard actions on the user's desktop, executed as a batch (permission-gated).
/// One tool call carries a whole predictable sequence, which is what makes desktop
/// work tolerable: every action of its own would cost a model round trip.
/// </summary>
public sealed class ComputerBatchTool(
    ComputerUseService service,
    Func<UiSettings>? settings = null,
    string? imageDirectory = null) : ITool
{
    /// <summary>Every action a batch may contain, in the reference's order.</summary>
    private static readonly string[] Actions =
    [
        "key", "type", "mouse_move", "left_click", "left_click_drag", "right_click", "middle_click",
        "double_click", "triple_click", "scroll", "hold_key", "screenshot", "zoom", "cursor_position",
        "left_mouse_down", "left_mouse_up", "wait",
    ];

    private static readonly HashSet<string> KnownActions = [.. Actions];

    /// <summary>
    /// What each action needs from the app it lands in. Plain left clicks (and their
    /// double/triple forms) sit at the "click" tier; anything a modifier or another
    /// button can turn into a paste or a context menu needs the full tier.
    /// </summary>
    private static readonly Dictionary<string, InputNeed> ActionNeeds = new(StringComparer.Ordinal)
    {
        ["mouse_move"] = InputNeed.Pointer,
        ["scroll"] = InputNeed.Click,
        ["left_click"] = InputNeed.Click,
        ["double_click"] = InputNeed.Click,
        ["triple_click"] = InputNeed.Click,
        ["left_mouse_down"] = InputNeed.Click,
        ["left_mouse_up"] = InputNeed.Click,
        ["right_click"] = InputNeed.FullMouse,
        ["middle_click"] = InputNeed.FullMouse,
        ["left_click_drag"] = InputNeed.FullMouse,
        ["key"] = InputNeed.Keyboard,
        ["type"] = InputNeed.Keyboard,
        ["hold_key"] = InputNeed.Keyboard,
    };

    private const int BetweenActionsMs = 10;
    private const double MaxDurationSeconds = 100;
    private const int MaxRepeat = 100;
    private const int MaxScrollAmount = 100;

    public string Name => "computer_batch";

    /// <summary>
    /// The reference composes this doc from two literals — its <c>Be()</c> writes
    /// <c>`${e.description}${F}`</c> whenever the batch-only form is served — so
    /// the two halves are separate constants here as well. Joined, the sentence
    /// exists only on the wire and in no bundle.
    /// </summary>
    public string Description => BatchDescription + BatchOnlySuffix;

    private const string BatchDescription =
        "Execute a sequence of actions in ONE tool call. Each individual tool call requires a model→API " +
        "round trip (seconds); batching a predictable sequence eliminates all but one. Use this whenever you " +
        "can predict the outcome of several actions ahead — e.g. click a field, type into it, press Return. " +
        "Actions execute sequentially and stop on the first error. " +
        FrontmostRule +
        " The frontmost check runs before EACH action inside the batch — if an action opens a non-allowed " +
        "app, the next action's gate fires and the batch stops there. Screenshot and zoom actions are " +
        "allowed and their images are returned interleaved with the per-action outputs. Coordinates you " +
        "write in THIS batch — clicks AND zoom regions — always refer to the full-screen screenshot taken " +
        "BEFORE this call, never to a zoom and never to a mid-batch screenshot. After the batch returns, " +
        "the most recent full screenshot it produced becomes the new coordinate reference for your next " +
        "call.";

    /// <summary>
    /// The reference's <c>$</c>, interpolated into every tool doc of this server
    /// that dispatches input. Kept as its own constant for the same reason the
    /// reference keeps it as one: it is the same sentence in all of them.
    /// </summary>
    internal const string FrontmostRule =
        "The frontmost application must be in the session allowlist at the time of this call, or this tool " +
        "returns an error and does nothing.";

    /// <summary>
    /// The reference's <c>F</c>, appended whenever the individual interaction
    /// tools are not served — which is always here, since this port has no
    /// single-action tool either.
    /// </summary>
    private const string BatchOnlySuffix =
        " IMPORTANT: in this session the individual interaction tools (left_click, type, key, scroll, drag, " +
        "etc.) are NOT available — this is the ONLY way to click, type, or otherwise interact with the " +
        "computer. A single action is just a one-item batch.";

    public JsonObject InputSchema =>
        CapturedMcpSchemas.Schema(InternalMcpServerNames.ComputerUse, "computer_batch");

    public bool IsReadOnly => false;

    public string DescribeCall(JsonObject arguments)
    {
        if (arguments["actions"] is not JsonArray actions || actions.Count == 0)
        {
            return "ComputerBatch()";
        }

        var names = actions
            .Select(a => (a as JsonObject) is { } item ? JsonArgs.GetString(item, "action") ?? "?" : "?")
            .Take(6)
            .ToList();
        var more = actions.Count > names.Count ? ", …" : "";
        return $"ComputerBatch({actions.Count} action{(actions.Count == 1 ? "" : "s")}: {string.Join(", ", names)}{more})";
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (arguments["actions"] is not JsonArray actions || actions.Count == 0)
        {
            return ToolResult.Error("actions must be a non-empty array");
        }

        // There is one desktop, and two sessions driving it at once produce a
        // batch whose coordinates were read off someone else's screen.
        using var desktop = DesktopLock.TryAcquire(context.SessionId);
        if (!desktop.Held)
        {
            return ToolResult.Error(DesktopLock.InUseMessage);
        }

        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i] is not JsonObject item)
            {
                return ToolResult.Error($"actions[{i}] must be an object");
            }

            if (JsonArgs.GetString(item, "action") is not { Length: > 0 } action)
            {
                return ToolResult.Error($"actions[{i}].action must be a string");
            }

            if (!KnownActions.Contains(action))
            {
                return ToolResult.Error(
                    $"actions[{i}].action=\"{action}\" is not allowed in a batch. Allowed: {string.Join(", ", Actions)}.");
            }
        }

        // Once, before the batch: the reference turns the flag off for the actions
        // inside it, so a batch hides at its start and not per action.
        if (settings?.Invoke() is { } batchSettings)
        {
            _ = ComputerUseHide.HideUngranted(batchSettings, context.SessionId);
        }

        // Every coordinate in this batch is read against the frame as it was before
        // the batch ran; a screenshot inside the batch only re-aims the next call.
        var state = new BatchState { Frame = service.Frame };
        var done = new List<(string Action, ActionOutcome Outcome)>();

        for (var i = 0; i < actions.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Aborted(done, i, actions.Count);
            }

            if (i > 0)
            {
                try
                {
                    await Task.Delay(BetweenActionsMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Aborted(done, i, actions.Count);
                }
            }

            var item = (JsonObject)actions[i]!;
            var action = JsonArgs.GetString(item, "action")!;
            var outcome = await RunAsync(action, item, state, context.SessionId, cancellationToken)
                .ConfigureAwait(false);
            done.Add((action, outcome));

            if (outcome.IsError)
            {
                // A failed batch drops its images: the model is being told to
                // re-observe, and stale frames only invite guessing at what changed.
                var failed = Render(done, actions.Count, omitImages: true);
                failed.Add($"Batch stopped at actions[{i}] ({action}). {i} completed, {actions.Count - i - 1} remaining.");
                return new ToolResult(string.Join("\n", failed), IsError: true);
            }
        }

        var images = done.Select(d => d.Outcome.Image).OfType<ImageBlock>().ToList();
        var lines = Render(done, actions.Count, omitImages: false);
        if (JsonArgs.GetBool(arguments, "save_to_disk") && images.Count > 0)
        {
            lines.Add(SaveImages(images));
        }

        return new ToolResult(string.Join("\n", lines), IsError: false, images.Count > 0 ? images : null);
    }

    /// <summary>
    /// Writes the batch's images next to the app's own settings so the user can be
    /// handed the file (SendUserFile) rather than a screenshot they can't keep.
    /// </summary>
    private string SaveImages(List<ImageBlock> images)
    {
        var directory = imageDirectory
            ?? Path.Combine(Path.GetTempPath(), "jarvis-code", "screenshots");
        try
        {
            Directory.CreateDirectory(directory);
            var saved = new List<string>();
            foreach (var image in images)
            {
                var path = Path.Combine(
                    directory,
                    $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{saved.Count + 1}.jpg");
                File.WriteAllBytes(path, Convert.FromBase64String(image.Base64Data));
                saved.Add(path);
            }

            return $"Saved to disk: {string.Join(", ", saved)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return $"Could not save the image(s) to disk: {ex.Message}";
        }
    }

    /// <summary>The reference's per-action line: "[2/5] left_click: Clicked."</summary>
    private static List<string> Render(
        List<(string Action, ActionOutcome Outcome)> done,
        int total,
        bool omitImages)
    {
        var lines = new List<string>();
        for (var i = 0; i < done.Count; i++)
        {
            var (action, outcome) = done[i];
            var text = outcome.Text.Length > 0 ? outcome.Text : "ok";
            var omitted = omitImages && outcome.Image is not null ? " [Image omitted due to error]" : "";
            lines.Add($"[{i + 1}/{total}] {action}: {(outcome.IsError ? "FAILED — " : "")}{text}{omitted}");
        }

        return lines;
    }

    private static ToolResult Aborted(List<(string Action, ActionOutcome Outcome)> done, int completed, int total)
    {
        var lines = Render(done, total, omitImages: true);
        lines.Add($"Batch aborted after {completed} of {total} actions (user interrupt).");
        return new ToolResult(string.Join("\n", lines), IsError: true);
    }

    private async Task<ActionOutcome> RunAsync(
        string action,
        JsonObject item,
        BatchState state,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (ActionNeeds.TryGetValue(action, out var need) && settings?.Invoke() is { } current)
        {
            // A modifier turns a plain click into one the "click" tier must refuse.
            if (need == InputNeed.Click && JsonArgs.GetString(item, "text") is { Length: > 0 })
            {
                need = InputNeed.FullMouse;
            }

            if (ComputerUseGrants.CheckForeground(current, sessionId, need) is { } refusal)
            {
                return ActionOutcome.Fail(refusal);
            }

            // The frontmost app is not necessarily the one a coordinate lands in.
            if (need is not InputNeed.Keyboard
                && item["coordinate"] is not null
                && ReadPoint(item, "coordinate", state.Frame, out var target) is null
                && ComputerUseGrants.CheckClickTarget(current, sessionId, target, need) is { } blocked)
            {
                return ActionOutcome.Fail(blocked);
            }
        }

        try
        {
            return action switch
            {
                "screenshot" => Screenshot(item, sessionId),
                "zoom" => Zoom(item, state.Frame),
                "cursor_position" => CursorPosition(state.Frame),
                "wait" => await WaitAsync(item, cancellationToken).ConfigureAwait(false),
                "hold_key" => await HoldKeyAsync(item, sessionId, cancellationToken).ConfigureAwait(false),
                "type" => Type(item),
                "key" => await KeyAsync(item, sessionId, cancellationToken).ConfigureAwait(false),
                "scroll" => Scroll(item, state.Frame),
                "mouse_move" => MouseMove(item, state.Frame),
                "left_click_drag" => Drag(item, state),
                "left_mouse_down" => MouseDown(state),
                "left_mouse_up" => MouseUp(state),
                "left_click" or "right_click" or "middle_click" or "double_click" or "triple_click" =>
                    Click(action, item, state, sessionId),
                // Unreachable while the upfront validation and this switch agree —
                // which is exactly why it must not silently fall through to a click.
                _ => ActionOutcome.Fail($"Unknown action \"{action}\"."),
            };
        }
        catch (OperationCanceledException)
        {
            return ActionOutcome.Fail($"{action} aborted (user interrupt).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException
            or IOException or ArgumentException)
        {
            return ActionOutcome.Fail($"{action} failed: {ex.Message}");
        }
    }

    // ---- actions ----

    private ActionOutcome Screenshot(JsonObject item, string? sessionId)
    {
        if (ReadScale(item, out var scale) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        var capture = service.CaptureCurrentFrame(scale, sessionId);
        return new ActionOutcome(capture.Summary, IsError: false, capture.Image);
    }

    private ActionOutcome Zoom(JsonObject item, Rectangle frame)
    {
        if (item["region"] is not JsonArray region || region.Count != 4)
        {
            return ActionOutcome.Fail("region must be an array of length 4: [x0, y0, x1, y1]");
        }

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!TryNumber(region[i], out var number) || number < 0)
            {
                return ActionOutcome.Fail("region values must be non-negative numbers");
            }

            values[i] = number;
        }

        if (values[2] <= values[0])
        {
            return ActionOutcome.Fail("region x1 must be greater than x0");
        }

        if (values[3] <= values[1])
        {
            return ActionOutcome.Fail("region y1 must be greater than y0");
        }

        if (!service.HasCaptured)
        {
            return ActionOutcome.Fail("take a screenshot before zooming (region coords are relative to it)");
        }

        if (values[2] > frame.Width || values[3] > frame.Height)
        {
            return ActionOutcome.Fail($"region exceeds the coordinate frame ({frame.Width}×{frame.Height})");
        }

        if (ReadScale(item, out var scale) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        var rectangle = new Rectangle(
            (int)values[0],
            (int)values[1],
            Math.Max(1, (int)(values[2] - values[0])),
            Math.Max(1, (int)(values[3] - values[1])));
        return new ActionOutcome(string.Empty, IsError: false, service.CaptureRegion(frame, rectangle, scale));
    }

    /// <summary>
    /// The reference's <c>Rn</c>: inside the last screenshot's frame the answer
    /// is in that frame's own pixels, and outside it — or before any screenshot —
    /// it falls back to screen coordinates and says why. Both spellings are the
    /// reference's, and so is each note.
    /// </summary>
    private static ActionOutcome CursorPosition(Rectangle frame)
    {
        var cursor = ComputerUseService.CursorPosition();
        var x = cursor.X - frame.X;
        var y = cursor.Y - frame.Y;
        return ComputerUseService.InFrame(frame, x, y)
            ? ActionOutcome.Ok($"{{\"x\":{x},\"y\":{y},\"coordinateSpace\":\"image_pixels\"}}")
            : ActionOutcome.Ok(
                $"{{\"x\":{cursor.X},\"y\":{cursor.Y},\"coordinateSpace\":\"logical_points\",\"note\":\"" +
                CursorOnAnotherMonitor + "\"}");
    }

    /// <summary>The reference's note for a cursor the last screenshot cannot place.</summary>
    private const string CursorOnAnotherMonitor =
        "cursor is on a different monitor than your last screenshot; take a fresh screenshot";

    private static async Task<ActionOutcome> WaitAsync(JsonObject item, CancellationToken cancellationToken)
    {
        if (ReadDuration(item, out var seconds) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ActionOutcome.Fail("Wait aborted (user interrupt).");
        }

        return ActionOutcome.Ok($"Waited {seconds.ToString("0.###", CultureInfo.InvariantCulture)}s.");
    }

    private async Task<ActionOutcome> HoldKeyAsync(
        JsonObject item, string? sessionId, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetString(item, "text") is not { Length: > 0 } chord)
        {
            return ActionOutcome.Fail("text is required");
        }

        if (ReadDuration(item, out var seconds) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        if (SystemComboRefusal(chord, sessionId) is { } systemCombo)
        {
            return ActionOutcome.Fail(systemCombo);
        }

        try
        {
            if (await service.HoldChordAsync(chord, (int)(seconds * 1000), cancellationToken).ConfigureAwait(false) is { } chordError)
            {
                return ActionOutcome.Fail(chordError);
            }
        }
        catch (OperationCanceledException)
        {
            return ActionOutcome.Fail("Key hold aborted (user interrupt).");
        }

        return ActionOutcome.Ok("Key held.");
    }

    /// <summary>
    /// The reference's win32 rule: past <see cref="ClipboardPasteAbove"/>
    /// characters the text is pasted rather than typed key by key, which is what
    /// its `clipboardPasteMultiline` default (true, in its own defaults table)
    /// switches on. It says so in the result, because the clipboard the user had
    /// is gone either way.
    /// </summary>
    private ActionOutcome Type(JsonObject item)
    {
        if (JsonArgs.GetString(item, "text") is not { } text)
        {
            return ActionOutcome.Fail("text is required");
        }

        if (text.Length > ClipboardPasteAbove && service.TypeViaClipboard(text))
        {
            return ActionOutcome.Ok("Typed (via clipboard).");
        }

        service.TypeText(text);
        return ActionOutcome.Ok($"Typed {text.Length} char(s).");
    }

    /// <summary>The reference's win32 threshold for pasting instead of typing.</summary>
    internal const int ClipboardPasteAbove = 16;

    private async Task<ActionOutcome> KeyAsync(
        JsonObject item, string? sessionId, CancellationToken cancellationToken)
    {
        if (JsonArgs.GetString(item, "text") is not { Length: > 0 } chord)
        {
            return ActionOutcome.Fail("text is required");
        }

        if (SystemComboRefusal(chord, sessionId) is { } systemCombo)
        {
            return ActionOutcome.Fail(systemCombo);
        }

        var repeat = 1;
        if (item["repeat"] is not null)
        {
            if (!TryNumber(item["repeat"], out var number) || number != Math.Floor(number) || number < 1)
            {
                return ActionOutcome.Fail("repeat must be a positive integer");
            }

            if (number > MaxRepeat)
            {
                return ActionOutcome.Fail($"repeat exceeds maximum of {MaxRepeat}");
            }

            repeat = (int)number;
        }

        for (var i = 0; i < repeat; i++)
        {
            if (i > 0)
            {
                try
                {
                    await Task.Delay(ComputerUseService.TypeDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return ActionOutcome.Fail($"Key repeat aborted after {i} of {repeat} presses (user interrupt).");
                }
            }

            if (service.PressChord(chord) is { } error)
            {
                return ActionOutcome.Fail(error);
            }
        }

        return ActionOutcome.Ok(repeat > 1 ? $"Key pressed {repeat} times." : "Key pressed.");
    }

    private ActionOutcome Scroll(JsonObject item, Rectangle frame)
    {
        if (ReadPoint(item, "coordinate", frame, out var point) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        var direction = JsonArgs.GetString(item, "scroll_direction");
        if (direction is not ("up" or "down" or "left" or "right"))
        {
            return ActionOutcome.Fail("scroll_direction must be 'up', 'down', 'left', or 'right'");
        }

        if (!TryNumber(item["scroll_amount"], out var amount) || amount != Math.Floor(amount) || amount < 0)
        {
            return ActionOutcome.Fail("scroll_amount must be a non-negative int");
        }

        if (amount > MaxScrollAmount)
        {
            return ActionOutcome.Fail($"scroll_amount exceeds maximum of {MaxScrollAmount}");
        }

        var clicks = (int)amount;
        var dx = direction switch { "left" => -clicks, "right" => clicks, _ => 0 };
        var dy = direction switch { "up" => clicks, "down" => -clicks, _ => 0 };
        service.Scroll(point, dx, dy);
        return ActionOutcome.Ok("Scrolled.");
    }

    private ActionOutcome MouseMove(JsonObject item, Rectangle frame)
    {
        if (ReadPoint(item, "coordinate", frame, out var point) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        service.MoveMouse(point);
        return ActionOutcome.Ok("Moved.");
    }

    private ActionOutcome Drag(JsonObject item, BatchState state)
    {
        if (ReadPoint(item, "coordinate", state.Frame, out var end) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        var start = ComputerUseService.CursorPosition();
        if (item["start_coordinate"] is not null
            && ReadPoint(item, "start_coordinate", state.Frame, out start) is { } startError)
        {
            return ActionOutcome.Fail(startError);
        }

        if (state.MouseHeld)
        {
            service.MouseUp();
            state.MouseHeld = false;
        }

        service.Drag(start, end);
        return ActionOutcome.Ok("Dragged.");
    }

    private ActionOutcome MouseDown(BatchState state)
    {
        if (state.MouseHeld)
        {
            return ActionOutcome.Fail("mouse button already held, call left_mouse_up first");
        }

        service.MouseDown();
        state.MouseHeld = true;
        return ActionOutcome.Ok("Mouse button pressed.");
    }

    private ActionOutcome MouseUp(BatchState state)
    {
        service.MouseUp();
        state.MouseHeld = false;
        return ActionOutcome.Ok("Mouse button released.");
    }

    private ActionOutcome Click(string action, JsonObject item, BatchState state, string? sessionId)
    {
        if (ReadPoint(item, "coordinate", state.Frame, out var point) is { } error)
        {
            return ActionOutcome.Fail(error);
        }

        List<ushort> modifiers = [];
        if (JsonArgs.GetString(item, "text") is { Length: > 0 } chord)
        {
            if (ComputerUseService.ParseChord(chord, out modifiers) is { } chordError)
            {
                return ActionOutcome.Fail(chordError);
            }

            if (SystemComboRefusal(chord, sessionId) is not null)
            {
                return ActionOutcome.Fail(
                    $"The modifier chord \"{chord}\" would fire a system shortcut. Request the " +
                    "systemKeyCombos grant flag via request_access, or use only modifier keys " +
                    "(shift, ctrl, alt, win) in the text parameter.");
            }
        }

        var (button, times) = action switch
        {
            "right_click" => ("right", 1),
            "middle_click" => ("middle", 1),
            "double_click" => ("left", 2),
            "triple_click" => ("left", 3),
            _ => ("left", 1),
        };

        // A click while the button is held would nest a press inside a press; the
        // reference lets go first, so a stray left_mouse_down cannot wedge the batch.
        if (state.MouseHeld)
        {
            service.MouseUp();
            state.MouseHeld = false;
        }

        service.Click(point, button, times, modifiers);
        return ActionOutcome.Ok("Clicked.");
    }

    /// <summary>
    /// OS-owned chords (alt+tab, win+r, ctrl+alt+delete) need the systemKeyCombos
    /// grant flag — one stray chord can log the user out or open the Run box.
    /// </summary>
    private string? SystemComboRefusal(string chord, string? sessionId)
    {
        if (settings?.Invoke() is not { RequireComputerUseGrants: true } current)
        {
            return null;
        }

        if (current.ComputerUseSystemKeyCombos ||
            ComputerUseSessionGrants.Peek(current, sessionId)?.SystemKeyCombos == true)
        {
            return null;
        }

        return ComputerUseGrants.IsSystemShortcut(chord)
            ? $"\"{chord}\" is a system-level shortcut. Request the `systemKeyCombos` grant via " +
              "request_access to use it."
            : null;
    }

    // ---- argument readers ----

    /// <summary>
    /// A number in whatever shape it arrived in. Arguments parsed from the wire are
    /// doubles, while a JsonValue built in process keeps its int backing and matches
    /// neither — reading only one of the two silently rejects half the callers.
    /// </summary>
    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue json)
        {
            return false;
        }

        // A JsonValue built in process is typed: one created from an int answers
        // TryGetValue<int> and nothing else, so every backing has to be asked for.
        if (json.TryGetValue<double>(out var number))
        {
            value = number;
            return true;
        }

        if (json.TryGetValue<int>(out var whole))
        {
            value = whole;
            return true;
        }

        if (json.TryGetValue<long>(out var integer))
        {
            value = integer;
            return true;
        }

        if (json.TryGetValue<decimal>(out var exact))
        {
            value = (double)exact;
            return true;
        }

        if (json.TryGetValue<string>(out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a [x, y] pair as frame pixels and maps it onto the screen, with the
    /// reference's four refusals — its <c>V</c> for the shape and its <c>H</c>
    /// for a point the frame does not contain.
    /// </summary>
    private static string? ReadPoint(JsonObject item, string key, Rectangle frame, out Point point)
    {
        point = Point.Empty;
        if (item[key] is null)
        {
            return $"{key} is required";
        }

        if (item[key] is not JsonArray pair || pair.Count != 2)
        {
            return $"{key} must be an array of length 2";
        }

        var values = new int[2];
        for (var i = 0; i < 2; i++)
        {
            if (!TryNumber(pair[i], out var number) ||
                double.IsNaN(number) || double.IsInfinity(number) || number < 0)
            {
                return $"{key} must be a tuple of non-negative finite numbers";
            }

            values[i] = (int)Math.Round(number);
        }

        if (!ComputerUseService.InFrame(frame, values[0], values[1]))
        {
            return $"{key} [{values[0]}, {values[1]}] is outside the coordinate frame " +
                $"({frame.Width}x{frame.Height})" + OutsideFrameAdvice;
        }

        point = ComputerUseService.ToScreen(frame, values[0], values[1]);
        return null;
    }

    /// <summary>
    /// The reference's own second half of that sentence, kept apart because its
    /// literal carries the frame size as a hole.
    /// </summary>
    private const string OutsideFrameAdvice =
        " \u2014 coordinates are pixels in the full-resolution coordinate frame. Take a new screenshot and " +
        "pick a point inside it.";

    /// <summary>The reference's <c>Qpn</c>: the smallest scale it will return an image at.</summary>
    private const double MinScale = 0.1;

    /// <summary>Shared by the screenshot tool and the screenshot/zoom actions.</summary>
    internal static string? ReadScale(JsonObject arguments, out double scale)
    {
        scale = 1.0;
        if (arguments["scale"] is null)
        {
            return null;
        }

        if (!TryNumber(arguments["scale"], out var number) || double.IsNaN(number) ||
            number < MinScale || number > 1)
        {
            // Interpolated where the reference interpolates it (its Qpn), so
            // each half is the literal the bundle carries.
            return $"scale must be a number in [{MinScale}, 1] \u2014 e.g. 0.5 for a half-size image";
        }

        scale = number;
        return null;
    }

    private static string? ReadDuration(JsonObject item, out double seconds)
    {
        seconds = 0;
        if (!TryNumber(item["duration"], out var number) || double.IsNaN(number) || double.IsInfinity(number))
        {
            return "duration must be a number";
        }

        if (number < 0)
        {
            return "duration must be non-negative";
        }

        if (number > MaxDurationSeconds)
        {
            return "duration is too long. Duration is in seconds.";
        }

        seconds = number;
        return null;
    }
}

/// <summary>
/// Builds the desktop-control pair. Both tools share one service because the
/// coordinates the model sends only mean something against the screenshot that
/// service last took — and neither tool exists at all when the user has switched
/// desktop control off.
/// </summary>
public static class ComputerUseTools
{
    /// <param name="teach">
    /// Supplied by a surface that can raise the teach overlay; without it the
    /// guided-tour tools are simply not offered.
    /// </param>
    public static IReadOnlyList<ITool> CreateIfEnabled(UiSettingsStore settings, TeachController? teach = null) =>
        settings.Current.ComputerUseEnabled ? Create(settings, teach) : [];

    /// <summary>
    /// The tools themselves, built whether or not computer use is switched on.
    /// The server definition answers the switch per turn instead, so flipping it
    /// mid-session is felt on the next model call rather than only after the
    /// window rebuilds its tool list.
    /// </summary>
    public static IReadOnlyList<ITool> Create(UiSettingsStore settings, TeachController? teach = null)
    {
        var service = new ComputerUseService(() => settings.Current);
        var images = Path.Combine(
            Path.GetDirectoryName(settings.FilePath) ?? Path.GetTempPath(), "screenshots");
        var batch = new ComputerBatchTool(service, () => settings.Current, images);
        return
        [
            new ScreenshotTool(service, () => settings.Current),
            batch,
            .. ComputerUseExtras.Create(settings, service),
            .. teach is null ? [] : TeachTools.Create(settings, teach, batch, service),
        ];
    }

    /// <summary>
    /// Computer use as the reference's <c>computer-use</c> in-process MCP server.
    ///
    /// The reference declares 42 tools here and a live ccd session on Windows
    /// advertises ten: request_access, the three teach tools, computer_batch,
    /// open_application, switch_display, list_granted_applications and the two
    /// clipboard tools. The other 32 are its macOS background-accessibility
    /// family (app_*), its full-control mode pair, and the single-action tools —
    /// which the reference does not offer at all, because every click, keystroke
    /// and screenshot rides <c>computer_batch</c>.
    ///
    /// <see cref="ScreenshotTool"/> is therefore not part of this server. It is
    /// this port's own tool and stays bare-named, recorded as an addition in
    /// Deltas/reference-surface-deltas.tsv.
    /// </summary>
    /// <summary>
    /// The computer-use tools this port carries that the reference does not, so
    /// they keep bare names. Today that is <see cref="ScreenshotTool"/> alone.
    /// </summary>
    public static IReadOnlyList<ITool> BareTools(UiSettingsStore settings) =>
        [.. CreateIfEnabled(settings).Where(static t => t is ScreenshotTool)];

    public static InternalMcpServerDefinition Server(
        UiSettingsStore settings, TeachController? teach = null) =>
        new(InternalMcpServerNames.ComputerUse,
            [.. Create(settings, teach).Where(static t => t is not ScreenshotTool)])
        {
            // The reference resolves this per turn, so a switch flipped
            // mid-session takes the server away (or gives it back) on the next
            // model call rather than at the next window rebuild.
            IsEnabled = static context =>
                context.SessionType == InternalMcpSessionContext.CodeSessionType &&
                context.ComputerUseEnabled,

            // The reference pushes this block unconditionally — unlike the
            // chrome one, it is about which tier of tool to reach for, which
            // matters whether or not these tools are deferred.
            Instructions = McpServerInstructions.ComputerUse,
        };
}
