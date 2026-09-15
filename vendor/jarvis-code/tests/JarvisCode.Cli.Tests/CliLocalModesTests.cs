using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl.Dialogs;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli.Tests;

[Collection("repl")]
public sealed class CliLocalModesTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "jarvis-cli-local-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldDirectory = Environment.CurrentDirectory;
    private readonly string? _oldProfile = Environment.GetEnvironmentVariable("JARVISCODE_PROFILE");
    private readonly string _profile = "test-local-" + Guid.NewGuid().ToString("N");
    private readonly FixtureProvider _provider = new();
    private string _profileRoot = "";

    public CliLocalModesTests()
    {
        Directory.CreateDirectory(_workspace);
        Environment.CurrentDirectory = _workspace;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _profile);
    }

    private CliOptions Options => new()
    {
        Prompt = "fixture question", Print = true, NoSessionPersistence = true, SettingSources = "",
        Settings = """{"DefaultModelId":"fixture-model","CustomModels":[{"ProviderId":"fixture","ModelId":"fixture-model","DisplayName":"Fixture","MaxContextTokens":200000}]}""",
    };

    private CliServices Services(CliOptions options)
    {
        var services = CliServices.Create(options, _ => new ProviderRegistry([_provider]));
        _profileRoot = services.App.Paths.Root;
        return services;
    }

    [Fact]
    public void Local_flag_dispatch_is_enabled_after_runtime_implementation()
    {
        foreach (var flag in new[] { "agent", "agents", "bg", "bare", "betas", "exclude-dynamic-system-prompt-sections",
                     "from-pr", "plugin-dir", "plugin-url", "prompt-suggestions", "safe-mode", "setting-sources", "worktree", "tmux" })
            Assert.False(RootOptions.Unsupported.ContainsKey(flag));
    }

    [Fact]
    public void Explicit_system_prompt_files_are_loaded_and_disable_default_snapshotting()
    {
        var file = Path.Combine(_workspace, "system.txt"); File.WriteAllText(file, "Fixture system instructions.");
        var options = CliOptions.From(CommandLine.Parse(["--system-prompt-file", file], RootOptions.Specs));
        Assert.Equal("Fixture system instructions.", options.SystemPrompt);
        Assert.False(options.SystemPromptSnapshot);
        Assert.Throws<CliError>(() => CliOptions.From(CommandLine.Parse(["--system-prompt-file", file, "--system-prompt", "other"], RootOptions.Specs)));
    }

    [Fact]
    public async Task Bare_sets_simple_environment_skips_autodiscovery_and_preserves_explicit_agent_and_name()
    {
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CODE_SIMPLE");
        Directory.CreateDirectory(Path.Combine(_workspace, ".claude"));
        File.WriteAllText(Path.Combine(_workspace, "CLAUDE.md"), "SHOULD_NOT_AUTOLOAD");
        var options = Options with { Bare = true, AgentProfile = "reviewer", SessionName = "Named session", InlineAgents =
            """{"reviewer":{"description":"A reviewer","prompt":"EXPLICIT_ROLE_FIXTURE","tools":["Read"],"maxTurns":4}}""" };
        using (var services = Services(options))
        {
            await services.InitializeAsync(options, _workspace, CancellationToken.None);
            Assert.Equal("1", Environment.GetEnvironmentVariable("CLAUDE_CODE_SIMPLE"));
            var session = await services.ResolveSessionAsync(options, _workspace, CancellationToken.None);
            var runner = new CliTurnRunner(services, options, new UiPermissionGate { WorkingDirectory = _workspace });
            await runner.RunAsync(session, options.Prompt, new CliTurnEvents(), CancellationToken.None);
            var request = Assert.Single(_provider.Requests);
            Assert.Contains("EXPLICIT_ROLE_FIXTURE", request.SystemPrompt);
            Assert.DoesNotContain("SHOULD_NOT_AUTOLOAD", string.Join('\n', request.Messages.Select(message => message.GetText())));
            Assert.All(request.Tools, tool => Assert.Equal("Read", tool.Name));
            Assert.Equal("Named session", session.Title);
        }
        Assert.Equal(previous, Environment.GetEnvironmentVariable("CLAUDE_CODE_SIMPLE"));
    }

    [Fact]
    public async Task Safe_mode_ignores_explicit_plugins_agents_hooks_and_mcp()
    {
        var options = Options with { SafeMode = true, AgentProfile = "missing", InlineAgents = "invalid",
            PluginDirectories = ["missing-plugin"], McpConfigs = ["missing-mcp.json"] };
        using var services = Services(options);
        await services.InitializeAsync(options, _workspace, CancellationToken.None);
        await services.ConnectMcpAsync(options, _workspace, CancellationToken.None);
        Assert.Empty(services.Customizations.Plugins.Agents);
        Assert.Empty(services.Customizations.ResolveSkills(_workspace, services.App.Paths, services.App.UiSettings.Current));
        Assert.Empty(services.LoadHooks(_workspace).Hooks);
        Assert.Null(CliAgents.Primary(services, options, _workspace));
        Assert.Equal("1", Environment.GetEnvironmentVariable("CLAUDE_CODE_SAFE_MODE"));
    }

    [Fact]
    public async Task Explicit_plugin_contributions_live_for_the_session_without_installing()
    {
        var root = Path.Combine(_workspace, "plugin");
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        Directory.CreateDirectory(Path.Combine(root, "commands"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"), """{"name":"fixture"}""");
        File.WriteAllText(Path.Combine(root, "commands", "check.md"), "---\ndescription: Fixture check\n---\nInspect the fixture.");
        var options = Options with { PluginDirectories = [root] };
        using var services = Services(options);
        await services.InitializeAsync(options, _workspace, CancellationToken.None);
        Assert.Contains(services.Customizations.ResolveSkills(_workspace, services.App.Paths, services.App.UiSettings.Current),
            skill => skill.Name == "fixture:check");
        Assert.True(File.Exists(Path.Combine(root, ".claude-plugin", "plugin.json")));
    }

    [Fact]
    public void Explicit_settings_hooks_are_parsed_without_a_settings_temp_file()
    {
        var options = Options with { Settings = """{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"fixture-only"}]}]},"env":{"JARVIS_FIXTURE_ENV":"present"}}""" };
        var previous = Environment.GetEnvironmentVariable("JARVIS_FIXTURE_ENV");
        using (var services = Services(options))
        {
            Assert.Equal("present", Environment.GetEnvironmentVariable("JARVIS_FIXTURE_ENV"));
            Assert.Equal("fixture-only", services.LoadHooks(_workspace).Hooks.Single().Command);
        }
        Assert.Equal(previous, Environment.GetEnvironmentVariable("JARVIS_FIXTURE_ENV"));
    }

    [Fact]
    public async Task Bare_api_key_helper_executes_only_the_explicit_settings_command()
    {
        var options = Options with { Bare = true, Settings = """{"apiKeyHelper":"Write-Output 'fixture-helper-key'"}""" };
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            using var services = Services(options);
            await services.InitializeAsync(options, _workspace, CancellationToken.None);
            Assert.Equal("fixture-helper-key", services.App.Settings.GetKey("anthropic"));
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous); }
    }

    [Fact]
    public async Task Worktree_creation_is_real_and_cleanup_retains_changes()
    {
        await Git("init"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.test");
        File.WriteAllText(Path.Combine(_workspace, "tracked.txt"), "original");
        await Git("add", "tracked.txt"); await Git("commit", "-m", "fixture");
        var clean = await CliWorkspace.CreateAsync(_workspace, "clean", CancellationToken.None);
        Assert.Equal("original", File.ReadAllText(Path.Combine(clean.Path, "tracked.txt")));
        await clean.DisposeAsync(); Assert.False(Directory.Exists(clean.Path));
        var changed = await CliWorkspace.CreateAsync(_workspace, "changed", CancellationToken.None);
        File.WriteAllText(Path.Combine(changed.Path, "tracked.txt"), "preserved change");
        await changed.DisposeAsync();
        Assert.Equal("preserved change", File.ReadAllText(Path.Combine(changed.Path, "tracked.txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_workspace, "tracked.txt")));
        var mainLocation = CliGitLocation.Read(_workspace);
        var linkedLocation = CliGitLocation.Read(changed.Path);
        Assert.Equal(Path.GetFullPath(_workspace), linkedLocation.Repository, ignoreCase: true);
        Assert.Equal("worktree-changed", linkedLocation.Branch);
        var picker = new ResumePicker([
            new(new SessionSummary("main", "Main", DateTimeOffset.Now, 1, _workspace), "Main", "", mainLocation.Branch) { Repository = mainLocation.Repository },
            new(new SessionSummary("linked", "Linked", DateTimeOffset.Now, 1, changed.Path), "Linked", "", linkedLocation.Branch) { Repository = linkedLocation.Repository, Worktree = true }],
            _workspace, new Repl.Render.Ansi(false, false));
        Assert.Equal(2, picker.Visible.Count);
        picker.Handle(null, new KeyPress("b", Ctrl: true)); Assert.Single(picker.Visible);
        picker.Handle(null, new KeyPress("b", Ctrl: true));
        picker.Handle(null, new KeyPress("w", Ctrl: true)); Assert.Single(picker.Visible);
        await Assert.ThrowsAsync<CliError>(() => CliWorkspace.CreateAsync(_workspace, "../escape", CancellationToken.None));
    }

    [Fact]
    public async Task Pr_resume_matches_stored_binding_not_a_similar_title()
    {
        using var services = Services(Options);
        var linked = Session.CreateNew(_workspace); linked.Title = "Linked";
        var other = Session.CreateNew(_workspace); other.Title = "PR 42";
        await services.App.Sessions.SaveAsync(linked); await services.App.Sessions.SaveAsync(other);
        services.App.UiSettings.Current.SessionPullRequests[linked.Id] =
            [new SessionPullRequest { Number = 42, Url = "https://example.test/org/repo/pull/42", Repo = "org/repo" }];
        var result = await services.ResolveSessionAsync(Options with { FromPr = true, FromPrValue = "42" }, _workspace, CancellationToken.None);
        Assert.Equal(linked.Id, result.Id);
        await Assert.ThrowsAsync<CliError>(() => services.ResolveSessionAsync(Options with { FromPr = true, FromPrValue = "43" }, _workspace, CancellationToken.None));
    }

    [Fact]
    public async Task Beta_headers_apply_to_every_registry_request_and_deduplicate()
    {
        var registry = new CliBetaRegistry(new ProviderRegistry([_provider]), ["fixture-beta"]);
        await foreach (var _ in registry.Get("fixture").StreamChatAsync(new LlmRequest
        { ModelId = "fixture-model", SystemPrompt = "child call", Messages = [], ExtraHeaders = [new("anthropic-beta", "base-beta,fixture-beta")] }, CancellationToken.None)) { }
        Assert.Equal("base-beta,fixture-beta", Assert.Single(_provider.Requests.Single().ExtraHeaders).Value);
        Assert.Same(_provider, ProviderDecorators.Unwrap(registry.Get("fixture")));
        Assert.Throws<CliError>(() => CliBetaRegistry.Merge([], ["invalid\r\nheader"]));
    }

    [Fact]
    public async Task Suggestions_use_tool_free_model_requests_and_emit_the_sdk_shape()
    {
        var model = new ModelInfo("fixture", "fixture-model", "Fixture", 200000);
        var session = Session.CreateNew(_workspace); session.Messages.Add(ChatMessage.FromUserText("What next?"));
        var result = await CliPromptSuggestions.GenerateAsync(_provider, model, session, [], CancellationToken.None);
        Assert.Equal("fixture answer", result);
        Assert.Empty(_provider.Requests.Single().Tools);
        var frame = CliPromptSuggestions.Frame(session.Id, result!);
        Assert.Equal("prompt_suggestion", frame["type"]!.GetValue<string>());
        Assert.Equal(session.Id, frame["session_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suggestions_skip_providers_that_cannot_guarantee_tool_free_inference(bool decorated)
    {
        _provider.Capabilities = new ProviderCapabilities { SupportsToolFreeInference = false };
        ILlmProvider provider = decorated
            ? new CliBetaRegistry(new ProviderRegistry([_provider]), ["fixture-beta"]).Get("fixture")
            : _provider;
        var model = new ModelInfo("fixture", "fixture-model", "Fixture", 200000);
        var session = Session.CreateNew(_workspace);
        session.Messages.Add(ChatMessage.FromUserText("Please inspect the project using its tools."));

        Assert.Null(await CliPromptSuggestions.GenerateAsync(provider, model, session, [], CancellationToken.None));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public void Model_picker_supports_effort_scope_and_cancellation_without_mutation()
    {
        var dialog = new ChoiceDialog("Model", [new("one", "One"), new("two", "Two")], "ModelPicker");
        dialog.Handle(null, new KeyPress("down"));
        dialog.Handle(null, new KeyPress("right"));
        dialog.Handle(null, KeyPress.Typed("s"));
        Assert.Equal(3, dialog.EffortIndex); Assert.True(dialog.SessionOnly);
        Assert.Equal("two", dialog.Handle(null, new KeyPress("enter")).Value);
        Assert.Equal(DialogOutcome.Cancelled, dialog.Handle(null, new KeyPress("escape")).Outcome);
    }

    [Fact]
    public void Background_registry_rejects_path_escape_and_stale_process_identity()
    {
        Assert.Throws<CliError>(() => CliBackground.SessionDirectory("../outside"));
        Assert.False(CliBackground.IsRunning(new BackgroundSession(Guid.NewGuid().ToString(), Environment.ProcessId,
            DateTime.UtcNow.AddDays(-1).Ticks, _workspace, "stale")));
        Assert.Equal("user", JsonNode.Parse(CliBackground.UserFrame("quoted \" prompt"))!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sdk_permission_rules_have_their_own_replaceable_layer_and_retain_original_deny()
    {
        using var services = Services(Options);
        var session = Session.CreateNew(_workspace);
        var gate = new UiPermissionGate { WorkingDirectory = _workspace, IgnoreSettingsFiles = true,
            SuppliedRuleLines = ["deny Write"], AdditionalDirectories = session.AdditionalDirectories };
        var updates = new SdkPermissionUpdates(services, Options, gate, session, _ => { });
        await updates.ApplyAsync(JsonNode.Parse("""
            [{"type":"addRules","behavior":"allow","rules":[{"toolName":"Read"}]},
             {"type":"replaceRules","behavior":"allow","rules":[{"toolName":"Write"}]},
             {"type":"addRules","behavior":"ask","rules":[{"toolName":"Edit"}]}]
            """)!.AsArray(), CancellationToken.None);
        Assert.Equal(["allow Write", "ask Edit"], gate.SdkRuleLines);
        Assert.Equal(["deny Write"], gate.SuppliedRuleLines);
        Assert.Equal(JarvisCode.Core.Permissions.RuleAction.Ask, JarvisCode.Core.Permissions.PermissionRuleEngine.Parse("ask Edit")!.Action);
        await updates.ApplyAsync(JsonNode.Parse("""[{"type":"setMode","mode":"plan"},{"type":"setMode","mode":"default"}]""")!.AsArray(), CancellationToken.None);
        Assert.Equal(JarvisCode.Core.Permissions.PermissionMode.Manual, gate.Mode);
    }

    [Fact]
    public async Task Persistent_sdk_directory_and_project_rule_updates_are_read_on_next_launch()
    {
        var options = Options with { SettingSources = "user,project,local" };
        var extra = Path.Combine(_workspace, "additional"); Directory.CreateDirectory(extra);
        using (var services = Services(options))
        {
            var session = Session.CreateNew(_workspace);
            var updates = new SdkPermissionUpdates(services, options, new UiPermissionGate(), session, _ => { });
            await updates.ApplyAsync(new JsonArray(new JsonObject
            { ["type"] = "addDirectories", ["destination"] = "userSettings", ["directories"] = new JsonArray(extra) },
                JsonNode.Parse("""{"type":"addRules","behavior":"deny","destination":"localSettings","rules":[{"toolName":"Write"}]}""")), CancellationToken.None);
        }
        using var resumed = Services(options);
        Assert.Contains(extra, resumed.CliSettings.AdditionalDirectories);
        Assert.Contains("deny Write", resumed.App.Settings.Current.PermissionRuleLines);
    }

    [Fact]
    public async Task Explicit_lsp_plugin_is_discovered_and_bare_mode_suppresses_its_tools()
    {
        var root = Path.Combine(_workspace, "lsp-plugin");
        Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
        File.WriteAllText(Path.Combine(root, ".claude-plugin", "plugin.json"), """{"name":"fixture-lsp"}""");
        File.WriteAllText(Path.Combine(root, ".lsp.json"), """{"fixture":{"command":"node","args":["fixture-not-started.js"],"extensionToLanguage":{".fixture":"fixture"}}}""");
        var options = Options with { PluginDirectories = [root], Bare = true };
        using var services = Services(options);
        await services.InitializeAsync(options, _workspace, CancellationToken.None);
        Assert.NotEmpty(services.Customizations.PluginLspFiles);
        Assert.True(services.Customizations.DisableLsp);
        var session = Session.CreateNew(_workspace);
        var runner = new CliTurnRunner(services, options, new UiPermissionGate { WorkingDirectory = _workspace });
        await runner.RunAsync(session, options.Prompt, new CliTurnEvents(), CancellationToken.None);
        Assert.DoesNotContain(_provider.Requests.Single().Tools, tool => tool.Name == "LSP");
    }

    [Fact]
    public async Task Fleet_dispatch_preserves_flags_and_applies_interactive_effort_selection()
    {
        CliOptions? launched = null;
        var console = new ScriptedConsole(100, 30,
            [new("e"), new("down"), new("enter"), new("n"),
             .. ScriptedConsole.Type("fleet fixture prompt"), new("enter"), new("q")]);
        var exit = await AgentFleet.RunAsync(["--settings", Options.Settings!, "--setting-sources", "", "--model", "fixture-model",
            "--agent", "reviewer", "--agents", """{"reviewer":{"description":"Reviews","prompt":"Review this work."}}"""],
            CancellationToken.None, console, (options, _) =>
            { launched = options; return Task.FromResult(Guid.NewGuid().ToString()); });
        _profileRoot = JarvisCode.App.Services.ProfilePaths.Create(_profile).Root;
        Assert.Equal(0, exit);
        Assert.NotNull(launched);
        Assert.Equal("fixture-model", launched.Model);
        Assert.Equal("reviewer", launched.AgentProfile);
        Assert.Equal("medium", launched.Effort);
        Assert.Equal("fleet fixture prompt", launched.Prompt);
        Assert.True(launched.Background);
        Assert.Contains("Manage background agents", console.Output);
    }

    [Fact]
    public async Task Hosted_mcp_initialize_instructions_reach_the_real_outgoing_turn()
    {
        using var services = Services(Options);
        await services.App.Mcp.RegisterHostedAsync(new JarvisCode.Core.Mcp.McpServerConfig("fixture", "", [], new Dictionary<string, string>())
            { Type = "sdk" }, new InstructionClient(), CancellationToken.None);
        var runner = new CliTurnRunner(services, Options, new UiPermissionGate { WorkingDirectory = _workspace });
        await runner.RunAsync(Session.CreateNew(_workspace), Options.Prompt, new CliTurnEvents(), CancellationToken.None);
        var request = _provider.Requests.Single();
        Assert.Contains("FIXTURE_MCP_INITIALIZE_INSTRUCTIONS", request.SystemPrompt + "\n" + string.Join('\n', request.Messages.Select(message => message.GetText())));
    }

    [Fact]
    public async Task Sdk_rewind_uses_persisted_user_uuid_and_restores_files_without_removing_messages()
    {
        using var services = Services(Options);
        var session = Session.CreateNew(_workspace);
        var id = Guid.NewGuid().ToString();
        session.Messages.Add(ChatMessage.FromUserText("fixture") with { SdkUserMessageId = id, SdkCheckpointTurnNumber = 1 });
        var file = Path.Combine(_workspace, "rewind.txt"); File.WriteAllText(file, "before");
        var checkpoint = services.App.Checkpoints.BeginTurn(session.Id, 1, "fixture", 0);
        await checkpoint.RecordBeforeChangeAsync(file, CancellationToken.None);
        File.WriteAllText(file, "after");
        await services.App.Sessions.SaveAsync(session);
        var resumed = await services.App.Sessions.LoadAsync(session.Id);
        var result = await CliSdkControls.RewindFilesAsync(services, resumed!, id, CancellationToken.None);
        Assert.True(result["canRewind"]!.GetValue<bool>());
        Assert.Equal("before", File.ReadAllText(file));
        Assert.Single(resumed!.Messages);
        await Assert.ThrowsAsync<CliError>(() => CliSdkControls.RewindFilesAsync(services, resumed, "unknown", CancellationToken.None));
    }

    [Fact]
    public async Task Context_usage_has_consistent_totals_and_explicit_estimate_provenance()
    {
        using var services = Services(Options);
        var session = Session.CreateNew(_workspace);
        var runner = new CliTurnRunner(services, Options, new UiPermissionGate { WorkingDirectory = _workspace });
        await runner.RunAsync(session, "fixture", new CliTurnEvents(), CancellationToken.None);
        var usage = CliSdkControls.ContextUsage(services, runner, session, services.ResolveModel("fixture-model"));
        Assert.Equal("fixture-model", usage["model"]!.GetValue<string>());
        Assert.True(usage["totalTokens"]!.GetValue<long>() > 0);
        Assert.Equal(usage["totalTokens"]!.GetValue<long>(), usage["categories"]!.AsArray().Sum(category => category!["tokens"]!.GetValue<long>()));
        Assert.Contains("estimates", usage["estimationMethod"]!.GetValue<string>());
    }

    private sealed class InstructionClient : JarvisCode.Core.Mcp.IMcpClient
    {
        public string ServerName => "fixture";
        public string ServerType => "sdk";
        public string Instructions => "FIXTURE_MCP_INITIALIZE_INSTRUCTIONS";
        public Task<IReadOnlyList<JarvisCode.Core.Mcp.McpToolDescriptor>> ListToolsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<JarvisCode.Core.Mcp.McpToolDescriptor>>([new("echo", "Echo", new JsonObject { ["type"] = "object" }) { AlwaysLoad = true }]);
        public Task<IReadOnlyList<JarvisCode.Core.Mcp.McpResourceDescriptor>> ListResourcesAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<JarvisCode.Core.Mcp.McpResourceDescriptor>>([]);
        public Task<IReadOnlyList<JarvisCode.Core.Mcp.McpPromptDescriptor>> ListPromptsAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<JarvisCode.Core.Mcp.McpPromptDescriptor>>([]);
        public Task<string> ReadResourceAsync(string uri, CancellationToken token) => throw new NotSupportedException();
        public Task<string> GetPromptAsync(string name, JsonObject args, CancellationToken token) => throw new NotSupportedException();
        public Task<JarvisCode.Core.Mcp.McpCallResult> CallToolAsync(string name, JsonObject args, CancellationToken token,
            JarvisCode.Core.Mcp.McpElicitationCallback? elicit = null) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task Git(params string[] args)
    {
        var result = await CliWorkspace.GitAsync(_workspace, CancellationToken.None, args);
        Assert.True(result.Code == 0, result.Error);
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _oldDirectory;
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _oldProfile);
        if (Directory.Exists(_workspace))
        {
            foreach (var file in Directory.EnumerateFiles(_workspace, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_workspace, true);
        }
        if (Directory.Exists(_profileRoot)) Directory.Delete(_profileRoot, true);
    }

    private sealed class FixtureProvider : ILlmProvider, IProviderCapabilities
    {
        public string Id => "fixture";
        public string DisplayName => "Fixture";
        public bool RequiresApiKey => false;
        public ProviderCapabilities Capabilities { get; set; } = new();
        public List<LlmRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new TextDeltaEvent("fixture answer");
            yield return new ResponseCompletedEvent(false, new Usage(10, 2), "end_turn");
        }
    }
}
