using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Ide;

namespace JarvisCode.Core.Tests;

public sealed class IdeDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-ide-discovery-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void Discovery_checks_process_lifetime_and_workspace_boundary_without_exposing_the_token()
    {
        Directory.CreateDirectory(_root);
        var workspace = Path.Combine(_root, "project"); Directory.CreateDirectory(workspace);
        var id = new string('a', 32);
        var token = new string('b', 64);
        var data = new JsonObject
        {
            ["transport"] = "jarvis-pipe", ["id"] = id, ["pid"] = Environment.ProcessId,
            ["startedAt"] = (long)(Process.GetCurrentProcess().StartTime.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds,
            ["workspaceFolders"] = new JsonArray(workspace), ["ideName"] = "Test editor",
            ["authToken"] = token, ["pipeName"] = $"jarvis-ide-{Environment.ProcessId}-{id}",
        };
        var file = Path.Combine(_root, id + ".lock"); File.WriteAllText(file, data.ToJsonString());
        var bridge = new IdeBridge(_root);
        var editor = Assert.Single(bridge.Discover(workspace));
        Assert.DoesNotContain(token, JsonSerializer.Serialize(editor));
        Assert.Empty(bridge.Discover(workspace + "-other"));
        Assert.Single(bridge.Discover(Path.Combine(workspace, "nested")));
        data["startedAt"] = 1; File.WriteAllText(file, data.ToJsonString());
        Assert.Empty(bridge.Discover(workspace));
        File.WriteAllText(file, "{}");
        Assert.Empty(bridge.Discover(workspace));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
