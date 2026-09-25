using System.Collections.Concurrent;
using System.Security.Claims;
using Jarvis.McpServer.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Jarvis.McpServer.Infrastructure;

/// <summary>
/// Tracks only stateful MCP sessions so agent/admin catalog changes can request a tools/list refresh.
/// Stateless MCP clients remain fully functional through the permanent dynamic gateway tools.
/// </summary>
public sealed class McpToolCatalogChangeHub
{
    private sealed record Registration(string OwnerId, string DeviceId, ModelContextProtocol.Server.McpServer Server);
    private readonly ConcurrentDictionary<string, Registration> _sessions = new(StringComparer.Ordinal);

    public async Task RunSessionAsync(HttpContext http, ModelContextProtocol.Server.McpServer server, CancellationToken ct)
    {
        var ownerId = CurrentAccess.UserId(http.User);
        var deviceId = http.User.FindFirstValue(CurrentAccess.DeviceClaim);
        var key = server.SessionId;
        if (key is null || string.IsNullOrWhiteSpace(deviceId))
        {
            await server.RunAsync(ct);
            return;
        }

        _sessions[key] = new(ownerId, deviceId, server);
        try { await server.RunAsync(ct); }
        finally { _sessions.TryRemove(key, out _); }
    }

    public Task NotifyDeviceAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
        NotifyAsync(registration =>
            StringComparer.Ordinal.Equals(registration.OwnerId, ownerId) &&
            StringComparer.Ordinal.Equals(registration.DeviceId, deviceId), ct);

    public Task NotifyAllAsync(CancellationToken ct = default) => NotifyAsync(_ => true, ct);

    private async Task NotifyAsync(Func<Registration, bool> predicate, CancellationToken ct)
    {
        var targets = _sessions.ToArray().Where(pair => predicate(pair.Value)).ToArray();
        foreach (var pair in targets)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await pair.Value.Server.SendNotificationAsync(
                    NotificationMethods.ToolListChangedNotification, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }
}
