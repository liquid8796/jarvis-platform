namespace Jarvis.McpServer.Domain;

/// <summary>Gateway-owned names, never supplied by an Agent manifest or publication alias.</summary>
public static class ConnectionIdentityToolNames
{
    public const string Profile = "jarvis__profile";
    public const string WhoAmI = "jarvis__whoami";
    public static bool IsReserved(string name) => name is Profile or WhoAmI;
}

// Explicit projections prevent Identity/Device entities (passwords, stamps, tokens) being serialized.
public sealed record ConnectionProfile(string Id, string? Name, string? Email, string? Nickname);
public sealed record ConnectionAccount(string Id, string? Name, string? Email, string Role, string Status);
public sealed record ConnectionDevice(string Id, string? Name, bool Enabled, bool Online,
    string? Platform, string? AgentVersion, long? LastSeenAt);
public sealed record ConnectionServer(string Origin, string Version);
public sealed record ConnectionIdentity(ConnectionProfile Profile, ConnectionAccount Account,
    ConnectionDevice Device, ConnectionServer Server);
