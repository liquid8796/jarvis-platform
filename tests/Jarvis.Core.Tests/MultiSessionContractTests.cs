using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class MultiSessionContractTests
{
    [Fact]
    public void Agent_can_connect_without_a_default_workspace()
    {
        var options = new AgentOptions("https://agent.example.test", Guid.NewGuid().ToString(), "");
        Assert.Equal("wss", options.ValidateAndGetWebSocketUri().Scheme);
        Assert.Empty(new WorkspaceDirectories("").Directories);
    }

    [Fact]
    public void Empty_workspace_never_falls_back_to_the_process_directory()
    {
        var folders = new WorkspaceDirectories("");
        var error = Assert.Throws<InvalidOperationException>(() => folders.Resolve("relative.txt"));
        Assert.Contains("WORKSPACE_REQUIRED", error.Message);
        var absolute = Path.Combine(Path.GetTempPath(), "jarvis-absolute.txt");
        Assert.Equal(Path.GetFullPath(absolute), folders.Resolve(absolute));
    }

    [Fact]
    public async Task Shipping_connection_publishes_session_and_workspace_tools()
    {
        await using var connection = new AgentConnection([], new DenyApproval(), new LocalControlGate());
        var ids = connection.Descriptors.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in new[] { "session.open", "session.get", "session.list", "session.send_message", "session.read_events", "session.close", "workspace.get", "workspace.set" })
            Assert.Contains(id, ids);
    }

    private sealed class DenyApproval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
