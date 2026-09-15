using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Core.Tests;
public sealed class SecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-tests-" + Guid.NewGuid());
    public SecurityTests() => Directory.CreateDirectory(_root);
    [Fact] public void Working_context_resolves_relative_path() => Assert.Equal(Path.Combine(_root, "a.txt"), new WorkspaceDirectories(_root).Resolve("a.txt"));
    [Theory] [InlineData("../outside.txt")] [InlineData("folder/../../outside.txt")]
    public void Working_context_accepts_parent_paths(string path) => Assert.Equal(Path.GetFullPath(path, _root), new WorkspaceDirectories(_root).Resolve(path));
    [Fact] public void Working_context_accepts_sibling_prefix() => Assert.Equal(Path.GetFullPath(_root + "-sibling/a"), new WorkspaceDirectories(_root).Resolve(_root + "-sibling/a"));
    [Fact] public void Gate_starts_disarmed_and_requires_explicit_arm()
    { var gate = new LocalControlGate(); Assert.False(gate.IsArmed); gate.Arm(); Assert.True(gate.IsArmed); gate.Disarm(); Assert.False(gate.IsArmed); }
    [Fact] public void Gate_is_a_manual_latch_without_an_expiration_deadline()
    {
        var gate = new LocalControlGate(); gate.Arm();
        // Repeated reads and unrelated work never consume an expiring lease.
        for (var i = 0; i < 10000; i++) Assert.True(gate.IsArmed);
        gate.Disarm(); Assert.False(gate.IsArmed);
    }
    [Fact] public void Gate_reports_transitions_only_and_does_not_rearm_itself()
    {
        var gate = new LocalControlGate(); var events = new List<bool>(); gate.Changed += events.Add;
        gate.Disarm(); gate.Arm(); gate.Arm(); gate.Disarm(); gate.Disarm();
        Assert.Equal(new[] { true, false }, events); Assert.False(gate.IsArmed);
    }
    [Fact] public void New_gate_does_not_inherit_previous_process_grant()
    { var previous = new LocalControlGate(); previous.Arm(); Assert.False(new LocalControlGate().IsArmed); }
    [Fact] public void Gate_handles_concurrent_access_and_explicit_final_pause()
    {
        var gate = new LocalControlGate();
        Parallel.For(0, 1000, i => { if (i % 2 == 0) gate.Arm(); else gate.Disarm(); _ = gate.IsArmed; });
        gate.Disarm(); Assert.False(gate.IsArmed);
    }
    [Fact] public void Agent_selects_wss_and_preserves_port()
    { var uri = new AgentOptions("https://jarvis.example:8443", Guid.NewGuid().ToString(), _root).ValidateAndGetWebSocketUri(); Assert.Equal("wss://jarvis.example:8443/agent/connect", uri.AbsoluteUri); }
    [Theory] [InlineData("http://example.com")] [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com/other")] [InlineData("https://example.com?token=secret")]
    public void Agent_rejects_unsafe_origin(string origin) => Assert.Throws<ArgumentException>(() => new AgentOptions(origin, Guid.NewGuid().ToString(), _root).ValidateAndGetWebSocketUri());
    [Fact] public void Http_loopback_requires_explicit_consent()
    {
        Assert.Throws<ArgumentException>(() => new AgentOptions("http://localhost:18765", Guid.NewGuid().ToString(), _root).ValidateAndGetWebSocketUri());
        Assert.Equal("ws", new AgentOptions("http://localhost:18765", Guid.NewGuid().ToString(), _root, true).ValidateAndGetWebSocketUri().Scheme);
    }
    [Fact] public void Schema_enforces_types_and_additional_properties()
    {
        var schema = SchemaGuard.Compile(WireJson.Element(new { type = "object", properties = new { count = new { type = "integer", minimum = 0 } }, required = new[] { "count" }, additionalProperties = false }));
        Assert.True(SchemaGuard.Matches(schema, WireJson.Element(new { count = 2 })));
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(new { count = -1 })));
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(new { count = "2" })));
        Assert.False(SchemaGuard.Matches(schema, WireJson.Element(new { count = 2, extra = true })));
    }
    [Fact] public void Schema_rejects_external_reference_without_fetching()
    { var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{\"$ref\":\"https://example.invalid/schema.json\"}"); Assert.Throws<ArgumentException>(() => SchemaGuard.Compile(json)); }
    [Fact] public void Widget_has_no_same_origin_or_network_capability()
    { var html = SandboxHtml.Wrap(new("<script>bad()</script>", "<h1>Sample</h1>")); Assert.Contains("sandbox=", html); Assert.DoesNotContain("allow-same-origin", html); Assert.Contains("connect-src", html); Assert.DoesNotContain("<script>bad()</script>", html); }
    public void Dispose() => Directory.Delete(_root, true);
}
