using JarvisCode.Cli;
using JarvisCode.Core.Permissions;

namespace JarvisCode.Cli.Tests;

/// <summary>
/// The rest of the reference surface: tool-name mapping, the typed options,
/// and the help texts (captured from the reference CLI 2.1.251 and brand-swapped,
/// so these assertions are what keeps that generation honest).
/// </summary>
public class CliSurfaceTests
{
    [Theory]
    [InlineData("PowerShell", "PowerShell")]
    [InlineData("Read", "Read")]
    [InlineData("MultiEdit", "Edit")]
    [InlineData("Task", "Agent")]
    [InlineData("TodoWrite", "todo_write")]
    [InlineData("mcp__server__tool", "mcp__server__tool")]
    public void Reference_tool_names_map_onto_engine_names(string cli, string engine) =>
        Assert.Equal(engine, ToolNames.Map(cli));

    [Fact]
    public void Batch_debug_and_release_notes_are_real_rows_rather_than_refusals()
    {
        var table = JarvisCode.Cli.Repl.ReplCommandTable.Commands;
        var batch = table.Single(c => c.Name == "batch");
        Assert.Equal(JarvisCode.Cli.Repl.ReplCommandKind.Prompt, batch.Kind);
        Assert.Equal(
            "Research and plan a large-scale change, then execute it in parallel across " +
            "5–30 isolated worktree agents that each open a PR.",
            batch.Description);
        Assert.Equal("<instruction>", batch.ArgumentHint);

        var debug = table.Single(c => c.Name == "debug");
        Assert.Equal(JarvisCode.Cli.Repl.ReplCommandKind.Prompt, debug.Kind);
        Assert.Equal("Enable debug logging for this session and help diagnose issues", debug.Description);
        Assert.Equal("[issue description]", debug.ArgumentHint);

        var notes = table.Single(c => c.Name == "release-notes");
        Assert.Equal(JarvisCode.Cli.Repl.ReplCommandKind.Dialog, notes.Kind);
        Assert.Equal("View release notes", notes.Description);

        Assert.All([batch, debug, notes], row => Assert.Null(row.Unavailable));
    }

    [Fact]
    public void Tool_entry_splits_the_permission_pattern()
    {
        Assert.Equal(("Bash", "git *"), ToolNames.ParseEntry("Bash(git *)"));
        Assert.Equal(("Edit", null), ToolNames.ParseEntry("Edit"));
    }

    [Fact]
    public void Reference_prefix_patterns_become_prefix_globs() =>
        // "prefix:*" is a prefix glob with no separator, the convention the
        // skill-frontmatter mapper already uses (SkillCatalogTests).
        Assert.Equal(("Bash", "npm run*"), ToolNames.ParseEntry("Bash(npm run:*)"));

    [Fact]
    public void Allowed_and_disallowed_tools_become_gate_rule_lines()
    {
        Assert.Equal(["allow Bash git *", "allow Edit"],
            ToolNames.ToRuleLines(["Bash(git *)", "Edit"], "allow"));
        Assert.Equal(["deny WebFetch"], ToolNames.ToRuleLines(["WebFetch"], "deny"));
    }

    [Fact]
    public void Empty_tools_list_disables_every_tool()
    {
        // --tools "" is the reference's "disable all tools". It arrives through
        // the parser as an empty list, because the comma split drops "" — the
        // filter must still come back empty rather than null (keep everything).
        var options = CliOptions.From(CommandLine.Parse(["--tools", ""], RootOptions.Specs));
        Assert.True(options.HasToolsFilter);
        var filter = ToolNames.BuildToolFilter(options.Tools, options.HasToolsFilter);
        Assert.NotNull(filter);
        Assert.Empty(filter);
    }

    [Fact]
    public void Absent_tools_flag_keeps_the_whole_registry() =>
        Assert.Null(ToolNames.BuildToolFilter([], declared: false));

    [Fact]
    public void Default_keeps_the_whole_registry() =>
        Assert.Null(ToolNames.BuildToolFilter(["default"]));

    [Fact]
    public void Named_tools_keep_only_those()
    {
        var filter = ToolNames.BuildToolFilter(["PowerShell", "Read"]);
        Assert.NotNull(filter);
        Assert.Contains("PowerShell", filter);
        Assert.Contains("Read", filter);
        Assert.DoesNotContain("Write", filter);
    }

    [Theory]
    [InlineData("acceptEdits", PermissionMode.AcceptEdits)]
    [InlineData("bypassPermissions", PermissionMode.Bypass)]
    [InlineData("manual", PermissionMode.Manual)]
    [InlineData("plan", PermissionMode.Plan)]
    [InlineData("auto", PermissionMode.Auto)]
    public void Permission_modes_map_onto_the_engine(string name, PermissionMode expected)
    {
        var options = CliOptions.From(CommandLine.Parse(["--permission-mode", name], RootOptions.Specs));
        Assert.Equal(expected, options.ResolvePermissionMode().Mode);
    }

    [Fact]
    public void DontAsk_is_auto_mode_with_prompts_refused()
    {
        var options = CliOptions.From(CommandLine.Parse(["--permission-mode", "dontAsk"], RootOptions.Specs));
        var (mode, dontAsk) = options.ResolvePermissionMode();
        Assert.Equal(PermissionMode.Auto, mode);
        Assert.True(dontAsk);
    }

    [Fact]
    public void Dangerously_skip_permissions_wins_over_the_mode_flag()
    {
        var options = CliOptions.From(CommandLine.Parse(
            ["--permission-mode", "manual", "--dangerously-skip-permissions"], RootOptions.Specs));
        Assert.Equal(PermissionMode.Bypass, options.ResolvePermissionMode().Mode);
    }

    [Theory]
    [InlineData("low", "Low")]
    [InlineData("xhigh", "Extra high")]
    [InlineData("max", "Max")]
    [InlineData("nonsense", null)]
    public void Wire_effort_names_map_to_the_engine_ladder(string wire, string? expected) =>
        Assert.Equal(expected, CliOptions.MapEffort(wire));

    [Fact]
    public void Output_format_implies_print_mode()
    {
        // The reference's --output-format only works with --print, and passing it
        // alone behaves as print mode rather than opening a REPL.
        var options = CliOptions.From(CommandLine.Parse(["--output-format", "json"], RootOptions.Specs));
        Assert.True(options.Print);
    }

    [Fact]
    public void Positionals_join_into_the_prompt()
    {
        var options = CliOptions.From(CommandLine.Parse(["-p", "hello", "world"], RootOptions.Specs));
        Assert.Equal("hello world", options.Prompt);
    }

    [Fact]
    public void Root_help_is_the_reference_text_with_this_brand()
    {
        Assert.StartsWith("Usage: jarvis [options] [command] [prompt]", HelpTexts.Root);
        Assert.Contains("Jarvis Code - starts an interactive session by default", HelpTexts.Root);
        Assert.DoesNotContain("Claude Code", HelpTexts.Root);
        Assert.DoesNotContain("claude ", HelpTexts.Root);
    }

    [Fact]
    public void Root_help_lists_every_declared_option()
    {
        // Hidden options are the reference's own: it parses them without
        // printing them, and our help is that help byte for byte.
        foreach (var spec in RootOptions.Specs.Where(s => !s.Hidden))
        {
            Assert.Contains(spec.Long, HelpTexts.Root);
        }

        foreach (var spec in RootOptions.Specs.Where(s => s.Hidden))
        {
            Assert.DoesNotContain(HelpTexts.Root.Split('\n'), line =>
                System.Text.RegularExpressions.Regex.IsMatch(line,
                    "^  (?:-[A-Za-z], )?" + System.Text.RegularExpressions.Regex.Escape(spec.Long) + "(?:\\s|$)"));
        }
    }

    [Fact]
    public void Root_help_lists_every_subcommand()
    {
        foreach (var name in Subcommands.Names.Except(["plugins", "kill", "upgrade"]))
        {
            Assert.Contains(name, HelpTexts.Root);
        }
    }

    [Fact]
    public void Every_subcommand_has_its_own_help_text()
    {
        foreach (var name in Subcommands.Names)
        {
            var help = Subcommands.HelpFor(name);
            Assert.False(string.IsNullOrWhiteSpace(help), $"no help text for '{name}'");
            Assert.StartsWith("Usage: jarvis", help);
        }
    }

    [Fact]
    public void Unsupported_flags_are_all_real_options()
    {
        // A typo in the refusal table would silently never fire.
        foreach (var key in RootOptions.Unsupported.Keys)
        {
            Assert.Contains(RootOptions.Specs, spec => spec.Key == key);
        }
    }
}
