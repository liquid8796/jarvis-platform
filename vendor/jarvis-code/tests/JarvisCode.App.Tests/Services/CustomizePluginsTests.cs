using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;

namespace JarvisCode.App.Tests.Services;

/// <summary>The install-scope menu and the roots it names.</summary>
public sealed class InstallScopesTests
{
    [Fact]
    public void TheThreeScopesAreOfferedInTheReferencesOrder() =>
        Assert.Equal(
            ["Install for me", "Install for project (shared)", "Install for project (personal)"],
            InstallScopes.All.Select(InstallScopes.Label));

    [Fact]
    public void EachScopeNamesTheFolderThisAppReallyWritesTo()
    {
        Assert.Equal(
            "Installs to C:/profile/skills — available in all projects on this machine.",
            InstallScopes.Description(InstallScope.User, "C:/profile/skills"));
        Assert.Equal(
            "Installs to .jarvis/ — shared with your team via git.",
            InstallScopes.Description(InstallScope.Project, "C:/profile/skills"));
        Assert.Equal(
            "Installs to .jarvis.local/ — personal, gitignored.",
            InstallScopes.Description(InstallScope.Local, "C:/profile/skills"));
    }

    [Fact]
    public void OnlyAProjectInstallPrintsAScopeCell()
    {
        Assert.Null(InstallScopes.ScopeCell(PluginInfo.UserScope));
        Assert.Equal("Project (shared)", InstallScopes.ScopeCell(PluginInfo.ProjectScope));
        Assert.Equal("Project (personal)", InstallScopes.ScopeCell(PluginInfo.LocalScope));
    }

    [Fact]
    public void TheScopeRootsSitUnderTheProjectsTwoFolders()
    {
        var paths = ProfilePaths.Create("tests");

        Assert.Equal(
            Path.Combine("C:", "repo", ".jarvis", "skills"),
            InstallScopes.SkillsRoot(InstallScope.Project, paths, Path.Combine("C:", "repo")));
        Assert.Equal(
            Path.Combine("C:", "repo", ".jarvis.local", "plugins"),
            InstallScopes.PluginsRoot(InstallScope.Local, paths, Path.Combine("C:", "repo")));
        Assert.Equal(
            paths.PluginsDirectory,
            InstallScopes.PluginsRoot(InstallScope.User, paths, Path.Combine("C:", "repo")));
    }

    [Fact]
    public void TheUserRootIsReadBeforeTheProjectsTwo()
    {
        var paths = ProfilePaths.Create("tests");

        var roots = PluginRoots.For(paths, Path.Combine("C:", "repo"));

        Assert.Equal(
            [PluginInfo.UserScope, PluginInfo.ProjectScope, PluginInfo.LocalScope],
            roots.Select(r => r.Scope));
        Assert.Single(PluginRoots.For(paths, cwd: null));
    }
}

/// <summary>Reading a plugin's manifest, in both of the places it comes in.</summary>
public sealed class PluginManifestsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jarvis-plugin-manifest-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginManifestsTests() => Directory.CreateDirectory(_root);

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
    public void TheNestedManifestIsPreferredOverTheBareOne()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(_root, ".claude-plugin", "plugin.json"), """{"name":"nested"}""");
        File.WriteAllText(Path.Combine(_root, "plugin.json"), """{"name":"bare"}""");

        Assert.Equal("nested", PluginManifests.Read(_root)!.Name);
    }

    [Fact]
    public void AnAuthorComesAsAStringOrAsAnObject()
    {
        var text = PluginManifests.Parse(JsonNode.Parse("""{"author":"Ada"}""")!.AsObject());
        var nested = PluginManifests.Parse(JsonNode.Parse("""{"author":{"name":"Ada"}}""")!.AsObject());

        Assert.Equal("Ada", text.Author);
        Assert.Equal("Ada", nested.Author);
    }

    [Fact]
    public void KeywordsFallBackToTags()
    {
        var manifest = PluginManifests.Parse(JsonNode.Parse("""{"tags":["a","b"]}""")!.AsObject());

        Assert.Equal(["a", "b"], manifest.Keywords);
    }

    [Fact]
    public void TheStatusTellsMissingFromUnreadable()
    {
        Assert.Equal("missing", PluginManifests.Status(_root).Status);

        File.WriteAllText(Path.Combine(_root, "plugin.json"), "{ not json");
        Assert.Equal("invalid", PluginManifests.Status(_root).Status);

        File.WriteAllText(Path.Combine(_root, "plugin.json"), """{"name":"ok"}""");
        Assert.Equal("ok", PluginManifests.Status(_root).Status);
    }
}

/// <summary>The Plugins list's filter, sections and sort.</summary>
public sealed class PluginListPresentationTests
{
    private static PluginRow Row(
        string name,
        bool enabled = true,
        DateTimeOffset? updated = null,
        int? uses = null,
        string scope = PluginInfo.UserScope,
        string? displayName = null) =>
        new(
            new PluginInfo(name, $"C:/plugins/{name}", 0, 0, 0, false, false) { Scope = scope, Root = "C:/plugins" },
            displayName is null ? null : new PluginManifest(displayName, null, null, null, null, [], null),
            updated, enabled, [], uses, SkillCreator.You, null);

    [Fact]
    public void ShowKeepsWhatTheFacetNames()
    {
        var rows = new[] { Row("on"), Row("off", enabled: false) };

        Assert.Equal(2, PluginListPresentation.Show(rows, PluginShow.All).Count);
        Assert.Equal(["on"], PluginListPresentation.Show(rows, PluginShow.Enabled).Select(r => r.Name));
        Assert.Equal(["off"], PluginListPresentation.Show(rows, PluginShow.Disabled).Select(r => r.Name));
    }

    [Fact]
    public void AttentionComesOutFirst()
    {
        var rows = new[] { Row("fine"), Row("broken", enabled: false) };

        var sections = PluginListPresentation.Split(rows, r => !r.Enabled);

        Assert.Equal(["broken"], sections.Attention.Select(r => r.Name));
        Assert.Equal(["fine"], sections.Main.Select(r => r.Name));
        Assert.Empty(sections.Shared);
    }

    [Fact]
    public void MostUsedIsOnlyOfferedOnceSomethingHasBeenUsed()
    {
        Assert.Equal(["Last edited", "Name"], PluginListPresentation.SortOptions(false).Select(o => o.Label));
        Assert.Equal(
            ["Last edited", "Name", "Most used by me"], PluginListPresentation.SortOptions(true).Select(o => o.Label));
        Assert.Equal(
            PluginSort.Updated, PluginListPresentation.EffectiveSort(PluginSort.MostUsedByMe, haveOwnUses: false));
    }

    [Fact]
    public void LastEditedPutsNewestFirstAndUndatedLast()
    {
        var now = DateTimeOffset.Parse("2026-09-02T00:00:00Z");
        var rows = new[] { Row("undated"), Row("older", updated: now.AddDays(-3)), Row("newer", updated: now) };

        var sorted = rows.OrderBy(r => r, Comparer<PluginRow>.Create(
            (a, b) => PluginListPresentation.Compare(PluginSort.Updated, a, b))).ToList();

        Assert.Equal(["newer", "older", "undated"], sorted.Select(r => r.Name));
    }

    [Fact]
    public void ARowIsFoundByItsDisplayNameAndItsScope()
    {
        var rows = new[]
        {
            Row("alpha", displayName: "Alpha Tools"),
            Row("beta", scope: PluginInfo.ProjectScope),
        };

        Assert.Equal(["alpha"], PluginListPresentation.Search(rows, "tools").Select(r => r.Name));
        Assert.Equal(["beta"], PluginListPresentation.Search(rows, "shared").Select(r => r.Name));
    }

    [Fact]
    public void SkillCountLabelSingularizes()
    {
        Assert.Equal("1 skill", PluginListPresentation.SkillCountLabel(1));
        Assert.Equal("3 skills", PluginListPresentation.SkillCountLabel(3));
    }

    [Fact]
    public void AMarketplaceNameDecidesWhoAPluginIsCreditedTo()
    {
        Assert.Equal(SkillCreator.You, PluginListPresentation.CreatorOf(null));
        Assert.Equal(
            SkillCreator.Anthropic,
            PluginListPresentation.CreatorOf(new InstalledPluginRecord("anthropic-skills", null, default, null)));
        Assert.Equal(
            SkillCreator.Org,
            PluginListPresentation.CreatorOf(new InstalledPluginRecord("acme", null, default, null)));
    }
}

/// <summary>Uploading a plugin archive, and the enabled switch behind the rows.</summary>
public sealed class PluginLibraryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jarvis-plugin-library-" + Guid.NewGuid().ToString("N")[..8]);

    private string Plugins => Path.Combine(_root, "plugins");

    public PluginLibraryTests() => Directory.CreateDirectory(Plugins);

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

    private string Zip(string name, Action<string> build)
    {
        var source = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(source);
        build(source);
        var zip = Path.Combine(_root, name + ".zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(
            Path.Combine(_root, "src"), zip, System.IO.Compression.CompressionLevel.Fastest,
            includeBaseDirectory: false);
        return zip;
    }

    [Fact]
    public void AnArchiveWithNeitherAManifestNorASkillIsRefusedWithTheReferencesRule()
    {
        var zip = Zip("nothing", source => File.WriteAllText(Path.Combine(source, "readme.txt"), "x"));

        var outcome = PluginLibrary.Upload(zip, Plugins, overwrite: false);

        Assert.Equal(PluginUploadOutcome.Invalid, outcome.Kind);
        Assert.Equal(PluginLibrary.ManifestRule, outcome.Message);
    }

    [Fact]
    public void AManifestArchiveInstallsUnderItsDeclaredName()
    {
        var zip = Zip("devkit", source =>
        {
            Directory.CreateDirectory(Path.Combine(source, ".claude-plugin"));
            File.WriteAllText(
                Path.Combine(source, ".claude-plugin", "plugin.json"), """{"name":"devkit","version":"1.2.0"}""");
        });

        var outcome = PluginLibrary.Upload(zip, Plugins, overwrite: false);

        Assert.Equal(PluginUploadOutcome.Ok, outcome.Kind);
        Assert.Equal("devkit", outcome.PluginName);
        Assert.True(File.Exists(Path.Combine(Plugins, "devkit", ".claude-plugin", "plugin.json")));
        Assert.NotNull(new InstalledPluginsIndex(Plugins).Get("devkit"));
    }

    [Fact]
    public void ATopLevelSkillMdBecomesAOneSkillPlugin()
    {
        var zip = Zip("solo", source =>
            File.WriteAllText(
                Path.Combine(source, "SKILL.md"), "---\nname: solo\ndescription: One skill\n---\n\nBody"));

        var outcome = PluginLibrary.Upload(zip, Plugins, overwrite: false);

        Assert.Equal(PluginUploadOutcome.Ok, outcome.Kind);
        Assert.True(File.Exists(Path.Combine(Plugins, "solo", "skills", "solo", "SKILL.md")));
    }

    [Fact]
    public void ATakenNameIsAConflictUntilOverwriteIsAsked()
    {
        var zip = Zip("devkit", source =>
            File.WriteAllText(Path.Combine(source, "plugin.json"), """{"name":"devkit"}"""));
        Assert.Equal(PluginUploadOutcome.Ok, PluginLibrary.Upload(zip, Plugins, overwrite: false).Kind);

        Assert.Equal(PluginUploadOutcome.Conflict, PluginLibrary.Upload(zip, Plugins, overwrite: false).Kind);
        Assert.Equal(PluginUploadOutcome.Ok, PluginLibrary.Upload(zip, Plugins, overwrite: true).Kind);
    }

    [Fact]
    public void TheEnabledSwitchIsKeyedByScopeAndName()
    {
        var store = new UiSettingsStore(Path.Combine(_root, "ui-settings.json"));
        var user = new PluginInfo("devkit", "d", 0, 0, 0, false, false) { Scope = PluginInfo.UserScope };
        var project = new PluginInfo("devkit", "d", 0, 0, 0, false, false) { Scope = PluginInfo.ProjectScope };

        PluginLibrary.SetEnabled(store, user, false);

        Assert.False(PluginLibrary.IsEnabled(store.Current, user));
        Assert.True(PluginLibrary.IsEnabled(store.Current, project));

        PluginLibrary.SetEnabled(store, user, true);
        Assert.True(PluginLibrary.IsEnabled(store.Current, user));
        Assert.Empty(store.Current.DisabledPlugins);
    }

    [Fact]
    public void ADisabledPluginContributesNothingToATurn()
    {
        Directory.CreateDirectory(Path.Combine(Plugins, "devkit", "skills", "ship"));
        File.WriteAllText(
            Path.Combine(Plugins, "devkit", "skills", "ship", "SKILL.md"),
            "---\nname: ship\ndescription: Ships\n---\n\nBody");
        var paths = ProfilePaths.Create(null);
        var ui = new UiSettings();

        var all = JarvisCode.Core.Customization.Plugins.LoadAll([(Plugins, PluginInfo.UserScope)]);
        Assert.Single(all.Installed);
        Assert.Single(all.Skills);

        ui.DisabledPlugins.Add($"{PluginInfo.UserScope}:devkit");
        var active = JarvisCode.Core.Customization.Plugins.LoadAll(
            [(Plugins, PluginInfo.UserScope)], plugin => PluginLibrary.IsEnabled(ui, plugin));

        Assert.Empty(active.Installed);
        Assert.Empty(active.Skills);
        Assert.NotNull(paths);
    }

    [Fact]
    public void TheFirstRootThatCarriesANameWins()
    {
        var project = Path.Combine(_root, "project-plugins");
        foreach (var (root, marker) in new[] { (Plugins, "user"), (project, "project") })
        {
            Directory.CreateDirectory(Path.Combine(root, "devkit"));
            File.WriteAllText(Path.Combine(root, "devkit", "plugin.json"), $$"""{"name":"{{marker}}"}""");
        }

        var content = JarvisCode.Core.Customization.Plugins.LoadAll(
            [(Plugins, PluginInfo.UserScope), (project, PluginInfo.ProjectScope)]);

        var plugin = Assert.Single(content.Installed);
        Assert.Equal(PluginInfo.UserScope, plugin.Scope);
    }
}

/// <summary>What a git marketplace's sync answered.</summary>
public sealed class MarketplaceSyncTests
{
    [Fact]
    public void GitOutputPicksOneOfTheReferencesFourReasons()
    {
        Assert.Equal(
            MarketplaceSync.AccessFailed, MarketplaceSync.FailureReason("ERROR: Repository not found."));
        Assert.Equal(
            MarketplaceSync.AccessFailed, MarketplaceSync.FailureReason("fatal: Authentication failed for ..."));
        Assert.Equal(MarketplaceSync.TooLarge, MarketplaceSync.FailureReason("pack exceeds size limit"));
        Assert.Equal(MarketplaceSync.ValidationErrors, MarketplaceSync.FailureReason("invalid manifest json"));
        Assert.Equal(MarketplaceSync.Temporary, MarketplaceSync.FailureReason("connection reset"));
    }

    [Fact]
    public void AnUpdateIsOfferedOnlyWhenTheVersionsDiffer()
    {
        Assert.False(MarketplaceSync.UpdateFor(null, null).Available);
        Assert.False(
            MarketplaceSync.UpdateFor(new InstalledPluginRecord(null, "1.0.0", default, null), null).Available);
    }
}
