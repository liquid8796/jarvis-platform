using System.Diagnostics;
using System.IO;

namespace JarvisCode.Parity.Tests;

/// <summary>Owns only fresh directories created for a single CLI argument check.</summary>
internal sealed class CliProcessIsolation : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "jarvis-parity-cli-" + Guid.NewGuid().ToString("N"));
    internal string Profile { get; } = "parity-cli-" + Guid.NewGuid().ToString("N");
    internal string ProfileRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarvisCode-" + Profile);

    public void Configure(ProcessStartInfo info)
    {
        var workspace = Path.Combine(Root, "workspace");
        var config = Path.Combine(Root, "claude-config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(config);
        info.WorkingDirectory = workspace;
        foreach (var name in info.Environment.Keys.ToArray())
            if (new[] { "ANTHROPIC_", "CLAUDE_", "AWS_", "GOOGLE_", "GCLOUD_", "AZURE_", "OPENAI_", "OLLAMA_", "VERTEX_", "BEDROCK_", "JARVISCODE_" }
                .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                info.Environment.Remove(name);
        info.Environment["CLAUDE_CONFIG_DIR"] = config;
        info.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        info.Environment["JARVISCODE_PROFILE"] = Profile;
        info.Environment["HOME"] = Root;
        info.Environment["USERPROFILE"] = Root;
    }

    public void Dispose()
    {
        TryDelete(Root);
        TryDelete(ProfileRoot);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
