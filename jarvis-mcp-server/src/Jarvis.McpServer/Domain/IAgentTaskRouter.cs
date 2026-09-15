using Jarvis.Protocol;
namespace Jarvis.McpServer.Domain;

public interface IAgentTaskRouter
{
    bool SupportsTasks(string ownerId, string deviceId);
    Task<RemoteTaskReply> TaskAsync(string ownerId, string deviceId, string operation,
        RemoteTaskRequest request, CancellationToken cancellationToken);
}
