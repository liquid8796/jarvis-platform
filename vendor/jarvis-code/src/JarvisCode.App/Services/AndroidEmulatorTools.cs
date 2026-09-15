using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>One emulator the live panel is showing.</summary>
public sealed record AndroidPanelAttachment(string Serial, string? DeviceName);

/// <summary>
/// The live emulator panel, as the <c>control</c> tool reaches it. The
/// reference's <c>attach</c>/<c>detach</c> drive its SimulatorService, which
/// owns the panel; this is the same seam over this app's pane host.
/// </summary>
public interface IAndroidEmulatorPanel
{
    /// <summary>What the panel is showing, in the order it was attached.</summary>
    IReadOnlyList<AndroidPanelAttachment> Attachments { get; }

    /// <summary>Opens the pane on one emulator. Null on success, the reason otherwise.</summary>
    Task<string?> AttachAsync(string serial, string? deviceName, CancellationToken cancellationToken);

    /// <summary>Closes the pane, answering with whatever it was showing.</summary>
    IReadOnlyList<AndroidPanelAttachment> Detach();
}

/// <summary>
/// The reference's Android Emulator server: one <c>alwaysLoad</c> tool named
/// <c>control</c>, ten actions over adb, reached on the wire as
/// <c>mcp__Claude_Code_Android_Emulator__control</c>.
///
/// Ported from app.asar 1.40609.1.0, <c>.vite/build/index.chunk-DIYseJ6l.js</c>:
/// the definition <c>Qe</c>, its handler <c>Ze</c> and the wrapper <c>X</c>
/// around it, over the adb bridge in <c>index.chunk-DpY2AzrH.js</c>. The
/// declaration is recorded in
/// <c>tests/JarvisCode.Parity.Tests/Captures/Mcp/android-emulator-1.40609.1.0.json</c>
/// and pinned field by field by <c>AndroidEmulatorParityTests</c>.
///
/// The reference gates the whole server behind a remote flag
/// (<c>claudeAndroidEmulatorAccessEnabled</c>) that ships off, so no live
/// session sees it. There is no flag channel here: the gate is adb being on
/// this machine and the Settings switch this build already had.
/// </summary>
public static class AndroidEmulatorTools
{
    /// <summary>
    /// This build's own bare tools that the reference's <c>control</c> replaces.
    /// Each stays resolvable as an alias so a stored session still replays, and
    /// none is advertised — the same arrangement the claude-in-chrome rename
    /// made. <c>android_logcat</c> is deliberately absent: <c>control</c> has no
    /// counterpart action, so that one stays a tool of this app's own.
    /// </summary>
    internal static readonly IReadOnlyList<string> LegacyToolNames =
    [
        "android_devices", "android_screenshot", "android_input", "android_install",
    ];

    /// <summary>
    /// The server, or null when this machine cannot answer for it. The reference
    /// removes a server whose <c>isEnabled</c> says no; here that is the Settings
    /// switch (re-read per turn, so flipping it is felt on the next model call)
    /// and adb being installed.
    /// </summary>
    public static InternalMcpServerDefinition? Server(
        UiSettings settings,
        IAndroidEmulatorPanel? panel = null,
        Func<string?>? findAdb = null)
    {
        var locate = findAdb ?? AndroidEmulator.FindAdb;
        if (locate() is not { } adb)
        {
            return null;
        }

        return new InternalMcpServerDefinition(
            InternalMcpServerNames.AndroidEmulator,
            [new ControlTool(new AndroidEmulatorBridge(adb), settings, panel)])
        {
            AlwaysLoad = true,
            IsEnabled = _ => settings.AndroidToolsEnabled,
            LegacyNames = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["control"] = LegacyToolNames,
            },
        };
    }

    /// <summary>The one tool this server declares.</summary>
    private sealed class ControlTool(
        AndroidEmulatorBridge bridge, UiSettings settings, IAndroidEmulatorPanel? panel) : ITool
    {
        public string Name => "control";

        /// <summary>The reference's <c>Ke</c>, verbatim.</summary>
        public string Description =>
            "Run, test, and visually verify Android apps on an Android emulator on this machine (emulators " +
            "only — physical devices are not supported). Use this whenever the user wants to see or try " +
            "their Android app — \"run my app\", \"test this on Android\", \"does this look right?\" — not " +
            "only when they mention the emulator by name. If the user wants the app on their real device " +
            "(\"on my phone\", \"on my device\"), build and install with your normal build tools instead, " +
            "and say the live panel only shows emulators. 'attach' opens a live panel so the user can watch " +
            "— when the user wants to see the app, call 'attach' FIRST, before building: it opens instantly " +
            "on a running emulator; when none is running it returns an error naming the available AVDs — " +
            "pass one as 'device' to 'attach' to boot it (a cold boot the user must approve). Then build the " +
            "APK with gradle; 'launch' installs and starts it into the panel (it re-attaches on its own, but " +
            "do not rely on that instead of the early attach). Screenshots and tap/swipe/text verification " +
            "are headless and need no panel. Don't open the panel when the user only asked to build/compile " +
            "or to run unit tests. Coordinates are in screenshot pixels (origin top-left) — the same space " +
            "as the PNG returned by 'screenshot', whose result states its dimensions; 'attach'/'launch' also " +
            "report this coordinate space. The tool maps coordinates to device pixels itself — do not " +
            "rescale them.";

        public JsonObject InputSchema =>
            CapturedMcpSchemas.Schema(InternalMcpServerNames.AndroidEmulator, "control");

        /// <summary>
        /// Seven of the ten actions drive the device, so the tool as a whole is
        /// not read-only and every call reaches the permission gate — which is
        /// where this port asks what the reference asks with its own per-device
        /// consent dialog.
        /// </summary>
        public bool IsReadOnly => false;

        public string DescribeCall(JsonObject arguments) =>
            $"AndroidEmulator({JsonArgs.GetString(arguments, "action") ?? "?"})";

        public async Task<ToolResult> ExecuteAsync(
            JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            // The reference re-checks its gates inside the handler as well as in
            // isEnabled, so a switch flipped mid-turn answers rather than acting.
            if (!settings.AndroidToolsEnabled)
            {
                return ToolResult.Error(AndroidEmulatorMessages.TurnedOffInSettings);
            }

            var (call, error) = AndroidEmulatorMessages.Validate(arguments);
            if (call is null)
            {
                return ToolResult.Error(error!);
            }

            try
            {
                return await RunAsync(call, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or NotSupportedException or ArgumentException)
            {
                // The reference's own catch in X: the action's name, then what went wrong.
                return ToolResult.Error(AndroidEmulatorMessages.ActionFailed(call.Action, ex.Message));
            }
        }

        private async Task<ToolResult> RunAsync(
            AndroidEmulatorMessages.Parsed call, CancellationToken cancellationToken)
        {
            // detach answers before anything device-related, as the reference's does.
            if (call.Action == "detach")
            {
                var detached = panel?.Detach() ?? [];
                return ToolResult.Success(detached.Count == 0
                    ? AndroidEmulatorMessages.NoPanelWasAttached
                    : AndroidEmulatorMessages.Detached(detached.Select(static a =>
                        AndroidEmulatorMessages.DeviceLabel(a.DeviceName, a.Serial))));
            }

            if (call.Action is "build" or "build_status")
            {
                return ToolResult.Error(AndroidEmulatorMessages.BuildIsIosOnly);
            }

            if (call.Action == "touch2_path")
            {
                return ToolResult.Error(AndroidEmulatorMessages.Touch2PathIsIosOnly);
            }

            var devices = await bridge.ListDevicesAsync(cancellationToken);
            var (serial, resolveError) = Resolve(devices, call.Device);
            if (resolveError is not null)
            {
                return ToolResult.Error(resolveError);
            }

            if (serial is null)
            {
                var boot = await BootAsync(call, devices, cancellationToken);
                if (boot.Error is not null)
                {
                    return ToolResult.Error(boot.Error);
                }

                serial = boot.Serial!;
                devices = await bridge.ListDevicesAsync(cancellationToken);
            }

            // A listed name already carries its serial (the reference's own
            // `{AVD} ({serial})`), and every sentence that names a device adds
            // one itself — so the label takes the bare half, its `wi`.
            var deviceName = devices.FirstOrDefault(d => d.Serial == serial)?.Name is { Length: > 0 } listed
                ? AndroidEmulator.StripSerialSuffix(listed)
                : null;
            var label = AndroidEmulatorMessages.DeviceLabel(deviceName, serial);
            return call.Action switch
            {
                "attach" => await AttachAsync(serial, deviceName, label, cancellationToken),
                "launch" => await LaunchAsync(call, serial, deviceName, cancellationToken),
                "screenshot" => await ScreenshotAsync(serial, cancellationToken),
                "tap" => await TapAsync(call, serial, cancellationToken),
                "swipe" => await SwipeAsync(call, serial, cancellationToken),
                "touch_path" => await TouchPathAsync(call, serial, cancellationToken),
                "text" => await TypeAsync(call, serial, cancellationToken),
                "button" => await ButtonAsync(call, serial, cancellationToken),
                "open_url" => await OpenUrlAsync(call, serial, cancellationToken),
                _ => ToolResult.Error($"'action' must be one of: {string.Join(", ", AndroidEmulatorMessages.Actions)}"),
            };
        }

        // ---- resolving the device (the reference's Xe over its Ti/Ci/Si) ----

        private (string? Serial, string? Error) Resolve(
            IReadOnlyList<AndroidEmulator.Device> devices, string? requested)
        {
            if (requested is not { Length: > 0 })
            {
                // The panel's own attachment first, then the first booted emulator.
                var attached = panel?.Attachments.FirstOrDefault()?.Serial;
                if (attached is { Length: > 0 } && devices.Any(d => d.Serial == attached))
                {
                    return (attached, null);
                }

                return (devices.FirstOrDefault(static d => d.State == "Booted")?.Serial, null);
            }

            // The reference's rule: a spaceless string is always read as a serial.
            if (!requested.Contains(' '))
            {
                return AndroidEmulator.IsEmulatorSerial(requested) && devices.Any(d => d.Serial == requested)
                    ? (requested, null)
                    : (null, null);
            }

            // Its Ci: an exact name match, refusing an ambiguous one by name.
            var booted = devices.Where(static d => d.State == "Booted").ToList();
            var matches = booted.Where(d => NameMatches(d, requested)).ToList();
            if (matches.Count > 1)
            {
                return (null,
                    $"Multiple devices match '{requested}': {string.Join(", ", matches.Select(static m => m.Serial))}. " +
                    "Pass the adb serial instead.");
            }

            return matches.Count == 1 ? (matches[0].Serial, null) : (null, null);
        }

        /// <summary>The reference's <c>Si</c>: the bare name, or the name with its serial appended.</summary>
        private static bool NameMatches(AndroidEmulator.Device device, string requested)
        {
            if (string.Equals(device.Name, requested, StringComparison.Ordinal))
            {
                return true;
            }

            var suffix = $" ({device.Serial})";
            return (device.Name.EndsWith(suffix, StringComparison.Ordinal) &&
                    string.Equals(device.Name[..^suffix.Length], requested, StringComparison.Ordinal)) ||
                string.Equals(device.Name + suffix, requested, StringComparison.Ordinal);
        }

        /// <summary>
        /// The reference's unresolved branch: name the AVDs, refuse an action
        /// that needs a running emulator, and boot the named AVD for attach and
        /// launch — which are the only two actions it will cold-boot for.
        /// </summary>
        private async Task<(string? Serial, string? Error)> BootAsync(
            AndroidEmulatorMessages.Parsed call,
            IReadOnlyList<AndroidEmulator.Device> devices,
            CancellationToken cancellationToken)
        {
            var avds = await AndroidEmulatorBridge.ListAvdsAsync(cancellationToken);
            if (call.Device is not { Length: > 0 } requested)
            {
                return (null, AndroidEmulatorMessages.NoRunningEmulator(avds));
            }

            var avd = avds.FirstOrDefault(name => AndroidEmulator.AvdNamesMatch(name, requested));
            if (avd is null)
            {
                return (null, AndroidEmulator.IsEmulatorSerial(requested)
                    ? AndroidEmulatorMessages.EmulatorNotRunning(requested, avds)
                    : AndroidEmulatorMessages.NoSuchAvd(requested, avds));
            }

            if (call.Action is not ("attach" or "launch"))
            {
                return (null, AndroidEmulatorMessages.NeedsRunningEmulator(call.Action, avd));
            }

            if (call.Action == "launch" && Launchable(call) is { } launchError)
            {
                return (null, launchError);
            }

            // The reference refuses a name lookup it could not complete rather
            // than guessing which device the name meant.
            if (requested.Contains(' ') &&
                devices.Any(static d => d.State == "Booted" && d.Serial == d.Name))
            {
                var unnamed = devices.First(static d => d.State == "Booted" && d.Serial == d.Name);
                return (null, AndroidEmulatorMessages.NameLookupIncomplete(unnamed.Serial));
            }

            var known = devices.Select(static d => d.Serial).ToHashSet(StringComparer.Ordinal);
            return await bridge.BootAvdAsync(avd, known, cancellationToken);
        }

        // ---- the actions ----

        private async Task<ToolResult> AttachAsync(
            string serial, string? deviceName, string label, CancellationToken cancellationToken)
        {
            if (panel is null)
            {
                return ToolResult.Error(AndroidEmulatorMessages.CouldNotAttach(
                    "this session has no window to open it in"));
            }

            if (await panel.AttachAsync(serial, deviceName, cancellationToken) is { } failure)
            {
                return ToolResult.Error(AndroidEmulatorMessages.CouldNotAttach(failure));
            }

            return ToolResult.Success(AndroidEmulatorMessages.PanelOpened(
                label, await CoordinateSpaceAsync(serial, cancellationToken)));
        }

        private async Task<ToolResult> LaunchAsync(
            AndroidEmulatorMessages.Parsed call, string serial, string? deviceName, CancellationToken cancellationToken)
        {
            if (Launchable(call) is { } error)
            {
                return ToolResult.Error(error);
            }

            var apk = call.AppPath!;
            if (!File.Exists(apk))
            {
                return ToolResult.Error(AndroidEmulatorMessages.ActionFailed("launch", "apkPath does not exist"));
            }

            if (!apk.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Error(AndroidEmulatorMessages.ActionFailed("launch", "apkPath must be a .apk file"));
            }

            var (ok, output) = await bridge.InstallAndLaunchAsync(serial, apk, call.BundleId!, cancellationToken);
            if (!ok)
            {
                return ToolResult.Error(AndroidEmulatorMessages.ActionFailed("launch", output));
            }

            var attachFailure = panel is null
                ? "this session has no window to open it in"
                : await panel.AttachAsync(serial, deviceName, cancellationToken);
            return ToolResult.Success(attachFailure is null
                ? AndroidEmulatorMessages.Launched(
                    call.BundleId!,
                    AndroidEmulatorMessages.DeviceLabel(deviceName, serial),
                    await CoordinateSpaceAsync(serial, cancellationToken))
                : AndroidEmulatorMessages.LaunchedWithoutPanel(call.BundleId!, serial, attachFailure));
        }

        /// <summary>The reference's <c>Ye</c>, in its order: app_path first, then bundle_id.</summary>
        private static string? Launchable(AndroidEmulatorMessages.Parsed call) =>
            call.AppPath is not { Length: > 0 }
                ? AndroidEmulatorMessages.LaunchNeedsAppPath
                : call.BundleId is { Length: > 0 } id && IsPackageId(id)
                    ? null
                    : AndroidEmulatorMessages.LaunchNeedsBundleId;

        /// <summary>The reference's <c>Kn</c>.</summary>
        private static bool IsPackageId(string value) =>
            System.Text.RegularExpressions.Regex.IsMatch(
                value, @"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$");

        private async Task<ToolResult> ScreenshotAsync(string serial, CancellationToken cancellationToken)
        {
            var (exit, bytes, _) = await bridge.RunBinaryAsync(
                TimeSpan.FromSeconds(30), cancellationToken, "-s", serial, "exec-out", "screencap", "-p");
            if (exit != 0 || bytes.Length < 24)
            {
                return ToolResult.Error(AndroidEmulatorMessages.ScreenshotUndecodable);
            }

            if (ReadPngHeader(bytes) is not { } declared)
            {
                return ToolResult.Error(AndroidEmulatorMessages.ScreenshotUndecodable);
            }

            if (declared.Width is < 1 or > 8192 || declared.Height is < 1 or > 8192)
            {
                return ToolResult.Error(
                    AndroidEmulatorMessages.ScreenshotImplausible(declared.Width, declared.Height));
            }

            BitmapSource decoded;
            try
            {
                decoded = Decode(bytes);
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
            {
                return ToolResult.Error(AndroidEmulatorMessages.ScreenshotUndecodable);
            }

            var width = decoded.PixelWidth;
            var height = decoded.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return ToolResult.Error(AndroidEmulatorMessages.ScreenshotUndecodable);
            }

            if (width > 8192 || height > 8192)
            {
                return ToolResult.Error(AndroidEmulatorMessages.ScreenshotDecodedImplausible(width, height));
            }

            // The reference cross-checks the decoded size against wm size, either
            // way round, and refuses a capture the two disagree about.
            var reported = AndroidEmulator.ParseDisplaySize(
                (await bridge.ShellAsync(serial, cancellationToken, "wm", "size")).Output);
            if (reported is { } size &&
                !((width == size.Width && height == size.Height) || (width == size.Height && height == size.Width)))
            {
                return ToolResult.Error(
                    AndroidEmulatorMessages.ScreenshotInconsistent(width, height, size.Width, size.Height));
            }

            var (shotWidth, shotHeight) = AndroidEmulator.ScreenshotSize(width, height);
            var payload = shotWidth == width && shotHeight == height
                ? bytes
                : EncodePng(new TransformedBitmap(decoded, new ScaleTransform(
                    (double)shotWidth / width, (double)shotHeight / height)));

            return ToolResult.WithImage(
                AndroidEmulatorMessages.Screenshot(shotWidth, shotHeight, serial),
                new ImageBlock("image/png", Convert.ToBase64String(payload)));
        }

        private async Task<ToolResult> TapAsync(
            AndroidEmulatorMessages.Parsed call, string serial, CancellationToken cancellationToken)
        {
            if (call.X is null || call.Y is null)
            {
                return ToolResult.Error(AndroidEmulatorMessages.TapNeedsCoordinates);
            }

            var map = await MapAsync(serial, cancellationToken);
            var x = map.X(call.X.Value);
            var y = map.Y(call.Y.Value);

            // The reference's tap: a duration turns it into a same-point swipe,
            // which is how `input` expresses a long press.
            if (call.Duration is { } seconds)
            {
                var ms = AndroidEmulator.RoundHalfUp(Math.Min(seconds, 30) * 1000);
                await bridge.ShellAsync(serial, cancellationToken,
                    "input", "swipe", $"{x}", $"{y}", $"{x}", $"{y}", $"{ms}");
            }
            else
            {
                await bridge.ShellAsync(serial, cancellationToken, "input", "tap", $"{x}", $"{y}");
            }

            return ToolResult.Success(AndroidEmulatorMessages.Tapped(call.X.Value, call.Y.Value, serial));
        }

        private async Task<ToolResult> SwipeAsync(
            AndroidEmulatorMessages.Parsed call, string serial, CancellationToken cancellationToken)
        {
            if (call.X is null || call.Y is null || call.X2 is null || call.Y2 is null)
            {
                return ToolResult.Error(AndroidEmulatorMessages.SwipeNeedsCoordinates);
            }

            var map = await MapAsync(serial, cancellationToken);
            var ms = AndroidEmulator.RoundHalfUp(Math.Min(call.Duration ?? 0.3, 30) * 1000);
            await bridge.ShellAsync(serial, cancellationToken,
                "input", "swipe",
                $"{map.X(call.X.Value)}", $"{map.Y(call.Y.Value)}",
                $"{map.X(call.X2.Value)}", $"{map.Y(call.Y2.Value)}", $"{ms}");
            return ToolResult.Success(AndroidEmulatorMessages.Swiped(
                call.X.Value, call.Y.Value, call.X2.Value, call.Y2.Value, serial));
        }

        /// <summary>
        /// The reference's touchPath: <c>input motionevent</c> DOWN, then a MOVE
        /// per sample with its own delay, and an UP that runs even when a MOVE
        /// failed. Its own note says the fling velocity an app would see is lost
        /// because <c>motionevent</c> resets downTime per call.
        /// </summary>
        private async Task<ToolResult> TouchPathAsync(
            AndroidEmulatorMessages.Parsed call, string serial, CancellationToken cancellationToken)
        {
            if (call.Points is not { Count: >= 2 } points)
            {
                return ToolResult.Error(AndroidEmulatorMessages.TouchPathNeedsPoints);
            }

            var map = await MapAsync(serial, cancellationToken);
            var budget = 0;
            List<(int X, int Y, int Delay)> samples = [];
            foreach (var point in points)
            {
                if (samples.Count >= AndroidEmulator.MaxTouchPoints)
                {
                    break;
                }

                var requested = (int)Math.Clamp(point.DtMs ?? 0, 0, AndroidEmulator.MaxTouchSampleMs);
                var delay = Math.Min(requested, AndroidEmulator.MaxTouchPathMs - budget);
                budget += delay;
                samples.Add((map.X(point.X), map.Y(point.Y), delay));
            }

            // The reference keeps the path's real last point when it capped the count.
            if (samples.Count < points.Count)
            {
                var last = points[^1];
                samples[^1] = (map.X(last.X), map.Y(last.Y), samples[^1].Delay);
            }

            try
            {
                await bridge.ShellAsync(serial, cancellationToken,
                    "input", "motionevent", "DOWN", $"{samples[0].X}", $"{samples[0].Y}");
                for (var i = 1; i < samples.Count; i++)
                {
                    if (samples[i].Delay > 0)
                    {
                        await Task.Delay(samples[i].Delay, cancellationToken);
                    }

                    await bridge.ShellAsync(serial, cancellationToken,
                        "input", "motionevent", "MOVE", $"{samples[i].X}", $"{samples[i].Y}");
                }
            }
            finally
            {
                await bridge.ShellAsync(serial, CancellationToken.None,
                    "input", "motionevent", "UP", $"{samples[^1].X}", $"{samples[^1].Y}");
            }

            return ToolResult.Success(AndroidEmulatorMessages.TouchPath(points.Count, serial));
        }

        private async Task<ToolResult> TypeAsync(
            AndroidEmulatorMessages.Parsed call, string serial, CancellationToken cancellationToken)
        {
            if (call.Text is not { Length: > 0 } requested)
            {
                return ToolResult.Error(AndroidEmulatorMessages.TextNeedsText);
            }

            var window = requested.Length > AndroidEmulator.TextCap
                ? requested[..AndroidEmulator.TextCap]
                : requested;
            var sent = AndroidEmulator.SanitizeText(window);
            var dropped = AndroidEmulator.CodePointCount(window) - sent.Length;
            if (sent.Length > 0 && AndroidEmulator.EncodeInputText(sent) is { } encoded)
            {
                await bridge.ShellAsync(serial, cancellationToken, "input", "text", encoded);
            }

            return ToolResult.Success(AndroidEmulatorMessages.Typed(
                requested, sent.Length, dropped, AndroidEmulator.CountLineBreaks(window), serial));
        }

        private async Task<ToolResult> ButtonAsync(
            AndroidEmulatorMessages.Parsed call, string serial, CancellationToken cancellationToken)
        {
            if (call.Name is not { Length: > 0 } name)
            {
                return ToolResult.Error(AndroidEmulatorMessages.ButtonNeedsName);
            }

            if (!AndroidEmulator.ButtonKeycodes.TryGetValue(name, out var keycode))
            {
                return ToolResult.Error(AndroidEmulatorMessages.UnsupportedButton(name));
            }

            await bridge.ShellAsync(serial, cancellationToken, "input", "keyevent", $"{keycode}");
            return ToolResult.Success(AndroidEmulatorMessages.Pressed(name, serial));
        }

        private async Task<ToolResult> OpenUrlAsync(
            AndroidEmulatorMessages.Parsed call, string serial, CancellationToken cancellationToken)
        {
            if (AndroidEmulator.NormalizeUrl(call.Url) is not { } url)
            {
                return ToolResult.Error(AndroidEmulatorMessages.OpenUrlNeedsUrl);
            }

            if (!AndroidEmulator.IsUrlShellSafe(url))
            {
                return ToolResult.Error(AndroidEmulatorMessages.ActionFailed(
                    "open_url",
                    "url contains a single quote, backslash, or control character; percent-encode it " +
                    "(e.g. ' → %27) and retry"));
            }

            // The reference passes the whole am invocation as one exec-out word,
            // quoting the url so the device's shell reads it as one argument.
            await bridge.ShellAsync(serial, cancellationToken,
                $"am start -a android.intent.action.VIEW -d '{url}'");
            return ToolResult.Success(AndroidEmulatorMessages.Opened(url, serial));
        }

        // ---- the coordinate space ----

        private async Task<(Func<double, int> X, Func<double, int> Y)> MapAsync(
            string serial, CancellationToken cancellationToken)
        {
            if (await bridge.DisplaySizeAsync(serial, cancellationToken) is not { } display)
            {
                // Nothing to scale by: the caller's numbers are already device pixels.
                return (static x => AndroidEmulator.RoundHalfUp(x), static y => AndroidEmulator.RoundHalfUp(y));
            }

            var (shotWidth, shotHeight) = AndroidEmulator.ScreenshotSize(display.Width, display.Height);
            return (x => AndroidEmulator.MapCoordinate(x, shotWidth, display.Width),
                y => AndroidEmulator.MapCoordinate(y, shotHeight, display.Height));
        }

        /// <summary>The reference's <c>Ge</c>: its <c>We</c>, or the sentence it falls back to.</summary>
        private async Task<string> CoordinateSpaceAsync(string serial, CancellationToken cancellationToken)
        {
            if (await bridge.DisplaySizeAsync(serial, cancellationToken) is not { } display)
            {
                return AndroidEmulatorMessages.CoordinateSpaceUnknown;
            }

            var (width, height) = AndroidEmulator.ScreenshotSize(display.Width, display.Height);
            return AndroidEmulatorMessages.CoordinateSpace(width, height);
        }

        // ---- PNG ----

        /// <summary>The reference's <c>Ue</c>: the IHDR dimensions, without decoding.</summary>
        private static (int Width, int Height)? ReadPngHeader(byte[] bytes)
        {
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
            {
                return null;
            }

            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
                System.Text.Encoding.Latin1.GetString(bytes, 12, 4) != "IHDR")
            {
                return null;
            }

            return ((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)),
                (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
        }

        private static BitmapSource Decode(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }

        private static byte[] EncodePng(BitmapSource image)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
