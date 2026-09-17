namespace Jarvis.Protocol;

/// <summary>
/// Capability protocol layered on top of the stable version-1 WireMessage envelope.
/// Unknown/additive JSON fields remain compatible with legacy peers.
/// </summary>
public static class AgentProtocolVersion
{
    public const int Legacy = 1;
    public const int Current = 2;

    public static int Negotiate(int requested) => requested <= 0 ? Legacy : Math.Min(requested, Current);
}

public static class AgentProtocolCapabilities
{
    public const string TaskV1 = "task-v1";
    public const string CatalogSync = "catalog-sync-v1";
    public const string CapabilityLeases = "capability-leases-v1";

    public static IReadOnlyList<string> Agent { get; } = [TaskV1, CatalogSync, CapabilityLeases, AgentSessionRules.Capability, AgentExecutionSettings.Capability];
    public static IReadOnlyList<string> Server { get; } = [TaskV1, CatalogSync, CapabilityLeases, AgentSessionRules.Capability, AgentExecutionSettings.Capability];

    public static IReadOnlyList<string> Negotiate(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0) return [];
        var supported = Server.ToHashSet(StringComparer.Ordinal);
        return requested.Where(supported.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
