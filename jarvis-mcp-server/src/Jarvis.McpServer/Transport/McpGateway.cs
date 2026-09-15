using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Jarvis.McpServer.Security;
using Jarvis.Protocol;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
namespace Jarvis.McpServer.Transport;

/// <summary>Dynamic registry adapter. Permissions and schema come from the bound device, never from caller-supplied routing arguments.</summary>
public sealed class McpGateway(AppDbContext db, IAgentRouter router, IAuditWriter audit, IHttpContextAccessor http)
{
    private HttpContext Http => http.HttpContext ?? throw new InvalidOperationException("MCP HTTP request required.");
    public async Task<ListToolsResult> ListAsync(CancellationToken ct)
    {
        var (user, device) = await CurrentAccess.RequireMcpAsync(Http, db, ct);
        var capabilities = await CapabilitiesAsync(user.Id, device.Id, ct);
        var entries = await db.Tools.AsNoTracking().Where(t => t.Enabled).OrderBy(t => t.Name).ToListAsync(ct);
        return new ListToolsResult
        {
            Tools = entries.Where(t => capabilities.ContainsKey(t.AgentToolId)).Select(t => new Tool
            {
                Name = t.Name, Description = t.Description,
                InputSchema = capabilities[t.AgentToolId].InputSchema,
                Annotations = new ToolAnnotations { ReadOnlyHint = capabilities[t.AgentToolId].ReadOnly,
                    DestructiveHint = !capabilities[t.AgentToolId].ReadOnly, OpenWorldHint = true }
            }).ToList()
        };
    }
    public async Task<CallToolResult> CallAsync(CallToolRequestParams request, CancellationToken ct)
    {
        var (user, device) = await CurrentAccess.RequireMcpAsync(Http, db, ct);
        var entry = await db.Tools.AsNoTracking().SingleOrDefaultAsync(t => t.Name == request.Name && t.Enabled, ct);
        if (entry is null) return Error("Tool is disabled or unknown. Refresh the tool list.");
        var capabilities = await CapabilitiesAsync(user.Id, device.Id, ct);
        if (!capabilities.TryGetValue(entry.AgentToolId, out var tool)) return Error("Tool is not installed on your selected device.");
        var arguments = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>(), WireJson.Options);
        if (arguments.GetRawText().Length > 262144) return Error("Tool arguments exceed 256 KiB.");
        var schema = SchemaGuard.Compile(tool.InputSchema);
        if (!SchemaGuard.Matches(schema, arguments)) return Error("Arguments do not match the installed tool's JSON schema.");
        var correlation = Guid.NewGuid().ToString("N"); var timer = Stopwatch.StartNew();
        await audit.WriteAsync(new() { UserId = user.Id, DeviceId = device.Id, Action = "tool." + entry.Name,
            Outcome = "started", CorrelationId = correlation }, ct);
        try
        {
            // A grant is device-bound. A forged deviceId in arguments cannot choose a different computer.
            var reply = await router.CallAsync(user.Id, device.Id, entry.AgentToolId, arguments,
                "oauth:" + user.Id + ":" + device.Id, ct);
            var content = new List<ContentBlock> { new TextContentBlock { Text = reply.Text } };
            if (reply.Images is not null) content.AddRange(reply.Images.Select(i => new ImageContentBlock { Data = Convert.FromBase64String(i.Base64), MimeType = i.MimeType }));
            await FinishAsync(reply.IsError ? "error" : "completed");
            // Widgets are rendered locally in the agent; returning an artifact does not claim ChatGPT embedded rendering support.
            return new CallToolResult { Content = content, IsError = reply.IsError };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
            await FinishAsync(ex is OperationCanceledException ? "cancelled-or-timeout" : "error");
            return Error(ex is OperationCanceledException ? "Call cancelled or timed out. Verify state before repeating mutating actions." : ex.Message);
        }
        async Task FinishAsync(string outcome)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await audit.WriteAsync(new() { UserId = user.Id, DeviceId = device.Id, Action = "tool." + entry.Name,
                Outcome = outcome, CorrelationId = correlation, DurationMs = timer.ElapsedMilliseconds }, timeout.Token);
        }
    }
    private async Task<Dictionary<string, ToolDescriptor>> CapabilitiesAsync(string owner, string id, CancellationToken ct)
    {
        var json = await db.Devices.Where(d => d.OwnerId == owner && d.Id == id).Select(d => d.CapabilitiesJson).SingleAsync(ct);
        return (JsonSerializer.Deserialize<ToolDescriptor[]>(json, WireJson.Options) ?? []).ToDictionary(t => t.Id);
    }
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}


