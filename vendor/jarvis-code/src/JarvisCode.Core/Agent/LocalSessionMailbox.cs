using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace JarvisCode.Core.Agent;

public sealed record LocalSessionPeer(string EndpointId, string SessionId, string Title, int ProcessId,
    long ProcessStartedUtcTicks, string PipeName);

/// <summary>
/// Acknowledged same-user, same-profile IPC between desktop and CLI sessions.
/// The registry is discovery only: the receiving named pipe verifies the caller
/// PID against its live registry entry before accepting a message or subscription.
/// No TCP port, credential, or remotely reachable listener is used.
/// </summary>
public sealed class LocalSessionMailbox : IAsyncDisposable
{
    private const int MaximumPacketBytes = 262144;
    private readonly string _directory;
    private readonly string _registration;
    private readonly Func<string> _title;
    private readonly Action<LocalSessionPeer, string> _onMessage;
    private readonly Action<SessionIdleNotice> _onNotice;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SessionIdleSubscriptions _outgoing = new();
    private readonly ConcurrentDictionary<string, LocalSessionPeer> _watched = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (LocalSessionPeer Peer, DateTimeOffset Expires)> _subscribers = new(StringComparer.Ordinal);
    private readonly Timer _sweep;
    private readonly Task _server;
    private LocalSessionPeer _self;
    private bool _idle;
    private bool _finishedTurn;
    private DateTimeOffset _finishedAt;
    private string? _detail;
    private int _disposed;
    private readonly object _state = new();

    private sealed record Packet(string Kind, LocalSessionPeer From, string? Message = null, SessionIdleNotice? Notice = null);
    private sealed record Reply(bool Accepted, string? Error = null);

    public LocalSessionMailbox(string profileDirectory, string sessionId, Func<string> title,
        Action<LocalSessionPeer, string> onMessage, Action<SessionIdleNotice> onNotice)
    {
        _directory = Path.Combine(Path.GetFullPath(profileDirectory), "session-mailboxes");
        Directory.CreateDirectory(_directory);
        var identityPath = OperatingSystem.IsWindows() ? _directory.ToUpperInvariant() : _directory;
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath)))[..16];
        var endpoint = Guid.NewGuid().ToString("N");
        _self = new LocalSessionPeer(endpoint, sessionId, title(), Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, $"jarvis-session-{scope}-{endpoint}");
        _registration = Path.Combine(_directory, endpoint + ".json");
        _title = title; _onMessage = onMessage; _onNotice = onNotice;
        Publish();
        _server = Task.Run(ListenAsync);
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public string SessionId => _self.SessionId;
    public bool HasPendingSubscriptions => !_watched.IsEmpty || !_subscribers.IsEmpty;

    public IReadOnlyList<LocalSessionPeer> ListPeers()
        => [.. ReadPeers(Path.GetDirectoryName(_directory)!).Where(peer => peer.EndpointId != _self.EndpointId)];

    /// <summary>Read-only discovery for command-line status views; no temporary live session is published.</summary>
    public static IReadOnlyList<LocalSessionPeer> ReadPeers(string profileDirectory)
    {
        var directory = Path.Combine(Path.GetFullPath(profileDirectory), "session-mailboxes");
        if (!Directory.Exists(directory)) return [];
        var identityPath = OperatingSystem.IsWindows() ? directory.ToUpperInvariant() : directory;
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath)))[..16];
        List<LocalSessionPeer> peers = [];
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var peer = JsonSerializer.Deserialize<LocalSessionPeer>(File.ReadAllText(path));
                if (peer is not null && !string.IsNullOrWhiteSpace(peer.SessionId) && peer.Title is not null &&
                    Path.GetFileNameWithoutExtension(path) == peer.EndpointId &&
                    peer.PipeName == $"jarvis-session-{scope}-{peer.EndpointId}" && Alive(peer))
                    peers.Add(peer);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return peers;
    }

    public async Task<string?> SendAsync(string target, string message, CancellationToken cancellationToken = default)
    {
        if (Resolve(target, out var peer) is { } error) return error;
        return await RequestAsync(peer!, new Packet("message", CurrentSelf(), message), cancellationToken);
    }

    public async Task<string?> SubscribeAsync(string target, CancellationToken cancellationToken = default)
    {
        if (Resolve(target, out var peer) is { } error) return error;
        var remote = peer!;
        var refused = _outgoing.Subscribe(remote.EndpointId, _self.EndpointId, remote.Title, notice =>
        {
            _watched.TryRemove(remote.EndpointId, out _);
            if (Volatile.Read(ref _disposed) == 0) _onNotice(notice);
        });
        if (refused is not null) return refused;
        _watched[remote.EndpointId] = remote;
        string? result;
        try { result = await RequestAsync(remote, new Packet("subscribe", CurrentSelf()), cancellationToken); }
        catch
        {
            _outgoing.Cancel(remote.EndpointId, _self.EndpointId);
            _watched.TryRemove(remote.EndpointId, out _);
            throw;
        }
        if (result is not null)
        {
            _outgoing.Cancel(remote.EndpointId, _self.EndpointId);
            _watched.TryRemove(remote.EndpointId, out _);
        }
        return result;
    }

    public void NotifyBusy()
    {
        lock (_state) _idle = false;
        Publish();
    }

    public void NotifyIdle(string? lastTurnText)
    {
        lock (_state)
        {
            _idle = true; _finishedTurn = true; _finishedAt = DateTimeOffset.Now;
            _detail = SessionIdleSubscriptions.Sanitize(lastTurnText?.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)));
        }
        Publish();
        _ = FlushSubscribersAsync("idle");
    }

    private LocalSessionPeer CurrentSelf() => _self with { Title = _title() };
    private void Publish()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _self = CurrentSelf();
        var temporary = _registration + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_self));
            File.Move(temporary, _registration, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Utilities.DiagnosticLog.Write("session registry update failed: " + ex.Message); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string? Resolve(string target, out LocalSessionPeer? peer)
    {
        peer = null;
        if (string.IsNullOrWhiteSpace(target)) return "A target session is required.";
        if (_self.SessionId.Equals(target, StringComparison.Ordinal) || _self.Title.Equals(target, StringComparison.OrdinalIgnoreCase))
            return "The target is this session.";
        var matches = ListPeers().Where(p => p.SessionId.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
            p.Title.Equals(target, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return $"No live local session matches '{target}'.";
        if (matches.Length > 1) return $"'{target}' matches {matches.Length} live sessions; use a longer session id.";
        peer = matches[0];
        return null;
    }

    private string ExpectedPipe(string endpoint) => _self.PipeName[.._self.PipeName.LastIndexOf('-')] + "-" + endpoint;
    private bool Registered(LocalSessionPeer peer)
    {
        if (string.IsNullOrEmpty(peer.EndpointId) || peer.EndpointId.Length != 32 || !peer.EndpointId.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(peer.SessionId) || peer.Title is null || peer.PipeName != ExpectedPipe(peer.EndpointId) || !Alive(peer))
            return false;
        try
        {
            var registered = JsonSerializer.Deserialize<LocalSessionPeer>(File.ReadAllText(Path.Combine(_directory, peer.EndpointId + ".json")));
            return registered is not null && registered.ProcessId == peer.ProcessId &&
                registered.ProcessStartedUtcTicks == peer.ProcessStartedUtcTicks && registered.SessionId == peer.SessionId &&
                registered.PipeName == peer.PipeName;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private static bool Alive(LocalSessionPeer peer)
    {
        try
        {
            using var process = Process.GetProcessById(peer.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == peer.ProcessStartedUtcTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_self.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var packet = await ReadAsync<Packet>(pipe, timeout.Token);
                Reply reply;
                if (packet?.From is null || !Registered(packet.From) ||
                    (OperatingSystem.IsWindows() && (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != packet.From.ProcessId)))
                    reply = new Reply(false, "The sending session could not be verified.");
                else
                {
                    try { reply = Handle(packet); }
                    catch (Exception ex) { reply = new Reply(false, "The target could not accept the request: " + ex.Message); }
                }
                await WriteAsync(pipe, reply, timeout.Token);
                if (packet?.Kind == "subscribe" && reply.Accepted)
                {
                    bool idle;
                    lock (_state) idle = _idle && _finishedTurn;
                    if (idle) _ = FlushSubscribersAsync("idle");
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            { Utilities.DiagnosticLog.Write("session mailbox request failed: " + ex.Message); }
        }
    }

    private Reply Handle(Packet packet)
    {
        switch (packet.Kind)
        {
            case "message":
                if (string.IsNullOrWhiteSpace(packet.Message)) return new Reply(false, "message is required");
                _onMessage(packet.From, packet.Message);
                return new Reply(true);
            case "subscribe":
                if (_subscribers.Count >= SessionIdleSubscriptions.MaximumSubscriptions && !_subscribers.ContainsKey(packet.From.EndpointId))
                    return new Reply(false, "The target's idle subscription table is full.");
                _subscribers[packet.From.EndpointId] = (packet.From, DateTimeOffset.UtcNow + SessionIdleSubscriptions.Lifetime);
                return new Reply(true);
            case "notice" when packet.Notice?.Kind is "idle" or "exited":
                if (!_watched.ContainsKey(packet.From.EndpointId)) return new Reply(false, "No matching idle subscription is pending.");
                _outgoing.Receive(packet.From.EndpointId, packet.Notice);
                return new Reply(true);
            default: return new Reply(false, "Unknown session mailbox request.");
        }
    }

    private async Task FlushSubscribersAsync(string kind)
    {
        var notices = new List<(LocalSessionPeer Peer, SessionIdleNotice Notice)>();
        lock (_state)
        {
            if (kind == "idle" && !_idle) return;
            foreach (var pair in _subscribers)
                if (_subscribers.TryRemove(pair.Key, out var subscription))
                    notices.Add((subscription.Peer, new SessionIdleNotice(kind, _self.Title,
                        kind == "idle" ? _finishedAt : DateTimeOffset.Now, kind == "idle" ? _detail : null)));
        }
        await Task.WhenAll(notices.Select(async item =>
        {
            var error = await RequestAsync(item.Peer, new Packet("notice", CurrentSelf(), Notice: item.Notice), CancellationToken.None);
            if (error is not null) Utilities.DiagnosticLog.Write("idle notice was not acknowledged: " + error);
        }));
    }

    private void Sweep()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        foreach (var peer in _watched.Values)
            if (!Registered(peer)) _outgoing.Exit(peer.EndpointId);
        foreach (var (id, subscriber) in _subscribers)
            if (subscriber.Expires < DateTimeOffset.UtcNow || !Registered(subscriber.Peer)) _subscribers.TryRemove(id, out _);
    }

    private async Task<string?> RequestAsync(LocalSessionPeer peer, Packet packet, CancellationToken token)
    {
        if (!Registered(peer)) return "The target session has exited or changed its address.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", peer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            if (OperatingSystem.IsWindows() && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != peer.ProcessId))
                return "The target pipe does not belong to the registered session process.";
            await WriteAsync(pipe, packet, timeout.Token);
            var reply = await ReadAsync<Reply>(pipe, timeout.Token);
            return reply?.Accepted == true ? null : reply?.Error ?? "The target did not acknowledge the request.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
        { return "The target did not acknowledge the request: " + ex.Message; }
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length > MaximumPacketBytes) throw new IOException("Session message exceeds the 256 KB limit.");
        await stream.WriteAsync(BitConverter.GetBytes(data.Length), token);
        await stream.WriteAsync(data, token);
        await stream.FlushAsync(token);
    }

    private static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var length = BitConverter.ToInt32(prefix);
        if (length is < 1 or > MaximumPacketBytes) throw new IOException("Invalid session packet length.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sweep.Dispose();
        await FlushSubscribersAsync("exited");
        _outgoing.Dispose();
        _lifetime.Cancel();
        try { await _server; } catch (OperationCanceledException) { }
        try { File.Delete(_registration); } catch (IOException) { }
        _lifetime.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
