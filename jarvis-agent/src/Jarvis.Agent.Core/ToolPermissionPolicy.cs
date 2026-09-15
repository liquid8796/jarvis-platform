using System.Collections.Frozen;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

/// <summary>Local user's standing consent, keyed by exact installed tool IDs; no wildcard or auto-arm.</summary>
public sealed class ToolPermissionPolicy
{
    private FrozenSet<string> _grants = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public event Action? PermissionsRevoked;
    public ToolPermissionPolicy(IEnumerable<string>? grants = null) => Replace(grants ?? []);
    public bool HasFullPermission(string toolId) => Volatile.Read(ref _grants).Contains(toolId);
    public IReadOnlyList<string> FullPermissionTools => Volatile.Read(ref _grants).Order(StringComparer.Ordinal).ToArray();
    public bool RequiresApproval(ToolDescriptor tool) =>
        !HasFullPermission(tool.Id) && (!tool.ReadOnly || tool.Sensitive);

    public void Replace(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var next = toolIds.Select(ValidateId).ToFrozenSet(StringComparer.Ordinal);
        var previous = Interlocked.Exchange(ref _grants, next);
        if (previous.Any(id => !next.Contains(id))) PermissionsRevoked?.Invoke();
    }

    private static string ValidateId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Contains('*') && !id.Any(char.IsWhiteSpace)
            ? id : throw new ArgumentException("An exact tool ID is required; wildcard grants are not supported.");
}

/// <summary>Separate from DPAPI enrollment credentials. Atomic writes; damaged files fail closed.</summary>
public sealed class ToolPermissionStore(string filePath)
{
    private sealed record Document(int Version, string[] FullPermissionTools);
    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(filePath)) return [];
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(filePath), WireJson.Options)
            ?? throw new InvalidDataException("Tool permission settings are empty.");
        if (document.Version != 1 || document.FullPermissionTools is null)
            throw new InvalidDataException("Unsupported tool permission settings.");
        return new ToolPermissionPolicy(document.FullPermissionTools).FullPermissionTools;
    }

    public void Save(IEnumerable<string> toolIds)
    {
        var grants = new ToolPermissionPolicy(toolIds).FullPermissionTools.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document(1, grants),
                new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
            File.Move(temp, filePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
