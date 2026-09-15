using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The rules of the reference's Skills list and skill editor: how a row is
/// credited, which section it lands in, what each sort order orders by, and
/// what the editor accepts.
/// </summary>
public sealed class SkillListPresentationTests
{
    private static SkillDefinition Skill(
        string name, string source = "user", DateTimeOffset? updated = null) =>
        new(name, "d", "You", "body", $"C:/skills/{name}/SKILL.md",
            updated ?? DateTimeOffset.MinValue, source);

    private static SkillRow Row(
        string name,
        SkillRowKind kind = SkillRowKind.Skill,
        DateTimeOffset? updated = null,
        string? plugin = null,
        int? uses = null,
        SkillCreator creator = SkillCreator.You,
        bool enabled = true) =>
        new(kind, Skill(name), updated, plugin, plugin, uses, creator, enabled);

    [Fact]
    public void CreatorFollowsTheSource_PacksArePartners()
    {
        Assert.Equal(SkillCreator.You, SkillListPresentation.CreatorOf(Skill("mine")));
        Assert.Equal(SkillCreator.You, SkillListPresentation.CreatorOf(Skill("theirs", "project")));
        Assert.Equal(SkillCreator.Anthropic, SkillListPresentation.CreatorOf(Skill("verify", "built-in")));
        Assert.Equal(
            SkillCreator.Anthropic, SkillListPresentation.CreatorOf(Skill("anthropic:pdf", "built-in")));
        Assert.Equal(SkillCreator.Partners, SkillListPresentation.CreatorOf(Skill("vercel:deploy", "built-in")));
        Assert.Equal(SkillCreator.Org, SkillListPresentation.CreatorOf(Skill("devkit:ship", "plugin:devkit")));
    }

    [Fact]
    public void KindAndPluginComeFromTheSource()
    {
        Assert.Equal(SkillRowKind.Skill, SkillListPresentation.KindOf(Skill("mine")));
        Assert.Equal(SkillRowKind.BuiltIn, SkillListPresentation.KindOf(Skill("verify", "built-in")));
        Assert.Equal(SkillRowKind.Plugin, SkillListPresentation.KindOf(Skill("x", "plugin:devkit")));
        Assert.Equal("devkit", SkillListPresentation.PluginOf(Skill("x", "plugin:devkit")));
        Assert.Null(SkillListPresentation.PluginOf(Skill("x", "plugin")));
    }

    [Fact]
    public void APluginRowDropsThePluginPrefixFromItsName()
    {
        var row = new SkillRow(
            SkillRowKind.Plugin, Skill("devkit:ship", "plugin:devkit"), null, "devkit", "devkit", null,
            SkillCreator.Org, true);

        Assert.Equal("ship", row.Name);
    }

    [Fact]
    public void OwnUsesCountOnlyInsideTheNinetyDayWindow()
    {
        var now = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
        var recent = new SkillUsageEntry { UsageCount = 4, LastUsedAt = now.AddDays(-10) };
        var stale = new SkillUsageEntry { UsageCount = 9, LastUsedAt = now.AddDays(-120) };

        Assert.Equal(4, SkillListPresentation.OwnUses(recent, now));
        Assert.Null(SkillListPresentation.OwnUses(stale, now));
        Assert.Null(SkillListPresentation.OwnUses(null, now));
        Assert.Null(SkillListPresentation.OwnUses(new SkillUsageEntry { UsageCount = 0 }, now));
    }

    [Fact]
    public void CreatedByYouTakesTheUsersOwnSkillsBeforeAttentionDoes()
    {
        var rows = new[]
        {
            Row("mine"),
            Row("packaged", SkillRowKind.BuiltIn, creator: SkillCreator.Anthropic, enabled: false),
            Row("plugged", SkillRowKind.Plugin, plugin: "devkit", creator: SkillCreator.Org),
        };

        var sections = SkillListPresentation.Split(rows, row => !row.Enabled);

        Assert.Equal(["mine"], sections.CreatedByYou.Select(r => r.Name));
        Assert.Equal(["packaged"], sections.Attention.Select(r => r.Name));
        Assert.Equal(["plugged"], sections.Main.Select(r => r.Name));
    }

    [Fact]
    public void ASetFacetMustMatch()
    {
        var row = Row("x", SkillRowKind.Plugin, plugin: "devkit", creator: SkillCreator.Org);

        Assert.True(SkillListPresentation.Matches(SkillFilter.None, row));
        Assert.True(SkillListPresentation.Matches(new SkillFilter(SkillCreator.Org, "devkit"), row));
        Assert.False(SkillListPresentation.Matches(new SkillFilter(SkillCreator.You, null), row));
        Assert.False(SkillListPresentation.Matches(new SkillFilter(null, "other"), row));
        Assert.Equal(2, new SkillFilter(SkillCreator.Org, "devkit").ActiveCount);
    }

    [Fact]
    public void APluginRowIsSearchedWithItsPluginName()
    {
        var rows = new[]
        {
            Row("alpha"),
            Row("beta", SkillRowKind.Plugin, plugin: "devkit"),
        };

        Assert.Equal(["beta"], SkillListPresentation.Search(rows, "devkit").Select(r => r.Name));
        Assert.Equal(["alpha"], SkillListPresentation.Search(rows, "ALPHA").Select(r => r.Name));
        Assert.Equal(2, SkillListPresentation.Search(rows, "  ").Count);
    }

    [Fact]
    public void ASortWhoseOptionIsNotOfferedFallsBackToLastEdited()
    {
        Assert.Equal(
            SkillSort.Updated,
            SkillListPresentation.EffectiveSort(SkillSort.Plugin, havePluginRows: false, haveOwnUses: true));
        Assert.Equal(
            SkillSort.Updated,
            SkillListPresentation.EffectiveSort(SkillSort.MostUsedByMe, havePluginRows: true, haveOwnUses: false));
        Assert.Equal(
            SkillSort.Plugin,
            SkillListPresentation.EffectiveSort(SkillSort.Plugin, havePluginRows: true, haveOwnUses: false));
    }

    [Fact]
    public void LastEditedPutsNewestFirstAndUndatedLast()
    {
        var now = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
        var rows = new[]
        {
            Row("undated"),
            Row("older", updated: now.AddDays(-5)),
            Row("newer", updated: now.AddDays(-1)),
        };

        var sorted = rows.OrderBy(r => r, Comparer<SkillRow>.Create(
            (a, b) => SkillListPresentation.Compare(SkillSort.Updated, a, b))).ToList();

        Assert.Equal(["newer", "older", "undated"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void UndatedTiesBreakByKindThenName()
    {
        var rows = new[]
        {
            Row("zeta", SkillRowKind.BuiltIn),
            Row("beta", SkillRowKind.Skill),
            Row("alpha", SkillRowKind.Plugin, plugin: "p"),
        };

        var sorted = rows.OrderBy(r => r, Comparer<SkillRow>.Create(
            (a, b) => SkillListPresentation.Compare(SkillSort.Updated, a, b))).ToList();

        Assert.Equal(["beta", "zeta", "alpha"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void PluginSortGroupsByPluginAndPutsRowsWithoutOneLast()
    {
        var rows = new[]
        {
            Row("solo"),
            Row("b", SkillRowKind.Plugin, plugin: "zeta"),
            Row("a", SkillRowKind.Plugin, plugin: "alpha"),
        };

        var sorted = rows.OrderBy(r => r, Comparer<SkillRow>.Create(
            (a, b) => SkillListPresentation.Compare(SkillSort.Plugin, a, b))).ToList();

        Assert.Equal(["a", "b", "solo"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void MostUsedSortsByCountThenFallsBackToTheDate()
    {
        var now = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
        var rows = new[]
        {
            Row("never"),
            Row("twice", uses: 2, updated: now.AddDays(-9)),
            Row("also-twice", uses: 2, updated: now.AddDays(-1)),
            Row("five", uses: 5),
        };

        var sorted = rows.OrderBy(r => r, Comparer<SkillRow>.Create(
            (a, b) => SkillListPresentation.Compare(SkillSort.MostUsedByMe, a, b))).ToList();

        Assert.Equal(["five", "also-twice", "twice", "never"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void SortOptionsAreGatedTheReferencesWay()
    {
        Assert.Equal(
            ["Last edited", "Name"],
            SkillListPresentation.SortOptions(false, false).Select(o => o.Label));
        Assert.Equal(
            ["Last edited", "Name", "Plugin name", "Most used by me"],
            SkillListPresentation.SortOptions(true, true).Select(o => o.Label));
    }

    [Fact]
    public void CreditSaysByYouOrNamesTheAuthor()
    {
        Assert.Equal("by you", SkillListPresentation.Credit(Row("mine")));
        Assert.Equal(
            "by Anthropic",
            SkillListPresentation.Credit(Row("verify", SkillRowKind.BuiltIn, creator: SkillCreator.Anthropic)));
        Assert.Equal(
            "by devkit",
            SkillListPresentation.Credit(
                Row("x", SkillRowKind.Plugin, plugin: "devkit", creator: SkillCreator.Org)));
    }

    [Fact]
    public void TheAuthorCellIsTheReferencesThreeValues()
    {
        Assert.Equal("You", SkillListPresentation.AuthorCell(Row("a")));
        Assert.Equal(
            "Anthropic",
            SkillListPresentation.AuthorCell(Row("b", creator: SkillCreator.Anthropic)));
        Assert.Equal("Your admin", SkillListPresentation.AuthorCell(Row("c", creator: SkillCreator.Org)));
    }

    [Fact]
    public void RunsLabelSingularizes()
    {
        Assert.Equal("1 run", SkillListPresentation.RunsLabel(1));
        Assert.Equal("2 runs", SkillListPresentation.RunsLabel(2));
        Assert.Equal("1,200 runs", SkillListPresentation.RunsLabel(1200));
    }
}

/// <summary>The editor's validation, sanitizer and frontmatter surgery.</summary>
public sealed class SkillEditorRulesTests
{
    [Theory]
    [InlineData("Weekly Status", "weekly-status")]
    [InlineData("weekly_status", "weekly-status")]
    [InlineData("a!!b", "ab")]
    [InlineData("Ünïcode-ok", "ünïcode-ok")]
    public void TheNameIsSanitizedPerKeystroke(string typed, string expected) =>
        Assert.Equal(expected, SkillEditorRules.SanitizeName(typed));

    [Fact]
    public void TheSanitizedNameIsCutAtSixtyFour()
    {
        var name = SkillEditorRules.SanitizeName(new string('a', 200));

        Assert.Equal(SkillEditorRules.MaxNameLength, name.Length);
    }

    [Fact]
    public void ReservedWordsAreRefusedByName()
    {
        Assert.Equal("claude", SkillEditorRules.ReservedWordIn("my-claude-helper"));
        Assert.Equal("anthropic", SkillEditorRules.ReservedWordIn("Anthropic-tools"));
        Assert.Null(SkillEditorRules.ReservedWordIn("weekly-status"));
        Assert.Equal(
            "Name cannot contain the word “claude”.", SkillEditorRules.ReservedWordError("claude"));
    }

    [Fact]
    public void TheDescriptionCounterAndItsTwoRefusals()
    {
        Assert.False(SkillEditorRules.ShowsCounter(new string('a', 899)));
        Assert.True(SkillEditorRules.ShowsCounter(new string('a', 900)));
        Assert.False(SkillEditorRules.DescriptionTooLong(new string('a', 1024)));
        Assert.True(SkillEditorRules.DescriptionTooLong(new string('a', 1025)));
        Assert.True(SkillEditorRules.DescriptionHasXml("uses <thinking>tags</thinking>"));
        Assert.True(SkillEditorRules.DescriptionHasXml("uses ＜thinking＞"));
        // The reference's test is a bracket pair with something between them, not a
        // parsed tag: "a < b and c > d" is refused there too. A lone bracket is not.
        Assert.True(SkillEditorRules.DescriptionHasXml("a < b and c > d"));
        Assert.False(SkillEditorRules.DescriptionHasXml("a < b"));
        Assert.False(SkillEditorRules.DescriptionHasXml("plain words"));
    }

    [Fact]
    public void SaveIsBlockedUntilTheNameAndDescriptionAreBothUsable()
    {
        Assert.True(SkillEditorRules.NameAndDescriptionInvalid("", "fine"));
        Assert.True(SkillEditorRules.NameAndDescriptionInvalid("claude-thing", "fine"));
        Assert.True(SkillEditorRules.NameAndDescriptionInvalid("ok", "   "));
        Assert.True(SkillEditorRules.NameAndDescriptionInvalid("ok", new string('a', 1025)));
        Assert.True(SkillEditorRules.NameAndDescriptionInvalid("ok", "<b>x</b>"));
        Assert.False(SkillEditorRules.NameAndDescriptionInvalid("ok", "fine"));
    }

    [Fact]
    public void InstructionsThatOpenWithFrontmatterAreCaught()
    {
        Assert.True(SkillEditorRules.StartsWithFrontmatter("---\nname: x\n---\nbody"));
        Assert.True(SkillEditorRules.StartsWithFrontmatter("\n\n---\nname: x\n---\n"));
        Assert.False(SkillEditorRules.StartsWithFrontmatter("Do the thing."));
    }

    [Fact]
    public void ComposeWritesJsonQuotedFrontmatter()
    {
        var md = SkillEditorRules.ComposeSkillMd("weekly", "Says \"hi\"", "Body");

        Assert.Equal("---\nname: \"weekly\"\ndescription: \"Says \\u0022hi\\u0022\"\n---\n\nBody", md);
    }

    [Fact]
    public void TheDescriptionLineIsRewrittenInPlace()
    {
        var md = "---\nname: weekly\ndescription: old\nmodel: opus\n---\n\nBody";

        var updated = SkillEditorRules.WithDescription(md, "new");

        Assert.NotNull(updated);
        Assert.Contains("name: weekly", updated);
        Assert.Contains("description: \"new\"", updated);
        Assert.Contains("model: opus", updated);
        Assert.DoesNotContain("description: old", updated);
    }

    [Fact]
    public void ADescriptionIsAddedWhenTheFrontmatterHasNone()
    {
        var updated = SkillEditorRules.WithDescription("---\nname: weekly\n---\n\nBody", "new");

        Assert.NotNull(updated);
        Assert.Contains("description: \"new\"", updated);
    }

    [Fact]
    public void AFileWithNoFrontmatterCannotTakeADescription() =>
        Assert.Null(SkillEditorRules.WithDescription("Just a body.", "new"));

    [Fact]
    public void TheBodyIsWhatSitsUnderTheFrontmatter()
    {
        Assert.Equal("Body", SkillEditorRules.BodyOf("---\nname: x\n---\n\nBody"));
        Assert.Equal("Just a body.", SkillEditorRules.BodyOf("Just a body."));
    }

    [Fact]
    public void TheVersionLabelSaysWhichVersionIsOnScreen()
    {
        var at = DateTimeOffset.Parse("2026-03-04T00:00:00Z");

        Assert.Equal("Draft", SkillEditorRules.VersionLabel(false, false, null, null));
        Assert.Equal("Unsaved changes", SkillEditorRules.VersionLabel(true, true, 3, at));
        Assert.Equal("Current version", SkillEditorRules.VersionLabel(true, false, null, null));
        Assert.StartsWith("Saved ", SkillEditorRules.VersionLabel(true, false, null, at));
        Assert.StartsWith("v3 · ", SkillEditorRules.VersionLabel(true, false, 3, at));
        Assert.Equal("v3", SkillEditorRules.VersionLabel(true, false, 3, null));
    }
}

/// <summary>The writes the Skills page makes to a skills directory.</summary>
public sealed class SkillLibraryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jarvis-skill-library-" + Guid.NewGuid().ToString("N")[..8]);

    private string Skills => Path.Combine(_root, "skills");

    private string Store => Path.Combine(_root, "versions");

    public SkillLibraryTests() => Directory.CreateDirectory(Skills);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void CreateWritesAFolderSkillBothProductsRead()
    {
        var path = SkillLibrary.Create(Skills, "weekly", "Weekly status", "Do the thing.");

        Assert.Equal(Path.Combine(Skills, "weekly", "SKILL.md"), path);
        var text = File.ReadAllText(path);
        Assert.Contains("name: \"weekly\"", text);
        Assert.Contains("Do the thing.", text);
        Assert.True(SkillLibrary.Exists(Skills, "weekly"));
    }

    [Fact]
    public void SavingAVersionSnapshotsWhatWasThereFirst()
    {
        var path = SkillLibrary.Create(Skills, "weekly", "First", "One.");
        var versions = new SkillVersions(Store);

        var number = SkillLibrary.SaveVersion(path, "weekly", "Second", "Two.", versions);

        Assert.Equal(1, number);
        Assert.Contains("Two.", File.ReadAllText(path));
        Assert.Contains("description: \"Second\"", File.ReadAllText(path));
        var saved = versions.Read("weekly", 1);
        Assert.NotNull(saved);
        Assert.Contains("One.", saved);
        Assert.Equal(1, versions.Head("weekly"));
    }

    [Fact]
    public void VersionsAreListedNewestFirst()
    {
        var versions = new SkillVersions(Store);
        versions.Save("weekly", "v1");
        versions.Save("weekly", "v2");

        var listed = versions.List("weekly");

        Assert.Equal([2, 1], listed.Select(v => v.Version));
        Assert.StartsWith("v2 · ", listed[0].Label);
    }

    [Fact]
    public void DuplicateTakesACopySuffixAndRenamesTheFrontmatter()
    {
        SkillLibrary.Create(Skills, "weekly", "Weekly", "Body");

        var first = SkillLibrary.Duplicate(Skills, "weekly");
        var second = SkillLibrary.Duplicate(Skills, "weekly");

        Assert.Equal("weekly-copy", first);
        Assert.Equal("weekly-copy-2", second);
        Assert.Contains("name: weekly-copy", File.ReadAllText(Path.Combine(Skills, first, "SKILL.md")));
    }

    [Fact]
    public void RenameMovesTheFolderAndTheName()
    {
        SkillLibrary.Create(Skills, "weekly", "Weekly", "Body");

        SkillLibrary.Rename(Skills, "weekly", "monthly");

        Assert.False(SkillLibrary.Exists(Skills, "weekly"));
        Assert.True(SkillLibrary.Exists(Skills, "monthly"));
        Assert.Contains("name: monthly", File.ReadAllText(Path.Combine(Skills, "monthly", "SKILL.md")));
    }

    [Fact]
    public void UploadRefusesAnUnknownExtension()
    {
        var file = Path.Combine(_root, "notes.txt");
        File.WriteAllText(file, "x");

        var outcome = SkillLibrary.Upload(Skills, file, overwrite: false, versions: null);

        Assert.Equal(SkillUploadOutcome.Invalid, outcome.Kind);
        Assert.Equal(
            "Skill files must have a .skill, .zip, or .md file extension.", outcome.Messages.Single());
    }

    [Fact]
    public void UploadRefusesAMarkdownFileWithNoFrontmatter()
    {
        var file = Path.Combine(_root, "weekly.md");
        File.WriteAllText(file, "Just a body.");

        var outcome = SkillLibrary.Upload(Skills, file, overwrite: false, versions: null);

        Assert.Equal(SkillUploadOutcome.Invalid, outcome.Kind);
        Assert.Equal(
            ".md file must contain skill name and description formatted in YAML", outcome.Messages.Single());
    }

    [Fact]
    public void UploadInstallsAMarkdownSkillAsAFolder()
    {
        var file = Path.Combine(_root, "weekly.md");
        File.WriteAllText(file, "---\nname: weekly\ndescription: Weekly status\n---\n\nBody");

        var outcome = SkillLibrary.Upload(Skills, file, overwrite: false, versions: null);

        Assert.Equal(SkillUploadOutcome.Ok, outcome.Kind);
        Assert.Equal("weekly", outcome.SkillName);
        Assert.True(File.Exists(Path.Combine(Skills, "weekly", "SKILL.md")));
    }

    [Fact]
    public void ATakenNameIsAConflictUntilTheUserSaysReplace()
    {
        var file = Path.Combine(_root, "weekly.md");
        File.WriteAllText(file, "---\nname: weekly\ndescription: Weekly status\n---\n\nSecond");
        SkillLibrary.Create(Skills, "weekly", "Weekly", "First");
        var versions = new SkillVersions(Store);

        var conflict = SkillLibrary.Upload(Skills, file, overwrite: false, versions);
        Assert.Equal(SkillUploadOutcome.Conflict, conflict.Kind);

        var replaced = SkillLibrary.Upload(Skills, file, overwrite: true, versions);
        Assert.Equal(SkillUploadOutcome.Ok, replaced.Kind);
        Assert.Contains("Second", File.ReadAllText(Path.Combine(Skills, "weekly", "SKILL.md")));
        Assert.Contains("First", versions.Read("weekly", 1));
    }

    [Fact]
    public void UploadRefusesAReservedName()
    {
        var file = Path.Combine(_root, "claude-helper.md");
        File.WriteAllText(file, "---\nname: claude-helper\ndescription: x\n---\n\nBody");

        var outcome = SkillLibrary.Upload(Skills, file, overwrite: false, versions: null);

        Assert.Equal(SkillUploadOutcome.Invalid, outcome.Kind);
        Assert.Equal("That name is reserved. Choose a different one.", outcome.Messages.Single());
    }

    [Fact]
    public void AZipMustCarryASkillMd()
    {
        var zip = Path.Combine(_root, "empty.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(
                   zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }

        var outcome = SkillLibrary.Upload(Skills, zip, overwrite: false, versions: null);

        Assert.Equal(SkillUploadOutcome.Invalid, outcome.Kind);
        Assert.Equal(".zip or .skill file must include a SKILL.md file", outcome.Messages.Single());
    }

    [Fact]
    public void AZippedSkillFolderInstallsWithItsResources()
    {
        var source = Path.Combine(_root, "src", "weekly");
        Directory.CreateDirectory(Path.Combine(source, "scripts"));
        File.WriteAllText(
            Path.Combine(source, "SKILL.md"), "---\nname: weekly\ndescription: Weekly\n---\n\nBody");
        File.WriteAllText(Path.Combine(source, "scripts", "run.py"), "print()");
        var zip = Path.Combine(_root, "weekly.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(
            Path.Combine(_root, "src"), zip, System.IO.Compression.CompressionLevel.Fastest,
            includeBaseDirectory: false);

        var outcome = SkillLibrary.Upload(Skills, zip, overwrite: false, versions: null);

        Assert.Equal(SkillUploadOutcome.Ok, outcome.Kind);
        Assert.True(File.Exists(Path.Combine(Skills, "weekly", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Skills, "weekly", "scripts", "run.py")));
    }

    [Fact]
    public void FilesListsSkillMdFirstAndSpotsTheExtraOnes()
    {
        var path = SkillLibrary.Create(Skills, "weekly", "Weekly", "Body");
        Assert.False(SkillLibrary.HasExtraFiles(path));

        File.WriteAllText(Path.Combine(Skills, "weekly", "notes.md"), "x");

        Assert.Equal(["SKILL.md", "notes.md"], SkillLibrary.Files(path));
        Assert.True(SkillLibrary.HasExtraFiles(path));
    }
}
