namespace JarvisCode.Core.Mcp;

/// <summary>
/// The installed CLI 2.1.260's host-list gates for root combinators and invalid
/// schemas. Both local cached feature values measured on 2026-09-08 are ["*"].
/// Supplying other lists preserves the reference's per-server URL decision;
/// an empty list disables that pass and a bare domain includes its subdomains.
/// </summary>
public sealed record McpSchemaPolicy
{
    public IReadOnlyList<string> NormalizeHosts { get; init; } = ["*"];
    public IReadOnlyList<string> DropInvalidHosts { get; init; } = ["*"];

    public bool Normalize(McpServerConfig server) => Matches(NormalizeHosts, server);
    public bool DropInvalid(McpServerConfig server) => Matches(DropInvalidHosts, server);

    private static bool Matches(IReadOnlyList<string> hosts, McpServerConfig server)
    {
        if (hosts.Contains("*", StringComparer.Ordinal)) return true;
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)) return false;
        return hosts.Any(host => !string.IsNullOrEmpty(host) &&
            (uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)));
    }
}
