using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Auditing;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class AgentAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Persistent_audit_is_reason_coded_bounded_and_does_not_store_raw_session_ids()
    {
        var session = "js_" + new string('a', 32);
        using var log = new FileAgentAuditLog(_root);
        log.Write("PROCESS_TIMEOUT", "process", "Process reached its timeout.",
            "process.launch", session, "call-1", 42);
        var records = log.Query(10, "PROCESS_TIMEOUT", "process.launch", session);
        var record = Assert.Single(records);
        Assert.Equal("process", record.Category);
        Assert.Equal(42, record.ProcessId);
        Assert.NotEqual(session, record.SessionId);
        var persisted = string.Join("\n", Directory.GetFiles(_root, "*.jsonl").Select(File.ReadAllText));
        Assert.DoesNotContain(session, persisted, StringComparison.Ordinal);
        Assert.Contains("PROCESS_TIMEOUT", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_query_defaults_to_the_current_session_and_all_sessions_requires_full_permission()
    {
        var session = "js_" + new string('b', 32);
        using var log = new FileAgentAuditLog(_root);
        log.Write("PAUSED", "security", "Paused.", sessionId: session);
        log.Write("OTHER", "security", "Other.", sessionId: "js_" + new string('c', 32));
        var tools = new AuditToolSet(log);
        var query = Assert.Single(tools.Tools);
        var context = new AgentExecutionContext(Path.GetTempPath(), "call", session)
        { OwnerId = "owner", AgentDeviceId = "device" };
        var scoped = await query.ExecuteAsync(WireJson.Element(new { limit = 10 }), context, CancellationToken.None);
        Assert.False(scoped.IsError, scoped.Text);
        using (var json = JsonDocument.Parse(scoped.Text))
            Assert.Single(json.RootElement.GetProperty("records").EnumerateArray());
        var denied = await query.ExecuteAsync(WireJson.Element(new { all_sessions = true }), context, CancellationToken.None);
        Assert.True(denied.IsError);
        var all = await query.ExecuteAsync(WireJson.Element(new { all_sessions = true }),
            context with { FullPermission = true }, CancellationToken.None);
        Assert.False(all.IsError, all.Text);
        using var allJson = JsonDocument.Parse(all.Text);
        Assert.Equal(2, allJson.RootElement.GetProperty("records").GetArrayLength());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
