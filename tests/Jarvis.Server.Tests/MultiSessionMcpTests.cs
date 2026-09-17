using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Server.Tests;

public sealed partial class AgentTaskMcpTests
{
    [Fact]
    public async Task Two_oauth_clients_share_an_agent_without_sharing_workspace_tasks_or_mailbox()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new SessionContextProbe());
        using var first = await GrantAsync(app, admin, peer.DeviceId);
        using var second = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(first, "tools/list", new { });
        var a = ParseText(await RawSessionCall(first, "session__open", new { label = "Laptop chat" }));
        var b = ParseText(await RawSessionCall(second, "session__open", new { label = "Phone chat" }));
        var ha = a.GetProperty("sessionHandle").GetString()!;
        var hb = b.GetProperty("sessionHandle").GetString()!;
        var sa = a.GetProperty("sessionId").GetString()!;
        var sb = b.GetProperty("sessionId").GetString()!;
        Assert.NotEqual(sa, sb);
        Assert.NotEqual(ha, hb);
        var missing = await RawSessionCall(first, "test__session_context", new { });
        Assert.True(missing.GetProperty("isError").GetBoolean());
        Assert.Contains("SESSION_REQUIRED", missing.GetRawText());
        var clear = await RawSessionCall(first, "workspace__set", new { _jarvis = new { sessionHandle = ha }, path = (string?)null, expectedRevision = 0 });
        Assert.False(clear.GetProperty("isError").GetBoolean(), clear.GetRawText());
        var ca = ParseText(await RawSessionCall(first, "test__session_context", new { _jarvis = new { sessionHandle = ha } }));
        var cb = ParseText(await RawSessionCall(second, "test__session_context", new { _jarvis = new { sessionHandle = hb } }));
        Assert.Empty(ca.GetProperty("workspace").GetString()!);
        Assert.Equal(peer.Workspace, cb.GetProperty("workspace").GetString());
        Assert.Equal(sa, ca.GetProperty("sessionId").GetString());
        Assert.Equal(sb, cb.GetProperty("sessionId").GetString());
        var selected = Path.Combine(app.Root, "prompt-selected-workspace"); Directory.CreateDirectory(selected);
        var selection = await RawSessionCall(first, "workspace__set", new { _jarvis = new { sessionHandle = ha }, path = selected, expectedRevision = 1 });
        Assert.False(selection.GetProperty("isError").GetBoolean(), selection.GetRawText());
        var conflict = await RawSessionCall(first, "workspace__set", new { _jarvis = new { sessionHandle = ha }, path = peer.Workspace, expectedRevision = 1 });
        Assert.Contains("WORKSPACE_REVISION_CONFLICT", conflict.GetRawText());
        var sent = await RawSessionCall(first, "session__send_message", new { _jarvis = new { sessionHandle = ha }, targetSessionId = sb, text = "Build ready; coordination data only." });
        Assert.False(sent.GetProperty("isError").GetBoolean(), sent.GetRawText());
        var events = await RawSessionCall(second, "session__read_events", new { _jarvis = new { sessionHandle = hb } });
        Assert.Contains("Build ready", ParseText(events).GetRawText());
        var metadata = await RawSessionCall(second, "session__list", new { _jarvis = new { sessionHandle = hb } });
        Assert.DoesNotContain("sessionHandle", ParseText(metadata).GetRawText(), StringComparison.Ordinal);
        Assert.Contains(sa, ParseText(metadata).GetRawText());
        var created = await RawSessionCall(first, "agent_task_create", new { _jarvis = new { sessionHandle = ha }, goal = "Owned session task" });
        Assert.False(created.GetProperty("isError").GetBoolean(), created.GetRawText());
        var taskId = ParseText(created).GetProperty("task").GetProperty("taskId").GetString()!;
        var foreign = await RawSessionCall(second, "agent_task_get", new { _jarvis = new { sessionHandle = hb }, taskId });
        Assert.True(foreign.GetProperty("isError").GetBoolean());
        Assert.Contains("not_found", foreign.GetRawText());
        var closed = await RawSessionCall(first, "session__close", new { _jarvis = new { sessionHandle = ha } });
        Assert.False(closed.GetProperty("isError").GetBoolean(), closed.GetRawText());
        var resumedClosed = await RawSessionCall(first, "session__open", new { _jarvis = new { sessionHandle = ha } });
        Assert.Contains("SESSION_CLOSED", resumedClosed.GetRawText());
        Assert.False((await RawSessionCall(second, "session__get", new { _jarvis = new { sessionHandle = hb } })).GetProperty("isError").GetBoolean());
        Assert.Equal("CANCELLED", (await peer.TerminalAsync(admin, taskId)).Status);
        var missingTaskSession = await RawSessionCall(first, "agent_task_get", new { taskId });
        AssertOutputMatches(listed, "agent_task_get", missingTaskSession);
        AssertOutputMatches(listed, "agent_task_create", created);
        AssertOutputMatches(listed, "agent_task_get", foreign);
        AssertOutputMatches(listed, "session__open", await RawSessionCall(second, "session__open", new { _jarvis = new { sessionHandle = hb } }));
    }

    private static async Task<JsonElement> RawSessionCall(HttpClient client, string name, object arguments) =>
        (await RpcAsync(client, "tools/call", new { name, arguments })).GetProperty("result").Clone();
    private static JsonElement ParseText(JsonElement result) => JsonSerializer.Deserialize<JsonElement>(result.GetProperty("content")[0].GetProperty("text").GetString()!);
    private sealed class SessionContextProbe : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.session_context", "test__session_context", "test", "Return synthetic session context", WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply(JsonSerializer.Serialize(new { context.Workspace, context.SessionId, context.OwnerId, context.AgentDeviceId }, WireJson.Options)));
    }
}
