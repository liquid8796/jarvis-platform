using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Artifacts;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ArtifactRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-artifact-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AgentSessionIdentity _identity = new("owner-a", "device-a", AgentSessionRules.NewSessionId());

    [Fact]
    public async Task Runtime_persists_artifacts_and_lists_metadata_without_content()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "artifacts.db");
        string artifactId;
        using (var runtime = new ArtifactRuntimeToolSet(database))
        {
            var tools = ById(runtime);
            var created = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
            {
                title = "Persistent note",
                kind = "markdown",
                content = "# Hello\nDurable artifact",
                metadataJson = "{\"source\":\"test\"}"
            }), Context(_identity), default);
            Assert.False(created.IsError, created.Text);
            artifactId = Property(created, "artifactId").GetString()!;
        }

        using (var reopened = new ArtifactRuntimeToolSet(database))
        {
            var tools = ById(reopened);
            var read = await tools["artifact.get"].ExecuteAsync(WireJson.Element(new { artifactId }), Context(_identity), default);
            Assert.Equal("# Hello\nDurable artifact", Property(read, "content").GetString());
            Assert.Equal(1, Property(read, "revision").GetInt64());

            var listed = await tools["artifact.list"].ExecuteAsync(WireJson.Element(new { }), Context(_identity), default);
            using var json = JsonDocument.Parse(listed.Text);
            var row = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal(artifactId, row.GetProperty("artifactId").GetString());
            Assert.False(row.TryGetProperty("content", out _));
        }
    }

    [Fact]
    public void Store_enforces_scope_and_optimistic_revision()
    {
        Directory.CreateDirectory(_root);
        using var store = new SqliteArtifactRuntimeStore(Path.Combine(_root, "scope.db"));
        var created = store.Create(_identity, "Scoped", "text", "v1", null);
        var updated = store.Update(_identity, created.ArtifactId, 1, null, null, "v2", null);
        Assert.Equal(2, updated.Revision);
        var conflict = Assert.Throws<AgentRequestException>(() =>
            store.Update(_identity, created.ArtifactId, 1, null, null, "stale", null));
        Assert.Equal("ARTIFACT_REVISION_CONFLICT", conflict.Code);

        var otherSession = new AgentSessionIdentity(_identity.OwnerId, _identity.DeviceId, AgentSessionRules.NewSessionId());
        Assert.Throws<KeyNotFoundException>(() => store.Get(otherSession, created.ArtifactId));
        var otherDevice = new AgentSessionIdentity(_identity.OwnerId, "device-b", _identity.SessionId);
        Assert.Throws<KeyNotFoundException>(() => store.Get(otherDevice, created.ArtifactId));
    }

    [Fact]
    public async Task Show_returns_widget_and_uses_local_sink_with_network_denying_csp()
    {
        Directory.CreateDirectory(_root);
        WidgetArtifact? displayed = null;
        using var runtime = new ArtifactRuntimeToolSet(Path.Combine(_root, "show.db"),
            (widget, _) => { displayed = widget; return Task.CompletedTask; });
        var tools = ById(runtime);
        var reply = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "Interactive",
            kind = "html",
            content = "<html><head><base href='https://evil.example/'><meta http-equiv='refresh' content='0;url=https://evil.example'></head><body><script>window.localOnly=1</script><h1>Hello</h1></body></html>",
            show = true
        }), Context(_identity), default);

        Assert.False(reply.IsError, reply.Text);
        Assert.NotNull(reply.Widget);
        Assert.Same(displayed, reply.Widget);
        Assert.Contains("connect-src &#39;none&#39;", reply.Widget!.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<base", reply.Widget.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http-equiv='refresh'", reply.Widget.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1>Hello</h1>", reply.Widget.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markdown_and_text_rendering_escape_untrusted_markup()
    {
        Directory.CreateDirectory(_root);
        using var runtime = new ArtifactRuntimeToolSet(Path.Combine(_root, "escape.db"));
        var tools = ById(runtime);
        var markdown = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "Markdown",
            kind = "markdown",
            content = "# Safe\n<script>alert(1)</script>",
            show = true
        }), Context(_identity), default);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", markdown.Widget!.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", markdown.Widget.Html, StringComparison.Ordinal);

        var text = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "Text",
            kind = "text",
            content = "<img src=x onerror=alert(1)>",
            show = true
        }), Context(_identity), default);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", text.Widget!.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Svg_is_rendered_as_data_image_and_json_is_pretty_printed()
    {
        Directory.CreateDirectory(_root);
        using var runtime = new ArtifactRuntimeToolSet(Path.Combine(_root, "formats.db"));
        var tools = ById(runtime);
        var svg = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "Vector",
            kind = "svg",
            content = "<svg xmlns='http://www.w3.org/2000/svg'><circle cx='5' cy='5' r='4'/></svg>",
            show = true
        }), Context(_identity), default);
        Assert.Contains("data:image/svg+xml;base64,", svg.Widget!.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<circle", svg.Widget.Html, StringComparison.OrdinalIgnoreCase);

        var json = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "JSON",
            kind = "json",
            content = "{\"b\":2,\"a\":1}",
            show = true
        }), Context(_identity), default);
        Assert.Contains("&quot;b&quot;: 2", json.Widget!.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_is_revision_bound_and_hides_content_by_default()
    {
        Directory.CreateDirectory(_root);
        using var runtime = new ArtifactRuntimeToolSet(Path.Combine(_root, "delete.db"));
        var tools = ById(runtime);
        var created = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "Delete me", kind = "text", content = "secret"
        }), Context(_identity), default);
        var id = Property(created, "artifactId").GetString()!;
        var deleted = await tools["artifact.delete"].ExecuteAsync(WireJson.Element(new
        {
            artifactId = id, expectedRevision = 1
        }), Context(_identity), default);
        Assert.False(deleted.IsError, deleted.Text);
        Assert.Equal(2, Property(deleted, "revision").GetInt64());
        Assert.True(Property(deleted, "deleted").GetBoolean());

        var missing = await tools["artifact.get"].ExecuteAsync(WireJson.Element(new { artifactId = id }), Context(_identity), default);
        Assert.True(missing.IsError);
        var includingDeleted = await tools["artifact.get"].ExecuteAsync(WireJson.Element(new
        {
            artifactId = id, includeDeleted = true
        }), Context(_identity), default);
        Assert.False(includingDeleted.IsError, includingDeleted.Text);
        using (var deletedJson = JsonDocument.Parse(includingDeleted.Text))
            Assert.False(deletedJson.RootElement.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task Tool_surface_requires_explicit_session_and_valid_metadata()
    {
        Directory.CreateDirectory(_root);
        using var runtime = new ArtifactRuntimeToolSet(Path.Combine(_root, "validation.db"));
        var tools = ById(runtime);
        var ephemeral = new AgentExecutionContext(_root, "call", AgentSessionRules.NewEphemeralExecutionId());
        var noSession = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "No session", kind = "text", content = "x"
        }), ephemeral, default);
        Assert.True(noSession.IsError);

        var badMetadata = await tools["artifact.create"].ExecuteAsync(WireJson.Element(new
        {
            title = "Bad metadata", kind = "text", content = "x", metadataJson = "[]"
        }), Context(_identity), default);
        Assert.True(badMetadata.IsError);
        Assert.Contains("JSON object", badMetadata.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_tool_descriptors_cover_all_operations()
    {
        Directory.CreateDirectory(_root);
        using var runtime = new ArtifactRuntimeToolSet(Path.Combine(_root, "descriptor.db"));
        var byId = ById(runtime);
        Assert.Equal(new[]
        {
            "artifact.create", "artifact.delete", "artifact.get", "artifact.list", "artifact.show", "artifact.update"
        }, byId.Keys.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal("artifact_create", byId["artifact.create"].Descriptor.Name);
        Assert.Equal("artifact_show", byId["artifact.show"].Descriptor.Name);
    }

    private static Dictionary<string, IAgentTool> ById(ArtifactRuntimeToolSet runtime) =>
        runtime.Tools.ToDictionary(tool => tool.Descriptor.Id, StringComparer.Ordinal);

    private AgentExecutionContext Context(AgentSessionIdentity identity) =>
        new(_root, "artifact-test", identity.SessionId)
        {
            OwnerId = identity.OwnerId,
            AgentDeviceId = identity.DeviceId,
            SessionCancellation = CancellationToken.None
        };

    private static JsonElement Property(ToolReply reply, string name)
    {
        using var document = JsonDocument.Parse(reply.Text);
        return document.RootElement.GetProperty(name).Clone();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
