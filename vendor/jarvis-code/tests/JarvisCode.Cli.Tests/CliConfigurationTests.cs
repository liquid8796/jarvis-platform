using System.IO;
using System.Text.Json;
using JarvisCode.Cli;
using JarvisCode.Core.Settings;

namespace JarvisCode.Cli.Tests;

public sealed class CliConfigurationTests
{
    [Fact]
    public void Additional_settings_preserve_credentials_and_do_not_persist_overlay_values()
    {
        var profile = new SettingsFixture(new AppSettings { Language = "en",
            ApiKeySets = new() { ["fake"] = ["fixture-not-a-secret"] } });
        var store = new CliSettingsStore(profile, new CliOptions { Prompt = "", Settings = "{\"language\":\"vi\"}" },
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var effective = store.Load();
        Assert.Equal("vi", effective.Language);
        Assert.Equal("fixture-not-a-secret", effective.ApiKeySets["fake"].Single());
        effective.ShellTimeoutSeconds = 37;
        store.Save(effective);
        Assert.Equal("en", profile.Saved!.Language);
        Assert.Equal(37, profile.Saved.ShellTimeoutSeconds);
        Assert.Equal("fixture-not-a-secret", profile.Saved.ApiKeySets["fake"].Single());
    }

    [Fact]
    public void Bare_auth_uses_explicit_environment_without_loading_the_keychain()
    {
        var profile = new SettingsFixture(new AppSettings());
        var store = new CliSettingsStore(profile, new CliOptions { Prompt = "", Bare = true }, Path.GetTempPath(),
            name => name switch { "ANTHROPIC_API_KEY" => "fixture-key", "ANTHROPIC_BASE_URL" => "http://localhost:12345", _ => null });
        var settings = store.Load();
        Assert.Equal(0, profile.Loads);
        Assert.Equal("fixture-key", settings.ApiKeySets["anthropic"].Single());
        Assert.Equal("http://localhost:12345", settings.AnthropicBaseUrl);
        store.Save(settings);
        Assert.Null(profile.Saved);
    }

    [Fact]
    public void Bare_does_not_accept_oauth_tokens_or_inline_key_sets_as_environment_auth()
    {
        var store = new CliSettingsStore(new SettingsFixture(new AppSettings()), new CliOptions
        { Prompt = "", Bare = true, Settings = """{"ApiKeySets":{"anthropic":["inline-key"]}}""" }, Path.GetTempPath(),
            name => name == "ANTHROPIC_AUTH_TOKEN" ? "oauth-token" : null);
        Assert.False(store.Load().ApiKeySets.ContainsKey("anthropic"));
    }

    [Fact]
    public void Reading_user_aliases_does_not_replace_decrypted_profile_keys_with_raw_disk_values()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-user-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.json");
        try
        {
            File.WriteAllText(file, """{"agent":"reviewer","ApiKeySets":{"anthropic":["encrypted-disk-placeholder"]}}""");
            var store = new CliSettingsStore(new SettingsFixture(new AppSettings { ApiKeySets = new() { ["anthropic"] = ["decrypted-fixture-key"] } }),
                new CliOptions { Prompt = "" }, root, userSettingsPath: file);
            Assert.Equal("decrypted-fixture-key", store.Load().ApiKeySets["anthropic"].Single());
            Assert.Equal("reviewer", store.Agent);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Runtime_overrides_are_not_saved_when_an_unrelated_setting_changes()
    {
        var profile = new SettingsFixture(new AppSettings { ThinkingEffortName = "Medium" });
        var store = new CliSettingsStore(profile, new CliOptions { Prompt = "" },
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = store.Load();
        settings.ThinkingEffortName = "Max";
        store.MarkRuntimeInitialized();
        settings.ShellTimeoutSeconds = 99;
        store.Save(settings);
        Assert.Equal("Medium", profile.Saved!.ThinkingEffortName);
        Assert.Equal(99, profile.Saved.ShellTimeoutSeconds);
    }

    [Fact]
    public void Saving_a_cli_preference_preserves_accounts_added_to_the_profile_after_startup()
    {
        var original = new AppSettings { ApiKeySets = new() { ["first"] = ["fixture-first"] } };
        var profile = new SettingsFixture(original);
        var store = new CliSettingsStore(profile, new CliOptions { Prompt = "" },
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var settings = store.Load();
        original.ApiKeySets["added-by-desktop"] = ["fixture-second"];
        settings.ShellTimeoutSeconds = 44;
        store.Save(settings);
        Assert.Equal("fixture-second", profile.Saved!.ApiKeySets["added-by-desktop"].Single());
        Assert.Equal(44, profile.Saved.ShellTimeoutSeconds);
    }

    [Fact]
    public void Sources_validate_and_an_empty_list_is_deliberately_empty()
    {
        Assert.Empty(CliSettingsStore.ParseSources(""));
        Assert.Equal(3, CliSettingsStore.ParseSources(null).Count);
        Assert.Throws<CliError>(() => CliSettingsStore.ParseSources("user,typo"));
    }

    [Fact]
    public void Agent_definitions_keep_capabilities_models_and_turn_caps()
    {
        var agent = CliAgents.Parse("""
            {"reviewer":{"description":"Reviews changes","prompt":"Find correctness defects.","tools":["Read","Task"],"disallowedTools":["Write"],"model":"sonnet","maxTurns":9}}
            """).Single();
        Assert.Equal(["Read", "Agent"], agent.Tools);
        Assert.Equal(["Write"], agent.DisallowedTools);
        Assert.Equal("sonnet", agent.Model);
        Assert.Equal(9, agent.MaxTurns);
        Assert.Throws<CliError>(() => CliAgents.Parse("{\"broken\":{\"description\":\"missing prompt\"}}"));
    }

    [Fact]
    public void Relocation_moves_machine_sections_without_losing_static_instructions()
    {
        var result = CliPromptSections.Split("Intro\n\n# Environment\nC:/project\n\n# Harness\nUse tools.\n\n# Memory\nC:/memory\n\n# Context management\nKeep context.\ngitStatus: branch main\n");
        Assert.Contains("Use tools.", result.Static);
        Assert.Contains("Keep context.", result.Static);
        Assert.DoesNotContain("C:/project", result.Static);
        Assert.DoesNotContain("gitStatus:", result.Static);
        Assert.Contains("C:/project", result.Dynamic);
        Assert.Contains("C:/memory", result.Dynamic);
        Assert.Contains("branch main", result.Dynamic);
    }

    [Theory]
    [InlineData("https://example.test", "https://example.test/v1/messages")]
    [InlineData("https://example.test/v1/", "https://example.test/v1/messages")]
    [InlineData("https://example.test/custom/v1/messages", "https://example.test/custom/v1/messages")]
    public void Explicit_anthropic_base_urls_normalize_once(string input, string expected) =>
        Assert.Equal(expected, JarvisCode.Providers.Anthropic.AnthropicProvider.EndpointFromBaseUrl(input));

    private sealed class SettingsFixture(AppSettings original) : ISettingsStore
    {
        public int Loads { get; private set; }
        public AppSettings? Saved { get; private set; }
        public AppSettings Load() { Loads++; return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original))!; }
        public void Save(AppSettings settings) => Saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
    }
}
