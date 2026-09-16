using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Diagnostics;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class AgentDoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-doctor-secret-" + Guid.NewGuid().ToString("N"));

    private sealed class Tool : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = new("test.read", "test__read", "test", "doctor fixture",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement arguments, AgentExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolReply("ok"));
    }

    [Fact]
    public void Snapshot_reports_operational_health_without_sensitive_paths_or_grant_ids()
    {
        Directory.CreateDirectory(_root);
        var pluginRoot = Path.Combine(_root, "plugins"); Directory.CreateDirectory(pluginRoot);
        var taskRoot = Path.Combine(_root, "tasks"); Directory.CreateDirectory(taskRoot);
        var registry = new DynamicToolRegistry([new Tool()]);
        var policy = new ToolPermissionPolicy(["secret.full.permission"]);
        policy.GrantLease(new ToolCapabilityLease("lease-secret", "test.read", ToolCapabilityScope.Session,
            "secret-session", null, DateTimeOffset.UtcNow.AddMinutes(1), [_root], null));

        var snapshot = AgentDoctor.Capture(new AgentDoctorInput(
            "1.0.56", new Version(1, 0, 56, 0), registry, policy, pluginRoot, [], taskRoot, 0, true, false));
        var json = JsonSerializer.Serialize(snapshot, WireJson.Options);

        Assert.Equal(AgentProtocolVersion.Current, snapshot.ProtocolVersion);
        Assert.Equal(1, snapshot.ToolCount);
        Assert.Equal(registry.Snapshot.Generation, snapshot.CatalogGeneration);
        Assert.Equal(registry.Snapshot.Digest, snapshot.CatalogDigest);
        Assert.Equal(1, snapshot.FullPermissionCount);
        Assert.Equal(1, snapshot.ActiveCapabilityLeaseCount);
        Assert.True(snapshot.PluginHealthy);
        Assert.True(snapshot.TaskStoreHealthy);
        Assert.False(snapshot.VersionDrift);
        Assert.DoesNotContain(_root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.full.permission", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-session", json, StringComparison.Ordinal);
        Assert.DoesNotContain("lease-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_plugin_metadata_is_reported_by_type_without_leaking_manifest_content()
    {
        Directory.CreateDirectory(_root);
        var pluginRoot = Path.Combine(_root, "plugins"); Directory.CreateDirectory(pluginRoot);
        var marker = "SUPER_SECRET_PLUGIN_TEXT";
        File.WriteAllText(Path.Combine(pluginRoot, "bad.plugin.json"), marker);
        var taskRoot = Path.Combine(_root, "tasks"); Directory.CreateDirectory(taskRoot);

        var snapshot = AgentDoctor.Capture(new AgentDoctorInput("1.0.56", new Version(1, 0, 56, 0),
            new DynamicToolRegistry([new Tool()]), new ToolPermissionPolicy(), pluginRoot, [], taskRoot, 0, false, false));
        var json = JsonSerializer.Serialize(snapshot, WireJson.Options);

        Assert.False(snapshot.PluginHealthy);
        Assert.Contains("error", snapshot.PluginStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(marker, json, StringComparison.Ordinal);
        Assert.DoesNotContain(pluginRoot, json, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
