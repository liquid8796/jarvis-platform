using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Server.Tests;

public sealed partial class AgentTaskMcpTests
{
    [Fact]
    public async Task Every_published_tool_advertises_a_meaningful_object_output_schema()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new OutputFixtureTool(new("ok")));
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var tools = listed.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, t => t.GetProperty("name").GetString() == "schema_fixture");
        Assert.Equal(6, tools.Count(t => t.GetProperty("name").GetString()!.StartsWith("agent_task_", StringComparison.Ordinal)));
        foreach (var tool in tools)
        {
            Assert.True(tool.TryGetProperty("outputSchema", out var schema), tool.GetProperty("name").GetString());
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.NotEmpty(schema.GetProperty("properties").EnumerateObject());
            var compiled = SchemaGuard.Compile(schema);
            Assert.False(SchemaGuard.Matches(compiled, WireJson.Element(new { })), "Empty output must not satisfy " + tool.GetProperty("name"));
            Assert.False(SchemaGuard.Matches(compiled, WireJson.Element(Array.Empty<string>())));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_results_preserve_legacy_text_and_expose_the_same_error_flag(bool isError)
    {
        const string text = "[\"opaque legacy JSON text\",\"tiếng Việt\"]";
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new OutputFixtureTool(new(text, isError)));
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var result = await CallAsync(client, "schema_fixture", new { });
        var structured = AssertOutputMatches(listed, "schema_fixture", result);
        Assert.Equal(text, result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(text, structured.GetProperty("text").GetString());
        Assert.Equal(isError, structured.GetProperty("isError").GetBoolean());
        Assert.Equal(isError, result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal(2, structured.EnumerateObject().Count());
    }

    [Fact]
    public async Task Image_results_keep_image_blocks_without_base64_in_structured_content()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aR1sAAAAASUVORK5CYII=";
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var reply = new ToolReply("Screenshot fixture", Images: [new("image/png", png)], Widget: new("Local widget", "<b>local only</b>"));
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new OutputFixtureTool(reply));
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var result = await CallAsync(client, "schema_fixture", new { });
        var structured = AssertOutputMatches(listed, "schema_fixture", result);
        var content = result.GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("Screenshot fixture", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal("image/png", content[1].GetProperty("mimeType").GetString());
        Assert.Equal(png, content[1].GetProperty("data").GetString());
        Assert.DoesNotContain(png, structured.GetRawText());
        Assert.DoesNotContain("local only", result.GetRawText());
        Assert.Equal(2, structured.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("invalid-input")]
    [InlineData("denied")]
    [InlineData("paused")]
    public async Task Ordinary_validation_and_local_permission_errors_match_the_advertised_schema(string scenario)
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new OutputFixtureTool(new("must not run"), sensitive: scenario == "denied"),
            arm: true, approve: true);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        if (scenario == "paused") peer.Connection.Pause();
        if (scenario == "denied") peer.Approval.Answer = false;
        object args = scenario == "invalid-input" ? new { unexpected = true } : new { };
        var result = await CallAsync(client, "schema_fixture", args);
        var structured = AssertOutputMatches(listed, "schema_fixture", result);
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.True(structured.GetProperty("isError").GetBoolean());
        Assert.NotEqual("must not run", structured.GetProperty("text").GetString());
        Assert.Equal(result.GetProperty("content")[0].GetProperty("text").GetString(), structured.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("plan")]
    [InlineData("get")]
    [InlineData("artifacts")]
    [InlineData("cancel")]
    [InlineData("tools")]
    public async Task Every_task_operation_returns_schema_compatible_validation_errors(string operation)
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var name = "agent_task_" + operation;
        var result = await CallAsync(client, name, new { unexpected = true });
        var structured = AssertOutputMatches(listed, name, result);
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(result.GetProperty("content")[0].GetProperty("text").GetString(), structured.GetProperty("error").GetString());
        Assert.False(structured.TryGetProperty("task", out _));
        Assert.False(structured.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Task_tool_descriptors_are_wrapped_only_in_structured_content()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var result = await CallAsync(client, "agent_task_tools", new { });
        var structured = AssertOutputMatches(listed, "agent_task_tools", result);
        var legacy = JsonSerializer.Deserialize<JsonElement>(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(JsonValueKind.Array, legacy.ValueKind);
        Assert.True(JsonElement.DeepEquals(legacy, structured.GetProperty("tools")));
        Assert.Contains(legacy.EnumerateArray(), t => t.GetProperty("id").GetString() == "process.start");
        var bad = JsonNode.Parse(structured.GetRawText())!;
        bad["tools"]![0]!["readOnly"] = "false";
        Assert.False(SchemaGuard.Matches(SchemaGuard.Compile(OutputSchema(listed, "agent_task_tools")), WireJson.Element(bad)));
    }

    [Fact]
    public async Task Task_artifact_pages_match_the_schema_and_omit_nullable_fields()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, new OutputFixtureTool(new("artifact output")));
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var created = await CallAsync(client, "agent_task_create", new
        {
            goal = "Two schema fixture artifacts", steps = new[] {
                new { id = "first", toolId = "test.output" },
                new { id = "second", toolId = "test.output" } }
        });
        Assert.False(created.TryGetProperty("isError", out var createError) && createError.GetBoolean(), created.GetRawText());
        var task = created.GetProperty("structuredContent").GetProperty("task").Deserialize<RemoteTaskSnapshot>(WireJson.Options)!;
        Assert.Equal("COMPLETED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
        var first = await CallAsync(client, "agent_task_artifacts", new { taskId = task.TaskId, offset = 0, limit = 1 });
        var firstData = AssertOutputMatches(listed, "agent_task_artifacts", first);
        AssertLegacyTaskJson(first, firstData);
        Assert.Equal(1, firstData.GetProperty("nextOffset").GetInt32());
        var artifact = Assert.Single(firstData.GetProperty("artifacts").EnumerateArray());
        Assert.Equal(0, artifact.GetProperty("sequence").GetInt32());
        Assert.Equal("first", artifact.GetProperty("stepId").GetString());
        Assert.True(artifact.GetProperty("success").GetBoolean());
        Assert.False(artifact.GetProperty("truncated").GetBoolean());
        Assert.False(artifact.TryGetProperty("exitCode", out _));
        Assert.False(artifact.TryGetProperty("error", out _));
        var second = await CallAsync(client, "agent_task_artifacts", new { taskId = task.TaskId, offset = 1, limit = 1 });
        var secondData = AssertOutputMatches(listed, "agent_task_artifacts", second);
        AssertLegacyTaskJson(second, secondData);
        Assert.False(secondData.TryGetProperty("nextOffset", out _));
        Assert.Equal("second", Assert.Single(secondData.GetProperty("artifacts").EnumerateArray()).GetProperty("stepId").GetString());
        var empty = await CallAsync(client, "agent_task_artifacts", new { taskId = task.TaskId, offset = 2, limit = 1 });
        Assert.Empty(AssertOutputMatches(listed, "agent_task_artifacts", empty).GetProperty("artifacts").EnumerateArray());
    }

    [Fact]
    public async Task Task_schemas_validate_wire_contract_types_including_failure_and_optional_fields()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        using var client = await GrantAsync(app, admin, peer.DeviceId);
        var listed = await RpcAsync(client, "tools/list", new { });
        var task = new RemoteTaskSnapshot(Guid.NewGuid().ToString("N"), "fixture", "project", "FAILED", "verify", 0, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "failed", RootTaskId: Guid.NewGuid().ToString("N"));
        var reply = WireJson.Element(new RemoteTaskReply(Task: task, Artifacts: [new(0, "verify", "VERIFY", "process.start", 5,
            false, "retained partial output", true, -1, DateTimeOffset.UtcNow, "nonzero exit")], NextOffset: 1));
        var schema = SchemaGuard.Compile(OutputSchema(listed, "agent_task_artifacts"));
        Assert.True(SchemaGuard.Matches(schema, reply));
        Assert.True(SchemaGuard.Matches(schema, WireJson.Element(RemoteTaskReply.Failure("offline", "Agent offline"))));
        var bad = JsonNode.Parse(reply.GetRawText())!;
        bad["artifacts"]![0]!["success"] = "false";
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(bad)));
        bad = JsonNode.Parse(reply.GetRawText())!;
        bad["nextOffset"] = "1";
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(bad)));
        bad = JsonNode.Parse(reply.GetRawText())!;
        bad["task"]!.AsObject().Remove("updatedAt");
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(bad)));
    }

    private static JsonElement OutputSchema(JsonElement listed, string name)
    {
        var tool = listed.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);
        Assert.True(tool.TryGetProperty("outputSchema", out var schema), "Missing outputSchema for " + name);
        return schema;
    }

    private static JsonElement AssertOutputMatches(JsonElement listed, string name, JsonElement result)
    {
        Assert.True(result.TryGetProperty("structuredContent", out var structured), "Missing structuredContent for " + name);
        Assert.Equal(JsonValueKind.Object, structured.ValueKind);
        Assert.True(SchemaGuard.Matches(SchemaGuard.Compile(OutputSchema(listed, name)), structured), name + ": " + structured.GetRawText());
        return structured;
    }

    private static void AssertLegacyTaskJson(JsonElement result, JsonElement structured) =>
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(result.GetProperty("content")[0].GetProperty("text").GetString()!), structured));

    private sealed class OutputFixtureTool(ToolReply reply, bool sensitive = false) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.output", "schema_fixture", "test", "Controlled output schema fixture",
            WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), true, sensitive);
        public Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct) => Task.FromResult(reply);
    }
}
