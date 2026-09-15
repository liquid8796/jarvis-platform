using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Cli.Repl.Input;
using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Tests;

/// <summary>
/// The REPL's key layer, driven as pure state: chord spelling, the binding
/// table's resolution rules, the keybindings.json loader's validation, the line
/// editor's readline operations, paste collapsing, history navigation and the
/// three completions. Nothing here needs a terminal.
/// </summary>
public class ReplKeyLayerTests
{
    [Theory]
    [InlineData("meta+p", "alt+p")]
    [InlineData("Control+K", "ctrl+k")]
    [InlineData("super+c", "cmd+c")]
    [InlineData("shift+ctrl+a", "ctrl+shift+a")]
    [InlineData("esc", "escape")]
    [InlineData("↑", "up")]
    public void Chords_fold_onto_the_reference_spelling(string spelling, string expected) =>
        Assert.Equal(expected, KeyChords.NormalizeChord(spelling));

    [Fact]
    public void A_sequence_normalizes_chord_by_chord() =>
        Assert.Equal("ctrl+x ctrl+e", KeyChords.NormalizeSpelling("ctrl+x  ctrl+e"));

    [Fact]
    public void A_space_is_the_space_key() => Assert.Equal("space", KeyChords.NormalizeSpelling(" "));

    [Fact]
    public void An_empty_key_part_is_a_parse_error() =>
        Assert.Equal("Empty key part in \"ctrl++k\"", KeyChords.EmptyPartError("ctrl++k"));

    [Fact]
    public void A_press_keys_the_table_by_its_normalized_chord() =>
        Assert.Equal("ctrl+shift+b", new KeyPress("b", Ctrl: true, Shift: true).Chord);

    [Fact]
    public void The_default_table_carries_the_reference_bindings()
    {
        var map = KeyMap.Default;
        Assert.Equal("chat:submit", map.Resolve("Chat", ["enter"]));
        Assert.Equal("chat:newline", map.Resolve("Chat", ["ctrl+j"]));
        Assert.Equal("chat:cycleMode", map.Resolve("Chat", ["shift+tab"]));
        Assert.Equal("chat:imagePaste", map.Resolve("Chat", ["alt+v"]));
        Assert.Equal("history:previous", map.Resolve("Chat", ["up"]));
        // Global is consulted after the focused context.
        Assert.Equal("app:toggleTranscript", map.Resolve("Chat", ["ctrl+o"]));
        Assert.Equal("select:accept", map.Resolve("Select", ["enter"]));
    }

    [Fact]
    public void A_user_block_overrides_a_default_and_null_unbinds_it()
    {
        var load = KeybindingsFile.Parse(
            """
            { "bindings": [ { "context": "Chat", "bindings": { "ctrl+j": "chat:submit", "ctrl+s": null } } ] }
            """);
        Assert.Equal(2, load.UserBindingCount);
        Assert.Equal("chat:submit", load.Map.Resolve("Chat", ["ctrl+j"]));
        Assert.Null(load.Map.Resolve("Chat", ["ctrl+s"]));
    }

    [Theory]
    [InlineData("{}", KeybindingsFile.MissingBindingsArray)]
    [InlineData("""{ "bindings": {} }""", KeybindingsFile.BindingsNotArray)]
    [InlineData("""{ "bindings": [ { "context": 3 } ] }""", KeybindingsFile.InvalidBlockStructure)]
    public void A_broken_file_reports_the_reference_sentence(string text, string expected)
    {
        var load = KeybindingsFile.Parse(text);
        Assert.Equal(expected, Assert.Single(load.Warnings).Message);
        // A file that cannot be read leaves the defaults in force.
        Assert.Equal("chat:submit", load.Map.Resolve("Chat", ["enter"]));
    }

    [Fact]
    public void An_unknown_context_names_the_valid_ones()
    {
        var load = KeybindingsFile.Parse("""{ "bindings": [ { "context": "Nope", "bindings": {} } ] }""");
        var issue = Assert.Single(load.Warnings);
        Assert.Equal("Unknown context \"Nope\"", issue.Message);
        Assert.StartsWith("Valid contexts: Global, Chat,", issue.Suggestion);
    }

    [Fact]
    public void An_unknown_action_says_the_binding_is_ignored_and_suggests_the_nearest()
    {
        var load = KeybindingsFile.Parse(
            """{ "bindings": [ { "context": "Chat", "bindings": { "ctrl+y": "chat:submitt" } } ] }""");
        var issue = Assert.Single(load.Warnings);
        Assert.Equal("Unknown action \"chat:submitt\" for \"ctrl+y\" in Chat — this binding is ignored", issue.Message);
        Assert.Equal("Did you mean \"chat:submit\"?", issue.Suggestion);
    }

    [Fact]
    public void An_unknown_namespace_lists_the_namespaces() =>
        Assert.StartsWith("Valid action namespaces: app:, strip:", KeybindingsFile.SuggestAction("frobnicate:now"));

    [Fact]
    public void A_command_binding_belongs_in_chat()
    {
        var load = KeybindingsFile.Parse(
            """{ "bindings": [ { "context": "Select", "bindings": { "ctrl+y": "command:help" } } ] }""");
        var issue = Assert.Single(load.Warnings);
        Assert.Equal("Command binding \"command:help\" must be in \"Chat\" context, not \"Select\"", issue.Message);
        Assert.Equal(KeybindingsFile.CommandContextSuggestion, issue.Suggestion);
    }

    [Fact]
    public void A_reserved_key_says_why_it_cannot_work()
    {
        var load = KeybindingsFile.Parse(
            """{ "bindings": [ { "context": "Chat", "bindings": { "ctrl+m": "chat:stash" } } ] }""");
        Assert.Contains(load.Warnings, w =>
            w.Message == "\"ctrl+m\" may not work: Cannot be rebound - identical to Enter in terminals (both send CR)");
    }

    [Fact]
    public void A_key_written_twice_in_one_block_is_reported_from_the_raw_text()
    {
        var load = KeybindingsFile.Parse(
            """{ "bindings": [ { "context": "Chat", "bindings": { "ctrl+y": "chat:stash", "ctrl+y": "chat:undo" } } ] }""");
        var duplicate = Assert.Single(load.Warnings, w => w.Type == "duplicate");
        Assert.Equal("Duplicate key \"ctrl+y\" in Chat bindings", duplicate.Message);
        Assert.Equal(KeybindingsFile.DuplicateKeySuggestion, duplicate.Suggestion);
    }

    [Fact]
    public void A_two_chord_sequence_waits_for_its_second_chord()
    {
        var router = new KeyRouter(KeyMap.Default);
        Assert.Equal(KeyRouteKind.Pending, router.Route("Chat", new KeyPress("x", Ctrl: true)).Kind);
        Assert.Equal(KeyRoute.Fires("chat:externalEditor"), router.Route("Chat", new KeyPress("e", Ctrl: true)));
        Assert.Empty(router.Pending);
    }

    [Fact]
    public void A_dead_sequence_still_gives_the_second_press_its_own_chance()
    {
        var router = new KeyRouter(KeyMap.Default);
        // ctrl+x opens three sequences; "up" continues none of them, so it
        // falls back to what it means on its own rather than being swallowed.
        router.Route("Chat", new KeyPress("x", Ctrl: true));
        Assert.Equal(KeyRoute.Fires("history:previous"), router.Route("Chat", new KeyPress("up")));
        Assert.Empty(router.Pending);
    }

    [Fact]
    public void A_sequence_that_is_bound_whole_still_fires()
    {
        var router = new KeyRouter(KeyMap.Default);
        router.Route("Chat", new KeyPress("x", Ctrl: true));
        Assert.Equal(KeyRoute.Fires("chat:queueSubmit"), router.Route("Chat", new KeyPress("enter")));
    }

    [Fact]
    public void An_unbound_press_is_reported_as_text() =>
        Assert.Equal(KeyRouteKind.Unbound, new KeyRouter(KeyMap.Default).Route("Chat", KeyPress.Typed("a")).Kind);

    [Theory]
    // The reference's default style is keyCase "title", modCase "lower": a
    // modifier stays lowercase and only a named key is title-cased.
    [InlineData("ctrl+o", "ctrl+o")]
    [InlineData("shift+tab", "shift+Tab")]
    [InlineData("escape", "Esc")]
    [InlineData("ctrl+x ctrl+e", "ctrl+x ctrl+e")]
    public void A_chord_renders_in_the_default_style(string chord, string expected) =>
        Assert.Equal(expected, ChordFormat.Format(chord));

    [Fact]
    public void A_shared_modifier_collapses_over_a_list_of_arrows() =>
        Assert.Equal("shift+↑/↓", ChordFormat.Format(["shift+up", "shift+down"]));

    [Fact]
    public void The_compact_style_writes_ctrl_as_a_caret() =>
        Assert.Equal("^o", ChordFormat.Format("ctrl+o", ChordStyle.Compact));

    [Fact]
    public void A_hint_reads_chord_then_action() =>
        Assert.Equal("ctrl+o to expand", ChordFormat.Hint("ctrl+o", "expand", ChordStyle.LowerKeys));

    [Fact]
    public void Kills_accumulate_into_one_ring_entry_and_yank_back()
    {
        var editor = new LineEditor();
        editor.Set("hello world", 11);
        editor.BackwardKillWord();
        editor.BackwardKillWord();
        Assert.Equal("", editor.Text);
        Assert.True(editor.Yank());
        Assert.Equal("hello world", editor.Text);
    }

    [Fact]
    public void A_paste_placeholder_is_deleted_whole()
    {
        var editor = new LineEditor();
        editor.Set("look at [Pasted text #1 +4 lines]");
        Assert.True(editor.DeleteTokenBefore());
        // The reference deletes from the placeholder's own start, so the space
        // before it stays: its regex skips the whitespace it matched.
        Assert.Equal("look at ", editor.Text);
    }

    [Fact]
    public void Undo_coalesces_edits_inside_the_debounce_window()
    {
        var editor = new LineEditor();
        var now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        editor.PushUndo(now);
        editor.Insert("a");
        editor.PushUndo(now.AddMilliseconds(100));
        editor.Insert("b");
        Assert.True(editor.Undo());
        Assert.Equal("", editor.Text);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void A_long_paste_collapses_and_expands_back_for_the_model()
    {
        var composer = new Composer();
        var now = DateTime.UtcNow;
        composer.Type("see ", now);
        composer.Paste(new string('x', 900), rows: 40, now);
        Assert.Equal("see [Pasted text #1]", composer.Text);
        Assert.Equal("see " + new string('x', 900), composer.Expanded);
    }

    [Fact]
    public void A_multi_line_paste_counts_its_lines_in_the_placeholder()
    {
        var composer = new Composer();
        composer.Paste("a\nb\nc\nd", rows: 40, DateTime.UtcNow);
        Assert.Equal("[Pasted text #1 +3 lines]", composer.Text);
    }

    [Fact]
    public void A_short_paste_goes_in_as_text()
    {
        var composer = new Composer();
        composer.Paste("just this", rows: 40, DateTime.UtcNow);
        Assert.Equal("just this", composer.Text);
    }

    [Fact]
    public void Pasting_again_expands_the_placeholder_in_place()
    {
        var composer = new Composer();
        var now = DateTime.UtcNow;
        composer.Paste(new string('x', 900), rows: 40, now);
        Assert.True(composer.CanExpandPaste);
        composer.Paste(new string('x', 900), rows: 40, now);
        Assert.Equal(new string('x', 900), composer.Text);
        Assert.False(composer.CanExpandPaste);
    }

    [Fact]
    public void A_backslash_before_the_newline_is_consumed()
    {
        var composer = new Composer();
        var now = DateTime.UtcNow;
        composer.Type("first\\", now);
        composer.Newline(now);
        Assert.Equal("first\n", composer.Text);
    }

    [Theory]
    [InlineData("!ls", "Bash")]
    [InlineData("#remember this", "Memory")]
    [InlineData("hello", "Prompt")]
    public void The_first_character_decides_the_composer_mode(string text, string expected)
    {
        var composer = new Composer();
        composer.Set(text);
        Assert.Equal(expected, composer.Mode.ToString());
    }

    [Fact]
    public void History_stashes_the_draft_and_gives_it_back()
    {
        var history = new PromptHistory(Path.Combine(Path.GetTempPath(), $"jarvis-history-{Guid.NewGuid():N}.jsonl"));
        history.Add("first prompt", "C:/repo", "s1");
        history.Add("second prompt", "C:/repo", "s1");
        var navigator = new HistoryNavigator(history, "C:/repo", "s1");
        var composer = new Composer();
        composer.Set("half typed");

        Assert.True(navigator.Previous(composer));
        Assert.Equal("second prompt", composer.Text);
        Assert.True(navigator.Previous(composer));
        Assert.Equal("first prompt", composer.Text);
        Assert.False(navigator.Previous(composer));
        Assert.True(navigator.Next(composer));
        Assert.True(navigator.Next(composer));
        Assert.Equal("half typed", composer.Text);
    }

    [Fact]
    public void An_immediate_repeat_is_not_stored_twice()
    {
        var history = new PromptHistory(Path.Combine(Path.GetTempPath(), $"jarvis-history-{Guid.NewGuid():N}.jsonl"));
        Assert.True(history.Add("same", "C:/repo", "s1"));
        Assert.False(history.Add("same", "C:/repo", "s1"));
    }

    [Fact]
    public void The_search_filters_and_cycles_its_scope()
    {
        var history = new PromptHistory(Path.Combine(Path.GetTempPath(), $"jarvis-history-{Guid.NewGuid():N}.jsonl"));
        history.Add("build the parser", "C:/repo", "s1");
        history.Add("run the tests", "C:/repo", "s2");
        var search = new HistorySearch(history, "C:/repo", "s1");
        search.SetQuery("tests");
        Assert.Equal("run the tests", search.Current?.Display);
        Assert.Equal(HistoryScope.Project, search.Scope);
        search.CycleScope();
        Assert.Equal(HistoryScope.Session, search.Scope);
        // Session scope is this session's own prompts, so the other session's is gone.
        Assert.Null(search.Current);
    }

    [Fact]
    public void The_slash_completion_offers_commands_and_completes_the_box()
    {
        var commands = new List<SlashMenuItem>
        {
            new() { Kind = SlashMenuItemKind.Button, Label = "compact", SkillDescription = "Free up context" },
            new() { Kind = SlashMenuItemKind.Button, Label = "context", SkillDescription = "Show usage" },
        };
        var menu = AutocompleteEngine.For("/comp", 5, commands, _ => []);
        Assert.NotNull(menu);
        Assert.Equal(AutocompleteKind.Command, menu.Kind);
        Assert.Equal("compact", menu.Selected?.Value);
        var (text, offset) = menu.Accept("/comp", 5);
        Assert.Equal("/compact ", text);
        Assert.Equal(9, offset);
    }

    [Fact]
    public void An_at_token_completes_a_path()
    {
        var menu = AutocompleteEngine.For("look at @src", 12, [], prefix =>
            prefix == "src" ? ["src/"] : []);
        Assert.NotNull(menu);
        Assert.Equal(AutocompleteKind.FileMention, menu.Kind);
        var (text, _) = menu.Accept("look at @src", 12);
        Assert.Equal("look at @src/", text);
    }

    [Fact]
    public void Shell_mode_completes_a_bare_path()
    {
        var menu = AutocompleteEngine.For("!cat REA", 8, [], prefix => prefix == "REA" ? ["README.md"] : []);
        Assert.NotNull(menu);
        Assert.Equal(AutocompleteKind.Path, menu.Kind);
        var (text, _) = menu.Accept("!cat REA", 8);
        Assert.Equal("!cat README.md ", text);
    }

    [Fact]
    public void A_double_press_fires_only_inside_the_window()
    {
        var press = new DoublePress();
        var now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(press.Press(now));
        Assert.True(press.Press(now.AddMilliseconds(400)));
        Assert.False(press.Press(now.AddSeconds(5)));
        Assert.False(press.Press(now.AddSeconds(9)));
    }

    [Fact]
    public void Vim_normal_mode_deletes_a_word()
    {
        var composer = new Composer(vimEnabled: true);
        var now = DateTime.UtcNow;
        composer.Set("hello world", 0);
        Assert.True(composer.HandleVim(new KeyPress("escape"), now));
        Assert.True(composer.HandleVim(KeyPress.Typed("d"), now));
        Assert.True(composer.HandleVim(KeyPress.Typed("w"), now));
        Assert.Equal("world", composer.Text);
        Assert.Equal("NORMAL", composer.Vim.ModeName);
    }

    [Fact]
    public void Vim_insert_mode_shows_the_reference_indicator()
    {
        var composer = new Composer(vimEnabled: true);
        Assert.Equal("-- INSERT --", composer.VimIndicator);
    }
}
