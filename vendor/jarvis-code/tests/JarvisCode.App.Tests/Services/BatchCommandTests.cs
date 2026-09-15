using System.IO;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// /batch, against the reference CLI 2.1.257's own <c>kt()</c> registration and
/// its two refusals: an empty instruction is answered before the repository is
/// looked at, and a working directory outside a repository is refused before the
/// prompt is built.
/// </summary>
public sealed class BatchCommandTests
{
    [Fact]
    public void An_empty_instruction_is_answered_with_the_examples()
    {
        Assert.Equal(BatchCommand.NoInstruction, BatchCommand.Compose("", insideGitRepository: true));
        Assert.Equal(BatchCommand.NoInstruction, BatchCommand.Compose("   ", insideGitRepository: false));
        Assert.Contains("/batch migrate from react to vue", BatchCommand.NoInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_a_repository_the_instruction_is_refused_rather_than_orchestrated()
    {
        var composed = BatchCommand.Compose("rename every logger", insideGitRepository: false);
        Assert.Equal(BatchCommand.NotAGitRepository, composed);
        Assert.False(BatchCommand.IsPrompt(composed));
    }

    [Fact]
    public void The_prompt_carries_the_instruction_the_unit_range_and_the_worker_template()
    {
        var composed = BatchCommand.Compose("  replace lodash  ", insideGitRepository: true);
        Assert.True(BatchCommand.IsPrompt(composed));
        Assert.Contains("## User Instruction\n\nreplace lodash\n", composed, StringComparison.Ordinal);
        Assert.Contains("Break the work into 5–30 self-contained units.", composed, StringComparison.Ordinal);
        Assert.Contains("closer to 5; hundreds of files → closer to 30.", composed, StringComparison.Ordinal);
        Assert.Contains(BatchCommand.WorkerInstructions, composed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_names_this_builds_own_tools()
    {
        var composed = BatchCommand.Compose("x", insideGitRepository: true);
        Assert.Contains("Call the `EnterPlanMode` tool now", composed, StringComparison.Ordinal);
        Assert.Contains("Call `ExitPlanMode` to present the plan", composed, StringComparison.Ordinal);
        Assert.Contains("using the `Agent` tool", composed, StringComparison.Ordinal);
        Assert.Contains("use the `AskUserQuestion` tool", composed, StringComparison.Ordinal);
        Assert.Contains("Invoke the `Skill` tool with `skill: \"code-review\"`",
            BatchCommand.WorkerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repository_is_found_from_a_subdirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-batch-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "src", "deep");
        Directory.CreateDirectory(nested);
        try
        {
            Assert.False(BatchCommand.InsideGitRepository(nested));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Assert.True(BatchCommand.InsideGitRepository(nested));
            Assert.True(BatchCommand.InsideGitRepository(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_linked_worktrees_git_file_counts_as_a_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../.git/worktrees/x\n");
            Assert.True(BatchCommand.InsideGitRepository(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
