using System.Net.WebSockets;
using Jarvis.McpServer.Domain;
using Jarvis.Protocol;

namespace Jarvis.McpServer.Infrastructure;

public sealed partial class WsAgentRouter : IAgentTaskRouter
{
    public bool SupportsTasks(string ownerId, string deviceId) =>
        _peers.TryGetValue(deviceId, out var peer) && peer.OwnerId == ownerId &&
        peer.Wire.State == WebSocketState.Open && peer.TaskProtocolVersion == RemoteTaskRules.ProtocolVersion;

    public async Task<RemoteTaskReply> TaskAsync(string ownerId, string deviceId, string operation,
        RemoteTaskRequest request, CancellationToken cancellationToken)
    {
        if (!RemoteTaskRules.Operations.Contains(operation)) throw new ArgumentException("Unknown task operation.");
        if (!_peers.TryGetValue(deviceId, out var peer) || peer.OwnerId != ownerId)
            throw new InvalidOperationException("Your selected agent is offline.");
        if (peer.TaskProtocolVersion != RemoteTaskRules.ProtocolVersion)
            throw new InvalidOperationException("Selected agent does not support task-v1. Upgrade the agent; ordinary tools remain available.");
        if (!await IsAuthorizedAsync(deviceId, ownerId, peer.TokenHash, cancellationToken))
            throw new UnauthorizedAccessException("Device authorization expired or was revoked.");
        using var admission = await peer.EnterAsync(control: true, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<RemoteTaskReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.TaskPending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await peer.Wire.SendAsync(new("task.request") { Id = id, TaskOperation = operation,
                TaskRequest = request with { OwnerId = ownerId, TaskId = RemoteTaskRules.TaskId(request.TaskId) },
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(15) }, timeout.Token);
            var reply = await completion.Task.WaitAsync(timeout.Token);
            if (reply.Error is null && (reply.Task is null || reply.Task.TaskId != RemoteTaskRules.TaskId(request.TaskId)))
                throw new InvalidDataException("Agent returned a mismatched task identity.");
            return reply;
        }
        // A lost create acknowledgement is not a reason to replay a task. Caller can query the same taskId.
        finally { peer.TaskPending.TryRemove(id, out _); }
    }
}
