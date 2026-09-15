using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Composition;
using JarvisCode.App.ViewModels;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Services;

/// <summary>
/// The reference ListAgents: what SendMessage can reach — this session's
/// background agents, and the other local Code sessions on this machine.
/// </summary>
public sealed class ListAgentsTool(Func<CancellationToken, Task<string>> listing) : ITool
{
    public ListAgentsTool(AppServices services, ChatViewModel viewModel)
        : this(_ => BuildListingAsync(services, viewModel)) { }
    private const int MaxSessions = 15;

    public string Name => "ListAgents";

    public string Description =>
        "Lists agents you can SendMessage to: this session's background agents (finished ones accept " +
        "follow-ups), and other local Code sessions (a message arrives there as a task notification). " +
        "Names are the address — copy them exactly.";

    public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) => "ListAgents()";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
        ToolResult.Success(await listing(cancellationToken));

    /// <summary>
    /// The listing itself, so the /list-agents command shows the user exactly
    /// what the tool shows the model rather than a second version of it.
    /// </summary>
    public static async Task<string> BuildListingAsync(AppServices services, ChatViewModel viewModel)
    {
        var baseListing = await BuildListingAsync(services, viewModel.Session.Id, viewModel.Workers);
        var peers = viewModel.EnsureMailbox().ListPeers();
        return baseListing + (peers.Count == 0 ? "" : "\nLive session mailboxes:\n" +
            string.Join('\n', peers.Select(p => $"  \"{p.Title}\" · {p.SessionId} · process {p.ProcessId}")));
    }

    public static async Task<string> BuildListingAsync(
        AppServices services, string currentSessionId, JarvisCode.Core.Agent.AgentWorkerManager workerRegistry)
    {
        var text = new StringBuilder();
        var workers = workerRegistry.List();
        text.AppendLine(workers.Count == 0
            ? "Background agents: none in this session."
            : "Background agents:\n" + string.Join('\n', workers.Select(static w =>
                $"  {w.Id} · {w.AgentType} · {w.Status.ToString().ToLowerInvariant()} · {w.PromptPreview}")));

        try
        {
            var sessions = (await services.Sessions.ListAsync())
                .Where(s => s.Id != currentSessionId)
                .OrderByDescending(static s => s.UpdatedAt)
                .Take(MaxSessions)
                .ToList();
            if (sessions.Count > 0)
            {
                text.AppendLine("Other local sessions (SendMessage by title or id prefix):");
                foreach (var session in sessions)
                    text.AppendLine($"  \"{session.Title}\" · {session.Id[..Math.Min(8, session.Id.Length)]} · updated {session.UpdatedAt:yyyy-MM-dd HH:mm}");
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            text.AppendLine($"(Could not list local sessions: {ex.Message})");
        }

        return text.ToString().TrimEnd();
    }
}
