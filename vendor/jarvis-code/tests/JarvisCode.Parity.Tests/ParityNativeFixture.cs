using System.Diagnostics;
using System.IO;

namespace JarvisCode.Parity.Tests;

/// <summary>Verification-only workspace/settings/evidence; never copies normal-profile hooks or accounts.</summary>
internal static class ParityNativeFixture
{
    internal const string EvidenceVariable = "JARVIS_PARITY_EVIDENCE_DIR";
    internal const string LiveSettingsVariable = "JARVIS_PARITY_LIVE_SETTINGS";

    internal static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "jarvis-parity-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void Configure(ProcessStartInfo info, string workspace)
    {
        info.WorkingDirectory = workspace;
        foreach (var name in info.Environment.Keys.ToArray())
            if (new[] { "ANTHROPIC_", "CLAUDE_", "OPENAI_", "AWS_", "GOOGLE_", "GCLOUD_", "AZURE_", "VERTEX_", "BEDROCK_" }
                    .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                name is "JARVIS_PROFILE" or "JARVISCODE_PROFILE" || name.StartsWith("JARVIS_APPROVE_", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(name);
        info.Environment["JARVIS_PARITY_ISOLATED"] = "1";
        info.Environment["JARVIS_IDE_REGISTRY"] = Path.Combine(workspace, "ide");
        info.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(workspace, "claude-config");
        info.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
    }

    internal static void CopyLiveSettings(string profileRoot)
    {
        var source = Environment.GetEnvironmentVariable(LiveSettingsVariable);
        if (Environment.GetEnvironmentVariable("JARVIS_E2E") != "1" || string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            throw new InvalidOperationException("Live verification requires JARVIS_E2E=1 and an explicit JARVIS_PARITY_LIVE_SETTINGS file.");
        Directory.CreateDirectory(profileRoot);
        // Keep encrypted provider settings byte-for-byte. No decryption, account discovery, hooks or token-store copies.
        File.Copy(source, Path.Combine(profileRoot, "settings.json"), overwrite: false);
    }

    internal static void Retain(string label, string profileRoot, string screenshot, ProcessRun? run)
    {
        var evidence = Environment.GetEnvironmentVariable(EvidenceVariable);
        if (string.IsNullOrWhiteSpace(evidence)) return;
        var safeLabel = new string(label.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        var directory = Path.Combine(Path.GetFullPath(evidence), safeLabel + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (File.Exists(screenshot)) File.Copy(screenshot, Path.Combine(directory, "screenshot.png"));
        if (run is not null)
        {
            File.WriteAllText(Path.Combine(directory, "stdout.log"), run.StandardOutput);
            File.WriteAllText(Path.Combine(directory, "stderr.log"), run.StandardError);
            File.WriteAllText(Path.Combine(directory, "process.txt"), $"exit={run.ExitCode}\ntimedOut={run.TimedOut}\n");
        }
        var logs = Path.Combine(profileRoot, "logs");
        if (!Directory.Exists(logs)) return;
        foreach (var file in Directory.EnumerateFiles(logs, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory, "logs", Path.GetRelativePath(logs, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    internal static void DeleteWorkspace(string workspace)
    {
        var full = Path.GetFullPath(workspace);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(full) != parent || !Path.GetFileName(full).StartsWith("jarvis-parity-workspace-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean an unowned parity workspace.");
        try { if (Directory.Exists(full)) Directory.Delete(full, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
