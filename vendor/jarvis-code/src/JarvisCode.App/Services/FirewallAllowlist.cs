using System.Net.Sockets;
using JarvisCode.Core.Settings;

namespace JarvisCode.App.Services;

/// <summary>One host the app needs to reach, and whether the last test reached it.</summary>
public sealed record AllowlistHost(string Host, bool? Reachable);

/// <summary>
/// The "Firewall allowlist" the reference's third-party inference window shows
/// (ion-dist <c>c71860c77-DuPx-LoQ.js</c>: <c>DauzyWMEs/</c> for the heading,
/// <c>1gKYoIkZf0</c> for "{reachable} of {total} reachable", <c>HVOVcW54d6</c> for
/// "Test connectivity", <c>W41+8Xj7fP</c> for "Copy hostnames" and
/// <c>83Dth0tmbB</c> for "Download .txt"): the hosts a network policy has to let
/// through, gathered from the endpoints this build is actually configured with.
/// </summary>
public static class FirewallAllowlist
{
    /// <summary>Every host the configured endpoints name, deduplicated and sorted.</summary>
    public static IReadOnlyList<string> Hosts(AppSettings settings, string? searchEndpoint = null)
    {
        var urls = new List<string?>
        {
            "https://api.anthropic.com",
            settings.OllamaBaseUrl,
            settings.NvidiaBaseUrl,
            settings.OpenRouterBaseUrl,
            settings.TokenRouterBaseUrl,
            settings.DeepSeekBaseUrl,
            settings.ZhipuBaseUrl,
            settings.MiniMaxBaseUrl,
            settings.LlmApiBaseUrl,
            searchEndpoint ?? settings.SearxngBaseUrl,
        };

        if (settings.BedrockRegion is { Length: > 0 } bedrock)
        {
            urls.Add($"https://bedrock-runtime.{bedrock}.amazonaws.com");
        }

        if (settings.VertexRegion is { Length: > 0 } vertex)
        {
            urls.Add(vertex == "global"
                ? "https://aiplatform.googleapis.com"
                : $"https://{vertex}-aiplatform.googleapis.com");
        }

        urls.AddRange(settings.CustomProviders.Select(p => p.BaseUrl));

        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            if (Host(url) is { } host)
            {
                hosts.Add(host);
            }
        }

        return [.. hosts];
    }

    private static string? Host(string? url) =>
        url is { Length: > 0 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : null;

    /// <summary>The reference's summary line.</summary>
    public static string Summary(int reachable, int total) => $"{reachable} of {total} reachable";

    /// <summary>Opens a TCP connection to each host on 443; a refused or unresolved host reads as unreachable.</summary>
    public static async Task<IReadOnlyList<AllowlistHost>> TestAsync(
        IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        var results = new List<AllowlistHost>(hosts.Count);
        foreach (var host in hosts)
        {
            results.Add(new AllowlistHost(host, await ReachableAsync(host, cancellationToken)));
        }

        return results;
    }

    private static async Task<bool> ReachableAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await client.ConnectAsync(host, 443, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }
}
