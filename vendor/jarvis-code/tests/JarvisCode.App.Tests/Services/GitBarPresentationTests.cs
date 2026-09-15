using System.Text.Json;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The PR bar's state machine, checked branch by branch against the reference's own
/// (`yt`/`Lt`/`Tt`/`Ot` in the ccd chunk `ca80fca8d` and the CI helpers beside them).
/// </summary>
public sealed class GitBarPresentationTests
{
    private static GitBarInput Repo(bool hasGithub = true) => new()
    {
        IsGitRepo = true,
        HasGithubRepo = hasGithub,
        LocalOnly = !hasGithub,
        RepoName = "jarvis-code",
        BranchName = "feature",
        BaseBranch = "master",
        HasChanges = true,
        Added = 4,
        Removed = 1,
    };

    [Fact]
    public void No_pr_and_no_github_remote_is_commit_mode()
    {
        var row = GitBarPresentation.Resolve(Repo(hasGithub: false));
        Assert.Equal(PrMode.Commit, row.Mode);
        Assert.Equal("Commit changes", row.ButtonLabel);
    }

    [Theory]
    [InlineData("create", PrMode.Create, "Create PR")]
    [InlineData("draft", PrMode.Draft, "Create draft PR")]
    [InlineData("compose", PrMode.Compose, "Manually create PR")]
    public void The_stored_create_mode_names_the_button(string mode, PrMode expected, string label)
    {
        var row = GitBarPresentation.Resolve(Repo() with { CreateMode = mode });
        Assert.Equal(expected, row.Mode);
        Assert.Equal(label, row.ButtonLabel);
    }

    [Fact]
    public void Compose_falls_back_to_create_without_a_repository()
    {
        Assert.Equal("create", GitBarPresentation.EffectiveCreateMode("compose", hasRepo: false));
        Assert.Equal("compose", GitBarPresentation.EffectiveCreateMode("compose", hasRepo: true));
        Assert.Equal("draft", GitBarPresentation.EffectiveCreateMode("draft", hasRepo: false));
        Assert.Equal("create", GitBarPresentation.EffectiveCreateMode("nonsense", hasRepo: true));
    }

    [Fact]
    public void An_open_pr_shows_view_and_no_button()
    {
        var row = GitBarPresentation.Resolve(Repo() with
        {
            Pr = new PrInfo(12, "https://example.test/pr/12", PrDisplayState.Open),
        });
        Assert.Equal(PrMode.View, row.Mode);
        Assert.False(row.ShowButton);
        Assert.Equal("Open", row.StateLabel);
    }

    [Fact]
    public void Merged_and_queued_rows_are_accented_and_hide_the_trailing_cluster()
    {
        var merged = GitBarPresentation.Resolve(Repo() with
        {
            Pr = new PrInfo(3, "u", PrDisplayState.Merged),
            Behind = 2,
        });
        Assert.Equal(GitBarAccent.Purple, merged.Accent);
        Assert.False(merged.ShowDiff);
        Assert.Equal(0, merged.Behind);
        Assert.Null(merged.Ci);
        Assert.Equal("Merged", merged.ButtonLabel);

        var queued = GitBarPresentation.Resolve(Repo() with
        {
            Pr = new PrInfo(3, "u", PrDisplayState.Queued),
        });
        Assert.Equal(GitBarAccent.Yellow, queued.Accent);
        Assert.Equal("Queued", queued.ButtonLabel);
    }

    [Theory]
    [InlineData(false, false, false, true, null)]
    [InlineData(true, false, false, false, "Add a GitHub remote to create a pull request")]
    public void The_disabled_reason_follows_the_reference_order(
        bool noRemote, bool onBase, bool notPushed, bool hasChanges, string? expected)
    {
        var input = Repo(hasGithub: !noRemote) with
        {
            OnBaseBranch = onBase,
            BranchNotPushed = notPushed,
            HasChanges = hasChanges,
            LocalOnly = false,
        };
        Assert.Equal(expected, GitBarPresentation.DisabledReason(input, GitBarPresentation.Mode(input)));
    }

    [Fact]
    public void No_changes_reads_differently_in_commit_mode()
    {
        var commit = Repo(hasGithub: false) with { HasChanges = false, Added = 0, Removed = 0 };
        Assert.Equal("No changes to commit", GitBarPresentation.DisabledReason(commit, PrMode.Commit));

        var create = Repo() with { HasChanges = false, Added = 0, Removed = 0 };
        Assert.Equal("No changes to create a PR from", GitBarPresentation.DisabledReason(create, PrMode.Create));
    }

    [Fact]
    public void A_running_turn_disables_the_button_with_its_own_reason()
    {
        var input = Repo() with { IsResponding = true };
        Assert.Equal("Jarvis is working — wait for the turn to finish",
            GitBarPresentation.DisabledReason(input, PrMode.Create));
        Assert.True(GitBarPresentation.Resolve(input).ButtonDisabled);
    }

    [Theory]
    [InlineData(0, 0, 0, CiStatus.Unavailable)]
    [InlineData(1, 1, 0, CiStatus.Failed)]
    [InlineData(3, 0, 0, CiStatus.Passed)]
    [InlineData(0, 0, 2, CiStatus.Pending)]
    public void The_ci_dot_follows_the_counts(int passed, int failed, int pending, CiStatus expected)
    {
        var pr = new PrInfo(1, "u", PrDisplayState.Open, Checks: new CiCounts(passed, failed, pending));
        Assert.Equal(expected, GitBarPresentation.CiState(pr));
    }

    [Fact]
    public void Checks_that_all_skipped_read_as_skipped_and_a_settled_pr_as_unavailable()
    {
        // Every check finished and none of them passed: the reference's "skipped".
        var skipped = new PrInfo(1, "u", PrDisplayState.Open, Checks: new CiCounts(0, 0, 0, 2));
        Assert.Equal(CiStatus.Skipped, GitBarPresentation.CiState(skipped));

        var merged = new PrInfo(1, "u", PrDisplayState.Merged);
        Assert.Equal(CiStatus.Unavailable, GitBarPresentation.CiState(merged));
        Assert.Equal(CiStatus.Loading, GitBarPresentation.CiState(new PrInfo(1, "u", PrDisplayState.Open)));
    }

    [Fact]
    public void The_ci_label_carries_the_count_in_the_reference_plural_forms()
    {
        Assert.Equal("CI: 1 check failing",
            GitBarPresentation.CiLabel(CiStatus.Failed, new CiCounts(0, 1, 0)));
        Assert.Equal("CI: 2 checks failing",
            GitBarPresentation.CiLabel(CiStatus.Failed, new CiCounts(0, 2, 0)));
        Assert.Equal("CI: all checks passed", GitBarPresentation.CiLabel(CiStatus.Passed, new CiCounts(2, 0, 0)));
        Assert.Equal("CI: status unavailable", GitBarPresentation.CiLabel(CiStatus.Unavailable));
    }

    [Fact]
    public void A_long_branch_keeps_its_last_ten_characters_whole()
    {
        Assert.Null(GitBarPresentation.SplitBranch("main"));
        var split = GitBarPresentation.SplitBranch("feature/a-very-long-branch-name");
        Assert.NotNull(split);
        Assert.Equal("ranch-name", split!.Value.Tail);
        Assert.Equal("feature/a-very-long-b", split.Value.Head);
    }

    [Fact]
    public void The_bar_hides_when_there_is_no_pr_and_nothing_changed()
    {
        var quiet = Repo() with { HasChanges = false, Added = 0, Removed = 0 };
        Assert.False(GitBarPresentation.Resolve(quiet).Visible);
        Assert.False(GitBarPresentation.Resolve(Repo() with { Dismissed = true }).Visible);
        Assert.False(GitBarPresentation.Resolve(new GitBarInput { IsGitRepo = false }).Visible);
    }

    [Fact]
    public void Gh_json_becomes_the_state_the_icon_reads()
    {
        const string json = """
            {
              "number": 7, "url": "https://example.test/pr/7", "state": "OPEN",
              "isDraft": false, "mergeable": "CONFLICTING", "reviewDecision": null,
              "baseRefName": "master",
              "statusCheckRollup": [
                {"__typename": "CheckRun", "status": "COMPLETED", "conclusion": "SUCCESS"},
                {"__typename": "CheckRun", "status": "IN_PROGRESS"},
                {"__typename": "StatusContext", "state": "FAILURE"}
              ]
            }
            """;
        using var document = JsonDocument.Parse(json);
        var pr = GitStatusProbe.FromJson(document.RootElement);
        Assert.NotNull(pr);
        Assert.Equal(7, pr!.Number);
        Assert.Equal(PrDisplayState.Conflicting, pr.State);
        Assert.Equal(new CiCounts(1, 1, 1), pr.Checks);
    }

    [Theory]
    [InlineData("MERGED", false, null, null, PrDisplayState.Merged)]
    [InlineData("CLOSED", false, null, null, PrDisplayState.Closed)]
    [InlineData("OPEN", true, null, null, PrDisplayState.Draft)]
    [InlineData("OPEN", false, null, "CHANGES_REQUESTED", PrDisplayState.ChangesRequested)]
    [InlineData("OPEN", false, "CONFLICTING", null, PrDisplayState.Conflicting)]
    [InlineData("OPEN", false, "MERGEABLE", "APPROVED", PrDisplayState.Approved)]
    [InlineData("OPEN", false, "MERGEABLE", null, PrDisplayState.Open)]
    public void The_display_state_widens_the_raw_github_state(
        string state, bool draft, string? mergeable, string? review, PrDisplayState expected) =>
        Assert.Equal(expected, GitStatusProbe.DisplayState(state, draft, mergeable, review));

    [Theory]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("/some/local/path", null)]
    public void A_remote_url_normalises_to_https(string remote, string? expected) =>
        Assert.Equal(expected, GitStatusProbe.NormalizeRemote(remote));
}
