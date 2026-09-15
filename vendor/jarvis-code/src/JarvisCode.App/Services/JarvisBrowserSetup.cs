using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace JarvisCode.App.Services;

/// <summary>
/// Installs the Jarvis Browser pieces: copies the extension to a stable folder,
/// writes the native-messaging host manifest pointing at this exe, and
/// registers it for Chromium browsers. Idempotent — safe to run every start.
/// </summary>
public static class JarvisBrowserSetup
{
    public const string HostName = "com.jarvis.browser";
    public const string ExtensionId = "fapmefacbdeodlinmlbkpnhihcjookpc";

    private static readonly string[] BrowserRegistryRoots =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\CocCoc\Browser\NativeMessagingHosts",
    ];

    public static string ExtensionDirectory(ProfilePaths paths) => Path.Combine(paths.Root, "jarvis-browser");

    public static void EnsureInstalled(ProfilePaths paths)
    {
        try
        {
            var target = ExtensionDirectory(paths);
            Directory.CreateDirectory(target);
            var source = Path.Combine(AppContext.BaseDirectory, "Assets", "JarvisBrowser");
            if (Directory.Exists(source))
            {
                foreach (var file in Directory.GetFiles(source))
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                }
            }

            var exe = Environment.ProcessPath;
            if (exe is null)
            {
                return;
            }

            var manifestPath = Path.Combine(target, "host-manifest.json");
            var manifest = new JsonObject
            {
                ["name"] = HostName,
                ["description"] = "Jarvis Browser bridge for Jarvis Code",
                ["path"] = exe,
                ["type"] = "stdio",
                ["allowed_origins"] = new JsonArray($"chrome-extension://{ExtensionId}/"),
            };
            File.WriteAllText(manifestPath, manifest.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            foreach (var root in BrowserRegistryRoots)
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{HostName}");
                    key.SetValue(null, manifestPath);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // That browser's hive is unavailable; the others still work.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Browser integration is optional; the app must still start.
        }
    }
}
