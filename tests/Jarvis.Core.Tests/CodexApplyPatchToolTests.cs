using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class CodexApplyPatchToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-apply-patch-" + Guid.NewGuid().ToString("N"));
    private readonly string _private = Path.Combine(Path.GetTempPath(), "jarvis-apply-private-" + Guid.NewGuid().ToString("N"));

    public CodexApplyPatchToolTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_private);
    }

    [Fact]
    public async Task Applies_add_update_move_and_delete_with_Codex_public_schema()
    {
        var observations = new SessionFileObservations();
        var tool = new CodexApplyPatchTool(observations, _private);
        var context = new AgentExecutionContext(_root, "call", "session");
        Assert.Equal("source.apply_patch", tool.Descriptor.Id);
        Assert.Equal("apply_patch", tool.Descriptor.Name);

        var existing = Path.Combine(_root, "existing.txt");
        var deleted = Path.Combine(_root, "delete-me.txt");
        await File.WriteAllTextAsync(existing, "alpha\r\nbeta\r\n");
        await File.WriteAllTextAsync(deleted, "remove\n");
        await observations.RememberAsync(context.IsolationScopeId, existing, CancellationToken.None);
        await observations.RememberAsync(context.IsolationScopeId, deleted, CancellationToken.None);

        var reply = await tool.ExecuteAsync(WireJson.Element(new
        {
            patch = """
                *** Begin Patch
                *** Update File: existing.txt
                *** Move to: moved.txt
                @@ alpha
                 alpha
                -beta
                +gamma
                *** Add File: added.txt
                +one
                +two
                *** Delete File: delete-me.txt
                *** End Patch
                """
        }), context, CancellationToken.None);

        Assert.False(reply.IsError, reply.Text);
        Assert.False(File.Exists(existing));
        Assert.Equal("alpha\r\ngamma\r\n", await File.ReadAllTextAsync(Path.Combine(_root, "moved.txt")));
        Assert.Equal("one\ntwo\n", await File.ReadAllTextAsync(Path.Combine(_root, "added.txt")));
        Assert.False(File.Exists(deleted));
    }

    [Fact]
    public async Task Rejects_stale_existing_file_without_writing()
    {
        var observations = new SessionFileObservations();
        var tool = new CodexApplyPatchTool(observations, _private);
        var context = new AgentExecutionContext(_root, "call", "session");
        var path = Path.Combine(_root, "stale.txt");
        await File.WriteAllTextAsync(path, "before\n");
        await observations.RememberAsync(context.IsolationScopeId, path, CancellationToken.None);
        await File.WriteAllTextAsync(path, "external\n");

        var ex = await Assert.ThrowsAsync<AgentRequestException>(() => tool.ExecuteAsync(WireJson.Element(new
        {
            patch = """
                *** Begin Patch
                *** Update File: stale.txt
                @@
                -before
                +after
                *** End Patch
                """
        }), context, CancellationToken.None));
        Assert.Equal("FILE_CHANGED", ex.Code);
        Assert.Equal("external\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Rejects_workspace_escape_and_private_profile_paths()
    {
        var tool = new CodexApplyPatchTool(new SessionFileObservations(), _private);
        var context = new AgentExecutionContext(_root, "call", "session");
        var escape = await tool.ExecuteAsync(WireJson.Element(new
        {
            patch = "*** Begin Patch\n*** Add File: ../escape.txt\n+no\n*** End Patch"
        }), context, CancellationToken.None);
        Assert.True(escape.IsError);
        Assert.Contains("escapes", escape.Text, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (Directory.Exists(_private)) Directory.Delete(_private, true);
    }
}
