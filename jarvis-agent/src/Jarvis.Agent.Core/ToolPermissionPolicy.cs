using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

/// <summary>Local user's standing consent, keyed by exact installed tool IDs; no wildcard or auto-arm.</summary>
public sealed class ToolPermissionPolicy
{
    private sealed record LeaseRegistration(ToolCapabilityLease Lease, CancellationTokenSource Stop);

    private FrozenSet<string> _grants = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    private FrozenSet<string> _alwaysApprovedConstrained = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LeaseRegistration> _leases = new(StringComparer.Ordinal);
    public event Action? PermissionsRevoked;
    public event Action? PermissionsChanged;

    public ToolPermissionPolicy(IEnumerable<string>? grants = null, IEnumerable<string>? alwaysApprovedConstrainedTools = null)
    {
        Replace(grants ?? []);
        ReplaceAlwaysApprovedConstrainedTools(alwaysApprovedConstrainedTools ?? []);
    }

    public static bool SupportsPermanentApproval(string toolId) => toolId == "unified_exec.exec_command";
    public bool HasFullPermission(string toolId) => Volatile.Read(ref _grants).Contains(toolId);
    public bool HasAlwaysApprovedConstrainedTool(string toolId) => Volatile.Read(ref _alwaysApprovedConstrained).Contains(toolId);
    public IReadOnlyList<string> FullPermissionTools => Volatile.Read(ref _grants).Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<string> AlwaysApprovedConstrainedTools => Volatile.Read(ref _alwaysApprovedConstrained).Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<ToolCapabilityLease> ActiveLeases => _leases.Values
        .Select(x => x.Lease).Where(x => x.ExpiresUtc > DateTimeOffset.UtcNow).OrderBy(x => x.ExpiresUtc).ToArray();

    public bool HasFullPermission(string toolId, JsonElement arguments, AgentExecutionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(context);
        var now = DateTimeOffset.UtcNow;
        var constrained = SupportsPermanentApproval(toolId);
        if (constrained && HasAlwaysApprovedConstrainedTool(toolId)) return true;
        if (!constrained && HasFullPermission(toolId)) return true;
        return _leases.Values.Any(registration => registration.Lease.Matches(toolId, arguments, context, now));
    }

    public bool RequiresApproval(ToolDescriptor tool) =>
        !(SupportsPermanentApproval(tool.Id) ? HasAlwaysApprovedConstrainedTool(tool.Id) : HasFullPermission(tool.Id)) &&
        (!tool.ReadOnly || tool.Sensitive);

    public bool RequiresApproval(ToolDescriptor tool, JsonElement arguments, AgentExecutionContext context)
    {
        // Empty write_stdin is a read-only poll of an already owner/session-bound process. Input and Ctrl+C still require approval.
        if (tool.Id == "unified_exec.write_stdin" &&
            (!arguments.TryGetProperty("chars", out var chars) || chars.ValueKind == JsonValueKind.String && chars.GetString() == ""))
            return false;
        return !HasFullPermission(tool.Id, arguments, context) && (!tool.ReadOnly || tool.Sensitive);
    }

    public void Replace(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var next = toolIds.Select(ValidateId).ToFrozenSet(StringComparer.Ordinal);
        var previous = Interlocked.Exchange(ref _grants, next);
        var changed = !previous.SetEquals(next);
        if (previous.Any(id => !next.Contains(id))) PermissionsRevoked?.Invoke();
        if (changed) PermissionsChanged?.Invoke();
    }

    public void ReplaceAlwaysApprovedConstrainedTools(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var next = toolIds.Select(ValidateAlwaysApprovedId).ToFrozenSet(StringComparer.Ordinal);
        var previous = Interlocked.Exchange(ref _alwaysApprovedConstrained, next);
        var changed = !previous.SetEquals(next);
        if (previous.Any(id => !next.Contains(id))) PermissionsRevoked?.Invoke();
        if (changed) PermissionsChanged?.Invoke();
    }

    public void GrantAlwaysApprovedConstrainedTool(string toolId)
    {
        var validated = ValidateAlwaysApprovedId(toolId);
        ReplaceAlwaysApprovedConstrainedTools(AlwaysApprovedConstrainedTools.Append(validated));
    }

    public bool RevokeAlwaysApprovedConstrainedTool(string toolId)
    {
        if (!HasAlwaysApprovedConstrainedTool(toolId)) return false;
        ReplaceAlwaysApprovedConstrainedTools(AlwaysApprovedConstrainedTools.Where(id => !StringComparer.Ordinal.Equals(id, toolId)));
        return true;
    }

    public void GrantLease(ToolCapabilityLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.Validate();
        var stop = new CancellationTokenSource();
        var registration = new LeaseRegistration(lease, stop);
        _leases.AddOrUpdate(lease.Id, registration, (_, previous) =>
        {
            previous.Stop.Cancel();
            previous.Stop.Dispose();
            return registration;
        });
        _ = ExpireLeaseAsync(registration);
    }

    public bool RevokeLease(string leaseId)
    {
        if (!_leases.TryRemove(leaseId, out var registration)) return false;
        registration.Stop.Cancel();
        registration.Stop.Dispose();
        PermissionsRevoked?.Invoke();
        return true;
    }

    public void RevokeAllLeases()
    {
        var revoked = false;
        foreach (var pair in _leases.ToArray())
        {
            if (!_leases.TryRemove(pair.Key, out var registration)) continue;
            revoked = true;
            registration.Stop.Cancel();
            registration.Stop.Dispose();
        }
        if (revoked) PermissionsRevoked?.Invoke();
    }

    private async Task ExpireLeaseAsync(LeaseRegistration registration)
    {
        try
        {
            var delay = registration.Lease.ExpiresUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, registration.Stop.Token).ConfigureAwait(false);
            if (_leases.TryGetValue(registration.Lease.Id, out var current) && ReferenceEquals(current, registration) &&
                _leases.TryRemove(registration.Lease.Id, out _)) PermissionsRevoked?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { registration.Stop.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private static string ValidateId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 256 && !id.Contains('*') && !id.Any(char.IsWhiteSpace)
            ? id : throw new ArgumentException("An exact tool ID is required; wildcard grants are not supported.");

    private static string ValidateAlwaysApprovedId(string id) =>
        SupportsPermanentApproval(ValidateId(id))
            ? id : throw new ArgumentException("Permanent approval is supported only for unified_exec.exec_command.");
}

public sealed record ToolPermissionSettings(IReadOnlyList<string> FullPermissionTools, IReadOnlyList<string> AlwaysApprovedConstrainedTools);

/// <summary>Separate from DPAPI enrollment credentials. Atomic writes; damaged files fail closed.</summary>
public sealed class ToolPermissionStore(string filePath)
{
    private sealed record Document(int Version, string[] FullPermissionTools, string[]? AlwaysApprovedConstrainedTools = null);

    public IReadOnlyList<string> Load() => LoadSettings().FullPermissionTools;

    public ToolPermissionSettings LoadSettings()
    {
        if (!File.Exists(filePath)) return new([], []);
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(filePath), WireJson.Options)
            ?? throw new InvalidDataException("Tool permission settings are empty.");
        if (document.FullPermissionTools is null || document.Version is not (1 or 2))
            throw new InvalidDataException("Unsupported tool permission settings.");
        if (document.Version == 2 && document.AlwaysApprovedConstrainedTools is null)
            throw new InvalidDataException("Unsupported tool permission settings.");
        var migrated = Migrate(document.FullPermissionTools,
            document.Version == 1 ? [] : document.AlwaysApprovedConstrainedTools!);
        var policy = new ToolPermissionPolicy(migrated.FullPermissionTools, migrated.AlwaysApprovedConstrainedTools);
        return new(policy.FullPermissionTools, policy.AlwaysApprovedConstrainedTools);
    }

    public void Save(IEnumerable<string> toolIds) => Save(new ToolPermissionSettings(toolIds.ToArray(), []));

    public void Save(ToolPermissionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var migrated = Migrate(settings.FullPermissionTools, settings.AlwaysApprovedConstrainedTools);
        var policy = new ToolPermissionPolicy(migrated.FullPermissionTools, migrated.AlwaysApprovedConstrainedTools);
        var grants = policy.FullPermissionTools.ToArray();
        var always = policy.AlwaysApprovedConstrainedTools.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new Document(2, grants, always),
                new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
            File.Move(temp, filePath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static ToolPermissionSettings Migrate(IEnumerable<string> fullPermissions,
        IEnumerable<string> alwaysApproved)
    {
        var full = new HashSet<string>(StringComparer.Ordinal);
        var always = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in alwaysApproved) always.Add(MigrateAlwaysApproved(id));
        foreach (var id in fullPermissions)
        {
            switch (id)
            {
                case "shell.PowerShell":
                case "shell.Bash":
                case "process.start":
                case "process.spawn":
                    always.Add("unified_exec.exec_command");
                    break;
                case "process.read":
                case "process.write_stdin":
                case "process.resize_pty":
                case "process.cancel":
                    full.Add("unified_exec.write_stdin");
                    break;
                case "filesystem.Write":
                case "filesystem.Edit":
                case "filesystem.NotebookEdit":
                    full.Add("source.apply_patch");
                    break;
                default:
                    full.Add(id.StartsWith("computer.", StringComparison.Ordinal)
                        ? "computer_use.computer_use"
                        : id);
                    break;
            }
        }
        return new(full.Order(StringComparer.Ordinal).ToArray(), always.Order(StringComparer.Ordinal).ToArray());
    }

    private static string MigrateAlwaysApproved(string id) => id is "process.start" or "process.spawn"
        ? "unified_exec.exec_command"
        : id;
}
