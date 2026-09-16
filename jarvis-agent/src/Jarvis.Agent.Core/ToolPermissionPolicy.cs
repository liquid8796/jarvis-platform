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
    private readonly ConcurrentDictionary<string, LeaseRegistration> _leases = new(StringComparer.Ordinal);
    public event Action? PermissionsRevoked;
    public ToolPermissionPolicy(IEnumerable<string>? grants = null) => Replace(grants ?? []);
    public bool HasFullPermission(string toolId) => Volatile.Read(ref _grants).Contains(toolId);
    public IReadOnlyList<string> FullPermissionTools => Volatile.Read(ref _grants).Order(StringComparer.Ordinal).ToArray();
    public IReadOnlyList<ToolCapabilityLease> ActiveLeases => _leases.Values
        .Select(x => x.Lease).Where(x => x.ExpiresUtc > DateTimeOffset.UtcNow).OrderBy(x => x.ExpiresUtc).ToArray();

    public bool HasFullPermission(string toolId, JsonElement arguments, AgentExecutionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(context);
        var now = DateTimeOffset.UtcNow;
        var constrained = toolId is "process.start" or "process.spawn";
        if (!constrained && HasFullPermission(toolId)) return true;
        return _leases.Values.Any(registration => registration.Lease.Matches(toolId, arguments, context, now));
    }

    public bool RequiresApproval(ToolDescriptor tool) =>
        !HasFullPermission(tool.Id) && (!tool.ReadOnly || tool.Sensitive);

    public bool RequiresApproval(ToolDescriptor tool, JsonElement arguments, AgentExecutionContext context) =>
        !HasFullPermission(tool.Id, arguments, context) && (!tool.ReadOnly || tool.Sensitive);

    public void Replace(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var next = toolIds.Select(ValidateId).ToFrozenSet(StringComparer.Ordinal);
        var previous = Interlocked.Exchange(ref _grants, next);
        if (previous.Any(id => !next.Contains(id))) PermissionsRevoked?.Invoke();
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
