using System.IO;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>
/// Profile-aware application data paths. The default profile uses
/// %APPDATA%\JarvisCode (matching JarvisCode.Host.AppPaths); a named profile
/// "work" gets a fully isolated %APPDATA%\JarvisCode-work tree.
/// </summary>
public sealed partial class ProfilePaths
{
    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidName();

    public string? ProfileName { get; }

    public string Root { get; }

    private ProfilePaths(string? profileName)
    {
        ProfileName = profileName;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Root = Path.Combine(appData, profileName is null ? "JarvisCode" : $"JarvisCode-{profileName}");
    }

    public static ProfilePaths Create(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName) ||
            string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase))
        {
            return new ProfilePaths(null);
        }

        if (!ValidName().IsMatch(profileName))
        {
            throw new ArgumentException(
                $"Invalid profile name '{profileName}'. Use letters, digits, '-' and '_' only.");
        }

        return new ProfilePaths(profileName);
    }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string UiSettingsFile => Path.Combine(Root, "ui-settings.json");
    public string SessionsDirectory => Path.Combine(Root, "sessions");
    public string CheckpointsDirectory => Path.Combine(Root, "checkpoints");
    public string MemoryRoot => Path.Combine(Root, "memory");
    public string UsageStatsFile => Path.Combine(Root, "usage-stats.json");
    public string ThemesDirectory => Path.Combine(Root, "themes");
    public string UserCommandsDirectory => Path.Combine(Root, "commands");
    public string UserAgentsDirectory => Path.Combine(Root, "agents");
    public string UserHooksFile => Path.Combine(Root, "hooks.json");
    public string UserMcpFile => Path.Combine(Root, "mcp.json");
    public string UserSkillsDirectory => Path.Combine(Root, "skills");
    public string PluginsDirectory => Path.Combine(Root, "plugins");
    public string RoutinesFile => Path.Combine(Root, "routines.json");

    /// <summary>
    /// The desktop's scheduled tasks, one {taskId}/SKILL.md per task — the
    /// reference's own storage shape for mcp__scheduled-tasks__*, and a separate
    /// store from the routines file the Scheduled page shows.
    /// </summary>
    public string ScheduledTasksDirectory => Path.Combine(Root, "scheduled-tasks");

    /// <summary>
    /// One run history per routine, behind the Scheduled detail page's History
    /// section. Separate from the sessions themselves so a line survives the
    /// session it names being archived or deleted.
    /// </summary>
    public string RoutineRunsDirectory => Path.Combine(Root, "routine-runs");
    public string KeybindingsFile => Path.Combine(Root, "keybindings.json");
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>One task board per session (the reference's task list).</summary>
    public string TasksDirectory => Path.Combine(Root, "tasks");

    /// <summary>Team files: one directory per session team, holding config.json.</summary>
    public string TeamsDirectory => Path.Combine(Root, "teams");

    /// <summary>Workflow runs: the persisted script and journal.jsonl per run.</summary>
    public string WorkflowRunsDirectory => Path.Combine(Root, "workflows");

    /// <summary>Name suffix so mutexes/pipes never collide across profiles.</summary>
    public string InstanceKey => ProfileName is null ? "JarvisCode" : $"JarvisCode-{ProfileName}";

    public string WindowTitleSuffix => ProfileName is null ? "" : $" ({ProfileName})";

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(CheckpointsDirectory);
        Directory.CreateDirectory(MemoryRoot);
        Directory.CreateDirectory(LogsDirectory);
    }
}
