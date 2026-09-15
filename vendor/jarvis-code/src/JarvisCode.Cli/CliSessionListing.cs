using System.Text;
using JarvisCode.Core.Agent;

namespace JarvisCode.Cli;

internal static class CliSessionListing
{
    public static string Build(AgentWorkerManager workers, LocalSessionMailbox mailbox, string sessionId)
    {
        var output = new StringBuilder("Background agents:");
        var agents = workers.List();
        if (agents.Count == 0) output.Append(" none in this session.");
        foreach (var agent in agents)
            output.Append($"\n  {agent.Id} · {agent.AgentType} · {agent.Status.ToString().ToLowerInvariant()} · {agent.PromptPreview}");
        var peers = mailbox.ListPeers().Where(peer => peer.SessionId != sessionId).ToArray();
        output.Append("\nOther live local sessions:");
        if (peers.Length == 0) output.Append(" none.");
        foreach (var peer in peers)
            output.Append($"\n  {peer.SessionId} · {peer.Title} · process {peer.ProcessId}");
        return output.ToString();
    }
}
