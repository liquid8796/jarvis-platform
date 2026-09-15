using System.IO;

namespace JarvisCode.Host;

/// <summary>Well-known per-user storage locations for Jarvis Code.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarvisCode");

    /// <summary>
    /// Machine-wide policy location, the reference's managed-settings directory.
    /// Instructions found here are the organization's and cannot be excluded by
    /// the machine they govern.
    /// </summary>
    public static string ManagedRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "JarvisCode");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string SessionsDirectory => Path.Combine(Root, "sessions");

    /// <summary>User-level custom slash commands (*.md), shadowed by project ones.</summary>
    public static string UserCommandsDirectory => Path.Combine(Root, "commands");

    /// <summary>User-level custom agent definitions (*.md), shadowed by project ones.</summary>
    public static string UserAgentsDirectory => Path.Combine(Root, "agents");

    /// <summary>User-level hooks configuration, merged before the project's hooks.json.</summary>
    public static string UserHooksFile => Path.Combine(Root, "hooks.json");

    /// <summary>User-level MCP server configuration, merged before the project's mcp.json.</summary>
    public static string UserMcpFile => Path.Combine(Root, "mcp.json");

    /// <summary>User-level skills (*.md), shadowed by project ones.</summary>
    public static string UserSkillsDirectory => Path.Combine(Root, "skills");

    /// <summary>Installed plugins: one folder per plugin bundling commands/agents/skills/hooks/mcp.</summary>
    public static string PluginsDirectory => Path.Combine(Root, "plugins");

    /// <summary>Scheduled routines (recurring tasks that run while the app is open).</summary>
    public static string RoutinesFile => Path.Combine(Root, "routines.json");
}
