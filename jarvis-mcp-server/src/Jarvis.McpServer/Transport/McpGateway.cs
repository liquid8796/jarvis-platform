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

/// <summary>
/// Selected-device live-schema adapter. Direct tools are dynamic; permanent search/call gateways keep
/// capabilities usable even when a client has not refreshed a changed tools/list yet.
/// </summary>
public sealed class McpGateway(
    AppDbContext db,
    IAgentRouter router,
    IAuditWriter audit,
    IHttpContextAccessor http,
    AgentTaskService tasks,
    McpSessionContext sessions,
    DeviceToolCatalog deviceTools)
{
    private HttpContext Http => http.HttpContext ?? throw new InvalidOperationException("MCP HTTP request required.");

    public async Task<ListToolsResult> ListAsync(CancellationToken ct)
    {
        var (user, device) = await CurrentAccess.RequireMcpAsync(Http, db, ct);
        var resolved = await deviceTools.ResolveAsync(user.Id, device.Id, ct);
        var capabilities = resolved.ToDictionary(t => t.Descriptor.Id, t => t.Descriptor, StringComparer.Ordinal);
        var scoped = capabilities.ContainsKey("session.open");

        var directCandidates = resolved.Where(IsDirectVisible).ToArray();
        var directNames = directCandidates.GroupBy(t => t.PublicName, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();

        var tools = directNames
            .Select(t => PublicTool(t.PublicDescriptor, t.PublicName, t.Description, scoped))
            .ToList();
        tools.AddRange(DynamicAgentToolGateway.List(scoped, sessions));

        // Session/workspace lifecycle tools are host bookkeeping and bypass administrator publication aliases.
        if (scoped)
            tools.AddRange(capabilities.Values
                .Where(t => AgentSessionRules.IsTool(t.Id) && t.Id != "session.close")
                .Select(t => PublicTool(t, t.Id.Replace(".", "__", StringComparison.Ordinal), t.Description, true)));

        if (tasks.SupportsTasks(user.Id, device.Id))
        {
            foreach (var tool in AgentTaskMcpTools.List())
            {
                if (scoped) tool.InputSchema = sessions.AugmentSchema(tool.InputSchema, required: false);
                tools.Add(tool);
            }
        }

        return new ListToolsResult
        {
            Tools = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList()
        };
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
            Annotations = new ToolAnnotations
            {
                Title = title,
                ReadOnlyHint = descriptor.ReadOnly,
                DestructiveHint = !descriptor.ReadOnly,
                OpenWorldHint = true
            }
        };
    }

    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken ct)
    {
        var (user, device) = await CurrentAccess.RequireMcpAsync(Http, db, ct);
        var resolved = await deviceTools.ResolveAsync(user.Id, device.Id, ct);
        var capabilities = resolved.ToDictionary(t => t.Descriptor.Id, t => t.Descriptor, StringComparer.Ordinal);
        var scoped = capabilities.ContainsKey("session.open");
        var arguments = JsonSerializer.SerializeToElement(
            request.Arguments ?? new Dictionary<string, JsonElement>(), WireJson.Options);
        if (arguments.GetRawText().Length > 262144)
            return Error("Tool arguments exceed 256 KiB.", request.Name);

        IssuedMcpSession? issued = null;
        string? handle = null;
        var forwarded = false;
        var sessionId = scoped
            ? AgentSessionRules.NewEphemeralExecutionId()
            : "oauth:" + user.Id + ":" + device.Id;

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
                    sessionId = issued.SessionId;
                    handle = issued.SessionHandle;
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

            if (DynamicAgentToolGateway.IsSearch(request.Name))
            {
                if (!SchemaGuard.Matches(SchemaGuard.Compile(DynamicAgentToolGateway.SearchInputSchema), arguments))
                    return Error("Arguments do not match jarvis__tool_search schema.", request.Name);
                return SearchTools(resolved, arguments);
            }

            if (DynamicAgentToolGateway.IsCall(request.Name))
            {
                if (!SchemaGuard.Matches(SchemaGuard.Compile(DynamicAgentToolGateway.CallInputSchema), arguments))
                    return Error("Arguments do not match jarvis__tool_call schema.", request.Name);
                var targetToolId = arguments.GetProperty("toolId").GetString()!;
                var target = resolved.SingleOrDefault(t => StringComparer.Ordinal.Equals(t.Descriptor.Id, targetToolId));
                if (target is null)
                    return Error("Tool is not installed on your selected device. Search the live catalog again.", request.Name);
                if (!target.Visible)
                    return Error("Tool is hidden by administrator policy.", request.Name);
                if (AgentSessionRules.IsTool(target.Descriptor.Id))
                    return Error("Session/workspace control tools must use their dedicated MCP names.", request.Name);

                var nestedArguments = arguments.GetProperty("arguments");
                if (!SchemaGuard.Matches(SchemaGuard.Compile(target.Descriptor.InputSchema), nestedArguments))
                    return Error("Arguments do not match the live installed tool schema.", request.Name);

                forwarded = true;
                return await DispatchAsync(
                    user.Id, device.Id, request.Name, target.Descriptor.Id, nestedArguments, sessionId,
                    null, null, ct);
            }

            if (RemoteTaskRules.IsReservedName(request.Name))
            {
                forwarded = true;
                if (explicitSession && request.Name == "agent_task_tools")
                {
                    var check = await router.CallAsync(
                        user.Id, device.Id, "session.get", WireJson.Element(new { }), sessionId, ct);
                    if (check.IsError) return Error(check.Text, request.Name);
                }

                var clean = new CallToolRequestParams
                {
                    Name = request.Name,
                    Arguments = arguments.Deserialize<Dictionary<string, JsonElement>>(WireJson.Options)
                };
                return await AgentTaskMcpTools.CallAsync(
                    tasks, user.Id, device.Id, clean, ct, explicitSession ? sessionId : null);
            }

            ToolDescriptor? tool;
            if (AgentSessionRules.IsPublicTool(request.Name))
            {
                if (!scoped ||
                    !capabilities.TryGetValue(request.Name.Replace("__", ".", StringComparison.Ordinal), out tool))
                    return Error("Session tools are not installed on this agent. Upgrade the agent and refresh tools.");
            }
            else
            {
                var matches = resolved
                    .Where(IsDirectVisible)
                    .Where(t => StringComparer.Ordinal.Equals(t.PublicName, request.Name))
                    .ToArray();
                if (matches.Length == 0)
                    return Error("Tool is hidden, unavailable or unknown. Use jarvis__tool_search to inspect the live selected-device catalog.");
                if (matches.Length > 1)
                    return Error("Tool name is ambiguous. Use jarvis__tool_search and jarvis__tool_call with a canonical tool ID.");
                tool = matches[0].Descriptor;
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
            return await DispatchAsync(
                user.Id, device.Id, request.Name, toolId, arguments, sessionId,
                request.Name == "session__open" ? handle : null, issued?.ExpiresAt, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or
            System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            var code = (ex as AgentRequestException)?.Code
                ?? (ex is OperationCanceledException ? "CANCELLED_OR_TIMEOUT" : "GATEWAY_REJECTED");
            if (!forwarded)
            {
                using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await audit.WriteAsync(new()
                    {
                        UserId = user.Id,
                        DeviceId = device.Id,
                        Action = "tool." + request.Name,
                        Outcome = "rejected:" + code,
                        CorrelationId = Guid.NewGuid().ToString("N")
                    }, auditTimeout.Token);
                }
                catch (Exception auditError) when (auditError is OperationCanceledException or InvalidOperationException or DbUpdateException)
                {
                    _ = auditError;
                }
            }

            return Error(ex is OperationCanceledException
                ? "Call cancelled or timed out. Completion may be unknown; verify state before repeating mutating actions."
                : ex.Message, request.Name, (ex as AgentRequestException)?.Code);
        }
    }

    private static CallToolResult SearchTools(
        IReadOnlyList<ResolvedDeviceTool> resolved,
        JsonElement arguments)
    {
        var query = arguments.TryGetProperty("query", out var queryNode)
            ? queryNode.GetString()?.Trim()
            : null;
        var category = arguments.TryGetProperty("category", out var categoryNode)
            ? categoryNode.GetString()?.Trim()
            : null;
        var limit = arguments.TryGetProperty("limit", out var limitNode)
            ? limitNode.GetInt32()
            : 20;

        var visible = resolved.Where(IsGatewayVisible).ToArray();
        var directCounts = visible.Where(IsDirectVisible).GroupBy(t => t.PublicName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var matches = visible
            .Where(t => string.IsNullOrEmpty(category) ||
                StringComparer.OrdinalIgnoreCase.Equals(t.Descriptor.Category, category))
            .Where(t => string.IsNullOrEmpty(query) ||
                Contains(t.Descriptor.Id, query) ||
                Contains(t.PublicName, query) ||
                Contains(t.Description, query) ||
                Contains(t.Descriptor.Category, query))
            .OrderBy(t => t.Descriptor.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(t => new
            {
                id = t.Descriptor.Id,
                name = t.PublicName,
                category = t.Descriptor.Category,
                description = t.Description,
                inputSchema = t.Descriptor.InputSchema,
                readOnly = t.Descriptor.ReadOnly,
                sensitive = t.Descriptor.Sensitive,
                publicationMode = t.PublicationMode.ToString(),
                direct = IsDirectVisible(t) && directCounts.TryGetValue(t.PublicName, out var count) && count == 1
            })
            .ToArray();

        var body = WireJson.Element(new { tools = matches });
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = body.GetRawText() }],
            IsError = false,
            StructuredContent = body
        };
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool IsGatewayVisible(ResolvedDeviceTool tool) =>
        tool.Visible && !AgentSessionRules.IsTool(tool.Descriptor.Id);

    private static bool IsDirectVisible(ResolvedDeviceTool tool) =>
        IsGatewayVisible(tool) &&
        !RemoteTaskRules.IsReservedName(tool.PublicName) &&
        !AgentSessionRules.IsPublicTool(tool.PublicName) &&
        !DynamicAgentToolNames.IsReserved(tool.PublicName);

    private static bool RequiresExplicitSession(string name) =>
        AgentSessionRules.IsPublicTool(name) && name != "session__open";

    private async Task<CallToolResult> DispatchAsync(
        string owner,
        string device,
        string name,
        string toolId,
        JsonElement arguments,
        string sessionId,
        string? handle,
        DateTimeOffset? expiresAt,
        CancellationToken ct)
    {
        var correlation = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        await audit.WriteAsync(new()
        {
            UserId = owner,
            DeviceId = device,
            Action = "tool." + name,
            Outcome = "started",
            CorrelationId = correlation
        }, ct);
        var outcome = "error";
        try
        {
            var reply = await router.CallAsync(owner, device, toolId, arguments, sessionId, ct);
            if (handle is not null && !reply.IsError)
            {
                var body = JsonNode.Parse(reply.Text)?.AsObject()
                    ?? throw new InvalidDataException("Invalid session metadata reply.");
                if (body["sessionId"]?.GetValue<string>() != sessionId)
                    throw new InvalidDataException("Agent returned a mismatched session identity.");
                body["sessionHandle"] = handle;
                if (expiresAt is { } expiry) body["handleExpiresAt"] = JsonValue.Create(expiry);
                reply = reply with { Text = body.ToJsonString(WireJson.Options) };
            }

            var content = new List<ContentBlock> { new TextContentBlock { Text = reply.Text } };
            if (reply.Images is not null)
                content.AddRange(reply.Images.Select(i =>
                    ImageContentBlock.FromBytes(Convert.FromBase64String(i.Base64), i.MimeType)));
            McpPromptContext.AppendTo(content, reply.UserPromptContext, reply.IsError);
            outcome = reply.IsError ? "error" : "completed";
            return new CallToolResult
            {
                Content = content,
                IsError = reply.IsError,
                StructuredContent = WireJson.Element(new { text = reply.Text, isError = reply.IsError })
            };
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled-or-timeout";
            throw;
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await audit.WriteAsync(new()
            {
                UserId = owner,
                DeviceId = device,
                Action = "tool." + name,
                Outcome = outcome,
                CorrelationId = correlation,
                DurationMs = timer.ElapsedMilliseconds
            }, timeout.Token);
        }
    }

    private static CallToolResult Error(string message, string? toolName = null, string? errorCode = null)
    {
        if (toolName is not null && RemoteTaskRules.IsReservedName(toolName))
        {
            var body = toolName == "agent_task_tools"
                ? WireJson.Element(new { error = message })
                : WireJson.Element(new { error = message, errorCode });
            return new()
            {
                IsError = true,
                Content = [new TextContentBlock { Text = body.GetRawText() }],
                StructuredContent = body
            };
        }

        return new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = WireJson.Element(new { text = message, isError = true })
        };
    }
}
