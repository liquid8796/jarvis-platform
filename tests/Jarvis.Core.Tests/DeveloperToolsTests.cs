using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.DeveloperTools;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class DeveloperToolsTests : IDisposable
{
    private sealed class Approval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-devtools-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "jarvis-devtools-outside-" + Guid.NewGuid().ToString("N"));

    public DeveloperToolsTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "src", "Alpha.cs"), "class Alpha { void TargetSymbol() {} }\n");
        File.WriteAllText(Path.Combine(_root, "src", "Beta.cs"), "class Beta { void TargetSymbol() {} }\n");
        File.WriteAllText(Path.Combine(_root, "bin", "Generated.cs"), "class TargetSymbol {}\n");
        File.WriteAllText(Path.Combine(_root, ".git", "ignored.txt"), "TargetSymbol\n");
    }

    private AgentExecutionContext Context() => new(_root, "call", "session");

    [Fact]
    public async Task Agent_connection_publishes_structured_developer_tools()
    {
        await using var connection = new AgentConnection([], new Approval(), new LocalControlGate());
        Assert.Contains(connection.Descriptors, descriptor => descriptor.Id == "developer.symbol_search" && descriptor.ReadOnly);
        Assert.Contains(connection.Descriptors, descriptor => descriptor.Id == "developer.test" && descriptor.Sensitive && !descriptor.ReadOnly);
    }

    [Fact]
    public async Task Symbol_search_is_workspace_scoped_bounded_and_excludes_build_vcs_directories()
    {
        var tool = new DeveloperSymbolSearchTool();
        var reply = await tool.ExecuteAsync(WireJson.Element(new { query = "TargetSymbol", path = "src", maxResults = 1 }), Context(), default);
        Assert.False(reply.IsError);
        var json = JsonSerializer.Deserialize<JsonElement>(reply.Text);
        Assert.Equal(1, json.GetProperty("matches").GetArrayLength());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain("bin", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".git", reply.Text, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(
            WireJson.Element(new { query = "TargetSymbol", path = _outside }), Context(), default));
    }

    [Fact]
    public void Dotnet_test_summary_parser_returns_structured_counts_and_bounds_output()
    {
        var parsed = DotnetTestSummaryParser.Parse("Passed!  - Failed:     2, Passed:    17, Skipped:     3, Total:    22, Duration: 1 s", exitCode: 1, maxOutputChars: 80);
        Assert.Equal(2, parsed.Failed);
        Assert.Equal(17, parsed.Passed);
        Assert.Equal(3, parsed.Skipped);
        Assert.Equal(22, parsed.Total);
        Assert.Equal(1, parsed.ExitCode);
        Assert.True(parsed.Output.Length <= 80);
    }

    [Fact]
    public async Task Structured_test_tool_validates_project_path_before_runner_is_called()
    {
        var calls = 0;
        var tool = new DeveloperTestTool((request, _) =>
        {
            calls++;
            return Task.FromResult(new DeveloperTestRunResult(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1", ""));
        });
        var ok = await tool.ExecuteAsync(WireJson.Element(new { project = "src", filter = "Fast" }), Context(), default);
        Assert.False(ok.IsError);
        Assert.Equal(1, calls);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(
            WireJson.Element(new { project = _outside }), Context(), default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Dap_launcher_owns_only_its_adapter_process_and_cancellation_kills_it()
    {
        var adapter = Path.Combine(_root, "adapter.exe");
        File.WriteAllText(adapter, "fixture");
        var fake = new FakeDapProcess();
        var launcher = new DapAdapterLauncher((request, _) => Task.FromResult<IDapOwnedProcess>(fake));

        await using var session = await launcher.LaunchAsync(new(adapter, ["--stdio"], _root), new WorkspaceDirectories(_root), default);
        Assert.False(fake.Killed);
        await session.StopAsync();
        Assert.True(fake.Killed);
        Assert.Equal(1, fake.KillCalls);
        await session.StopAsync();
        Assert.Equal(1, fake.KillCalls);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => launcher.LaunchAsync(new(adapter, [], _outside), new WorkspaceDirectories(_root), default));
    }

    private sealed class FakeDapProcess : IDapOwnedProcess
    {
        public int Id => 42;
        public bool HasExited { get; private set; }
        public bool Killed { get; private set; }
        public int KillCalls { get; private set; }
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(HasExited ? -1 : 0);
        public void Kill()
        {
            if (HasExited) return;
            Killed = true; HasExited = true; KillCalls++;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_outside)) Directory.Delete(_outside, true);
    }
}
