using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
using Microsoft.AspNetCore.WebUtilities;

namespace Jarvis.Server.Tests;

public sealed partial class AgentTaskMcpTests
{
    [Fact]
    public async Task OAuth_client_discovers_creates_and_reads_tasks_without_forging_device()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new MetadataFixtureTool());
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var names = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToArray();
        foreach (var operation in new[] { "create", "plan", "get", "artifacts", "cancel", "tools" })
            Assert.Contains("agent_task_" + operation, names);
        AssertToolMetadata(listed, "agent_task_artifacts", "Read Task Artifacts", readOnly: true, destructive: false, openWorld: true);
        AssertToolMetadata(listed, "session__open", "Session Open", readOnly: false, destructive: true, openWorld: true);
        AssertToolMetadata(listed, "process__read", "Process Read", readOnly: true, destructive: false, openWorld: true);
        AssertToolMetadata(listed, "workflow__AskUserQuestion", "Workflow Ask User Question", readOnly: true, destructive: false, openWorld: true);
        var tools = await CallAsync(client, "agent_task_tools", new { });
        Assert.Contains("process.start", tools.GetProperty("content")[0].GetProperty("text").GetString());
        var create = await CallAsync(client, "agent_task_create", new { goal = "MCP integration fixture" });
        Assert.False(create.TryGetProperty("isError", out var error) && error.GetBoolean(), create.GetRawText());
        var reply = JsonSerializer.Deserialize<JsonElement>(create.GetProperty("content")[0].GetProperty("text").GetString()!);
        var id = reply.GetProperty("task").GetProperty("taskId").GetString();
        Assert.Equal("NEEDS_PLAN", reply.GetProperty("task").GetProperty("status").GetString());
        var child = await CallAsync(client, "agent_task_create", new { goal = "MCP child fixture", parentTaskId = id, executionMode = "READ_ONLY" });
        Assert.False(child.TryGetProperty("isError", out var childError) && childError.GetBoolean(), child.GetRawText());
        var childReply = JsonSerializer.Deserialize<JsonElement>(child.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(id, childReply.GetProperty("task").GetProperty("parentTaskId").GetString());
        Assert.Equal(id, childReply.GetProperty("task").GetProperty("rootTaskId").GetString());
        Assert.Equal(1, childReply.GetProperty("task").GetProperty("depth").GetInt32());
        var get = await CallAsync(client, "agent_task_get", new { taskId = id });
        Assert.Contains("NEEDS_PLAN", get.GetRawText());
        var planned = await CallAsync(client, "agent_task_plan", new
        {
            taskId = id, goal = "MCP integration fixture",
            steps = new[] { new { id = "echo", toolId = "process.start", stage = "VERIFY", timeoutSeconds = 15,
                arguments = new { command = "echo MCP_FIXTURE_OK", timeoutSeconds = 15 }, expectedText = "MCP_FIXTURE_OK" } }
        });
        Assert.False(planned.TryGetProperty("isError", out var planError) && planError.GetBoolean(), planned.GetRawText());
        Assert.Equal("COMPLETED", (await peer.TerminalAsync(admin, id!)).Status);
        var artifacts = await CallAsync(client, "agent_task_artifacts", new { taskId = id });
        Assert.Contains("MCP_FIXTURE_OK", artifacts.GetRawText());
        await using var otherPeer = await TaskAgentPeer.ConnectAsync(app, admin);
        var otherTask = await otherPeer.CreateAsync(admin, new RemoteTaskPlan { Goal = "Other device task" });
        var otherRead = await CallAsync(client, "agent_task_get", new { taskId = otherTask.TaskId });
        Assert.True(otherRead.GetProperty("isError").GetBoolean());
        var forged = await CallAsync(client, "agent_task_create", new { goal = "forged", deviceId = Guid.NewGuid().ToString() });
        Assert.True(forged.GetProperty("isError").GetBoolean());
        var pending = await CallAsync(client, "agent_task_create", new { goal = "Cancel pending fixture" });
        var pendingReply = JsonSerializer.Deserialize<JsonElement>(pending.GetProperty("content")[0].GetProperty("text").GetString()!);
        var pendingId = pendingReply.GetProperty("task").GetProperty("taskId").GetString();
        var cancelled = await CallAsync(client, "agent_task_cancel", new { taskId = pendingId });
        Assert.Contains("CANCELLED", cancelled.GetRawText());
        foreach (var (name, result) in new (string, JsonElement)[]
        {
            ("agent_task_tools", tools), ("agent_task_create", create), ("agent_task_create", child),
            ("agent_task_get", get), ("agent_task_plan", planned), ("agent_task_artifacts", artifacts),
            ("agent_task_get", otherRead), ("agent_task_create", forged),
            ("agent_task_create", pending), ("agent_task_cancel", cancelled)
        }) AssertOutputMatches(listed, name, result);
        AssertLegacyTaskJson(create, create.GetProperty("structuredContent"));
        AssertLegacyTaskJson(child, child.GetProperty("structuredContent"));
        AssertLegacyTaskJson(otherRead, otherRead.GetProperty("structuredContent"));
    }
    private sealed record SessionHandle(string Value);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, SessionHandle> SessionHandles = new();
    private static async Task<JsonElement> CallAsync(HttpClient client, string name, object arguments)
    {
        var payload = JsonSerializer.SerializeToNode(arguments, WireJson.Options)!.AsObject();
        if (SessionHandles.TryGetValue(client, out var handle) && payload["_jarvis"] is null)
            payload["_jarvis"] = new System.Text.Json.Nodes.JsonObject { ["sessionHandle"] = handle.Value };
        return (await RpcAsync(client, "tools/call", new { name, arguments = payload })).GetProperty("result").Clone();
    }
    private static async Task<JsonElement> RpcAsync(HttpClient client, string method, object parameters)
    {
        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method, @params = parameters });
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
        {
            client.DefaultRequestHeaders.Remove("Mcp-Session-Id");
            client.DefaultRequestHeaders.Add("Mcp-Session-Id", sessionIds.Single());
        }
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        if (text.TrimStart().StartsWith('{')) return JsonSerializer.Deserialize<JsonElement>(text);
        var data = text.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonSerializer.Deserialize<JsonElement>(data[6..]);
    }
    private static void AssertToolMetadata(JsonElement listed, string name, string title,
        bool readOnly, bool destructive, bool openWorld)
    {
        var tool = listed.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == name);
        Assert.Equal(title, tool.GetProperty("title").GetString());
        var annotations = tool.GetProperty("annotations");
        Assert.Equal(title, annotations.GetProperty("title").GetString());
        Assert.Equal(readOnly, annotations.GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(destructive, annotations.GetProperty("destructiveHint").GetBoolean());
        Assert.Equal(openWorld, annotations.GetProperty("openWorldHint").GetBoolean());
    }
    private sealed class MetadataFixtureTool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("fixture.metadata", "workflow__AskUserQuestion", "workflow",
            "Fixture for human-readable MCP metadata.", WireJson.Element(new { type = "object" }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("ok"));
    }
    private static async Task<HttpClient> GrantAsync(ServerFixture app, HttpClient admin, string deviceId)
    {
        const string callback = "https://client.example/callback";
        const string resource = "https://jarvis.test/mcp";
        var client = app.Client();
        var registered = await client.PostAsJsonAsync("/connect/register", new { redirect_uris = new[] { callback }, token_endpoint_auth_method = "none" });
        registered.EnsureSuccessStatusCode();
        var registration = await registered.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = registration.GetProperty("client_id").GetString()!;
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        { ["client_id"] = clientId, ["redirect_uri"] = callback, ["response_type"] = "code", ["scope"] = "mcp:tools",
            ["resource"] = resource, ["state"] = "task-fixture", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256" });
        var html = await admin.GetStringAsync(authorize);
        var action = WebUtility.HtmlDecode(Regex.Match(html, "<form method=\"post\" action=\"([^\"]+)\"").Groups[1].Value);
        var form = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\">")
            .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));
        form["deviceId"] = deviceId; form["decision"] = "allow";
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var consent = await admin.PostAsync(action, new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var code = QueryHelpers.ParseQuery(consent.Headers.Location!.Query)["code"].ToString();
        var exchanged = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["redirect_uri"] = callback,
            ["code"] = code, ["code_verifier"] = verifier, ["resource"] = resource }));
        exchanged.EnsureSuccessStatusCode();
        var token = await exchanged.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.GetProperty("access_token").GetString());
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        await RpcAsync(client, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "task-test", version = "1" } });
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        var opened = (await RpcAsync(client, "tools/call", new { name = "session__open", arguments = new { label = "MCP output fixture" } })).GetProperty("result");
        Assert.False(opened.TryGetProperty("isError", out var openError) && openError.GetBoolean(), opened.GetRawText());
        using var metadata = JsonDocument.Parse(opened.GetProperty("content")[0].GetProperty("text").GetString()!);
        SessionHandles.Add(client, new(metadata.RootElement.GetProperty("sessionHandle").GetString()!));
        await ServerFixture.Csrf(admin);
        return client;
    }
}
