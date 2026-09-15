using Jarvis.Agent.Core;
using Jarvis.Agent.Core.ToolPrograms;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ToolProgramEngineTests
{
    private static AgentExecutionContext Context() => new(Path.GetTempPath(), "program", "session");

    [Fact]
    public async Task Program_calls_tools_branches_loops_and_returns_variables()
    {
        var calls = new List<string>();
        var engine = new ToolProgramEngine(async (tool, args, _, _) =>
        {
            await Task.Yield();
            calls.Add(tool + ":" + (args.TryGetProperty("value", out var value) ? value.ToString() : ""));
            return new ToolReply(tool == "test.echo" ? "ok" : args.GetProperty("value").ToString());
        });
        var program = WireJson.Element(new { instructions = new object[]
        {
            new { op = "call", tool = "test.echo", args = new { value = "start" }, save = "result" },
            new { op = "if", variable = "result", equals = "ok", then = new object[]
            {
                new { op = "forEach", item = "item", items = new[] { "a", "b" }, body = new object[]
                {
                    new { op = "call", tool = "test.item", args = new { value = "$item" }, save = "last" }
                } }
            } },
            new { op = "assert", variable = "last", equals = "b" },
            new { op = "return", variable = "last" }
        }});

        var result = await engine.ExecuteAsync(program, Context(), CancellationToken.None);

        Assert.Equal("b", result.Text);
        Assert.Equal(["test.echo:start", "test.item:a", "test.item:b"], calls);
    }

    [Fact]
    public async Task Program_rejects_recursion_unknown_variables_and_assertion_failures()
    {
        var engine = new ToolProgramEngine((_, _, _, _) => Task.FromResult(new ToolReply("ok")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(
            WireJson.Element(new { instructions = new[] { new { op = "call", tool = "tool_program.run", args = new { } } } }), Context(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ExecuteAsync(
            WireJson.Element(new { instructions = new[] { new { op = "return", variable = "missing" } } }), Context(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(
            WireJson.Element(new { instructions = new[] { new { op = "assert", variable = "missing", equals = "x" } } }), Context(), default));
    }

    [Fact]
    public async Task Program_enforces_instruction_call_and_output_budgets()
    {
        var engine = new ToolProgramEngine((_, _, _, _) => Task.FromResult(new ToolReply(new string('x', 200))),
            new ToolProgramLimits(MaxInstructions: 3, MaxToolCalls: 1, MaxOutputChars: 100, MaxElapsed: TimeSpan.FromSeconds(5)));
        var tooManyCalls = WireJson.Element(new { instructions = new object[]
        {
            new { op = "call", tool = "test.one", args = new { } },
            new { op = "call", tool = "test.two", args = new { } }
        }});
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(tooManyCalls, Context(), default));

        var output = WireJson.Element(new { instructions = new object[] { new { op = "call", tool = "test.one", args = new { }, save = "x" }, new { op = "return", variable = "x" } } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(output, Context(), default));
    }
}
