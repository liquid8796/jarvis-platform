using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Settings;
using JarvisCode.Core.Sessions;

namespace JarvisCode.App.Tests.Services;

/// <summary>
/// The logic the reference's settings pages carry, pulled out of the views so it
/// can be checked without a window: the branch-prefix rules, the transcript widths,
/// the notification levels, the shortcut recorder, the extension manifest and its
/// config substitution, the usage arithmetic and the settings search.
/// </summary>
public sealed class SettingsPagesTests
{
    // ---- branch prefix (the reference's rb / ub / Fa) ----

    [Theory]
    [InlineData("claude", null)]
    [InlineData("jarvis", null)]
    [InlineData("", null)]
    [InlineData("main", "reserved")]
    [InlineData("MASTER", "reserved")]
    [InlineData("prod-hotfix", "reserved")]
    [InlineData("release", "reserved")]
    [InlineData("-leading", "format")]
    [InlineData("has space", "format")]
    [InlineData("dots..inside", "format")]
    [InlineData("trailing.", "format")]
    [InlineData("ends.lock", "format")]
    public void BranchPrefixProblems(string prefix, string? expected) =>
        Assert.Equal(expected, BranchPrefixes.Problem(prefix));

    [Fact]
    public void BranchPrefixLengthIsFortyOne()
    {
        Assert.Null(BranchPrefixes.Problem(new string('a', 40)));
        Assert.Equal("format", BranchPrefixes.Problem(new string('a', 41)));
    }

    [Fact]
    public void EmptyBranchPrefixCommitsTheDefault() =>
        Assert.Equal("jarvis", BranchPrefixes.Commit("  ", "jarvis"));

    [Fact]
    public void InvalidBranchPrefixCommitsNothing() =>
        Assert.Null(BranchPrefixes.Commit("main", "jarvis"));

    // ---- transcript widths ----

    [Theory]
    [InlineData("s", 768)]
    [InlineData("m", 960)]
    [InlineData("l", 1280)]
    [InlineData(null, 768)]
    [InlineData("nonsense", 768)]
    public void TranscriptContentWidths(string? choice, double expected) =>
        Assert.Equal(expected, TranscriptWidths.ContentWidth(choice));

    [Fact]
    public void TranscriptMaxWidthAddsBothGutters() =>
        Assert.Equal(768 + 64, TranscriptWidths.MaxWidth("s"));

    // ---- notification levels ----

    [Fact]
    public void TaskCompleteHasNoBadgeLevel()
    {
        Assert.Equal(["off", "banner"], NotificationPolicy.LevelsFor("idle"));
        Assert.Equal(["off", "badge", "banner"], NotificationPolicy.LevelsFor("permission"));
    }

    [Fact]
    public void AbsentLevelReadsAsBanner()
    {
        var levels = new Dictionary<string, string>();
        Assert.Equal("banner", NotificationPolicy.LevelFor(levels, "permission"));
        Assert.True(NotificationPolicy.ShowsBanner(levels, "permission"));
    }

    [Fact]
    public void BadgeOnlyShowsNoBanner()
    {
        var levels = new Dictionary<string, string> { ["permission"] = "badge" };
        Assert.False(NotificationPolicy.ShowsBanner(levels, "permission"));
        Assert.True(NotificationPolicy.CountsForBadge(levels, "permission"));
    }

    [Fact]
    public void AttentionSwitchIsDisabledOnlyWhenBothAttentionTypesAreOff()
    {
        Assert.True(NotificationPolicy.AttentionSwitchDisabled(
            new Dictionary<string, string> { ["permission"] = "off", ["question"] = "off" }));
        Assert.False(NotificationPolicy.AttentionSwitchDisabled(
            new Dictionary<string, string> { ["permission"] = "off", ["question"] = "badge" }));
    }

    [Fact]
    public void FlashOnlyWhenAwayAndEnabled()
    {
        Assert.True(NotificationPolicy.ShouldFlash(true, appFocusedAndVisible: false));
        Assert.False(NotificationPolicy.ShouldFlash(true, appFocusedAndVisible: true));
        Assert.False(NotificationPolicy.ShouldFlash(false, appFocusedAndVisible: false));
    }

    // ---- the quick entry shortcut recorder ----

    [Theory]
    [InlineData("Ctrl+Alt+Space", "Control+Alt+Space")]
    [InlineData("Alt+Ctrl+Space", "Control+Alt+Space")]
    [InlineData("Cmd+Shift+K", "Shift+Windows+K")]
    [InlineData("", "")]
    public void ShortcutDisplayOrdersModifiers(string accelerator, string expected) =>
        Assert.Equal(expected, QuickEntryShortcuts.Display(accelerator));

    [Fact]
    public void BareKeysAreReservedUnlessFunctionKeys()
    {
        Assert.True(QuickEntryShortcuts.IsReserved([], "K"));
        Assert.False(QuickEntryShortcuts.IsReserved([], "F5"));
        Assert.True(QuickEntryShortcuts.IsReserved(["Ctrl"], "C"));
        Assert.False(QuickEntryShortcuts.IsReserved(["Ctrl", "Alt"], "Space"));
    }

    [Fact]
    public void ParsedChordRoundTrips()
    {
        var chord = QuickEntryShortcuts.Parse("Ctrl+Shift+Space");
        Assert.NotNull(chord);
        Assert.True(chord!.Value.Control);
        Assert.True(chord.Value.Shift);
        Assert.False(chord.Value.Alt);
        Assert.Equal("Space", chord.Value.Key);
    }

    // ---- extensions ----

    private const string Manifest = """
        {
          "manifest_version": "0.1",
          "name": "demo",
          "display_name": "Demo",
          "version": "1.0.0",
          "description": "A demo server",
          "server": {
            "type": "node",
            "entry_point": "server/index.js",
            "mcp_config": {
              "command": "node",
              "args": ["${__dirname}/server/index.js", "${user_config.roots}"],
              "env": { "TOKEN": "${user_config.token}" },
              "platform_overrides": { "win32": { "command": "node.exe" } }
            }
          },
          "user_config": {
            "token": { "type": "string", "title": "Token", "description": "API token", "required": true },
            "roots": { "type": "directory", "title": "Roots", "description": "Folders", "multiple": true }
          }
        }
        """;

    [Fact]
    public void ManifestParsesTheReferenceShape()
    {
        var manifest = DesktopExtensions.ParseManifest(Manifest);
        Assert.NotNull(manifest);
        Assert.Equal("demo", manifest!.Name);
        Assert.Equal("Demo", manifest.Title);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal(2, manifest.UserConfig.Count);
        Assert.True(manifest.UserConfig.First(f => f.Key == "token").Required);
        Assert.True(manifest.UserConfig.First(f => f.Key == "roots").Multiple);
    }

    [Fact]
    public void ManifestWithoutAVersionMarkerIsRefused() =>
        Assert.Null(DesktopExtensions.ParseManifest("""{"name":"x","version":"1","description":"d"}"""));

    [Fact]
    public void AnUnfilledRequiredFieldContributesNoServer()
    {
        var manifest = DesktopExtensions.ParseManifest(Manifest)!;
        var extension = new InstalledExtension("demo", @"C:\ext", manifest,
            new Dictionary<string, JsonNode?>(), true);
        Assert.True(DesktopExtensions.MissingRequiredConfig(extension));
        Assert.Null(DesktopExtensions.ResolveServer(extension));
    }

    [Fact]
    public void ServerResolutionAppliesOverridesAndSubstitutions()
    {
        var manifest = DesktopExtensions.ParseManifest(Manifest)!;
        var config = new Dictionary<string, JsonNode?>
        {
            ["token"] = JsonValue.Create("secret"),
            ["roots"] = new JsonArray("a", "b"),
        };
        var extension = new InstalledExtension("demo", @"C:\ext", manifest, config, true);

        var server = DesktopExtensions.ResolveServer(extension);
        Assert.NotNull(server);
        Assert.Equal("node.exe", (string?)server!["command"]);
        Assert.Null(server["platform_overrides"]);

        var args = (JsonArray)server["args"]!;
        Assert.Equal(3, args.Count);
        Assert.Equal(@"C:\ext/server/index.js", (string?)args[0]);
        Assert.Equal("a", (string?)args[1]);
        Assert.Equal("b", (string?)args[2]);
        Assert.Equal("secret", (string?)((JsonObject)server["env"]!)["TOKEN"]);
    }

    [Fact]
    public void BooleanSubstitutionReadsAsTrueOrFalse()
    {
        var values = new Dictionary<string, JsonNode?> { ["user_config.flag"] = JsonValue.Create(true) };
        var node = DesktopExtensions.Substitute(JsonValue.Create("--flag=${user_config.flag}"), values);
        Assert.Equal("--flag=true", (string?)node);
    }

    // ---- usage ----

    [Fact]
    public void UsageDaysFillGapsAndSplitBySurface()
    {
        var today = new DateOnly(2026, 9, 2);
        var data = new UsageStatsData();
        data.Days[UsageStatsData.KeyFor(today)] = new DayActivity
        {
            Tokens = 300,
            TokensBySurface = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = 100,
                ["code"] = 200,
            },
        };

        var days = UsageReport.Days(data, today, 3);
        Assert.Equal(3, days.Count);
        Assert.Equal(0, days[0].Total);
        Assert.Equal(100, days[2].ChatTokens);
        Assert.Equal(200, days[2].CodeTokens);
    }

    [Fact]
    public void UsageDaysRecordedBeforeTheSplitCountAsCode()
    {
        var today = new DateOnly(2026, 9, 2);
        var data = new UsageStatsData();
        data.Days[UsageStatsData.KeyFor(today)] = new DayActivity { Tokens = 500 };
        var days = UsageReport.Days(data, today, 1);
        Assert.Equal(500, days[0].CodeTokens);
        Assert.Equal(0, days[0].ChatTokens);
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(30, 1)]
    [InlineData(90, 7)]
    public void UsageBucketing(int window, int expected) => Assert.Equal(expected, UsageReport.BucketDays(window));

    [Fact]
    public void UsageBarsFoldWholeBuckets()
    {
        var today = new DateOnly(2026, 9, 2);
        var days = Enumerable.Range(0, 14)
            .Select(i => new UsageDay(today.AddDays(-i), 1, 1))
            .ToList();
        var bars = UsageReport.Bars(days, 7);
        Assert.Equal(2, bars.Count);
        Assert.Equal(14, bars[0].Total);
    }

    // ---- model discovery ----

    [Fact]
    public void DiscoverySentences()
    {
        Assert.Equal("found 1 model", ModelDiscovery.Found(1));
        Assert.Equal("found 3 models", ModelDiscovery.Found(3));
        Assert.Equal("found 3 models; 2 of yours not in the list", ModelDiscovery.FoundWithMissing(3, 2));
        Assert.Equal("Not returned by discovery: a, b", ModelDiscovery.NotReturned(["a", "b"]));
        Assert.Equal("…and 4 more", ModelDiscovery.AndMore(4));
    }

    [Fact]
    public void DiscoveryMissingIsCaseInsensitive() =>
        Assert.Equal(["c"], ModelDiscovery.Missing(["A", "c"], ["a", "b"]));

    // ---- credential helper ----

    [Fact]
    public void HelperParsesABareToken()
    {
        var (credential, headers) = CredentialHelpers.Parse("  sk-abc \n");
        Assert.Equal("sk-abc", credential);
        Assert.Equal(0, headers);
    }

    [Fact]
    public void HelperParsesJsonHeaders()
    {
        var (credential, headers) = CredentialHelpers.Parse("""{"headers":{"Authorization":"Bearer x","X-Extra":"y"}}""");
        Assert.Equal("Bearer x", credential);
        Assert.Equal(2, headers);
    }

    [Fact]
    public void HelperElapsedNamesTheLimit()
    {
        Assert.Equal("2.5 s", CredentialHelpers.Elapsed(2.5, hitLimit: false));
        Assert.Equal("10 s (limit)", CredentialHelpers.Elapsed(10, hitLimit: true));
    }

    // ---- settings search ----

    [Fact]
    public void SearchRanksExactBeforePrefix()
    {
        Assert.Equal(0, SettingsSearchIndex.Rank("general", "General"));
        Assert.Equal(1, SettingsSearchIndex.Rank("gen", "General"));
        Assert.Equal(-1, SettingsSearchIndex.Rank("zzz", "General"));
    }

    [Fact]
    public void SearchMatchesWordStarts() =>
        Assert.Equal(2, SettingsSearchIndex.Rank("br pre", "Branch prefix"));

    [Fact]
    public void SearchFindsARowInAnotherSection()
    {
        var sections = new List<SettingsSearchSection>
        {
            new("general", "Settings", "General", []),
            new("claude-code", "Settings", "Jarvis Code", []),
        };
        var rows = new List<SettingsSearchRow> { new("claude-code", "Branch prefix") };
        var hits = SettingsSearchIndex.Search(sections, rows, "branch");
        Assert.Contains(hits, h => h.Section.Id == "claude-code");
    }

    [Fact]
    public void EmptySearchListsEverySection()
    {
        var sections = new List<SettingsSearchSection> { new("general", "Settings", "General", []) };
        Assert.Single(SettingsSearchIndex.Search(sections, [], "  "));
    }

    [Fact]
    public void EverySearchRowNamesASectionTheDialogHas()
    {
        string[] known =
        [
            "general", "providers", "permissions", "usage", "claude-code", "import",
            "themes", "features", "fingerprints", "desktop", "desktop/extensions",
            "desktop/developer", "desktop/debug",
        ];
        foreach (var row in SettingsSearchIndex.Rows)
        {
            Assert.Contains(row.Section, known);
        }
    }

    // ---- session transfer ----

    [Theory]
    [InlineData("30", 30)]
    [InlineData("90", 90)]
    [InlineData("all", 0)]
    public void TransferRanges(string value, int days) => Assert.Equal(days, SessionTransfer.DaysFor(value));

    [Fact]
    public void EverythingIsInEveryRange() =>
        Assert.True(SessionTransfer.InRange(DateTime.UtcNow.AddYears(-5), days: 0));

    [Fact]
    public void OldSessionsFallOutOfANarrowRange() =>
        Assert.False(SessionTransfer.InRange(DateTime.UtcNow.AddDays(-40), days: 30));

    // ---- code themes ----

    [Fact]
    public void TheThemeSelectsOfferTheReferenceList()
    {
        Assert.Equal(28, CodeThemes.All.Count);
        Assert.Equal(CodeThemes.All.Count, CodeThemes.LightThemes.Count + CodeThemes.DarkThemes.Count);
        Assert.Equal("claude-light", CodeThemes.LightThemes[0].Id);
        Assert.Equal("claude-dark", CodeThemes.DarkThemes[0].Id);
    }

    [Fact]
    public void AnUnknownThemeIdFallsBackToTheClaudeThemeOfThatMode()
    {
        Assert.Equal("claude-dark", CodeThemes.Resolve("github-light", dark: true).Id);
        Assert.Equal("claude-light", CodeThemes.Resolve(null, dark: false).Id);
    }

    // ---- archive choices ----

    [Fact]
    public void ArchiveChoicesAreTheReferenceList() =>
        Assert.Equal([0, 1, 2, 7, 14, 30], ClaudeCodePage.ArchiveChoices(0));

    [Fact]
    public void AStoredArchiveValueJoinsTheListInOrder() =>
        Assert.Equal([0, 1, 2, 7, 14, 21, 30], ClaudeCodePage.ArchiveChoices(21));

    [Theory]
    [InlineData(0, "Never")]
    [InlineData(1, "1 day")]
    [InlineData(30, "30 days")]
    public void ArchiveLabels(int days, string expected) => Assert.Equal(expected, ClaudeCodePage.ArchiveLabel(days));

    // ---- computer-use deny list ----

    [Fact]
    public void DenyListMatchesTheWayGrantsDo()
    {
        var settings = new UiSettings();
        Assert.True(ComputerUseAppPolicy.Deny(settings, "Notepad.exe"));
        Assert.True(ComputerUseAppPolicy.IsDenied(settings, "notepad"));
        Assert.False(ComputerUseAppPolicy.Deny(settings, "NOTEPAD"));
        Assert.True(ComputerUseAppPolicy.Allow(settings, "notepad"));
        Assert.False(ComputerUseAppPolicy.IsDenied(settings, "Notepad.exe"));
    }

    [Fact]
    public void DenyCandidatesExcludeWhatIsAlreadyDenied()
    {
        var settings = new UiSettings();
        ComputerUseAppPolicy.Deny(settings, "notepad");
        Assert.Equal(["chrome", "code"], ComputerUseAppPolicy.Candidates(settings, ["code", "notepad", "chrome"]));
    }

    // ---- firewall allowlist ----

    [Fact]
    public void AllowlistNamesTheConfiguredHosts()
    {
        var settings = new JarvisCode.Core.Settings.AppSettings();
        var hosts = FirewallAllowlist.Hosts(settings);
        Assert.Contains("api.anthropic.com", hosts);
        Assert.Contains("openrouter.ai", hosts);
        Assert.Equal(hosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase), hosts);
    }

    [Fact]
    public void AllowlistSummaryIsTheReferenceLine() =>
        Assert.Equal("2 of 3 reachable", FirewallAllowlist.Summary(2, 3));
}
