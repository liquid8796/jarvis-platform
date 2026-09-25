using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.ToolPrograms;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class SafeScriptToolTests
{
    private static AgentExecutionContext Context() => new(Path.GetTempPath(), "script", "session");

    [Fact]
    public void Core_host_descriptor_surface_includes_both_code_modes_and_developer_tools()
    {
        var ids = AgentCoreHostTools.Descriptors.Select(descriptor => descriptor.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var id in new[] { "tool_program.run", "tool_script.run", "tool_repl.exec", "tool_repl.wait",
                     "tool_repl.sleep", "tool_repl.reset", "developer.symbol_search", "developer.test" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public async Task Script_invokes_guarded_tool_and_has_no_node_or_clr_globals()
    {
        string? calledTool = null;
        JsonElement calledArgs = default;
        var engine = new SafeScriptEngine((tool, args, _, _) =>
        {
            calledTool = tool;
            calledArgs = args.Clone();
            return Task.FromResult(new ToolReply("{\"value\":7}"));
        });

        var reply = await engine.ExecuteAsync("""
            const raw = await invokeTool("test.read", JSON.stringify({ value: 7 }));
            return {
              raw,
              globals: [typeof process, typeof require, typeof fetch, typeof clr, typeof XMLHttpRequest]
            };
            """, Context(), CancellationToken.None);

        Assert.Equal("test.read", calledTool);
        Assert.Equal(7, calledArgs.GetProperty("value").GetInt32());
        using var json = JsonDocument.Parse(reply.Text);
        Assert.Equal("{\"value\":7}", json.RootElement.GetProperty("raw").GetString());
        Assert.All(json.RootElement.GetProperty("globals").EnumerateArray(), value => Assert.Equal("undefined", value.GetString()));
    }

    [Theory]
    [InlineData("tool_script.run")]
    [InlineData("tool_program.run")]
    public async Task Script_rejects_composite_recursion(string toolId)
    {
        var calls = 0;
        var engine = new SafeScriptEngine((_, _, _, _) => { calls++; return Task.FromResult(new ToolReply("ok")); });
        var script = $"return await invokeTool({JsonSerializer.Serialize(toolId)}, '{{}}');";
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(script, Context(), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Script_enforces_statement_and_output_budgets()
    {
        var engine = new SafeScriptEngine((_, _, _, _) => Task.FromResult(new ToolReply(new string('x', 200))),
            new SafeScriptLimits(MaxScriptChars: 10_000, MaxToolCalls: 1, MaxOutputChars: 100, MaxStatements: 50,
                MaxMemoryBytes: 4 * 1024 * 1024, MaxElapsed: TimeSpan.FromSeconds(2)));

        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteAsync("let n=0; while(true){ n++; } return n;", Context(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(
            "return await invokeTool('test.read', '{}');", Context(), CancellationToken.None));
    }

    [Fact]
    public async Task Script_cannot_swallow_a_failed_nested_tool_call()
    {
        var engine = new SafeScriptEngine((_, _, _, _) =>
            throw new UnauthorizedAccessException("denied"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.ExecuteAsync(
            "try { await invokeTool('test.denied', '{}'); } catch (_) { } return 'ignored';",
            Context(), CancellationToken.None));
    }

    [Fact]
    public async Task Connection_registers_script_tool_and_nested_calls_reenter_guarded_policy()
    {
        var inner = new MutatingTool();
        var approval = new DenyApproval();
        var gate = new LocalControlGate(); gate.Arm();
        await using var connection = new AgentConnection([inner], approval, gate);

        Assert.Contains(connection.Descriptors, descriptor => descriptor.Id == "tool_script.run");
        var tool = connection.ToolRegistry.Snapshot.Tools["tool_script.run"];
        var args = WireJson.Element(new { script = "return await invokeTool('test.mutate', '{}');" });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(args, Context(), CancellationToken.None));
        Assert.Equal(1, approval.Calls);
        Assert.Equal(0, inner.Calls);
    }

    private sealed class DenyApproval : IApprovalService
    {
        public int Calls;
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(false); }
    }

    private sealed class MutatingTool : IAgentTool
    {
        public int Calls;
        public ToolDescriptor Descriptor { get; } = new("test.mutate", "test_mutate", "test", "synthetic mutating tool",
            WireJson.Element(new { type = "object", properties = new { }, additionalProperties = false }), false, true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new ToolReply("mutated")); }
    }
}
