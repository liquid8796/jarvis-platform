using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Artifacts;
using Jarvis.Agent.Core.Threads;
using Jarvis.Agent.Desktop.ViewModels;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Tools.BuiltIn;

namespace Jarvis.Agent.Windows.Tests;

public sealed class ToolPermissionsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-tool-permissions-" + Guid.NewGuid().ToString("N"));

    private sealed class Approval : IApprovalService
    {
        public Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class Questions : IUserQuestions
    {
        public Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken cancellationToken) =>
            Task.FromResult<UserQuestionAnswers?>(new(new Dictionary<string, string>()));
    }

    private sealed class Artifacts : IArtifactSink
    {
        public Task ShowAsync(WidgetArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Live_runtime_catalog_replaces_the_permission_snapshot_and_matches_its_count()
    {
        Directory.CreateDirectory(_root);
        var store = new ToolPermissionStore(Path.Combine(_root, "tool-permissions.json"));
        var policy = new ToolPermissionPolicy();
        var initial = new[]
        {
            Descriptor("filesystem.Read", "filesystem__Read", "filesystem"),
            Descriptor("thread.create", "thread__create", "thread")
        };
        var viewModel = new ToolPermissionsViewModel(initial, policy, store);
        viewModel.Items.Single(item => item.Id == "thread.create").FullPermission = true;

        await using var runtime = new AgentRuntime(new Approval(), new Questions(), new Artifacts(),
            settingsRoot: _root, pluginDirectory: Path.Combine(_root, "plugins"));
        viewModel.ReplaceDescriptors(runtime.Connection.Descriptors);

        Assert.Equal(102, runtime.Connection.Descriptors.Count);
        Assert.Equal(runtime.Connection.Descriptors.Count, viewModel.Items.Count);
        Assert.Equal(runtime.Connection.Descriptors.Select(tool => tool.Id).Order(),
            viewModel.Items.Select(item => item.Id).Order());
        Assert.Contains(viewModel.Items, item => item.Id == "artifact.create");
        Assert.Contains(viewModel.Items, item => item.Id == "async_input.request");
        Assert.True(viewModel.Items.Single(item => item.Id == "thread.create").FullPermission);
        Assert.Contains(viewModel.Items.Count.ToString(), viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Replacing_descriptors_prunes_removed_active_permissions_and_preserves_retained_drafts()
    {
        Directory.CreateDirectory(_root);
        var store = new ToolPermissionStore(Path.Combine(_root, "tool-permissions.json"));
        store.Save(new ToolPermissionSettings(["tool.removed"], []));
        var policy = new ToolPermissionPolicy();
        var viewModel = new ToolPermissionsViewModel(
            [Descriptor("tool.removed", "removed", "test"), Descriptor("tool.retained", "retained", "test")],
            policy, store);
        viewModel.Items.Single(item => item.Id == "tool.retained").FullPermission = true;

        viewModel.ReplaceDescriptors(
            [Descriptor("tool.retained", "retained_v2", "updated"), Descriptor("tool.added", "added", "test")]);

        Assert.DoesNotContain(viewModel.Items, item => item.Id == "tool.removed");
        Assert.False(policy.HasFullPermission("tool.removed"));
        Assert.True(viewModel.Items.Single(item => item.Id == "tool.retained").FullPermission);
        Assert.False(viewModel.Items.Single(item => item.Id == "tool.added").FullPermission);
        Assert.Equal("retained_v2", viewModel.Items.Single(item => item.Id == "tool.retained").Name);
        Assert.True(viewModel.HasChanges);
    }

    [Fact]
    public void Descriptor_only_runtime_extensions_match_their_executable_tool_sets()
    {
        Directory.CreateDirectory(_root);
        using var artifacts = new ArtifactRuntimeToolSet(Path.Combine(_root, "artifacts.db"));
        using var interactions = new ThreadInteractionRuntimeToolSet(Path.Combine(_root, "threads.db"), startPump: false);

        Assert.Equal(6, ArtifactRuntimeToolSet.Descriptors.Count);
        Assert.Equal(13, ThreadInteractionRuntimeToolSet.Descriptors.Count);
        Assert.Equal(artifacts.Tools.Select(tool => tool.Descriptor.Id).Order(),
            ArtifactRuntimeToolSet.Descriptors.Select(tool => tool.Id).Order());
        Assert.Equal(interactions.Tools.Select(tool => tool.Descriptor.Id).Order(),
            ThreadInteractionRuntimeToolSet.Descriptors.Select(tool => tool.Id).Order());
    }

    private static ToolDescriptor Descriptor(string id, string name, string category) =>
        new(id, name, category, id, WireJson.Element(new { type = "object", additionalProperties = false }),
            ReadOnly: false, Sensitive: true);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
