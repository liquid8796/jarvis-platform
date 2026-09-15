using System.IO;
using System.Text.Json;
using Microsoft.Win32;
namespace Jarvis.Agent.Windows;
public static class BrowserIntegration
{
    public const string PipeName = "JarvisAgent-browser";
    public const string HostName = "com.jarvis.agent.browser";
    public const string ExtensionId = "kaofhfhpenfnaekhbeikeapgchmfcnjj";
    public static string Install(string nativeHostExe)
    {
        if (!File.Exists(nativeHostExe) || Path.GetFileName(nativeHostExe) != "jarvis-agent.exe")
            throw new FileNotFoundException("Select the published jarvis-agent.exe CLI as the native-messaging host.");
        var target = Path.Combine(AgentProfile.Root, "browser-extension"); Directory.CreateDirectory(target);
        var source = Path.Combine(AppContext.BaseDirectory, "Assets", "Browser");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
        }
        // Keep the host manifest outside the unpacked extension directory.
        var manifest = Path.Combine(AgentProfile.Root, "browser-host.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { name = HostName, description = "Jarvis Agent browser bridge",
            path = Path.GetFullPath(nativeHostExe), type = "stdio", allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" } }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var browser in new[] { @"Software\Google\Chrome", @"Software\Microsoft\Edge", @"Software\CocCoc\Browser" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(browser + @"\NativeMessagingHosts\" + HostName);
            key.SetValue(null, manifest);
        }
        return target;
    }
    public static bool IsNativeHostInvocation(string[] args) => args.Any(a => a.StartsWith("chrome-extension://" + ExtensionId + "/", StringComparison.Ordinal));
}
