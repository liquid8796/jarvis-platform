using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>
/// The installed desktop's MM/y1n partition selection (1.46388.3.0). Code uses
/// a persistent workspace jar for Shared, its own jar for Separate, and a
/// workspace jar in memory for None. The common external-browser jar is the
/// separate Hj()/17519066 rollout arm, whose local default is off.
/// None never deletes a persistent jar, including the legacy default profile.
/// </summary>
internal static class BrowserStorageProfile
{
    private const string ConfigurationFile = "browser-storage.json";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Configuration> Profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private sealed record Configuration(string EngineFolder, bool KeepLegacyShared);

    /// <summary>
    /// Pins the engine root before any surface starts Chromium. Old releases
    /// chose engine/webview2 according to which surface opened first; retain
    /// the existing browser profile and its Shared login instead of stranding
    /// it in an unused folder. New installs use the reference's named jar.
    /// </summary>
    public static string EngineDirectory(string profileRoot)
    {
        var root = Path.GetFullPath(profileRoot);
        var configuration = Profiles.GetOrAdd(root, LoadOrCreate);
        return Path.Combine(root, configuration.EngineFolder);
    }

    public static string SharedPartition(string engineDirectory, string workingDirectory, bool externalBrowsingEnabled = false)
    {
        var parent = Directory.GetParent(Path.GetFullPath(engineDirectory))?.FullName;
        if (parent is not null && Profiles.TryGetValue(parent, out var configuration) &&
            configuration.KeepLegacyShared)
            return "";
        return Partition("shared", workingDirectory, "", externalBrowsingEnabled);
    }

    private static Configuration LoadOrCreate(string root)
    {
        var file = Path.Combine(root, ConfigurationFile);
        if (File.Exists(file))
        {
            var stored = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(file));
            if (stored?.EngineFolder is "engine" or "webview2") return stored;
            throw new InvalidDataException("The saved Browser storage profile is invalid.");
        }

        var folder = File.Exists(Path.Combine(root, "webview2", "Local State")) ? "webview2" : "engine";
        var legacy = File.Exists(Path.Combine(root, folder, "Local State"));
        var selected = new Configuration(folder, legacy);
        Directory.CreateDirectory(root);
        File.WriteAllText(file, JsonSerializer.Serialize(selected));
        return selected;
    }

    public static string Mode(UiSettings? settings) => settings?.BrowserPreviewStorage switch
    {
        "shared" => "shared",
        "session" => "session",
        // Older builds had just this toggle. Its true value must still mean
        // persistence when a settings file predates the three-way choice.
        _ => settings?.BrowserPersistSessions == true ? "shared" : "none",
    };

    public static string Partition(string mode, string workingDirectory, string sessionId, bool externalBrowsingEnabled = false) => mode switch
    {
        "shared" => externalBrowsingEnabled ? "persist:launch-preview-cowork-shared" : "persist:launch-preview-" + WorkspaceId(workingDirectory),
        "session" => "persist:launch-preview-session-" + sessionId,
        _ => "launch-preview-" + WorkspaceId(workingDirectory),
    };

    private static string WorkspaceId(string workingDirectory)
    {
        // rq takes the originating workspace path and the first twelve hex
        // digits of MD5. This is a storage identifier, not a security digest.
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(path)))[..12];
    }
}
