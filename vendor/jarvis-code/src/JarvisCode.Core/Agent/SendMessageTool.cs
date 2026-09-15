using System.Text.Json.Nodes;
using JarvisCode.Core.Tools;

namespace JarvisCode.Core.Agent;

/// <summary>
/// Continues a finished background agent with a follow-up message on its intact
/// conversation — the reference app's SendMessage. The reply arrives as another
/// task notification for the same agent id.
/// </summary>
public sealed class SendMessageTool : ITool
{
    public string Name => "SendMessage";

    public string Description =>
        """
        # SendMessage

        Send a message to another agent.

        ```json
        {"to": "researcher", "summary": "assign task 1", "message": "start on task #1"}
        ```

        | `to` | |
        |---|---|
        | `"researcher"` | Teammate by name |
        | `"main"` | The main conversation (background subagents only) |
        | `"agent-1"` | A background agent by id, when it has no name |
        | `"a session title"` | Another local session from ListAgents |

        Your plain text output is NOT visible to other agents — to communicate, you MUST call this tool.
        Messages from teammates are delivered automatically; you don't check an inbox. Refer to agents by
        name — names keep working after an agent completes (a send resumes it from its transcript). Use the
        raw `agentId` from its spawn result only when the agent has no name, or when a newer agent took the
        name (latest wins). When relaying, don't quote the original — it's already rendered to the user.

        ## Protocol responses (legacy)

        If you receive a JSON message with `type: "shutdown_request"` or `type: "plan_approval_request"`,
        respond with the matching `_response` type — echo the `request_id`, set `approve` true/false:

        ```json
        {"to": "team-lead", "message": {"type": "shutdown_response", "request_id": "...", "approve": true}}
        {"to": "researcher", "message": {"type": "plan_approval_response", "request_id": "...", "approve": false, "feedback": "add error handling"}}
        ```

        Approving shutdown terminates your process. Rejecting plan sends the teammate back to revise. Don't
        originate `shutdown_request` unless asked. Don't send structured JSON status messages — report
        progress through your task tools if you have them, otherwise in plain prose.
        """;

    public JsonObject InputSchema => SchemaBuilder.Object(
        [
            ("to", SchemaBuilder.String(
                "Recipient: a teammate's name, a background agent's id (e.g. \"agent-1\"), \"team-lead\" from " +
                "inside a teammate, or another local session")),
            ("message", new JsonObject
            {
                ["description"] =
                    "Plain text message content, or a protocol object " +
                    "({type: \"shutdown_request\"|\"shutdown_response\"|\"plan_approval_response\", …})",
            }),
            ("summary", SchemaBuilder.String(
                "A 5-10 word summary shown as a one-line preview in the UI. Defaults to the first line of a " +
                "plain-text message")),
            ("notify_when_idle", SchemaBuilder.Boolean(
                "Ask a session ON THIS MACHINE to send you ONE notice when it next goes idle (finishes its turn with " +
                "nothing queued) or exits — opt-in, one-shot, no polling. With a message: deliver it now AND subscribe. " +
                "Without a message (omit it): a pure subscription.")),
        ],
        "to", "message");

    public bool IsReadOnly => true;

    public string DescribeCall(JsonObject arguments) =>
        $"SendMessage({JsonArgs.GetString(arguments, "to") ?? "?"})";

    public async Task<ToolResult> ExecuteAsync(
        JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        // A teammate has no worker manager of its own — its only correspondent
        // is the lead — so the lead channel counts as availability too.
        if (context.Workers is null && context.SendToSessionAsync is null && context.ReportToLeadAsync is null)
            return ToolResult.Error("Background agents are not available in this context.");

        var to = JsonArgs.GetString(arguments, "to");
        var summary = JsonArgs.GetString(arguments, "summary");

        // `message` is plain text or one of the protocol objects; the object
        // form drives the shutdown and plan handshakes.
        var protocol = TeamProtocolMessage.TryParse(arguments["message"], out var protocolError);
        if (protocolError is not null)
            return ToolResult.Error(protocolError);
        if (protocol is not null)
        {
            if (string.IsNullOrWhiteSpace(to))
                return ToolResult.Error("Both to and message are required.");
            return await HandleProtocolAsync(to, protocol, context, cancellationToken);
        }

        var message = JsonArgs.GetString(arguments, "message");
        var notifyWhenIdle = JsonArgs.GetBool(arguments, "notify_when_idle");
        // With notify_when_idle the message may be omitted: that is a pure subscription.
        if (string.IsNullOrWhiteSpace(to) || (string.IsNullOrWhiteSpace(message) && !notifyWhenIdle))
            return ToolResult.Error("Both to and message are required.");
        if (notifyWhenIdle && (context.IsSubagent || context.Team is not null))
            return ToolResult.Error(NotifyOnlyFromMainConversation);
        if (notifyWhenIdle && (context.SubscribeToSessionIdleAsync is null || context.Workers?.IsRunning(to) == true))
            return ToolResult.Error(NotifyOnlyForLocalSessions);
        if (string.IsNullOrWhiteSpace(message))
        {
            // Nothing to deliver: subscribe and say so.
            var subscribeError = await context.SubscribeToSessionIdleAsync!(to);
            return subscribeError is null
                ? ToolResult.Success(Subscribed(to))
                : ToolResult.Error(subscribeError);
        }

        // A teammate reporting upwards: "team-lead" and "main" both address the
        // session that spawned it, which is why neither may name an agent.
        var recipient = TeamNames.Canonical(to);
        if (recipient is TeamNames.Leader or TeamNames.Main)
        {
            if (context.ReportToLeadAsync is not { } reportToLead)
            {
                return ToolResult.Error(
                    $"\"{to}\" addresses the session that spawned a teammate, and this agent has no lead to " +
                    "report to. Name a teammate, an agent id, or another session instead.");
            }

            await reportToLead(context.Team?.AgentName ?? "teammate", message, summary);
            return ToolResult.Success($"Message sent to {to}.");
        }

        string? error;
        if (context.Workers is not null)
        {
            // A running teammate reads its inbox between steps; a finished one
            // resumes on its kept conversation.
            if (context.Workers.IsRunning(to))
            {
                error = context.Workers.Deliver(to, message);
                if (error is null)
                    return ToolResult.Success(
                        $"Message delivered to {to}. It picks the message up before its next step; " +
                        "its report will arrive as a task notification.");
            }

            error = context.Workers.Continue(to, message);
            if (error is null)
                return ToolResult.Success(
                    $"Message sent to {to}. Its reply will arrive as a task notification — continue with other work.");
        }
        else
        {
            error = "Background agents are not available in this context.";
        }

        // Not a worker of this session — try the host's other local sessions.
        if (context.SendToSessionAsync is { } sendToSession)
        {
            var sessionError = await sendToSession(to, message);
            if (sessionError is null)
            {
                var delivered =
                    $"Message delivered to session '{to}' as a task notification. That session answers in its own " +
                    "transcript — no reply comes back here.";
                if (!notifyWhenIdle)
                    return ToolResult.Success(delivered);

                // The reference subscribes only after the message went through.
                var subscribeError = await context.SubscribeToSessionIdleAsync!(to);
                return subscribeError is null
                    ? ToolResult.Success(delivered + "\n" + Subscribed(to))
                    : ToolResult.Success(delivered + "\nThe message was delivered, but no idle subscription was made: " + subscribeError);
            }

            return ToolResult.Error($"{error} {sessionError}");
        }

        return ToolResult.Error(error);
    }

    /// <summary>The reference's refusals for notify_when_idle, verbatim (CLI 2.1.257).</summary>
    public const string NotifyOnlyForLocalSessions =
        "notify_when_idle is only supported for Claude sessions on this machine in this release (not teammates, " +
        "subagents, Remote Control or cloud sessions).";

    public const string NotifyOnlyFromMainConversation =
        "notify_when_idle is only available from the main conversation of this session (not from a subagent or teammate).";

    private static string Subscribed(string to) =>
        $"Subscribed to \"{to}\": one idle notice will arrive here when it next finishes a turn with nothing queued, " +
        "or exits. Do not poll for it.";

    /// <summary>
    /// The shutdown and plan handshakes. A request travels to the recipient's
    /// inbox as its JSON; a response settles the state it answers.
    /// </summary>
    private static async Task<ToolResult> HandleProtocolAsync(
        string to,
        TeamProtocolMessage protocol,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var sender = context.Team?.AgentName ?? TeamNames.Leader;
        switch (protocol.Kind)
        {
            case TeamProtocolKind.ShutdownRequest:
            {
                var requestId = $"shutdown-{Guid.NewGuid().ToString("n")[..8]}";
                var payload = protocol with { RequestId = requestId };
                if (!await DeliverProtocolAsync(to, payload, sender, context))
                    return ToolResult.Error(TeamProtocolMessages.ShutdownRequestFailed(to));
                return ToolResult.Success(TeamProtocolMessages.ShutdownRequestSent(to, requestId));
            }

            case TeamProtocolKind.ShutdownResponse:
            {
                if (protocol.Approve != true)
                {
                    var reason = protocol.Reason ?? "";
                    var relayed = await DeliverProtocolAsync(to, protocol, sender, context);
                    return relayed
                        ? ToolResult.Success(TeamProtocolMessages.ShutdownRejected(reason))
                        : ToolResult.Error(TeamProtocolMessages.ShutdownRejectionFailed);
                }

                // Approving one's own shutdown ends this agent; the lead hears
                // about it first so the exit is never silent.
                var confirmed = await DeliverProtocolAsync(to, protocol, sender, context);
                var confirmation = confirmed
                    ? TeamProtocolMessages.ShutdownConfirmationSent
                    : TeamProtocolMessages.ShutdownConfirmationFailed;
                context.RequestOwnShutdown?.Invoke();
                return ToolResult.Success(TeamProtocolMessages.ShutdownApproved(sender, confirmation));
            }

            default:
            {
                if (context.Team is { IsLeader: false })
                    return ToolResult.Error(TeamProtocolMessages.PlanApprovalNotLeader);

                var registry = context.PlanApprovals;
                var owner = registry?.OwnerOf(protocol.RequestId!);
                if (registry is not null && owner is null)
                {
                    return ToolResult.Error(
                        $"No plan is waiting on request id '{protocol.RequestId}'. Answer the id the " +
                        "teammate's request carried.");
                }

                owner ??= to;
                if (!await DeliverProtocolAsync(owner, protocol, sender, context))
                {
                    return ToolResult.Error(protocol.Approve == true
                        ? TeamProtocolMessages.PlanApprovalFailed(owner)
                        : TeamProtocolMessages.PlanRejectionFailed(owner));
                }

                registry?.Settle(protocol.RequestId!);
                if (context.TeamStore is { } store && context.Team?.TeamName is { } leadTeam)
                    store.SetAwaitingApproval(leadTeam, owner, awaiting: false);
                else if (context.TeamStore is { } ownStore && context.TeamName is { } sessionTeam)
                    ownStore.SetAwaitingApproval(sessionTeam, owner, awaiting: false);
                return ToolResult.Success(protocol.Approve == true
                    ? TeamProtocolMessages.PlanApproved(owner)
                    : TeamProtocolMessages.PlanRejected(owner, protocol.Feedback ?? ""));
            }
        }
    }

    /// <summary>Puts a protocol message in the recipient's inbox, wherever it lives.</summary>
    private static async Task<bool> DeliverProtocolAsync(
        string to, TeamProtocolMessage protocol, string sender, ToolExecutionContext context)
    {
        var json = protocol.ToJson();
        var recipient = TeamNames.Canonical(to);
        if (recipient is TeamNames.Leader or TeamNames.Main)
        {
            if (context.ReportToLeadAsync is not { } reportToLead)
                return false;
            await reportToLead(sender, json, TeamProtocolMessage.TypeName(protocol.Kind));
            return true;
        }

        if (context.Workers is { } workers)
        {
            if (workers.IsRunning(to))
                return workers.Deliver(to, json) is null;
            return workers.Continue(to, json) is null;
        }

        return false;
    }
}
