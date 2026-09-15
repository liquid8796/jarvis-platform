using System.Globalization;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>
/// Every sentence the reference's Android Emulator server answers with, and the
/// argument validator in front of them — ported from the desktop's own handler
/// (app.asar 1.40609.1.0, <c>.vite/build/index.chunk-DIYseJ6l.js</c>: the
/// declaration <c>Qe</c>, its handler <c>Ze</c>, the wrapper <c>X</c> and the
/// validator <c>we</c>).
///
/// These are tool results, which the model reads, so they stay the reference's
/// text — server name included. The one adaptation is the settings path in
/// <see cref="TurnedOffInSettings"/>: it tells the user where to switch the
/// server back on, and this app's page is called Jarvis Code.
/// </summary>
internal static class AndroidEmulatorMessages
{
    /// <summary>The reference's <c>In</c> — the server name, which its sentences interpolate.</summary>
    internal const string ServerName = "Claude Code Android Emulator";

    /// <summary>Its <c>D</c> plus <c>O</c>: every action name the shared validator admits.</summary>
    internal static readonly IReadOnlyList<string> AllActions =
    [
        "attach", "launch", "screenshot", "tap", "swipe", "touch_path", "touch2_path",
        "text", "button", "open_url", "detach", "build", "build_status",
    ];

    /// <summary>Its <c>j</c>: the ten this server offers, in declaration order.</summary>
    internal static readonly IReadOnlyList<string> Actions =
    [
        "attach", "launch", "screenshot", "tap", "swipe", "touch_path",
        "text", "button", "open_url", "detach",
    ];

    /// <summary>Its <c>H</c>: the three property names that all name the device.</summary>
    internal static readonly IReadOnlyList<string> DeviceKeys = ["device", "udid", "serial"];

    /// <summary>
    /// Its <c>Ce</c>: the names that, when passed by mistake, earn the extra
    /// "To target a device, use 'device'." line.
    /// </summary>
    private static readonly IReadOnlySet<string> DeviceAliasHints =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "device", "udid", "serial", "device_id", "deviceid", "device_name", "devicename",
            "device_udid", "simulator", "emulator", "avd", "target",
        };

    /// <summary>
    /// Its <c>edr</c>: every button name the shared validator keeps — the iOS
    /// five, the Android four and the volume pair. A name outside this set is
    /// dropped, so 'button' answers <see cref="ButtonNeedsName"/>; a name inside
    /// it but outside Android's four reaches
    /// <see cref="UnsupportedButton(string)"/> instead.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownButtons =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "HOME", "LOCK", "SIRI", "SIDE_BUTTON", "APPLE_PAY", "BACK", "RECENTS", "VOLUME_UP", "VOLUME_DOWN",
        };

    /// <summary>Its <c>Je</c>: the schema's own property names, in declaration order.</summary>
    internal static readonly IReadOnlyList<string> Properties =
    [
        "action", "app_path", "device", "udid", "serial", "bundle_id",
        "x", "y", "points", "x2", "y2", "duration", "text", "name", "url",
    ];

    // ---- the gates in front of the handler ----

    internal const string Disabled = $"{ServerName} is currently disabled.";

    internal const string TurnedOffByOrganization =
        $"{ServerName} has been turned off by the user's organization. It cannot be turned back on in Settings.";

    /// <summary>
    /// The reference names its own settings page here (Settings → Claude Code →
    /// Mobile simulators → Android Emulator). The row exists in this app under
    /// the same two headings on a page called Jarvis Code, so the path is
    /// rewritten rather than left pointing at a menu that is not there.
    /// </summary>
    internal const string TurnedOffInSettings =
        $"{ServerName} is turned off in Settings (Settings → Jarvis Code → Mobile simulators → " +
        "Android Emulator). The user can turn it back on there.";

    /// <summary>The reference's <c>Di.unavailableMessage</c>.</summary>
    internal const string AdbNotFound =
        "Couldn't find `adb`. Install Android SDK platform-tools (or set ANDROID_HOME).";

    internal const string BuildIsIosOnly =
        "'build' is iOS-only for now — build Android projects with gradle (e.g. ./gradlew assembleDebug) " +
        "in your shell, then 'launch' the APK.";

    internal const string Touch2PathIsIosOnly =
        "'touch2_path' is iOS-only (Android `input motionevent` is single-pointer).";

    // ---- resolving the device ----

    internal static string NoRunningEmulator(IReadOnlyList<string> avds) =>
        avds.Count > 0
            ? $"No running emulator. Available AVDs: {string.Join(", ", avds)}. Pass one as 'device' to " +
              "'attach' or 'launch' to boot it."
            : "No running emulator, and no AVDs to boot (Android SDK emulator not installed, or none created " +
              "yet). Create one in Android Studio's Device Manager.";

    internal static string EmulatorNotRunning(string device, IReadOnlyList<string> avds) =>
        $"Emulator '{device}' is not running." + (avds.Count > 0
            ? $" Available AVDs: {string.Join(", ", avds)}. Pass one as 'device' to 'attach' or 'launch' to boot it."
            : "");

    internal static string NoSuchAvd(string device, IReadOnlyList<string> avds) =>
        $"No AVD named '{device}'." +
        (avds.Count > 0 ? $" Available AVDs: {string.Join(", ", avds)}." : "") +
        " Physical devices are not supported; only Android emulators can be used.";

    internal static string NeedsRunningEmulator(string action, string avd) =>
        $"'{action}' needs a running emulator: pass an emulator serial, or run 'attach' with AVD '{avd}' " +
        "first (it boots the AVD if needed).";

    internal static string NameLookupIncomplete(string serial) =>
        $"Device names are temporarily unavailable (the name lookup for {serial} didn't complete). Pass the " +
        "adb serial, or retry.";

    // ---- the panel ----

    internal static string CouldNotAttach(string reason) => $"Could not attach emulator panel: {reason}";

    internal const string NoPanelWasAttached = "No emulator panel was attached.";

    internal static string Detached(IEnumerable<string> devices) => $"Detached {string.Join(", ", devices)}.";

    internal static string PanelOpened(string device, string coordinateSpace) =>
        $"Emulator panel opened for {device}. {coordinateSpace}";

    /// <summary>The reference's <c>We</c>, which every attach and launch closes with.</summary>
    internal static string CoordinateSpace(int width, int height) =>
        $"Coordinate space for screenshot/tap/swipe: {width}x{height} pixels (origin top-left).";

    /// <summary>Its <c>Ge</c>: what the same sentence becomes when the display could not be read.</summary>
    internal const string CoordinateSpaceUnknown =
        "Coordinate space could not be determined. Take a screenshot; its result states its dimensions.";

    /// <summary>
    /// The reference's <c>d</c>: a device is named by its own name with the
    /// serial after it, or by the serial alone when there is no other name.
    /// </summary>
    internal static string DeviceLabel(string? deviceName, string serial) =>
        deviceName is { Length: > 0 } name && !string.Equals(name, serial, StringComparison.Ordinal)
            ? $"{name} ({serial})"
            : serial;

    // ---- launch ----

    internal const string LaunchNeedsAppPath = "'launch' requires app_path (path to the .apk).";

    internal const string LaunchNeedsBundleId =
        "'launch' requires bundle_id (Android package id, e.g. com.example.app).";

    internal static string Launched(string bundleId, string device, string coordinateSpace) =>
        $"Installed and launched {bundleId} on {device}. Emulator panel opened. {coordinateSpace}";

    internal static string LaunchedWithoutPanel(string bundleId, string serial, string reason) =>
        $"Installed and launched {bundleId} on {serial}. Emulator panel could not be opened: {reason}";

    // ---- screenshot ----

    internal const string ScreenshotUndecodable =
        $"{ServerName} screenshot failed: the captured image could not be decoded. Try taking the screenshot " +
        "again; if this persists, detach and re-attach the emulator.";

    internal static string ScreenshotImplausible(int width, int height) =>
        $"{ServerName} screenshot failed: the image declares {width}x{height}, which exceeds any plausible " +
        "emulator display.";

    internal static string ScreenshotDecodedImplausible(int width, int height) =>
        $"{ServerName} screenshot failed: decoded dimensions {width}x{height} exceed any plausible emulator " +
        "display.";

    internal static string ScreenshotInconsistent(int width, int height, int deviceWidth, int deviceHeight) =>
        $"{ServerName} screenshot failed: the image decodes to {width}x{height} but the device reports its " +
        $"display as {deviceWidth}x{deviceHeight}. This looks like an inconsistent capture; try taking the " +
        "screenshot again.";

    internal static string Screenshot(int width, int height, string serial) =>
        $"Screenshot: {width}x{height} from {serial}. Tap/swipe coordinates are in this image's pixel space.";

    // ---- input ----

    internal const string TapNeedsCoordinates = "'tap' requires x and y (screenshot pixels).";

    internal const string SwipeNeedsCoordinates = "'swipe' requires x, y, x2, y2 (screenshot pixels).";

    internal const string TouchPathNeedsPoints =
        "'touch_path' requires points (array of ≥2 {x, y, dt_ms?} samples).";

    internal const string TextNeedsText = "'text' requires the text field.";

    internal const string ButtonNeedsName = "'button' requires name (HOME, BACK, RECENTS, or LOCK).";

    internal static string UnsupportedButton(string name) =>
        $"Button '{name}' is not supported by this tool; use {string.Join(", ", AndroidEmulator.Buttons)}.";

    internal const string OpenUrlNeedsUrl =
        "'open_url' requires a valid URL with an allowed scheme (file:, javascript:, data:, vbscript:, blob: " +
        "are blocked).";

    internal static string Tapped(double x, double y, string serial) =>
        $"Tapped at ({Number(x)}, {Number(y)}) on {serial}.";

    internal static string Swiped(double x, double y, double x2, double y2, string serial) =>
        $"Swiped from ({Number(x)}, {Number(y)}) to ({Number(x2)}, {Number(y2)}) on {serial}.";

    /// <summary>The reference's <c>R</c>, with its default "Touch" prefix.</summary>
    internal static string TouchPath(int points, string serial)
    {
        var kept = Math.Min(points, AndroidEmulator.MaxTouchPoints);
        return kept < points
            ? $"Touch path of {kept} points (capped from {points}) on {serial}."
            : $"Touch path of {kept} points on {serial}.";
    }

    internal static string Pressed(string name, string serial) => $"Pressed {name} on {serial}.";

    internal static string Opened(string url, string serial) => $"Opened {url} on {serial}.";

    /// <summary>
    /// The reference's text result, with its two nested clauses: the dropped
    /// characters, and — inside that clause — the line breaks among them.
    /// </summary>
    internal static string Typed(string requested, int sent, int dropped, int lineBreaks, string serial)
    {
        var breaks = lineBreaks > 0
            ? $" including {lineBreaks} line break{(lineBreaks == 1 ? "" : "s")} — adjacent lines were " +
              "joined and 'button' cannot re-inject ENTER"
            : "";
        var note = dropped > 0
            ? $" ({dropped} unsupported character{(dropped == 1 ? "" : "s")} dropped{breaks} — `input " +
              "text` covers printable ASCII minus shell metacharacters ` $ ; | & < > ( ))"
            : "";

        return requested.Length > AndroidEmulator.TextCap
            ? $"Typed {sent} characters{note} on {serial} (first {AndroidEmulator.TextCap} of " +
              $"{requested.Length} sent; resume from index {AndroidEmulator.TextCap} in a follow-up call)."
            : $"Typed {sent} characters{note} on {serial}.";
    }

    /// <summary>The reference's catch-all in <c>X</c>, when the handler threw.</summary>
    internal static string ActionFailed(string action, string reason) => $"{action} failed: {reason}";

    // ---- the validator (its `we`) ----

    /// <summary>What the validator answers with: either a refusal or the parsed call.</summary>
    internal sealed record Parsed
    {
        public required string Action { get; init; }

        public string? AppPath { get; init; }

        public string? Device { get; init; }

        public string? BundleId { get; init; }

        public double? X { get; init; }

        public double? Y { get; init; }

        public double? X2 { get; init; }

        public double? Y2 { get; init; }

        public double? Duration { get; init; }

        public string? Text { get; init; }

        public IReadOnlyList<(double X, double Y, double? DtMs)>? Points { get; init; }

        public string? Name { get; init; }

        public string? Url { get; init; }
    }

    /// <summary>
    /// The reference's <c>we</c>, in its order: the action first (tested against
    /// every action the shared handler knows, but reported against this
    /// server's own list), then the unknown-parameter scan, then the three
    /// device aliases, of which at most one may be passed.
    /// </summary>
    internal static (Parsed? Call, string? Error) Validate(JsonObject arguments)
    {
        var action = arguments["action"] is JsonValue actionValue && actionValue.TryGetValue<string>(out var a)
            ? a
            : null;
        if (action is null || !AllActions.Contains(action, StringComparer.Ordinal))
        {
            return (null, $"'action' must be one of: {string.Join(", ", Actions)}");
        }

        if (Actions.Contains(action, StringComparer.Ordinal))
        {
            var unknown = arguments
                .Select(static pair => pair.Key)
                .Where(static key => !Properties.Contains(key, StringComparer.Ordinal))
                .ToList();
            if (unknown.Count > 0)
            {
                var shown = unknown
                    .Take(8)
                    .Select(static key => key.Length > 40 ? key[..40] + "…" : key)
                    .ToList();
                if (unknown.Count > 8)
                {
                    shown.Add($"and {unknown.Count - 8} more");
                }

                var hint = unknown.Any(static key => DeviceAliasHints.Contains(key.ToLowerInvariant()))
                    ? "To target a device, use 'device'. "
                    : "";
                return (null,
                    $"Unknown parameter{(unknown.Count == 1 ? "" : "s")}: {string.Join(", ", shown)}. " +
                    hint + $"Valid parameters: {string.Join(", ", Properties)}.");
            }
        }

        string? device = null;
        string? emptyError = null;
        List<string> named = [];
        foreach (var key in DeviceKeys)
        {
            if (!arguments.TryGetPropertyValue(key, out var node) || node is null)
            {
                continue;
            }

            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (text.Length == 0)
                {
                    emptyError ??= NonEmptyDevice(key);
                    continue;
                }

                named.Add($"'{key}'");
                device ??= text;
                continue;
            }

            return (null, NonEmptyDevice(key));
        }

        if (device is null && emptyError is not null)
        {
            return (null, emptyError);
        }

        if (named.Count > 1)
        {
            return (null, $"{string.Join(" and ", named)} each name the device to act on; pass only one of them.");
        }

        return (new Parsed
        {
            Action = action,
            AppPath = String(arguments, "app_path"),
            Device = device,
            BundleId = String(arguments, "bundle_id"),
            X = Number(arguments, "x"),
            Y = Number(arguments, "y"),
            X2 = Number(arguments, "x2"),
            Y2 = Number(arguments, "y2"),
            Duration = Duration(Number(arguments, "duration")),
            Text = String(arguments, "text"),
            Points = Points(arguments["points"]),
            Name = String(arguments, "name") is { } name && KnownButtons.Contains(name) ? name : null,
            Url = String(arguments, "url"),
        }, null);
    }

    internal static string NonEmptyDevice(string key) =>
        $"'{key}' must be a non-empty string naming a device.";

    /// <summary>Its <c>ye</c>: a negative duration is dropped, and 30s is the ceiling.</summary>
    private static double? Duration(double? value) =>
        value is { } seconds && seconds >= 0 ? Math.Min(seconds, 30) : null;

    private static string? String(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// The reference's <c>L</c>: a finite JSON number, and nothing else — a
    /// string that looks like one is dropped rather than parsed. The three
    /// attempts are one fact about System.Text.Json rather than three rules: a
    /// JsonValue built in process from an int is not readable as a double.
    /// </summary>
    private static double? Number(JsonObject arguments, string key)
    {
        if (arguments[key] is not JsonValue value)
        {
            return null;
        }

        double number;
        if (value.TryGetValue(out double asDouble))
        {
            number = asDouble;
        }
        else if (value.TryGetValue(out long asLong))
        {
            number = asLong;
        }
        else if (value.TryGetValue(out int asInt))
        {
            number = asInt;
        }
        else
        {
            return null;
        }

        return double.IsFinite(number) ? number : null;
    }

    /// <summary>Its <c>be</c>: one bad sample drops the whole array.</summary>
    private static IReadOnlyList<(double X, double Y, double? DtMs)>? Points(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        List<(double X, double Y, double? DtMs)> points = [];
        foreach (var item in array)
        {
            if (item is not JsonObject point)
            {
                return null;
            }

            var x = Number(point, "x");
            var y = Number(point, "y");
            if (x is null || y is null)
            {
                return null;
            }

            points.Add((x.Value, y.Value, Number(point, "dt_ms")));
        }

        return points;
    }

    /// <summary>
    /// A number as JavaScript prints it inside a template literal: an integral
    /// value has no decimal point, and the rest keep theirs.
    /// </summary>
    private static string Number(double value) =>
        value == Math.Floor(value) && double.IsFinite(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
}
