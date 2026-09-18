using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Jarvis.Agent.Windows;

public static class BrowserIntegration
{
    public const string ExtensionPipeName = "JarvisAgent-browser-extension-v2";
    public const string ServicePipeName = "JarvisAgent-browser-service-v1";
    public const string HostName = "com.jarvis.agent.browser";
    public const string ExtensionId = "kaofhfhpenfnaekhbeikeapgchmfcnjj";
    public const string BrowserHostExeName = "jarvis-browser-host.exe";
    public const string BrowserServiceExeName = "jarvis-browser-service.exe";

    // Kept only so older callers compiled against PipeName keep targeting the new extension transport.
    public const string PipeName = ExtensionPipeName;

    public static string Install(string nativeHostExe)
    {
        if (!File.Exists(nativeHostExe) ||
            !Path.GetFileName(nativeHostExe).Equals(BrowserHostExeName, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"Select the published {BrowserHostExeName} native-messaging host.");

        var target = Path.Combine(AgentProfile.Root, "browser-extension");
        Directory.CreateDirectory(target);
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "Browser");
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException("Browser extension assets are missing from this Agent package.");

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }

        // Keep the host manifest outside the unpacked extension directory.
        var manifest = Path.Combine(AgentProfile.Root, "browser-host.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Jarvis Agent dedicated browser native host",
            path = Path.GetFullPath(nativeHostExe),
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" }
        }, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var browser in new[] { @"Software\Google\Chrome", @"Software\Microsoft\Edge", @"Software\CocCoc\Browser" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(browser + @"\NativeMessagingHosts\" + HostName);
            key.SetValue(null, manifest);
        }
        return target;
    }

    public static string ResolveCompanionExecutable(string fileName)
    {
        var direct = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(direct)) return direct;
        var browser = Path.Combine(AppContext.BaseDirectory, "browser", fileName);
        if (File.Exists(browser)) return browser;
        throw new FileNotFoundException($"Browser companion '{fileName}' is missing from this Agent package.");
    }

    // Backward compatibility for users whose pre-1.0.71 manifest still points at jarvis-agent.exe.
    public static bool IsNativeHostInvocation(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://" + ExtensionId + "/", StringComparison.Ordinal));
}
