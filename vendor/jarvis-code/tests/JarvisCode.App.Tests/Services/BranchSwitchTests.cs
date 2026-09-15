using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class BranchSwitchTests
{
    private static WorkingTreeStatus Dirty(params string[] paths) =>
        new([.. paths.Select(p => new WorkingTreeEntry(" M", p, false))], 0, 0);

    [Fact]
    public void CleanTreeNeverConflicts()
    {
        Assert.False(BranchSwitch.WouldConflict("main", WorkingTreeStatus.Empty, true, ["a.txt"]));
    }

    [Fact]
    public void UnmergedEntryAlwaysConflicts()
    {
        var status = new WorkingTreeStatus([new WorkingTreeEntry("UU", "a.txt", true)], 0, 0);
        Assert.True(BranchSwitch.WouldConflict("main", status, true, []));
    }

    [Fact]
    public void TargetThatLooksLikeAnOptionIsRefused()
    {
        Assert.True(BranchSwitch.WouldConflict("--force", Dirty("a.txt"), true, []));
    }

    [Fact]
    public void UnresolvableTargetCountsAsAConflict()
    {
        Assert.True(BranchSwitch.WouldConflict("gone", Dirty("a.txt"), false, []));
    }

    [Fact]
    public void DisjointPathsDoNotConflict()
    {
        Assert.False(BranchSwitch.WouldConflict("main", Dirty("src/a.cs"), true, ["docs/b.md"]));
    }

    [Fact]
    public void SamePathConflicts()
    {
        Assert.True(BranchSwitch.WouldConflict("main", Dirty("src/a.cs"), true, ["src/a.cs"]));
    }

    [Fact]
    public void DirtyDirectoryTheOtherSideChangedUnderConflicts()
    {
        // "src" is a parent of a changed path, which the reference's directory set covers.
        Assert.True(BranchSwitch.Collides(["src"], ["src/a.cs"]));
    }

    [Fact]
    public void DirtyPathUnderAChangedDirectoryConflicts()
    {
        Assert.True(BranchSwitch.Collides(["src/a.cs"], ["src"]));
    }

    [Fact]
    public void TrailingSlashOnADirtyPathIsIgnored()
    {
        Assert.True(BranchSwitch.Collides(["src/"], ["src"]));
    }

    [Fact]
    public void MessagesAreTheReferences()
    {
        Assert.Equal("epitaxy: pre-switch from feature", BranchSwitch.StashMessage("feature"));
        Assert.Equal("WIP: epitaxy pre-switch from feature", BranchSwitch.WipCommitMessage("feature"));
    }

    [Fact]
    public void CommandsFollowTheReference()
    {
        Assert.Equal(
            new[] { "stash", "push", "-u", "-m", "epitaxy: pre-switch from f" },
            BranchSwitch.Commands(DirtyTreeResolution.Stash, "f")[0]);

        var commit = BranchSwitch.Commands(DirtyTreeResolution.Commit, "f");
        Assert.Equal(new[] { "add", "-A" }, commit[0]);
        Assert.Equal(new[] { "commit", "-m", "WIP: epitaxy pre-switch from f", "--no-verify" }, commit[1]);

        var discard = BranchSwitch.Commands(DirtyTreeResolution.Discard, "f");
        Assert.Equal(new[] { "reset", "--hard" }, discard[0]);
        Assert.Equal(new[] { "clean", "-fd", "--", ":/" }, discard[1]);
    }

    [Fact]
    public void NothingToCommitIsSuccess()
    {
        Assert.True(BranchSwitch.IsBenign("nothing to commit, working tree clean"));
        Assert.True(BranchSwitch.IsBenign("No local changes to save"));
        Assert.False(BranchSwitch.IsBenign("fatal: not a git repository"));
    }

    [Fact]
    public void LabelsAreTheReferences()
    {
        Assert.Equal("Stash changes", BranchSwitch.ResolutionLabel(DirtyTreeResolution.Stash));
        Assert.Equal("Commit as WIP", BranchSwitch.ResolutionLabel(DirtyTreeResolution.Commit));
        Assert.Equal("Discard changes", BranchSwitch.ResolutionLabel(DirtyTreeResolution.Discard));
        Assert.Equal("1 file changed", BranchSwitch.FilesChanged(1));
        Assert.Equal("3 files changed", BranchSwitch.FilesChanged(3));
        Assert.Equal("2 additions, 5 deletions", BranchSwitch.DiffLabel(2, 5));
    }

    [Fact]
    public void UnmergedPairsAreGitsOwn()
    {
        foreach (var xy in new[] { "DD", "AU", "UD", "UA", "DU", "AA", "UU" })
        {
            Assert.True(BranchSwitch.IsUnmerged(xy));
        }

        Assert.False(BranchSwitch.IsUnmerged(" M"));
        Assert.False(BranchSwitch.IsUnmerged("??"));
    }
}
