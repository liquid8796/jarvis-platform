using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Agent.Windows;
public sealed record SavedProfile(string ServerUrl, string DeviceId, string Workspace, string ProtectedToken, bool AllowLoopbackHttp, IReadOnlyList<string>? AdditionalDirectories = null)
{
    public AgentOptions ToOptions() => new(ServerUrl, DeviceId, Workspace, AllowLoopbackHttp, AdditionalDirectories);
}
public static class AgentProfile
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAgent");
    private static string FilePath => Path.Combine(Root, "agent.local.json");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JarvisAgent/profile/v1");
    public static SavedProfile? Load() => File.Exists(FilePath) ? JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(FilePath), WireJson.Options) : null;
    public static string GetToken(SavedProfile profile) => Encoding.UTF8.GetString(ProtectedData.Unprotect(
        Convert.FromBase64String(profile.ProtectedToken), Entropy, DataProtectionScope.CurrentUser));
    public static void Save(AgentOptions options, string token)
    {
        options.ValidateAndGetWebSocketUri();
        if (!token.StartsWith("jra_", StringComparison.Ordinal) || token.Length != 68) throw new ArgumentException("Invalid enrollment token.");
        Directory.CreateDirectory(Root);
        var folders = new WorkspaceDirectories(options.Workspace, options.AdditionalDirectories);
        var profile = new SavedProfile(options.ServerUrl, options.DeviceId, folders.Primary,
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser)), options.AllowLoopbackHttp, folders.Additional);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
