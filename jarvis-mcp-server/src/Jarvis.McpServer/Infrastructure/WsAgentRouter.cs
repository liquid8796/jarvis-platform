using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.McpServer.Application;
using Jarvis.McpServer.Domain;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
namespace Jarvis.McpServer.Infrastructure;

/// <summary>One broker process per deployment. Never retries a dispatched command after a lost connection.</summary>
public sealed partial class WsAgentRouter(IDbContextFactory<AppDbContext> contexts, JarvisOptions options,
    ILogger<WsAgentRouter> logger) : IAgentRouter
{
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$")] private static partial Regex IdPattern();
    public bool IsOnline(string ownerId, string deviceId) =>
        _peers.TryGetValue(deviceId, out var peer) && peer.OwnerId == ownerId && peer.Wire.State == WebSocketState.Open;
    public void DisconnectDevice(string deviceId) { if (_peers.TryGetValue(deviceId, out var peer)) peer.Wire.Abort(); }
    public void DisconnectUser(string ownerId)
    { foreach (var peer in _peers.Values.Where(p => p.OwnerId == ownerId)) peer.Wire.Abort(); }

    public async Task AcceptAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest) { http.Response.StatusCode = 400; return; }
        // Browser JavaScript must not use this channel, even with a logged-in cookie.
        if (http.Request.Headers.ContainsKey("Origin")) { http.Response.StatusCode = 403; return; }
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer jra_", StringComparison.Ordinal) || header.Length > 200)
        { http.Response.StatusCode = 401; return; }
        var hash = DeviceService.TokenHash(header[7..]);
        await using var db = await contexts.CreateDbContextAsync(http.RequestAborted);
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.TokenHash == hash, http.RequestAborted);
        if (device is null || !await IsAuthorizedAsync(device.Id, device.OwnerId, hash, http.RequestAborted))
        { http.Response.StatusCode = 401; return; }
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var peer = new Peer(device.OwnerId, device.Id, hash, new WireSocket(socket));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var envelope = await peer.Wire.ReceiveAsync(handshake.Token);
            var hello = envelope?.Hello;
            if (envelope?.Type != "hello" || hello is null || hello.DeviceId != device.Id || hello.Tools is null || hello.Tools.Count is < 1 or > 256 ||
                hello.Platform is null || hello.Version is null ||
                hello.Tools.Any(t => t is null || string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.Name) || t.Description is null ||
                    !IdPattern().IsMatch(t.Id) || t.Name.Length > 64 ||
                    t.Description.Length > 8000 || t.InputSchema.ValueKind != JsonValueKind.Object ||
                    t.InputSchema.GetRawText().Length > 65536) || hello.Tools.Select(t => t.Id).Distinct().Count() != hello.Tools.Count)
                throw new InvalidDataException("Invalid agent manifest.");
            foreach (var descriptor in hello.Tools) _ = SchemaGuard.Compile(descriptor.InputSchema);
            peer.TaskProtocolVersion = hello.TaskProtocolVersion;
            peer.UpdateCatalog(hello.CatalogGeneration, hello.CatalogDigest);
            // A new connection replaces only this enrolled device, never other users' connections.
            _peers.AddOrUpdate(device.Id, peer, (_, previous) => { previous.Wire.Abort(); return peer; });
            var current = await db.Devices.SingleAsync(d => d.Id == device.Id, stop.Token);
            current.CapabilitiesJson = JsonSerializer.Serialize(hello.Tools, WireJson.Options);
            current.Platform = hello.Platform[..Math.Min(hello.Platform.Length, 200)];
            current.AgentVersion = hello.Version[..Math.Min(hello.Version.Length, 40)];
            current.LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(stop.Token);
            await peer.Wire.SendAsync(new WireMessage("welcome"), stop.Token);
            var heartbeat = WatchAsync(peer, stop.Token);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var message = await peer.Wire.ReceiveAsync(stop.Token);
                    if (message is null) break;
                    Interlocked.Exchange(ref peer.LastActivity, Environment.TickCount64);
                    switch (message.Type)
                    {
                        case "ping": await peer.Wire.SendAsync(new WireMessage("pong") { Timestamp = message.Timestamp }, stop.Token); break;
                        case "pong": break;
                        case "catalog.changed":
                            await ApplyCatalogChangedAsync(peer, message, stop.Token);
                            break;
                        case "task.result" when message.Id is not null && message.TaskReply is not null:
                            if (peer.TaskPending.TryRemove(message.Id, out var taskCompletion)) taskCompletion.TrySetResult(message.TaskReply);
                            break;
                        case "result" when message.Id is not null && message.Result is not null:
                            if (peer.Pending.TryRemove(message.Id, out var completion)) completion.TrySetResult(message.Result);
                            break;
                        default: throw new InvalidDataException("Unexpected agent envelope.");
                    }
                }
            }
            finally { stop.Cancel(); try { await heartbeat; } catch (OperationCanceledException) { } }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or JsonException or ArgumentException or OperationCanceledException or ObjectDisposedException or DbUpdateException)
        { logger.LogInformation("Agent disconnected device={DeviceId} reason={Reason}", device.Id, ex.GetType().Name); }
        finally
        {
            ((ICollection<KeyValuePair<string, Peer>>)_peers).Remove(new(device.Id, peer));
            foreach (var completion in peer.Pending.Values)
                completion.TrySetException(new IOException("Agent disconnected. Completion is unknown; inspect state before repeating a mutating command."));
            foreach (var completion in peer.TaskPending.Values)
                completion.TrySetException(new IOException("Agent disconnected; task acknowledgement or completion may be unknown."));
            peer.Wire.Abort();
        }
    }
    private async Task ApplyCatalogChangedAsync(Peer peer, WireMessage message, CancellationToken ct)
    {
        var tools = message.CatalogTools;
        var generation = message.CatalogGeneration;
        var digest = message.CatalogDigest;
        if (generation is null || generation <= peer.CatalogGeneration || string.IsNullOrWhiteSpace(digest) || digest.Length > 128 ||
            tools is null || tools.Count is < 1 or > 256 ||
            tools.Any(t => t is null || string.IsNullOrEmpty(t.Id) || string.IsNullOrEmpty(t.Name) || t.Description is null ||
                !IdPattern().IsMatch(t.Id) || t.Name.Length > 64 || t.Description.Length > 8000 ||
                t.InputSchema.ValueKind != JsonValueKind.Object || t.InputSchema.GetRawText().Length > 65536) ||
            tools.Select(t => t.Id).Distinct().Count() != tools.Count)
            throw new InvalidDataException("Invalid catalog update.");
        foreach (var descriptor in tools) _ = SchemaGuard.Compile(descriptor.InputSchema);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var device = await db.Devices.SingleAsync(d => d.Id == peer.DeviceId && d.OwnerId == peer.OwnerId, ct);
        device.CapabilitiesJson = JsonSerializer.Serialize(tools, WireJson.Options);
        device.LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        peer.UpdateCatalog(generation.Value, digest);
        await peer.Wire.SendAsync(new WireMessage("catalog.ack")
        {
            CatalogGeneration = generation.Value,
            CatalogDigest = digest
        }, ct);
    }

    private async Task<bool> IsAuthorizedAsync(string id, string owner, string hash, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return await db.Devices.AnyAsync(d => d.Id == id && d.OwnerId == owner && d.Enabled &&
            d.TokenHash == hash && d.TokenExpiresAt > now, ct) &&
            await db.Users.AnyAsync(u => u.Id == owner && u.Status == "active", ct);
    }
    private async Task WatchAsync(Peer peer, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (Environment.TickCount64 - Interlocked.Read(ref peer.LastActivity) > 50_000 ||
                !await IsAuthorizedAsync(peer.DeviceId, peer.OwnerId, peer.TokenHash, ct))
            { peer.Wire.Abort(); return; }
            try { await peer.Wire.SendAsync(new WireMessage("ping") { Timestamp = Environment.TickCount64 }, ct); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { peer.Wire.Abort(); return; }
        }
    }
    public async Task<ToolReply> CallAsync(string ownerId, string deviceId, string toolId, JsonElement arguments,
        string sessionId, CancellationToken cancellationToken)
    {
        if (!_peers.TryGetValue(deviceId, out var peer) || peer.OwnerId != ownerId)
            throw new InvalidOperationException("Your selected agent is offline.");
        if (!await IsAuthorizedAsync(deviceId, ownerId, peer.TokenHash, cancellationToken))
            throw new UnauthorizedAccessException("Device authorization expired or was revoked.");
        if (!await peer.Slots.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Agent busy. No command was dispatched.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ToolReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ToolTimeoutSeconds));
        try
        {
            await peer.Wire.SendAsync(new WireMessage("call") { Id = id, ToolId = toolId, SessionId = sessionId,
                Arguments = arguments, DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(options.ToolTimeoutSeconds),
                ExpectedCatalogGeneration = peer.CatalogGeneration > 0 ? peer.CatalogGeneration : null,
                ExpectedCatalogDigest = peer.CatalogDigest }, timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await peer.Wire.SendAsync(new WireMessage("cancel") { Id = id }, cancelTimeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
            throw;
        }
        finally { peer.Pending.TryRemove(id, out _); peer.Slots.Release(); }
    }
    private sealed class Peer(string owner, string device, string hash, WireSocket wire)
    {
        public string OwnerId { get; } = owner;
        public string DeviceId { get; } = device;
        public string TokenHash { get; } = hash;
        public WireSocket Wire { get; } = wire;
        public long LastActivity = Environment.TickCount64;
        public SemaphoreSlim Slots { get; } = new(4, 4);
        public ConcurrentDictionary<string, TaskCompletionSource<ToolReply>> Pending { get; } = new();
        public int TaskProtocolVersion { get; set; }
        public long CatalogGeneration { get; private set; }
        public string? CatalogDigest { get; private set; }
        public void UpdateCatalog(long generation, string? digest)
        {
            CatalogGeneration = generation;
            CatalogDigest = string.IsNullOrWhiteSpace(digest) ? null : digest;
        }
        public ConcurrentDictionary<string, TaskCompletionSource<RemoteTaskReply>> TaskPending { get; } = new();
    }
}
