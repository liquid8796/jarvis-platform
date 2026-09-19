using Jarvis.Agent.Core.Autonomous.Verification;

namespace Jarvis.Core.Tests;

public sealed class FrontendWorkspaceStateTests
{
    [Fact]
    public void Revision_tracks_untracked_edits_deletions_assets_and_ignores_build_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-source-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "app.tsx"), "one");
            var initial = FrontendWorkspaceState.Capture(root);
            Assert.True(initial.Complete);
            Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            File.WriteAllText(Path.Combine(root, "node_modules", "ignored.js"), "change");
            Assert.Equal(initial.Revision, FrontendWorkspaceState.Capture(root).Revision);
            File.WriteAllText(Path.Combine(root, "app.tsx"), "two");
            File.WriteAllText(Path.Combine(root, "new.svg"), "<svg/>");
            var edited = FrontendWorkspaceState.Capture(root);
            Assert.NotEqual(initial.Revision, edited.Revision);
            Assert.Equal(new[] { "app.tsx", "new.svg" }, edited.ChangedSince(initial.Files));
            File.Delete(Path.Combine(root, "app.tsx"));
            Assert.Contains("app.tsx", FrontendWorkspaceState.Capture(root).ChangedSince(edited.Files));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Missing_workspace_cannot_supply_a_revision()
    {
        var state = FrontendWorkspaceState.Capture(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.False(state.Complete);
        Assert.Empty(state.Revision);
    }
}
