using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// The adb half of the reference's Android Emulator server, ported from the
/// desktop's own bridge (app.asar 1.40609.1.0,
/// <c>.vite/build/index.chunk-DpY2AzrH.js</c>, the <c>Di</c> device service and
/// the helpers around it). Every regex, constant and command line here is the
/// reference's; nothing is invented.
///
/// Emulator serials only: the reference's <c>tr</c> is <c>/^emulator-\d+$/</c>
/// and a physical device never gets past it.
/// </summary>
internal static class AndroidEmulator
{
    /// <summary>The reference's <c>Nur</c>: what counts as an adb emulator serial.</summary>
    private static readonly Regex SerialPattern = new(@"^emulator-\d+$", RegexOptions.Compiled);

    /// <summary>Its <c>hr</c>: what counts as a configured AVD name.</summary>
    private static readonly Regex AvdPattern = new(@"^[A-Za-z0-9._][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    /// <summary>Its <c>jr</c>: one row of <c>adb devices -l</c>.</summary>
    private static readonly Regex DeviceRow = new(
        @"^(\S+)\s+(device|offline|unauthorized)\b(?:.*?\bmodel:(\S+))?", RegexOptions.Compiled);

    /// <summary>Its <c>Fr</c>: the size <c>wm size</c> prints, override winning.</summary>
    private static readonly Regex DisplaySize = new(
        @"(?:Override|Physical) size:\s*(\d+)x(\d+)", RegexOptions.Compiled);

    /// <summary>Its <c>Lr</c>: the rotation <c>dumpsys input</c> reports.</summary>
    private static readonly Regex SurfaceOrientation = new(@"SurfaceOrientation:\s*(\d)", RegexOptions.Compiled);

    /// <summary>Its <c>$r</c>: what <c>input text</c> cannot carry.</summary>
    private static readonly Regex Unsupported = new(@"[^\x20-\x7e]|[`$;|&<>()]", RegexOptions.Compiled);

    /// <summary>Its line-break count for the text result.</summary>
    private static readonly Regex LineBreaks = new("\r\n|[\r\n\u2028\u2029]", RegexOptions.Compiled);

    /// <summary>Its <c>an</c>: how much of a 'text' payload one call sends.</summary>
    internal const int TextCap = 4096;

    /// <summary>Its <c>B</c> cap: a touch path is clamped to this many samples.</summary>
    internal const int MaxTouchPoints = 256;

    /// <summary>Its <c>B</c> budget: the whole path's delays are clamped to 30s.</summary>
    internal const int MaxTouchPathMs = 30_000;

    /// <summary>Its per-sample delay clamp.</summary>
    internal const int MaxTouchSampleMs = 1000;

    /// <summary>Its <c>On</c>: the longest side <c>screenrecord</c> is asked for.</summary>
    internal const int StreamMaxSide = 1440;

    /// <summary>Its <c>Zur</c>, in declaration order — the buttons 'button' accepts.</summary>
    internal static readonly IReadOnlyList<string> Buttons = ["HOME", "BACK", "RECENTS", "LOCK"];

    /// <summary>Its <c>Qr</c>, for the four buttons this server offers.</summary>
    internal static readonly IReadOnlyDictionary<string, int> ButtonKeycodes =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["HOME"] = 3,
            ["BACK"] = 4,
            ["RECENTS"] = 187,
            ["LOCK"] = 26,
        };

    /// <summary>Its <c>Bur</c>: the schemes 'open_url' refuses.</summary>
    internal static readonly IReadOnlySet<string> BlockedUrlSchemes =
        new HashSet<string>(StringComparer.Ordinal) { "file", "javascript", "data", "vbscript", "blob" };

    /// <summary>Its <c>bi</c>: what an <c>am start -d '…'</c> argument may not contain.</summary>
    private static readonly Regex UnsafeUrl = new(@"['\\\x00-\x1f]", RegexOptions.Compiled);

    internal static bool IsEmulatorSerial(string? value) =>
        value is { Length: > 0 } && SerialPattern.IsMatch(value);

    internal static bool IsAvdName(string? value) =>
        value is { Length: > 0 } && AvdPattern.IsMatch(value);

    /// <summary>
    /// The reference's <c>wi</c>: a listed device's name carries its serial in
    /// parentheses, and this takes it back off for the sentences that add one
    /// themselves.
    /// </summary>
    internal static string StripSerialSuffix(string name) =>
        SerialSuffix.Replace(name, "");

    private static readonly Regex SerialSuffix = new(@" \(emulator-\d+\)$", RegexOptions.Compiled);

    /// <summary>Its <c>xr</c>: AVD names compare with underscores read as spaces.</summary>
    internal static bool AvdNamesMatch(string left, string right) =>
        string.Equals(left.Replace('_', ' '), right.Replace('_', ' '), StringComparison.Ordinal);

    /// <summary>One row of <c>adb devices -l</c>, in the reference's own shape.</summary>
    internal sealed record Device(string Serial, string State, string Name);

    /// <summary>The reference's <c>B</c>: emulator rows only, "device" reading as Booted.</summary>
    internal static IReadOnlyList<Device> ParseDevices(string output)
    {
        List<Device> devices = [];
        foreach (var line in output.Split('\n'))
        {
            var match = DeviceRow.Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var serial = match.Groups[1].Value;
            if (!IsEmulatorSerial(serial))
            {
                continue;
            }

            var model = match.Groups[3].Success ? match.Groups[3].Value : serial;
            var name = model.Replace('_', ' ');
            devices.Add(new Device(
                serial,
                match.Groups[2].Value == "device" ? "Booted" : match.Groups[2].Value,
                name.Length == 0 ? serial : name));
        }

        return devices;
    }

    /// <summary>The reference's <c>_r</c>: the AVD names <c>emulator -list-avds</c> printed.</summary>
    internal static IReadOnlyList<string> ParseAvds(string output) =>
    [
        .. output.Split('\n').Select(static line => line.Trim()).Where(IsAvdName),
    ];

    /// <summary>
    /// The reference's <c>Ir</c>: the last size line wins (an override overrides
    /// the physical size), and a size outside 1..8192 reads as unparsed.
    /// </summary>
    internal static (int Width, int Height)? ParseDisplaySize(string wmSizeOutput)
    {
        (int Width, int Height)? found = null;
        foreach (Match match in DisplaySize.Matches(wmSizeOutput))
        {
            found = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        if (found is not { } size)
        {
            return null;
        }

        return size.Width is < 1 or > 8192 || size.Height is < 1 or > 8192 ? null : size;
    }

    /// <summary>The reference's <c>Rr</c>: 0 unless dumpsys named 1, 2 or 3.</summary>
    internal static int ParseRotation(string dumpsysInputOutput)
    {
        var match = SurfaceOrientation.Match(dumpsysInputOutput);
        if (!match.Success)
        {
            return 0;
        }

        var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return value is 1 or 2 or 3 ? value : 0;
    }

    /// <summary>
    /// The reference's <c>zr</c>: <c>wm size</c> with the rotation applied — a
    /// quarter turn either way swaps the two sides.
    /// </summary>
    internal static (int Width, int Height) ApplyRotation((int Width, int Height) size, int rotation) =>
        rotation is 1 or 3 ? (size.Height, size.Width) : size;

    // ---- the image budget: the reference's Tu/ju, its dV table and its fV ----

    private const int PxPerToken = 28;
    private const int MaxTargetPx = 1568;
    private const int MaxTargetTokens = 1568;

    /// <summary>Its <c>Xpn</c>.</summary>
    private static int TokensPerSide(int side, int pxPerToken) => ((side - 1) / pxPerToken) + 1;

    /// <summary>Its <c>Zpn</c>.</summary>
    private static int Tokens(int width, int height, int pxPerToken) =>
        TokensPerSide(width, pxPerToken) * TokensPerSide(height, pxPerToken);

    /// <summary>
    /// JavaScript's <c>Math.round</c>: half away from zero on the positive side,
    /// where .NET's default rounds to even. Every coordinate here is
    /// non-negative, so half-up is the whole difference.
    /// </summary>
    internal static int RoundHalfUp(double value) => (int)Math.Floor(value + 0.5);

    /// <summary>
    /// The reference's <c>fV</c>: the largest size inside its pixel and token
    /// caps that keeps the aspect ratio, found by the same binary search.
    /// </summary>
    private static (int Width, int Height) Fit(int width, int height)
    {
        if (width <= MaxTargetPx && height <= MaxTargetPx && Tokens(width, height, PxPerToken) <= MaxTargetTokens)
        {
            return (width, height);
        }

        if (height > width)
        {
            var (w, h) = Fit(height, width);
            return (h, w);
        }

        var ratio = (double)width / height;
        var high = width;
        var low = 1;
        while (true)
        {
            if (low + 1 == high)
            {
                return (low, Math.Max(RoundHalfUp(low / ratio), 1));
            }

            var mid = (low + high) / 2;
            var midHeight = Math.Max(RoundHalfUp(mid / ratio), 1);
            if (mid <= MaxTargetPx && Tokens(mid, midHeight, PxPerToken) <= MaxTargetTokens)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }
    }

    /// <summary>
    /// The reference's <c>Z</c>: the screenshot's own pixel space, which is also
    /// the coordinate space every tap, swipe and touch path is expressed in.
    /// </summary>
    internal static (int Width, int Height) ScreenshotSize(int deviceWidth, int deviceHeight)
    {
        var (width, height) = Fit(deviceWidth, deviceHeight);
        return (Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// The reference's <c>Q</c>: one screenshot-space coordinate mapped into
    /// device pixels, clamped to the display.
    /// </summary>
    internal static int MapCoordinate(double value, int fromSide, int toSide) =>
        Math.Max(0, Math.Min(toSide - 1, RoundHalfUp(value * toSide / Math.Max(1, fromSide))));

    /// <summary>
    /// The reference's <c>hi</c>: the size it asks <c>screenrecord</c> for —
    /// the display scaled so its longest side is at most 1440, each side then
    /// floored to an even number, which h264 requires.
    /// </summary>
    internal static string StreamSize(int width, int height)
    {
        var scale = Math.Min(1.0, (double)StreamMaxSide / Math.Max(width, height));
        static int Even(double value) => (int)Math.Floor(value / 2) * 2;
        return $"{Even(width * scale)}x{Even(height * scale)}";
    }

    // ---- text ----

    /// <summary>
    /// The reference's <c>ni</c>: everything <c>input text</c> cannot carry is
    /// dropped — non-printable-ASCII plus the shell metacharacters adb's own
    /// shell would eat.
    /// </summary>
    internal static string SanitizeText(string value) => Unsupported.Replace(value, "");

    /// <summary>The reference's <c>ti</c>: a space is <c>%s</c> to <c>input text</c>.</summary>
    internal static string? EncodeInputText(string value) =>
        Unsupported.IsMatch(value) ? null : value.Replace(" ", "%s");

    /// <summary>How many code points a string carries — the reference's <c>/./gsu</c> count.</summary>
    internal static int CodePointCount(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i += char.IsSurrogatePair(value, i) ? 2 : 1)
        {
            count++;
        }

        return count;
    }

    /// <summary>How many line breaks a string carries, counted the reference's way.</summary>
    internal static int CountLineBreaks(string value) => LineBreaks.Matches(value).Count;

    /// <summary>
    /// The reference's URL check: parseable, and not one of the five schemes it
    /// blocks. Returns the normalized href it would pass to <c>am start</c>.
    /// </summary>
    internal static string? NormalizeUrl(string? value)
    {
        if (value is not { Length: > 0 } || value.Contains('\0'))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        return BlockedUrlSchemes.Contains(scheme) ? null : uri.AbsoluteUri;
    }

    /// <summary>The reference's <c>xi</c> guard on the <c>am start -d '…'</c> argument.</summary>
    internal static bool IsUrlShellSafe(string url) => !UnsafeUrl.IsMatch(url);

    // ---- where adb and the emulator launcher live ----

    /// <summary>PATH first, then ANDROID_HOME / ANDROID_SDK_ROOT / the default SDK spot.</summary>
    internal static string? FindAdb() => FindSdkTool(Path.Combine("platform-tools", "adb.exe"), "adb.exe");

    /// <summary>The AVD launcher, which is what <c>-list-avds</c> is asked of.</summary>
    internal static string? FindEmulator() => FindSdkTool(Path.Combine("emulator", "emulator.exe"), "emulator.exe");

    private static string? FindSdkTool(string relative, string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var trimmed = directory.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(trimmed, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var root in new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        })
        {
            if (root is { Length: > 0 })
            {
                var candidate = Path.Combine(root, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Runs adb. Separated from <see cref="AndroidEmulator"/> so the parsing and
/// arithmetic above stay testable without a device on the machine.
/// </summary>
internal sealed class AndroidEmulatorBridge(string adbPath)
{
    /// <summary>The reference's <c>k</c>: how long an ordinary adb call may take.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Its install timeout (<c>12e4</c>).</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(120);

    public string AdbPath => adbPath;

    /// <summary>Runs adb and returns its text output.</summary>
    public async Task<(int ExitCode, string Output)> RunAsync(
        CancellationToken cancellationToken, params string[] arguments)
    {
        var (exit, bytes, error) = await RunBinaryAsync(Timeout, cancellationToken, arguments);
        var text = Encoding.UTF8.GetString(bytes);
        return (exit, (text + error).Trim());
    }

    /// <summary>Runs <c>adb -s {serial} exec-out {command}</c>, the way the reference's <c>I</c> does.</summary>
    public Task<(int ExitCode, string Output)> ShellAsync(
        string serial, CancellationToken cancellationToken, params string[] command) =>
        RunAsync(cancellationToken, ["-s", serial, "exec-out", .. command]);

    /// <summary>Runs adb with raw stdout — <c>screencap -p</c> returns PNG bytes.</summary>
    public async Task<(int ExitCode, byte[] Output, string Error)> RunBinaryAsync(
        TimeSpan timeout, CancellationToken cancellationToken, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(adbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            using var buffer = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                return (-1, [], $"adb timed out after {timeout.TotalSeconds:0}s.");
            }

            await Task.WhenAny(Task.WhenAll(copy, stderr), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            return (process.ExitCode, buffer.ToArray(), stderr.IsCompletedSuccessfully ? stderr.Result.Trim() : "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, [], $"adb failed to start: {ex.Message}");
        }
    }

    /// <summary>Installs and starts an apk — the reference's <c>yi</c>, command for command.</summary>
    public async Task<(bool Ok, string Output)> InstallAndLaunchAsync(
        string serial, string apkPath, string packageId, CancellationToken cancellationToken)
    {
        var (exit, bytes, error) = await RunBinaryAsync(
            InstallTimeout, cancellationToken, "-s", serial, "install", "-r", apkPath);
        var output = (Encoding.UTF8.GetString(bytes) + error).Trim();
        if (exit != 0 || !output.Contains("Success", StringComparison.Ordinal))
        {
            return (false, output);
        }

        var (launchExit, launchOutput) = await ShellAsync(
            serial, cancellationToken, "monkey", "-p", packageId, "-c", "android.intent.category.LAUNCHER", "1");
        return launchExit == 0 ? (true, "") : (false, launchOutput);
    }

    /// <summary>The reference's <c>zr</c>: the display size with its rotation applied.</summary>
    public async Task<(int Width, int Height)?> DisplaySizeAsync(string serial, CancellationToken cancellationToken)
    {
        var size = await ShellAsync(serial, cancellationToken, "wm", "size");
        var rotation = await ShellAsync(serial, cancellationToken, "dumpsys", "input");
        return AndroidEmulator.ParseDisplaySize(size.Output) is { } parsed
            ? AndroidEmulator.ApplyRotation(parsed, AndroidEmulator.ParseRotation(rotation.Output))
            : null;
    }

    /// <summary>
    /// The reference's <c>Di.listDevices</c>: the emulator rows, each booted one
    /// renamed to <c>{AVD name} ({serial})</c> once its AVD name has been read
    /// off the device. The name a user picked for the AVD is what its own picker
    /// shows and what a <c>device</c> argument with a space is matched against;
    /// the model string <c>adb devices -l</c> prints is only the fallback.
    /// </summary>
    public async Task<IReadOnlyList<AndroidEmulator.Device>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var (exit, output) = await RunAsync(cancellationToken, "devices", "-l");
        if (exit != 0)
        {
            return [];
        }

        List<AndroidEmulator.Device> devices = [];
        foreach (var device in AndroidEmulator.ParseDevices(output))
        {
            if (device.State != "Booted")
            {
                devices.Add(device);
                continue;
            }

            var avd = await AvdNameAsync(device.Serial, cancellationToken);
            devices.Add(avd.Length == 0
                ? device
                : device with { Name = $"{avd} ({device.Serial})" });
        }

        return devices;
    }

    /// <summary>
    /// The reference's <c>ci</c>: the AVD this emulator was started from, read
    /// from either of the two properties the emulator publishes it under, with
    /// its underscores read as spaces. Empty when neither answers.
    /// </summary>
    public async Task<string> AvdNameAsync(string serial, CancellationToken cancellationToken)
    {
        foreach (var property in new[] { "ro.boot.qemu.avd_name", "ro.kernel.qemu.avd_name" })
        {
            var (exit, output) = await ShellAsync(serial, cancellationToken, "getprop", property);
            var value = exit == 0 ? output.Trim() : "";
            if (AndroidEmulator.IsAvdName(value))
            {
                return value.Replace('_', ' ');
            }
        }

        return "";
    }

    /// <summary>The reference's <c>yr</c>: how long it waits for a cold boot.</summary>
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Its <c>br</c>: how often it re-reads <c>adb devices</c> while waiting.</summary>
    private static readonly TimeSpan BootPoll = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The reference's <c>Ar</c>: spawn the launcher detached, then poll
    /// <c>adb devices -l</c> for a serial that was not there before and whose
    /// <c>sys.boot_completed</c> reads 1, for at most three minutes.
    /// </summary>
    /// <param name="known">
    /// The serials already attached before the boot, so a second emulator that
    /// was running all along is never mistaken for the one just started.
    /// </param>
    public async Task<(string? Serial, string? Error)> BootAvdAsync(
        string avd, HashSet<string> known, CancellationToken cancellationToken)
    {
        if (AndroidEmulator.FindEmulator() is not { } launcher)
        {
            return (null, "Android emulator launcher not found. Install the Android SDK emulator (or set " +
                "ANDROID_HOME), or start a device manually.");
        }

        var log = Path.Combine(
            Path.GetTempPath(),
            $"jarvis-emulator-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..48] + ".log");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(launcher)
            {
                ArgumentList = { "-avd", avd },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, $"emulator {ex.Message} before AVD '{avd}' came online (log: {log})");
        }

        var deadline = DateTimeOffset.UtcNow + BootTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var device in await ListDevicesAsync(cancellationToken))
            {
                if (known.Contains(device.Serial) || device.State != "Booted")
                {
                    continue;
                }

                var completed = await ShellAsync(
                    device.Serial, cancellationToken, "getprop", "sys.boot_completed");
                if (completed.Output.Trim() == "1")
                {
                    return (device.Serial, null);
                }
            }

            await Task.Delay(BootPoll, cancellationToken);
        }

        return (null, $"AVD '{avd}' did not finish booting within {BootTimeout.TotalSeconds:0}s. It may " +
            $"still be booting. Try attaching again in a minute (emulator log: {log}).");
    }

    /// <summary>Its <c>vr</c>: the configured AVDs, or none when the launcher is absent.</summary>
    public static async Task<IReadOnlyList<string>> ListAvdsAsync(CancellationToken cancellationToken)
    {
        if (AndroidEmulator.FindEmulator() is not { } emulator)
        {
            return [];
        }

        var startInfo = new ProcessStartInfo(emulator, "-list-avds")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return [];
            }

            return AndroidEmulator.ParseAvds(await stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process finished between the timeout and the kill.
        }
    }
}
