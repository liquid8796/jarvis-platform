using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Protocol;
namespace Jarvis.McpServer.Domain;

public sealed class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string TokenHash { get; set; } = "";
    public long TokenExpiresAt { get; set; }
    public string CapabilitiesJson { get; set; } = "[]";
    public string Platform { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long LastSeenAt { get; set; }
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
}
[JsonConverter(typeof(JsonStringEnumConverter<ToolPublicationMode>))]
public enum ToolPublicationMode
{
    Auto,
    Published,
    Hidden
}

public static class DynamicAgentToolNames
{
    public const string Search = "jarvis__tool_search";
    public const string Call = "jarvis__tool_call";
    public static bool IsReserved(string name) => name is Search or Call;
}

public static class ToolPublicationRules
{
    public static bool IsVisible(ToolPublicationMode mode) => mode != ToolPublicationMode.Hidden;
    public static bool IsReservedPublicName(string name) =>
        RemoteTaskRules.IsReservedName(name) || AgentSessionRules.IsPublicTool(name) || DynamicAgentToolNames.IsReserved(name);
}
/// <summary>
/// Server-side lifecycle rules for Agent capabilities that have been intentionally replaced.
/// A newer server never republishes these IDs even when an older enrolled Agent still advertises them.
/// ToolCatalogReconciler also removes their persisted policy rows.
/// </summary>
public static class AgentToolCatalogRules
{
    private sealed record PublicNameMigration(string LegacyName, string PreferredName);

    private static readonly IReadOnlyDictionary<string, PublicNameMigration> PublicNameMigrations =
        new Dictionary<string, PublicNameMigration>(StringComparer.Ordinal)
        {
            ["unity.list_tools"] = new("list_tools", "unity_list_tools"),
            ["unity.call_tool"] = new("call_tool", "unity_call_tool"),
            ["unity.list_resources"] = new("list_resources", "unity_list_resources"),
            ["unity.read_resource"] = new("read_resource", "unity_read_resource"),
            ["unity.list_prompts"] = new("list_prompts", "unity_list_prompts"),
            ["unity.get_prompt"] = new("get_prompt", "unity_get_prompt")
        };

    private static readonly HashSet<string> RetiredIds = new(StringComparer.Ordinal)
    {
        "filesystem.Write", "filesystem.Edit", "filesystem.NotebookEdit",
        "shell.PowerShell", "shell.Bash",
        "process.start", "process.spawn", "process.read", "process.write_stdin", "process.resize_pty", "process.cancel",
        "computer.get_state", "computer.screenshot", "computer.computer_batch", "computer.open_application",
        "computer.request_access", "computer.request_teach_access", "computer.teach_step", "computer.teach_batch",
        "computer.list_granted_applications", "computer.switch_display", "computer.read_clipboard", "computer.write_clipboard"
    };

    public static bool IsRetired(string toolId) => RetiredIds.Contains(toolId);

    public static bool TryGetPreferredPublicName(string toolId, out string preferredName)
    {
        if (PublicNameMigrations.TryGetValue(toolId, out var migration))
        {
            preferredName = migration.PreferredName;
            return true;
        }
        preferredName = "";
        return false;
    }

    public static bool IsLegacyDefaultPublicName(string toolId, string name) =>
        PublicNameMigrations.TryGetValue(toolId, out var migration) &&
        StringComparer.Ordinal.Equals(name, migration.LegacyName);

    public static bool TryResolveLegacyPublicName(string name, out string toolId)
    {
        foreach (var pair in PublicNameMigrations)
        {
            if (!StringComparer.Ordinal.Equals(pair.Value.LegacyName, name)) continue;
            toolId = pair.Key;
            return true;
        }
        toolId = "";
        return false;
    }

    public static ToolDescriptor NormalizePublicName(ToolDescriptor tool) =>
        TryGetPreferredPublicName(tool.Id, out var preferredName) &&
        !StringComparer.Ordinal.Equals(tool.Name, preferredName)
            ? tool with { Name = preferredName }
            : tool;

    public static IReadOnlyList<ToolDescriptor> Installed(IEnumerable<string> manifests) =>
        manifests
            .SelectMany(json => JsonSerializer.Deserialize<ToolDescriptor[]>(json, WireJson.Options) ?? [])
            .Where(tool => !IsRetired(tool.Id))
            .Select(NormalizePublicName)
            .DistinctBy(tool => tool.Id, StringComparer.Ordinal)
            .OrderBy(tool => tool.Category, StringComparer.Ordinal)
            .ThenBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
}

public sealed class ToolEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string AgentToolId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public ToolPublicationMode PublicationMode { get; set; } = ToolPublicationMode.Auto;
    // Retained as a persisted compatibility column for schema-v1 databases and older admin clients.
    // Runtime visibility is governed by PublicationMode; services keep this mirror synchronized.
    public bool Enabled { get; set; } = true;
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class AuditEvent
{
    public long Id { get; set; }
    public long Time { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public string UserId { get; set; } = "";
    public string? DeviceId { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? CorrelationId { get; set; }
    public long DurationMs { get; set; }
}
public sealed record DeviceView(string Id, string Name, bool Enabled, bool Online, string Platform,
    string AgentVersion, long LastSeenAt, int ToolCount, string Revision);
public sealed record EnrollmentResult(string DeviceId, string ServerUrl, string Token);
public sealed record UserView(string Id, string Email, string DisplayName, string Role, string Status);
public interface IAgentRouter
{
    bool IsOnline(string ownerId, string deviceId);
    Task<ToolReply> CallAsync(string ownerId, string deviceId, string toolId,
        System.Text.Json.JsonElement arguments, string sessionId, CancellationToken cancellationToken);
    void DisconnectDevice(string deviceId);
    void DisconnectUser(string ownerId);
}
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent entry, CancellationToken cancellationToken = default);
}
