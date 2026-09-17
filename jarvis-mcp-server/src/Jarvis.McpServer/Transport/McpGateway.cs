using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.McpServer.Application;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Security;
using Jarvis.Protocol;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;

namespace Jarvis.McpServer.Transport;

/// <summary>Dynamic installed-schema adapter with OAuth-bound optional application sessions, never caller-selected routing.</summary>
public sealed class McpGateway(AppDbContext db, IAgentRouter router, IAuditWriter audit, IHttpContextAccessor http,
    AgentTaskService tasks, McpSessionContext sessions)
{
    private HttpContext Http => http.HttpContext ?? throw new InvalidOperationException("MCP HTTP request required.");

    public async Task<ListToolsResult> ListAsync(CancellationToken ct)
    {
        var (user, device) = await CurrentAccess.RequireMcpAsync(Http, db, ct);
        var capabilities = await CapabilitiesAsync(user.Id, device.Id, ct);
        var scoped = capabilities.ContainsKey("session.open");
        var entries = await db.Tools.AsNoTracking().Where(t => t.Enabled).OrderBy(t => t.Name).ToListAsync(ct);
        var tools = entries.Where(t => capabilities.ContainsKey(t.AgentToolId) && !RemoteTaskRules.IsReservedName(t.Name) &&
                !AgentSessionRules.IsPublicTool(t.Name) && !AgentSessionRules.IsTool(t.AgentToolId))
            .Select(t => PublicTool(capabilities[t.AgentToolId], t.Name, t.Description, scoped)).ToList();
        // Like task lifecycle methods, these are installed host bookkeeping tools. They never execute
        // arbitrary catalog content, and the agent retains its normal exact-ID approval checks.
        if (scoped)
            tools.AddRange(capabilities.Values.Where(t => AgentSessionRules.IsTool(t.Id) && t.Id != "session.close")
                .Select(t => PublicTool(t, t.Id.Replace(".", "__", StringComparison.Ordinal), t.Description, true)));
        if (tasks.SupportsTasks(user.Id, device.Id))
            foreach (var tool in AgentTaskMcpTools.List())
            {
                if (scoped) tool.InputSchema = sessions.AugmentSchema(tool.InputSchema, required: false);
                tools.Add(tool);
            }
        return new ListToolsResult { Tools = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList() };
    }

    private Tool PublicTool(ToolDescriptor descriptor, string name, string description, bool scoped)
    {
        var requiresSession = scoped && descriptor.Id != "session.open" && AgentSessionRules.IsTool(descriptor.Id);
        var contextHint = !scoped || descriptor.Id == "session.open" ? "" : requiresSession
            ? " Requires this chat's _jarvis.sessionHandle returned by session__open."
            : " Optionally include this chat's _jarvis.sessionHandle for explicit session workspace and ownership.";
        var title = McpToolMetadata.TitleFor(name);
        return new()
        {
            Name = name,
            Title = title,
            Description = description + contextHint,
            InputSchema = scoped ? sessions.AugmentSchema(descriptor.InputSchema, requiresSession) : descriptor.InputSchema,
            OutputSchema = McpOutputSchemas.ToolReply,
            Annotations = new ToolAnnotations { Title = title, ReadOnlyHint = descriptor.ReadOnly,
                DestructiveHint = !descriptor.ReadOnly, OpenWorldHint = true }
        };
    }

    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken ct)
    {
        var (user, device) = await CurrentAccess.RequireMcpAsync(Http, db, ct);
        var capabilities = await CapabilitiesAsync(user.Id, device.Id, ct);
        var scoped = capabilities.ContainsKey("session.open");
        var arguments = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>(), WireJson.Options);
        if (arguments.GetRawText().Length > 262144) return Error("Tool arguments exceed 256 KiB.", request.Name);
        IssuedMcpSession? issued = null;
        string? handle = null;
        var forwarded = false;
        var sessionId = scoped ? AgentSessionRules.NewEphemeralExecutionId() : "oauth:" + user.Id + ":" + device.Id;
        try
        {
            if (scoped)
            {
                var context = sessions.Extract(arguments);
                arguments = context.Arguments;
                handle = context.SessionHandle;
                if (request.Name == "session__open" && handle is null)
                {
                    issued = sessions.Issue(user.Id, device.Id);
                    sessionId = issued.SessionId; handle = issued.SessionHandle;
                }
                else if (handle is not null)
                {
                    sessionId = sessions.Resolve(user.Id, device.Id, handle);
                }
                else if (RequiresExplicitSession(request.Name))
                {
                    sessionId = sessions.Resolve(user.Id, device.Id, null);
                }
            }
            var explicitSession = AgentSessionRules.IsSessionId(sessionId);
            if (RemoteTaskRules.IsReservedName(request.Name))
            {
                forwarded = true;
                if (explicitSession && request.Name == "agent_task_tools")
                {
                    var check = await router.CallAsync(user.Id, device.Id, "session.get", WireJson.Element(new { }), sessionId, ct);
                    if (check.IsError) return Error(check.Text, request.Name);
                }
                var clean = new CallToolRequestParams { Name = request.Name,
                    Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>(WireJson.Options) };
                return await AgentTaskMcpTools.CallAsync(tasks, user.Id, device.Id, clean, ct, explicitSession ? sessionId : null);
            }
            ToolDescriptor? tool;
            if (AgentSessionRules.IsPublicTool(request.Name))
            {
                if (!scoped || !capabilities.TryGetValue(request.Name.Replace("__", ".", StringComparison.Ordinal), out tool))
                    return Error("Session tools are not installed on this agent. Upgrade the agent and refresh tools.");
            }
            else
            {
                var entry = await db.Tools.AsNoTracking().SingleOrDefaultAsync(t => t.Name == request.Name && t.Enabled, ct);
                if (entry is null || AgentSessionRules.IsTool(entry.AgentToolId)) return Error("Tool is disabled or unknown. Refresh the tool list.");
                if (!capabilities.TryGetValue(entry.AgentToolId, out tool)) return Error("Tool is not installed on your selected device.");
            }
            if (!SchemaGuard.Matches(SchemaGuard.Compile(tool.InputSchema), arguments))
                return Error("Arguments do not match the installed tool's JSON schema.");
            var toolId = tool.Id;
            if (toolId == "session.open" && issued is null)
            {
                // Resuming must not recreate a missing/closed session from an old handle.
                toolId = "session.get";
                arguments = WireJson.Element(new { });
            }
            forwarded = true;
            return await DispatchAsync(user.Id, device.Id, request.Name, toolId, arguments, sessionId,
                request.Name == "session__open" ? handle : null, issued?.ExpiresAt, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or
            System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            var code = (ex as AgentRequestException)?.Code ?? (ex is OperationCanceledException ? "CANCELLED_OR_TIMEOUT" : "GATEWAY_REJECTED");
            if (!forwarded)
            {
                using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await audit.WriteAsync(new()
                    {
                        UserId = user.Id, DeviceId = device.Id, Action = "tool." + request.Name,
                        Outcome = "rejected:" + code, CorrelationId = Guid.NewGuid().ToString("N")
                    }, auditTimeout.Token);
                }
                catch (Exception auditError) when (auditError is OperationCanceledException or InvalidOperationException or DbUpdateException)
                {
                    _ = auditError;
                }
            }
            return Error(ex is OperationCanceledException
                ? "Call cancelled or timed out. Completion may be unknown; verify state before repeating mutating actions." : ex.Message, request.Name, (ex as AgentRequestException)?.Code);
        }
    }

    private static bool RequiresExplicitSession(string name) =>
        AgentSessionRules.IsPublicTool(name) && name != "session__open";

    private async Task<CallToolResult> DispatchAsync(string owner, string device, string name, string toolId,
        JsonElement arguments, string sessionId, string? handle, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        await audit.WriteAsync(new() { UserId = owner, DeviceId = device, Action = "tool." + name,
            Outcome = "started", CorrelationId = correlation }, ct);
        var outcome = "error";
        try
        {
            var reply = await router.CallAsync(owner, device, toolId, arguments, sessionId, ct);
            if (handle is not null && !reply.IsError)
            {
                var body = JsonNode.Parse(reply.Text)?.AsObject() ?? throw new InvalidDataException("Invalid session metadata reply.");
                if (body["sessionId"]?.GetValue<string>() != sessionId)
                    throw new InvalidDataException("Agent returned a mismatched session identity.");
                body["sessionHandle"] = handle;
                if (expiresAt is { } expiry) body["handleExpiresAt"] = JsonValue.Create(expiry);
                reply = reply with { Text = body.ToJsonString(WireJson.Options) };
            }
            var content = new List<ContentBlock> { new TextContentBlock { Text = reply.Text } };
            if (reply.Images is not null)
                content.AddRange(reply.Images.Select(i => ImageContentBlock.FromBytes(Convert.FromBase64String(i.Base64), i.MimeType)));
            outcome = reply.IsError ? "error" : "completed";
            return new CallToolResult { Content = content, IsError = reply.IsError,
                StructuredContent = WireJson.Element(new { text = reply.Text, isError = reply.IsError }) };
        }
        catch (OperationCanceledException) { outcome = "cancelled-or-timeout"; throw; }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await audit.WriteAsync(new() { UserId = owner, DeviceId = device, Action = "tool." + name,
                Outcome = outcome, CorrelationId = correlation, DurationMs = timer.ElapsedMilliseconds }, timeout.Token);
        }
    }

    private async Task<Dictionary<string, ToolDescriptor>> CapabilitiesAsync(string owner, string id, CancellationToken ct)
    {
        var json = await db.Devices.Where(d => d.OwnerId == owner && d.Id == id).Select(d => d.CapabilitiesJson).SingleAsync(ct);
        return (JsonSerializer.Deserialize<ToolDescriptor[]>(json, WireJson.Options) ?? []).ToDictionary(t => t.Id);
    }
    private static CallToolResult Error(string message, string? toolName = null, string? errorCode = null)
    {
        if (toolName is not null && RemoteTaskRules.IsReservedName(toolName))
        {
            var body = toolName == "agent_task_tools"
                ? WireJson.Element(new { error = message })
                : WireJson.Element(new { error = message, errorCode });
            return new() { IsError = true, Content = [new TextContentBlock { Text = body.GetRawText() }], StructuredContent = body };
        }
        return new()
        {
            IsError = true, Content = [new TextContentBlock { Text = message }],
            StructuredContent = WireJson.Element(new { text = message, isError = true })
        };
    }
}