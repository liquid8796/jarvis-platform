using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.Core.Hooks;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The two hook kinds that leave this process: an HTTP endpoint the payload is
/// POSTed to, and a tool on a configured MCP server. The rules live in
/// <see cref="RemoteHooks"/>; this is the wire.
/// </summary>
public static class HookTransports
{
    /// <summary>
    /// One client for every HTTP hook: a hook on PostToolUse fires on every tool
    /// call, and a client per call would leave sockets in TIME_WAIT. Redirects
    /// are off — a hook must not be talked into following one — and the connect
    /// callback refuses a private or link-local address at the socket, which is
    /// the only place a name cannot resolve to something else afterwards.
    /// </summary>
    private static readonly Lazy<HttpClient> HookClient = new(() => new HttpClient(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var endPoint = context.DnsEndPoint;
                IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);
                foreach (var address in addresses)
                {
                    if (RemoteHooks.IsBlockedAddress(address))
                    {
                        throw new HttpRequestException(
                            RemoteHooks.AddressBlocked(endPoint.Host, address.ToString()));
                    }
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, endPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
    {
        // Each call carries its own deadline, so the client itself never times out.
        Timeout = Timeout.InfiniteTimeSpan,
    });

    /// <summary>
    /// A sender that POSTs the payload and reads the answer back. Any status is
    /// returned rather than thrown — a non-2xx is the hook's verdict, not a
    /// transport failure.
    /// </summary>
    public static HttpHookSender CreateHttpSender() => async (request, cancellationToken) =>
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return new HookTransportResult(
                false, "", Error: $"HTTP hook blocked: {request.Url} is not an http(s) URL");
        }

        using var content = new StringContent(request.PayloadJson, Encoding.UTF8);
        content.Headers.Remove("Content-Type");
        using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        foreach (var (name, value) in request.Headers)
        {
            System.Net.Http.Headers.HttpHeaders target =
                name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                    ? content.Headers
                    : message.Headers;
            target.TryAddWithoutValidation(name, value);
        }

        if (!content.Headers.Contains("Content-Type"))
        {
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout <= TimeSpan.Zero ? RemoteHooks.DefaultTimeout : request.Timeout);
        try
        {
            using var response = await HookClient.Value.SendAsync(message, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            return new HookTransportResult(response.IsSuccessStatusCode, body, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HookTransportResult(false, "", Aborted: true);
        }
        catch (HttpRequestException ex)
        {
            return new HookTransportResult(false, "", Error: ex.Message);
        }
    };

    /// <summary>
    /// A caller that runs the hook's tool on a connected MCP server. A server
    /// that is not connected refuses by name rather than silently passing, which
    /// is what makes an mcp_tool hook a dependency you can see.
    /// </summary>
    public static McpHookCaller CreateMcpCaller(McpManager mcp, string workingDirectory) =>
        async (request, cancellationToken) =>
        {
            var adapter = mcp.Tools.OfType<McpToolAdapter>().FirstOrDefault(t =>
                string.Equals(t.ServerName, request.Server, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.SourceToolName, request.Tool, StringComparison.Ordinal));
            if (adapter is null)
            {
                return new HookTransportResult(false, "", Error: RemoteHooks.McpNotConnected(request.Server));
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout <= TimeSpan.Zero ? RemoteHooks.DefaultTimeout : request.Timeout);
            try
            {
                var result = await adapter.ExecuteAsync(
                    request.Input,
                    new ToolExecutionContext { WorkingDirectory = workingDirectory },
                    timeout.Token);
                return new HookTransportResult(!result.IsError, result.Content,
                    Error: result.IsError ? result.Content : null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new HookTransportResult(false, "", Aborted: true);
            }
        };
}
