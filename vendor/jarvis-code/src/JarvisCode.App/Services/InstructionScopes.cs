using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Settings;
using JarvisCode.Host;

namespace JarvisCode.App.Services;

/// <summary>
/// Installs the half of <see cref="ProjectInstructions.InstructionScope"/> that
/// does not change with the working directory: this installation's user
/// directory, the machine's policy directory, and the two switches the user
/// controls. Core takes its paths from outside, so a subagent or a skill that
/// loads instructions by working directory alone still sees every tier.
/// </summary>
public static class InstructionScopes
{
    /// <summary>
    /// The policy file an administrator drops beside the managed instructions.
    /// Its <c>jarvisMd</c> key is the reference's <c>claudeMd</c> policy setting:
    /// instructions the organization applies without shipping a file for them.
    /// </summary>
    public const string ManagedSettingsFileName = "managed-settings.json";

    /// <summary>Installs the scope and keeps it current as settings are saved.</summary>
    public static void Install(ProfilePaths paths, SettingsService settings, UiSettingsStore ui)
    {
        Apply(paths, settings.Current);
        settings.SettingsSaved += (_, _) => Apply(paths, settings.Current);
        ProjectInstructions.ExternalImportsApproved = directory =>
            ui.Current.ExternalInstructionImportsByProject.TryGetValue(
                ProjectKey(directory), out var approved) && approved;
    }

    /// <summary>
    /// The key a project's answer is filed under. Canonical so the same folder
    /// spelled two ways is one project, which is what makes "asked already"
    /// hold.
    /// </summary>
    public static string ProjectKey(string workingDirectory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return workingDirectory;
        }
    }

    private static void Apply(ProfilePaths paths, AppSettings settings) =>
        ProjectInstructions.HostScope = new ProjectInstructions.InstructionScope
        {
            WorkingDirectory = string.Empty,
            UserDirectory = paths.Root,
            ManagedDirectory = AppPaths.ManagedRoot,
            ManagedInstructions = ReadManagedInstructions(),
            Excludes = settings.InstructionFileExcludes,
        };

    /// <summary>
    /// Reads the organization's inline instructions, or null when this machine
    /// carries no policy file. Read from the policy directory rather than from
    /// the user's own settings: an instruction that claims to be the
    /// organization's may only come from where the organization writes.
    /// </summary>
    private static string? ReadManagedInstructions()
    {
        try
        {
            var path = Path.Combine(AppPaths.ManagedRoot, ManagedSettingsFileName);
            if (!File.Exists(path))
                return null;
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
                ? root["jarvisMd"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
