using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Cli.Repl;
using JarvisCode.Cli.Repl.Input;
using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Tests;

/// <summary>
/// The REPL end to end: the real loop, the real key dispatch, the real command
/// table and the real renderer, driven by a scripted console in a throwaway
/// profile. No turn is started, so nothing here reaches a provider.
/// </summary>
[Collection("repl")]
public class ReplLoopTests : IDisposable
{
    // A named profile is how this build isolates a run: it puts the whole
    // tree under %APPDATA%\JarvisCode-<name>, which the test then deletes.
    private readonly string _profileName = "test" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _profile;
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "jarvis-work-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _previousProfile = Environment.GetEnvironmentVariable("JARVISCODE_PROFILE");
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();

    public ReplLoopTests()
    {
        _profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JarvisCode-" + _profileName);
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(_workspace);
        Directory.SetCurrentDirectory(_workspace);
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _profileName);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        Environment.SetEnvironmentVariable("JARVISCODE_PROFILE", _previousProfile);
        try
        {
            Directory.Delete(_profile, recursive: true);
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A settings file with one model, so the REPL gets past model resolution.</summary>
    private string SettingsFile()
    {
        var path = Path.Combine(_profile, "settings.json");
        // The settings store serializes AppSettings with no naming policy, so
        // the file's keys are the property names as written.
        var settings = new JsonObject
        {
            ["DefaultModelId"] = "test-model",
            ["CustomModels"] = new JsonArray
            {
                new JsonObject
                {
                    ["ProviderId"] = "anthropic",
                    ["ModelId"] = "test-model",
                    ["DisplayName"] = "Test Model",
                    ["MaxContextTokens"] = 200000,
                },
            },
        };
        File.WriteAllText(path, settings.ToJsonString());
        return path;
    }

    /// <summary>Answers the trust dialog up front, the way a second run in a folder finds it answered.</summary>
    private void TrustWorkspace()
    {
        var ui = new JsonObject
        {
            // ui-settings.json is written camelCase, unlike settings.json.
            ["trustedWorkspaces"] = new JsonArray { Directory.GetCurrentDirectory() },
        };
        File.WriteAllText(Path.Combine(_profile, "ui-settings.json"), ui.ToJsonString());
    }

    private async Task<(int Exit, string Output)> RunAsync(params KeyPress[] keys)
    {
        TrustWorkspace();
        var options = CliOptions.From(CommandLine.Parse(
            ["--settings", SettingsFile(), "--no-session-persistence"],
            RootOptions.Specs));
        using var services = CliServices.Create(options);
        var console = new ScriptedConsole(100, 40, keys);
        using var repl = new InteractiveRepl(services, options, console);
        int exit = await repl.RunAsync(CancellationToken.None);
        return (exit, console.Output);
    }

    [Fact]
    public async Task The_session_opens_with_the_welcome_line_and_the_prompt_box()
    {
        var (exit, output) = await RunAsync();
        Assert.Equal(0, exit);
        Assert.Contains("Welcome to Jarvis Code", output);
        Assert.Contains("╭", output);
        // This build's gate defaults to auto, so the footer names the mode
        // rather than falling back to the reference's resting hint.
        Assert.Contains("auto mode on", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_project_mcp_statusline_and_session_runtime_start_only_after_workspace_trust(bool accept)
    {
        var marker = Path.Combine(_workspace, "mcp-started.txt");
        var statusMarker = Path.Combine(_workspace, "statusline-started.txt");
        var server = Path.Combine(_workspace, "fixture-mcp.ps1");
        File.WriteAllText(server, """
            param([string]$FixtureMarker)
            [System.IO.File]::WriteAllText($FixtureMarker, 'started')
            while ($null -ne ($fixtureLine = [Console]::ReadLine())) {
                $fixtureRequest = $fixtureLine | ConvertFrom-Json
                if ($null -eq $fixtureRequest.id) { continue }
                $fixtureResult = switch ($fixtureRequest.method) {
                    'initialize' { @{protocolVersion='2025-06-18';capabilities=@{tools=@{}};serverInfo=@{name='trust-fixture';version='1'}} }
                    'tools/list' { @{tools=@()} }
                    'resources/list' { @{resources=@()} }
                    'prompts/list' { @{prompts=@()} }
                    default { @{} }
                }
                [Console]::Out.WriteLine((@{jsonrpc='2.0';id=$fixtureRequest.id;result=$fixtureResult} | ConvertTo-Json -Depth 12 -Compress))
                [Console]::Out.Flush()
            }
            """);
        Directory.CreateDirectory(Path.Combine(_workspace, ".jarvis"));
        File.WriteAllText(Path.Combine(_workspace, ".jarvis", "mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["trust-fixture"] = new JsonObject
            {
                ["command"] = "powershell.exe",
                ["args"] = new JsonArray("-NoProfile", "-NonInteractive", "-File", server, marker),
            } },
        }.ToJsonString());
        File.WriteAllText(Path.Combine(_profile, "ui-settings.json"), new JsonObject
        {
            ["statuslineCommand"] = "[System.IO.File]::WriteAllText('" + statusMarker.Replace("'", "''", StringComparison.Ordinal) +
                                    "', 'started'); 'fixture status'",
        }.ToJsonString());
        var options = new CliOptions { Prompt = "", Settings = SettingsFile(), NoSessionPersistence = true };
        using var services = CliServices.Create(options);
        var pendingReads = 0;
        var console = new StartupOrderingConsole(accept ? [new("down"), new("enter")] : [new("enter")], index =>
        {
            if (index >= (accept ? 2 : 1)) return;
            pendingReads++;
            Assert.False(File.Exists(marker));
            Assert.False(File.Exists(statusMarker));
            Assert.Empty(JarvisCode.Core.Agent.LocalSessionMailbox.ReadPeers(_profile));
        });
        using var repl = new InteractiveRepl(services, options, console);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.Equal(accept ? 0 : 1, await repl.RunAsync(timeout.Token));
        Assert.True(pendingReads > 0);
        Assert.Equal(accept, File.Exists(marker));
        Assert.Equal(accept, File.Exists(statusMarker));
        Assert.Equal(accept, services.App.Mcp.ConnectedToolCounts.ContainsKey("trust-fixture"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_bare_helper_and_plugin_flags_are_preserved_after_trust_and_do_not_run_on_decline(bool accept)
    {
        var marker = Path.Combine(_workspace, "helper-started.txt");
        var plugin = Path.Combine(_workspace, "fixture-plugin");
        Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
        File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), """{"name":"trust-plugin"}""");
        var settings = JsonNode.Parse(File.ReadAllText(SettingsFile()))!.AsObject();
        settings["env"] = new JsonObject { ["ANTHROPIC_API_KEY"] = "" };
        settings["apiKeyHelper"] = "[System.IO.File]::WriteAllText('" + marker.Replace("'", "''", StringComparison.Ordinal) +
                                   "', 'started'); 'fixture-key'";
        var options = new CliOptions { Prompt = "", Bare = true, Settings = settings.ToJsonString(),
            PluginDirectories = [plugin], NoSessionPersistence = true };
        using var services = CliServices.Create(options);
        var console = new StartupOrderingConsole(accept ? [new("down"), new("enter")] : [new("enter")], index =>
        {
            if (index >= (accept ? 2 : 1)) return;
            Assert.False(File.Exists(marker));
            Assert.Empty(services.PluginRoots);
        });
        using var repl = new InteractiveRepl(services, options, console);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.Equal(accept ? 0 : 1, await repl.RunAsync(timeout.Token));
        Assert.Equal(accept, File.Exists(marker));
        Assert.Equal(accept, services.PluginRoots.ContainsKey("trust-plugin"));
        if (accept) Assert.Equal("fixture-key", services.App.Settings.GetKey("anthropic"));
    }

    private sealed class StartupOrderingConsole(KeyPress[] keys, Action<int> beforeRead) : IConsole
    {
        private readonly ScriptedConsole _inner = new(100, 40, keys);
        private int _reads;
        public int Width => _inner.Width;
        public int Height => _inner.Height;
        public bool IsInteractive => true;
        public bool SupportsAnsi => false;
        public event Action? Resized { add { } remove { } }
        public void Write(string text) => _inner.Write(text);
        public ValueTask<KeyPress?> ReadKeyAsync(CancellationToken cancellationToken)
        {
            beforeRead(_reads++);
            return _inner.ReadKeyAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Model_hook_ask_runs_a_real_confirmation_without_blocking_the_key_loop()
    {
        TrustWorkspace();
        var hook = Path.Combine(_workspace, "fixture-model-hook.ps1");
        File.WriteAllText(hook, "Write-Output '{\"permissionDecision\":\"ask\",\"permissionDecisionReason\":\"Fixture confirmation\"}'");
        var settingsPath = SettingsFile();
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["CustomModels"]!.AsArray().Add(new JsonObject
        { ["ProviderId"] = "anthropic", ["ModelId"] = "other-model", ["DisplayName"] = "Other", ["MaxContextTokens"] = 200000 });
        settings["hooks"] = new JsonObject { ["PreModelSwitch"] = new JsonArray(new JsonObject
        { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command",
            ["command"] = "powershell.exe -NoProfile -NonInteractive -File \"" + hook + "\"" }) }) };
        var options = new CliOptions { Prompt = "", NoSessionPersistence = true, SettingSources = "", Settings = settings.ToJsonString() };
        using var services = CliServices.Create(options);
        var console = new ScriptedConsole(100, 40, [.. ScriptedConsole.Type("/model other-model"), ScriptedConsole.Enter, KeyPress.Typed("y")]);
        using var repl = new InteractiveRepl(services, options, console);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await repl.RunAsync(timeout.Token));
        Assert.Contains("Confirm model switch", console.Output);
        Assert.Contains("Model set to other-model", console.Output);
    }

    [Fact]
    public async Task Typing_shows_the_characters_in_the_box()
    {
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("hello")]);
        Assert.Contains("> hello", output);
    }

    [Fact]
    public async Task A_slash_opens_the_command_completion()
    {
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("/stat")]);
        Assert.Contains("status", output);
    }

    [Fact]
    public async Task A_typed_command_runs_and_prints()
    {
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("/status"), ScriptedConsole.Enter]);
        Assert.Contains("Jarvis Code", output);
        Assert.Contains("Working directory:", output);
    }

    [Fact]
    public async Task An_unknown_command_says_so_with_the_reference_wording()
    {
        // /stats is one edit away (a deleted "u"), where /status is two, so the
        // reference's nearest-name rule offers the alias.
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("/stauts"), ScriptedConsole.Enter]);
        Assert.Contains("Unknown command: /stauts. Did you mean /stats?", output);
    }

    [Fact]
    public async Task A_cloud_command_refuses_with_its_reason()
    {
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("/teleport"), ScriptedConsole.Enter]);
        Assert.Contains("/teleport (cloud sessions) is not available in Jarvis Code.", output);
    }

    [Fact]
    public async Task The_question_mark_opens_and_closes_the_shortcuts_overlay()
    {
        var (_, output) = await RunAsync(KeyPress.Typed("?"));
        Assert.Contains("! for shell mode", output);
        Assert.Contains("/keybindings to customize", output);
    }

    [Fact]
    public async Task Shift_tab_cycles_the_permission_mode()
    {
        // The reference's order is manual → accept edits → auto → plan, and a
        // session opens here in auto.
        var (_, output) = await RunAsync(new KeyPress("tab", Shift: true));
        Assert.Contains("plan mode on", output);
    }

    [Fact]
    public async Task Ctrl_c_asks_before_exiting_and_then_exits()
    {
        var (exit, output) = await RunAsync(
            new KeyPress("c", Ctrl: true), new KeyPress("c", Ctrl: true));
        Assert.Equal(0, exit);
        Assert.Contains("again to exit", output);
    }

    [Fact]
    public async Task A_bang_line_puts_the_box_in_shell_mode()
    {
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("!echo")]);
        Assert.Contains("! echo", output);
    }

    [Fact]
    public async Task Help_lists_the_commands()
    {
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("/help"), ScriptedConsole.Enter]);
        Assert.Contains("/compact", output);
        Assert.Contains("/resume", output);
        Assert.Contains("/rewind", output);
    }

    [Fact]
    public async Task Rename_renames_the_session()
    {
        var (_, output) = await RunAsync(
            [.. ScriptedConsole.Type("/rename Parser work"), ScriptedConsole.Enter]);
        Assert.Contains("Renamed to “Parser work”.", output);
    }

    [Fact]
    public async Task Clear_starts_a_new_session_and_says_the_old_one_survives()
    {
        var (_, output) = await RunAsync([.. ScriptedConsole.Type("/clear"), ScriptedConsole.Enter]);
        Assert.Contains("Started a new session. The previous one is still resumable", output);
    }
}

/// <summary>The REPL tests move the process's working directory, so they never run in parallel.</summary>
[CollectionDefinition("repl", DisableParallelization = true)]
public sealed class ReplCollection;
