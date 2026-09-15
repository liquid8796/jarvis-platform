using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public class JumpListShapeLabelTests
{
    [Fact]
    public void CollapsesWhitespaceAndTrims() =>
        Assert.Equal("a b c", JumpList.ShapeLabel("  a \t b\n\nc  "));

    [Fact]
    public void KeepsALabelAtTheCapWhole()
    {
        var label = new string('x', JumpList.MaxLabelLength);

        Assert.Equal(label, JumpList.ShapeLabel(label));
    }

    [Fact]
    public void CutsALongerLabelToFiftyNineCharactersPlusAnEllipsis()
    {
        var shaped = JumpList.ShapeLabel(new string('x', JumpList.MaxLabelLength + 1));

        Assert.Equal(new string('x', JumpList.MaxLabelLength - 1) + "…", shaped);
        Assert.Equal(JumpList.MaxLabelLength, shaped.Length);
    }

    [Fact]
    public void NeverCutsThroughACharacter()
    {
        // Sixty astral characters: cutting by UTF-16 units would split a surrogate
        // pair and leave a lone half in the label the OS is handed.
        var shaped = JumpList.ShapeLabel(string.Concat(Enumerable.Repeat("😀", 61)));

        Assert.EndsWith("…", shaped, StringComparison.Ordinal);
        Assert.Equal(JumpList.MaxLabelLength - 1, shaped.EnumerateRunes().Count() - 1);

        // No half of a surrogate pair may be left standing on its own.
        for (var i = 0; i < shaped.Length; i++)
        {
            if (char.IsHighSurrogate(shaped[i]))
            {
                Assert.True(i + 1 < shaped.Length && char.IsLowSurrogate(shaped[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(shaped[i]));
            }
        }
    }

    [Fact]
    public void StripsBidiOverridesFromASessionTitle()
    {
        // A folder named with a right-to-left override would otherwise reorder the
        // row it lands in; the reference sanitizes before it truncates.
        var shaped = JumpList.ShapeLabel("repo\u202Egnp.exe");

        Assert.Equal("repognp.exe", shaped);
    }
}

public class JumpListModelTests
{
    private static JumpListSession Session(
        string id,
        string title = "",
        string cwd = "D:\\work\\repo",
        int minutesAgo = 0,
        bool archived = false,
        bool waiting = false,
        bool folderExists = true) =>
        new(id, title, cwd, DateTimeOffset.UnixEpoch.AddMinutes(1000 - minutesAgo),
            archived, waiting, folderExists);

    [Fact]
    public void WithNoSessionsThereIsNothingToContinueAndOnlyTheTasksSection()
    {
        var model = JumpList.Build([], "jump_list");

        Assert.Null(model.ContinueLast);
        Assert.Empty(model.CodeSessionIn);
        Assert.Empty(model.Waiting);

        var categories = JumpList.Categories(model);
        var only = Assert.Single(categories);
        Assert.Null(only.Name);
        Assert.Equal(
            [JumpList.NewChatLabel, JumpList.NewCodeSessionLabel],
            only.Items.Select(static i => i.Label));
    }

    [Fact]
    public void ContinueNamesTheMostRecentSession()
    {
        var model = JumpList.Build(
            [
                Session("local_a", "Older work", minutesAgo: 60),
                Session("local_b", "Newest work", minutesAgo: 1),
            ],
            "jump_list");

        Assert.Equal("Continue “Newest work”", model.ContinueLast!.Value.Label);
        Assert.Equal(
            "jarvis-code://code/continue?session=local_b&source=jump_list",
            model.ContinueLast.Value.Url);
    }

    [Fact]
    public void ContinueFallsBackToTheFolderNameThenToLastSession()
    {
        Assert.Equal(
            "Continue “repo”",
            JumpList.Build([Session("local_a")], "jump_list").ContinueLast!.Value.Label);

        Assert.Equal(
            "Continue “Last Session”",
            JumpList.Build([Session("local_a", cwd: "")], "jump_list").ContinueLast!.Value.Label);
    }

    [Fact]
    public void ArchivedSessionsAreLeftOutEntirely()
    {
        var model = JumpList.Build(
            [Session("local_a", "Archived", minutesAgo: 1, archived: true, waiting: true)],
            "jump_list");

        Assert.Null(model.ContinueLast);
        Assert.Empty(model.Waiting);
        Assert.Empty(model.CodeSessionIn);
    }

    [Fact]
    public void RecentFoldersAreDedupedAndCappedAtThree()
    {
        var model = JumpList.Build(
            [
                Session("local_a", cwd: "D:\\work\\one", minutesAgo: 1),
                Session("local_b", cwd: "D:\\work\\one", minutesAgo: 2),
                Session("local_c", cwd: "D:\\work\\two", minutesAgo: 3),
                Session("local_d", cwd: "D:\\work\\three", minutesAgo: 4),
                Session("local_e", cwd: "D:\\work\\four", minutesAgo: 5),
            ],
            "jump_list");

        Assert.Equal(
            ["one", "two", "three"],
            model.CodeSessionIn.Select(static e => e.Label));
        Assert.Equal(
            "jarvis-code://code/new?folder=D%3A%5Cwork%5Cone&source=jump_list",
            model.CodeSessionIn[0].Url);
    }

    [Fact]
    public void AFolderThatIsGoneGetsNoRow()
    {
        var model = JumpList.Build(
            [Session("local_a", cwd: "D:\\work\\gone", folderExists: false)],
            "jump_list");

        Assert.Empty(model.CodeSessionIn);
    }

    [Fact]
    public void WaitingSessionsComeOldestFirstAndAreCappedAtFive()
    {
        var sessions = Enumerable.Range(1, 7)
            .Select(i => Session($"local_{i}", $"S{i}", minutesAgo: i, waiting: true))
            .ToList();

        var model = JumpList.Build(sessions, "jump_list");

        // minutesAgo 7 is the oldest activity, so it has waited longest.
        Assert.Equal(["S7", "S6", "S5", "S4", "S3"], model.Waiting.Select(static e => e.Label));
        Assert.Equal(
            "jarvis-code://code/needs-input?session=local_7&source=jump_list",
            model.Waiting[0].Url);
    }

    [Fact]
    public void WaitingRowsFallBackToTheFolderThenToTheGenericLabel()
    {
        var model = JumpList.Build(
            [
                Session("local_a", cwd: "D:\\work\\repo", minutesAgo: 2, waiting: true),
                Session("local_b", cwd: "", minutesAgo: 1, waiting: true),
            ],
            "jump_list");

        Assert.Equal(["repo", JumpList.CodeSessionLabel], model.Waiting.Select(static e => e.Label));
    }

    [Fact]
    public void CategoriesAreWaitingThenFoldersThenTasks()
    {
        var model = JumpList.Build(
            [
                Session("local_a", "Waiting one", cwd: "D:\\work\\one", minutesAgo: 5, waiting: true),
                Session("local_b", "Recent", cwd: "D:\\work\\two", minutesAgo: 1),
            ],
            "jump_list");

        var categories = JumpList.Categories(model);

        Assert.Equal(
            [JumpList.NeedsYourInputLabel, JumpList.NewCodeSessionLabel, null],
            categories.Select(static c => c.Name));
        Assert.Equal(3, categories[^1].Items.Count);
    }

    [Fact]
    public void RowsTheUserRemovedAreNotOffered()
    {
        var model = JumpList.Build(
            [Session("local_a", "Waiting", cwd: "D:\\work\\one", minutesAgo: 5, waiting: true)],
            "jump_list");
        var removed = new HashSet<string>(
            [model.Waiting[0].Url, model.CodeSessionIn[0].Url], StringComparer.Ordinal);

        var categories = JumpList.Categories(model, removed);

        // Both custom categories are gone; the Tasks section always stands.
        var only = Assert.Single(categories);
        Assert.Null(only.Name);
    }

    [Fact]
    public void EveryUrlItProducesRoutesBack()
    {
        var model = JumpList.Build(
            [Session("local_a", "Waiting", cwd: "D:\\work\\one", minutesAgo: 5, waiting: true)],
            "jump_list");

        var kinds = JumpList.Categories(model)
            .SelectMany(static c => c.Items)
            .Select(static i => DeepLinks.Parse(i.Url).Kind)
            .ToList();

        Assert.DoesNotContain(DeepLinkKind.Unrecognized, kinds);
        Assert.Contains(DeepLinkKind.NeedsInput, kinds);
        Assert.Contains(DeepLinkKind.NewChat, kinds);
        Assert.Contains(DeepLinkKind.ContinueCodeSession, kinds);
    }
}
