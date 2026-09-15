using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// What is left of this app's own android_* family once the reference's own
/// server took the rest: <c>android_logcat</c>, which the reference's
/// <c>control</c> tool has no counterpart action for.
///
/// The other four — android_devices, android_screenshot, android_input and
/// android_install — are now actions of
/// <see cref="AndroidEmulatorTools"/>'s <c>control</c>, and their old names
/// survive there as never-advertised aliases so a stored session still replays.
/// This one stays bare and is declared as an addition in
/// <c>Deltas/reference-surface-deltas.tsv</c>. It exists only when adb does.
/// </summary>
public static class AndroidTools
{
    private static readonly TimeSpan AdbTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(120);

    public static IReadOnlyList<ITool> CreateIfAvailable(UiSettings settings)
    {
        if (!settings.AndroidToolsEnabled || FindAdb() is not { } adb)
        {
            return [];
        }

        return [new LogcatTool(new AndroidBridge(adb))];
    }

    /// <summary>PATH first, then ANDROID_HOME / the default SDK spot.</summary>
    internal static string? FindAdb()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var candidate = Path.Combine(directory.Trim(), "adb.exe");
            if (directory.Trim().Length > 0 && File.Exists(candidate))
                return candidate;
        }

        foreach (var root in new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        })
        {
            if (root is { Length: > 0 })
            {
                var candidate = Path.Combine(root, "platform-tools", "adb.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>"emulator-5554\tdevice" lines → the emulator serials (physical devices excluded).</summary>
    internal static List<string> ParseEmulatorSerials(string adbDevicesOutput)
    {
        var serials = new List<string>();
        foreach (var line in adbDevicesOutput.Split('\n'))
        {
            var parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts[1] == "device" &&
                parts[0].StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
            {
                serials.Add(parts[0]);
            }
        }

        return serials;
    }

    internal sealed class AndroidBridge(string adbPath)
    {
        public string AdbPath => adbPath;

        /// <summary>Runs adb and returns (exit, stdout+stderr) as text; binary output uses RunBinary.</summary>
        public (int ExitCode, string Output) Run(TimeSpan timeout, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(adbPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            try
            {
                using var process = Process.Start(startInfo)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    return (-1, $"adb timed out after {timeout.TotalSeconds:0}s.");
                }

                return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return (-1, $"adb failed to start: {ex.Message}");
            }
        }

        /// <summary>exec-out with raw stdout (screencap PNG bytes).</summary>
        public (int ExitCode, byte[] Output, string Error) RunBinary(TimeSpan timeout, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(adbPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            try
            {
                using var process = Process.Start(startInfo)!;
                using var buffer = new MemoryStream();
                var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    return (-1, [], $"adb timed out after {timeout.TotalSeconds:0}s.");
                }

                copy.Wait(TimeSpan.FromSeconds(5));
                return (process.ExitCode, buffer.ToArray(), stderr.Result.Trim());
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return (-1, [], $"adb failed to start: {ex.Message}");
            }
        }

        /// <summary>The target emulator serial, or an error line when none/ambiguous.</summary>
        public (string? Serial, string? Error) ResolveEmulator(string? requested)
        {
            var (exit, output) = Run(AdbTimeout, "devices");
            if (exit != 0)
                return (null, $"adb devices failed: {output}");
            var serials = ParseEmulatorSerials(output);
            if (requested is { Length: > 0 })
            {
                if (!requested.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
                    return (null, $"'{requested}' is not an Android emulator. Physical devices are not supported.");
                return serials.Contains(requested, StringComparer.OrdinalIgnoreCase)
                    ? (requested, null)
                    : (null, $"Emulator '{requested}' is not connected. Connected: {Describe(serials)}.");
            }

            return serials.Count switch
            {
                0 => (null, "No Android emulator is running. Start one from Android Studio's Device Manager " +
                            "(or `emulator -avd <name>`), then retry."),
                1 => (serials[0], null),
                _ => (null, $"Several emulators are running ({Describe(serials)}) — pass device to pick one."),
            };
        }

        private static string Describe(List<string> serials) =>
            serials.Count == 0 ? "none" : string.Join(", ", serials);
    }

    private static JsonObject DeviceProperty() => new()
    {
        ["type"] = "string",
        ["description"] = "Emulator serial (e.g. emulator-5554); optional when exactly one is running",
    };

    private sealed class LogcatTool(AndroidBridge bridge) : ITool
    {
        public string Name => "android_logcat";

        public string Description =>
            "Reads the tail of a running emulator's logcat. Optional filter (a logcat tag:priority expression " +
            "like 'MyApp:D *:S', or free text matched against the lines) and line count (default 200).";

        public JsonObject InputSchema => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["lines"] = new JsonObject { ["type"] = "integer", ["description"] = "How many recent lines (default 200, max 2000)" },
                ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "Substring to keep (matched case-insensitively)" },
                ["device"] = DeviceProperty(),
            },
        };

        public bool IsReadOnly => true;

        public string DescribeCall(JsonObject arguments) =>
            $"AndroidLogcat({JsonArgs.GetString(arguments, "filter") ?? "all"})";

        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
        {
            var (serial, error) = bridge.ResolveEmulator(JsonArgs.GetString(arguments, "device"));
            if (serial is null)
                return Task.FromResult(ToolResult.Error(error!));

            int lines = Math.Clamp((int)(arguments["lines"]?.GetValue<double>() ?? 200), 10, 2000);
            var (exit, output) = bridge.Run(AdbTimeout, "-s", serial, "logcat", "-d", "-t", lines.ToString());
            if (exit != 0)
                return Task.FromResult(ToolResult.Error($"logcat failed: {output}"));

            if (JsonArgs.GetString(arguments, "filter") is { Length: > 0 } filter)
            {
                output = string.Join('\n', output.Split('\n')
                    .Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            }

            return Task.FromResult(ToolResult.Success(output.Length == 0
                ? "(no matching log lines)"
                : context.Truncate(output, "logcat")));
        }
    }
}
