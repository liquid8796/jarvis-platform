using System.Text.Json.Nodes;

namespace JarvisCode.Core.Permissions;

/// <summary>
/// Project-scoped permission rules (ported from claw-code's settings precedence
/// chain): <c>.jarvis/settings.local.json</c> (personal, gitignored) and
/// <c>.jarvis/settings.json</c> (shared, committed) each carry
/// <c>{"permissions":{"rules":["allow shell git *", …]}}</c> in the same line
/// format as the global rules. Local rules come first, then project, then the
/// caller appends global — first match wins, so the most specific scope decides.
/// </summary>
public static class ProjectPermissions
{
    public const string SettingsFileName = "settings.json";
    public const string LocalSettingsFileName = "settings.local.json";

    /// <summary>
    /// The reference's <c>permissions.defaultMode</c> from the project's settings
    /// files, local first: acceptEdits, plan, manual (its "default") or dontAsk.
    /// <c>"auto"</c> and <c>"bypassPermissions"</c> are ignored here, as the
    /// reference ignores them (CLI 2.1.257: "set it in user or managed settings,
    /// or pass --permission-mode") — a mode that stops asking is not something a
    /// checked-in file gets to choose for the person who opened it. Null when
    /// neither file names an honoured mode.
    /// </summary>
    public static PermissionMode? LoadDefaultMode(string workingDirectory)
    {
        var jarvisDirectory = Path.Combine(workingDirectory, ".jarvis");
        if (!Directory.Exists(jarvisDirectory))
            return null;
        return ReadDefaultMode(Path.Combine(jarvisDirectory, LocalSettingsFileName))
               ?? ReadDefaultMode(Path.Combine(jarvisDirectory, SettingsFileName));
    }

    /// <summary>The reference's spellings, and what a project file may not set.</summary>
    public static PermissionMode? ParseProjectDefaultMode(string? value) => value?.Trim() switch
    {
        "acceptEdits" => PermissionMode.AcceptEdits,
        "plan" => PermissionMode.Plan,
        "default" or "manual" => PermissionMode.Manual,
        "dontAsk" => PermissionMode.Auto,
        // "auto" and "bypassPermissions" are refused from project and local settings.
        _ => null,
    };

    private static PermissionMode? ReadDefaultMode(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return ParseProjectDefaultMode(root?["permissions"]?["defaultMode"]?.GetValue<string>());
        }
        catch (Exception ex) when (
            ex is System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> LoadRuleLines(string workingDirectory)
    {
        var jarvisDirectory = Path.Combine(workingDirectory, ".jarvis");
        if (!Directory.Exists(jarvisDirectory))
            return [];
        var lines = new List<string>();
        AppendRules(lines, Path.Combine(jarvisDirectory, LocalSettingsFileName));
        AppendRules(lines, Path.Combine(jarvisDirectory, SettingsFileName));
        return lines;
    }

    private static void AppendRules(List<string> lines, string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?["permissions"]?["rules"] is not JsonArray rules)
                return;
            foreach (var entry in rules)
            {
                if (entry?.GetValue<string>() is { Length: > 0 } line)
                    lines.Add(line);
            }
        }
        catch (Exception ex) when (
            ex is System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // A malformed settings file must never break tool gating; the valid
            // sibling file still loads (claw-code degraded-but-working policy).
        }
    }
}
