using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Keys;

/// <summary>One binding: a chord sequence (one or two chords) → an action, in a context.</summary>
internal sealed record KeyBinding(IReadOnlyList<string> Chord, string? Action, string Context)
{
    public string Spelling => string.Join(' ', Chord);
}

/// <summary>
/// The reference CLI's default keybinding table, measured out of
/// <c>claude.exe</c> 2.1.257 (its <c>MG</c> array): every context, every chord
/// and every action, with the two platform choices the reference makes on
/// Windows — image paste on <c>alt+v</c> (<c>lNo</c>) and mode cycling on
/// <c>shift+tab</c> (<c>qNn</c>, Bun ≥ 1.2.23).
/// </summary>
internal static class KeyBindings
{
    public const string ImagePasteChord = "alt+v";
    public const string CycleModeChord = "shift+tab";

    /// <summary>The reference's <c>Y4e</c>: every context a block may name, in its order.</summary>
    public static readonly IReadOnlyList<string> Contexts =
    [
        "Global", "Chat", "Autocomplete", "Confirmation", "Help", "ProactivityMenu", "Transcript",
        "HistorySearch", "Task", "ThemePicker", "Settings", "Tabs", "Attachments", "Footer",
        "MessageSelector", "DiffDialog", "DiffPanel", "ModelPicker", "EffortSlider", "Select",
        "Plugin", "Scroll", "Agents",
    ];

    /// <summary>The reference's <c>uKn</c>: what each context means, for the keybindings file's own docs.</summary>
    public static readonly IReadOnlyDictionary<string, string> ContextDescriptions = new Dictionary<string, string>
    {
        ["Global"] = "Active everywhere, regardless of focus",
        ["Chat"] = "When the chat input is focused",
        ["Autocomplete"] = "When autocomplete menu is visible",
        ["Confirmation"] = "When a confirmation/permission dialog is shown",
        ["Help"] = "When the help overlay is open",
        ["ProactivityMenu"] = "When the proactivity menu is open",
        ["Transcript"] = "When viewing the transcript",
        ["HistorySearch"] = "When searching command history (ctrl+r)",
        ["Task"] = "When a task/agent is running in the foreground",
        ["ThemePicker"] = "When the theme picker is open",
        ["Settings"] = "When the settings menu is open",
        ["Tabs"] = "When tab navigation is active",
        ["Attachments"] = "When navigating image attachments in a select dialog",
        ["Footer"] = "When footer indicators are focused",
        ["MessageSelector"] = "When the message selector (rewind) is open",
        ["DiffDialog"] = "When the diff dialog is open",
        ["DiffPanel"] = "When the diff sidebar panel is open",
        ["ModelPicker"] = "When the model picker is open",
        ["EffortSlider"] = "When the effort slider is open",
        ["Select"] = "When a select/list component is focused",
        ["Plugin"] = "When the plugin dialog is open",
        ["Scroll"] = "When a scrollable view is focused (fullscreen layout)",
        ["Agents"] = "When the agents view (`claude agents`) is open",
    };

    /// <summary>The reference's <c>eEe</c>: every action a binding may name.</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        "app:interrupt", "app:exit", "app:toggleTodos", "app:toggleTranscript", "app:toggleBrief",
        "app:toggleReplTab", "app:toggleDiffNoiseFilter", "app:diffFileListUp", "app:diffFileListDown",
        "app:toggleDiffPreSession", "app:cycleDiffBase", "app:toggleTerminal", "app:redraw", "app:openArtifact",
        "strip:jump1", "strip:jump2", "strip:jump3", "strip:jump4", "strip:jump5", "strip:jump6", "strip:jump7",
        "strip:jump8", "strip:jump9", "strip:next", "strip:previous", "strip:toggle", "strip:new",
        "history:search", "history:previous", "history:next",
        "chat:cancel", "chat:killAgents", "chat:cycleMode", "chat:cycleProactivity", "chat:attentionUp",
        "chat:attentionDown", "chat:modelPicker", "chat:fastMode", "chat:thinkingToggle",
        "chat:workflowKeywordToggle", "chat:submit", "chat:queueSubmit", "chat:newline", "chat:undo",
        "chat:externalEditor", "chat:stash", "chat:imagePaste", "chat:clearInput", "chat:clearScreen",
        "autocomplete:accept", "autocomplete:dismiss", "autocomplete:previous", "autocomplete:next",
        "confirm:yes", "confirm:no", "confirm:previous", "confirm:next", "confirm:nextField",
        "confirm:previousField", "confirm:cycleMode", "confirm:toggle",
        "tabs:next", "tabs:previous",
        "transcript:toggleShowAll", "transcript:exit",
        "historySearch:next", "historySearch:accept", "historySearch:cancel", "historySearch:execute",
        "historySearch:cycleScope",
        "task:background",
        "theme:toggleSyntaxHighlighting", "theme:editCustom",
        "help:dismiss",
        "proactivityMenu:previousLevel", "proactivityMenu:nextLevel", "proactivityMenu:previousMode",
        "proactivityMenu:nextMode", "proactivityMenu:dismiss",
        "attachments:next", "attachments:previous", "attachments:remove", "attachments:exit",
        "footer:up", "footer:down", "footer:next", "footer:previous", "footer:openSelected",
        "footer:clearSelection", "footer:close", "footer:dismiss",
        "messageSelector:up", "messageSelector:down", "messageSelector:top", "messageSelector:bottom",
        "messageSelector:select",
        "diff:dismiss", "diff:previousSource", "diff:nextSource", "diff:back", "diff:viewDetails",
        "diff:previousFile", "diff:nextFile",
        "modelPicker:decreaseEffort", "modelPicker:increaseEffort", "modelPicker:thisSessionOnly",
        "effortSlider:thisSessionOnly",
        "select:next", "select:previous", "select:pageUp", "select:pageDown", "select:first", "select:last",
        "select:accept", "select:cancel",
        "plugin:toggle", "plugin:install", "plugin:favorite",
        "permission:toggleDebug",
        "settings:search", "settings:retry", "settings:periodDay", "settings:periodWeek", "settings:sortByTokens",
        "voice:pushToTalk",
        "scroll:pageUp", "scroll:pageDown", "scroll:lineUp", "scroll:lineDown", "scroll:top", "scroll:bottom",
        "scroll:halfPageUp", "scroll:halfPageDown", "scroll:fullPageUp", "scroll:fullPageDown",
        "selection:copy", "selection:clear", "selection:extendLeft", "selection:extendRight",
        "selection:extendUp", "selection:extendDown", "selection:extendLineStart", "selection:extendLineEnd",
        "agents:switchView", "agents:togglePin",
    ];

    private static readonly HashSet<string> ActionSet = new(Actions, StringComparer.Ordinal);

    /// <summary>The reference's <c>hBe</c>: a known action, or a <c>command:&lt;name&gt;</c> binding.</summary>
    public static bool IsValidAction(string action) =>
        ActionSet.Contains(action) || action.StartsWith("command:", StringComparison.Ordinal);

    /// <summary>A reserved key and why rebinding it cannot work (the reference's <c>X4e</c> + <c>Eun</c>).</summary>
    public sealed record ReservedKey(string Key, string Reason, string Severity);

    public static readonly IReadOnlyList<ReservedKey> Reserved =
    [
        new("ctrl+c", "Cannot be rebound - used for interrupt/exit (hardcoded)", "error"),
        new("ctrl+d", "Cannot be rebound - used for exit (hardcoded)", "error"),
        new("ctrl+m", "Cannot be rebound - identical to Enter in terminals (both send CR)", "error"),
        new("ctrl+[", "Cannot be rebound - identical to Escape in terminals", "error"),
        new("ctrl+i", "Cannot be rebound - identical to Tab in terminals", "error"),
        new("ctrl+h", "Cannot be rebound - identical to Backspace in terminals", "error"),
        new("capslock", "Caps Lock is not delivered to terminal applications", "error"),
        new("ctrl+z", "Unix process suspend (SIGTSTP)", "warning"),
        new("ctrl+\\", "Terminal quit signal (SIGQUIT)", "error"),
    ];

    private static KeyBinding B(string context, string chord, string action) =>
        new(chord.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyChords.NormalizeChord).ToArray(),
            action, context);

    /// <summary>The default table, in the reference's block order.</summary>
    public static readonly IReadOnlyList<KeyBinding> Defaults =
    [
        // Global
        B("Global", "ctrl+c", "app:interrupt"),
        B("Global", "ctrl+d", "app:exit"),
        B("Global", "ctrl+t", "app:toggleTodos"),
        B("Global", "ctrl+o", "app:toggleTranscript"),
        B("Global", "ctrl+tab", "strip:next"),
        B("Global", "ctrl+shift+tab", "strip:previous"),
        B("Global", "meta+n", "strip:new"),
        B("Global", "meta+1", "strip:jump1"),
        B("Global", "meta+2", "strip:jump2"),
        B("Global", "meta+3", "strip:jump3"),
        B("Global", "meta+4", "strip:jump4"),
        B("Global", "meta+5", "strip:jump5"),
        B("Global", "meta+6", "strip:jump6"),
        B("Global", "meta+7", "strip:jump7"),
        B("Global", "meta+8", "strip:jump8"),
        B("Global", "meta+9", "strip:jump9"),
        B("Global", "ctrl+shift+b", "app:toggleBrief"),
        B("Global", "ctrl+r", "history:search"),
        B("Global", "ctrl+up", "app:diffFileListUp"),
        B("Global", "ctrl+down", "app:diffFileListDown"),
        B("Global", "meta+up", "app:diffFileListUp"),
        B("Global", "meta+down", "app:diffFileListDown"),
        B("Global", "ctrl+]", "app:openArtifact"),
        // DiffPanel
        B("DiffPanel", "ctrl+x b", "app:cycleDiffBase"),
        // Chat
        B("Chat", "escape", "chat:cancel"),
        B("Chat", "ctrl+l", "chat:clearInput"),
        B("Chat", "cmd+k", "chat:clearScreen"),
        B("Chat", "ctrl+x ctrl+k", "chat:killAgents"),
        B("Chat", CycleModeChord, "chat:cycleMode"),
        B("Chat", "meta+p", "chat:modelPicker"),
        B("Chat", "meta+o", "chat:fastMode"),
        B("Chat", "meta+t", "chat:thinkingToggle"),
        B("Chat", "meta+w", "chat:workflowKeywordToggle"),
        B("Chat", "enter", "chat:submit"),
        B("Chat", "ctrl+x enter", "chat:queueSubmit"),
        B("Chat", "ctrl+j", "chat:newline"),
        B("Chat", "up", "history:previous"),
        B("Chat", "down", "history:next"),
        B("Chat", "ctrl+_", "chat:undo"),
        B("Chat", "ctrl+-", "chat:undo"),
        B("Chat", "ctrl+shift+-", "chat:undo"),
        B("Chat", "ctrl+shift+_", "chat:undo"),
        B("Chat", "ctrl+x ctrl+e", "chat:externalEditor"),
        B("Chat", "ctrl+g", "chat:externalEditor"),
        B("Chat", "ctrl+s", "chat:stash"),
        B("Chat", ImagePasteChord, "chat:imagePaste"),
        B("Chat", "space", "voice:pushToTalk"),
        // Autocomplete
        B("Autocomplete", "tab", "autocomplete:accept"),
        B("Autocomplete", "escape", "autocomplete:dismiss"),
        B("Autocomplete", "up", "autocomplete:previous"),
        B("Autocomplete", "down", "autocomplete:next"),
        // Settings
        B("Settings", "escape", "confirm:no"),
        B("Settings", "up", "select:previous"),
        B("Settings", "down", "select:next"),
        B("Settings", "k", "select:previous"),
        B("Settings", "j", "select:next"),
        B("Settings", "ctrl+p", "select:previous"),
        B("Settings", "ctrl+n", "select:next"),
        B("Settings", "space", "select:accept"),
        B("Settings", "enter", "select:accept"),
        B("Settings", "/", "settings:search"),
        B("Settings", "r", "settings:retry"),
        B("Settings", "d", "settings:periodDay"),
        B("Settings", "w", "settings:periodWeek"),
        B("Settings", "t", "settings:sortByTokens"),
        B("Settings", "ctrl+u", "scroll:halfPageUp"),
        B("Settings", "ctrl+d", "scroll:halfPageDown"),
        // Confirmation
        B("Confirmation", "y", "confirm:yes"),
        B("Confirmation", "n", "confirm:no"),
        B("Confirmation", "enter", "confirm:yes"),
        B("Confirmation", "escape", "confirm:no"),
        B("Confirmation", "up", "confirm:previous"),
        B("Confirmation", "down", "confirm:next"),
        B("Confirmation", "tab", "confirm:nextField"),
        B("Confirmation", "space", "confirm:toggle"),
        B("Confirmation", CycleModeChord, "confirm:cycleMode"),
        // Tabs
        B("Tabs", "tab", "tabs:next"),
        B("Tabs", "shift+tab", "tabs:previous"),
        B("Tabs", "right", "tabs:next"),
        B("Tabs", "left", "tabs:previous"),
        // Transcript
        B("Transcript", "ctrl+e", "transcript:toggleShowAll"),
        B("Transcript", "ctrl+c", "transcript:exit"),
        B("Transcript", "escape", "transcript:exit"),
        B("Transcript", "q", "transcript:exit"),
        B("Transcript", "ctrl+u", "scroll:halfPageUp"),
        B("Transcript", "ctrl+d", "scroll:halfPageDown"),
        B("Transcript", "ctrl+b", "scroll:fullPageUp"),
        B("Transcript", "ctrl+f", "scroll:fullPageDown"),
        B("Transcript", "ctrl+n", "scroll:lineDown"),
        B("Transcript", "ctrl+p", "scroll:lineUp"),
        B("Transcript", "g", "scroll:top"),
        B("Transcript", "shift+g", "scroll:bottom"),
        B("Transcript", "j", "scroll:lineDown"),
        B("Transcript", "k", "scroll:lineUp"),
        B("Transcript", "space", "scroll:fullPageDown"),
        B("Transcript", "b", "scroll:fullPageUp"),
        B("Transcript", "up", "scroll:lineUp"),
        B("Transcript", "down", "scroll:lineDown"),
        B("Transcript", "home", "scroll:top"),
        B("Transcript", "end", "scroll:bottom"),
        // HistorySearch
        B("HistorySearch", "ctrl+r", "historySearch:next"),
        B("HistorySearch", "escape", "historySearch:accept"),
        B("HistorySearch", "tab", "historySearch:accept"),
        B("HistorySearch", "ctrl+c", "historySearch:cancel"),
        B("HistorySearch", "enter", "historySearch:execute"),
        B("HistorySearch", "ctrl+s", "historySearch:cycleScope"),
        // Task
        B("Task", "ctrl+x ctrl+b", "task:background"),
        B("Task", "ctrl+b", "task:background"),
        // ThemePicker
        B("ThemePicker", "ctrl+t", "theme:toggleSyntaxHighlighting"),
        B("ThemePicker", "ctrl+e", "theme:editCustom"),
        // Scroll
        B("Scroll", "pageup", "scroll:pageUp"),
        B("Scroll", "pagedown", "scroll:pageDown"),
        B("Scroll", "wheelup", "scroll:lineUp"),
        B("Scroll", "wheeldown", "scroll:lineDown"),
        B("Scroll", "ctrl+home", "scroll:top"),
        B("Scroll", "ctrl+end", "scroll:bottom"),
        B("Scroll", "ctrl+shift+c", "selection:copy"),
        B("Scroll", "cmd+c", "selection:copy"),
        B("Scroll", "shift+left", "selection:extendLeft"),
        B("Scroll", "shift+right", "selection:extendRight"),
        B("Scroll", "shift+up", "selection:extendUp"),
        B("Scroll", "shift+down", "selection:extendDown"),
        B("Scroll", "shift+home", "selection:extendLineStart"),
        B("Scroll", "shift+end", "selection:extendLineEnd"),
        // Help
        B("Help", "escape", "help:dismiss"),
        // Attachments
        B("Attachments", "right", "attachments:next"),
        B("Attachments", "left", "attachments:previous"),
        B("Attachments", "backspace", "attachments:remove"),
        B("Attachments", "delete", "attachments:remove"),
        B("Attachments", "down", "attachments:exit"),
        B("Attachments", "escape", "attachments:exit"),
        // Footer
        B("Footer", "up", "footer:up"),
        B("Footer", "ctrl+p", "footer:up"),
        B("Footer", "down", "footer:down"),
        B("Footer", "ctrl+n", "footer:down"),
        B("Footer", "right", "footer:next"),
        B("Footer", "left", "footer:previous"),
        B("Footer", "enter", "footer:openSelected"),
        B("Footer", "escape", "footer:clearSelection"),
        B("Footer", "x", "footer:close"),
        B("Footer", "backspace", "footer:dismiss"),
        B("Footer", "delete", "footer:dismiss"),
        // MessageSelector
        B("MessageSelector", "up", "messageSelector:up"),
        B("MessageSelector", "down", "messageSelector:down"),
        B("MessageSelector", "k", "messageSelector:up"),
        B("MessageSelector", "j", "messageSelector:down"),
        B("MessageSelector", "ctrl+p", "messageSelector:up"),
        B("MessageSelector", "ctrl+n", "messageSelector:down"),
        B("MessageSelector", "ctrl+up", "messageSelector:top"),
        B("MessageSelector", "shift+up", "messageSelector:top"),
        B("MessageSelector", "meta+up", "messageSelector:top"),
        B("MessageSelector", "shift+k", "messageSelector:top"),
        B("MessageSelector", "ctrl+down", "messageSelector:bottom"),
        B("MessageSelector", "shift+down", "messageSelector:bottom"),
        B("MessageSelector", "meta+down", "messageSelector:bottom"),
        B("MessageSelector", "shift+j", "messageSelector:bottom"),
        B("MessageSelector", "enter", "messageSelector:select"),
        // DiffDialog
        B("DiffDialog", "escape", "diff:dismiss"),
        B("DiffDialog", "left", "diff:previousSource"),
        B("DiffDialog", "right", "diff:nextSource"),
        B("DiffDialog", "up", "diff:previousFile"),
        B("DiffDialog", "down", "diff:nextFile"),
        B("DiffDialog", "enter", "diff:viewDetails"),
        B("DiffDialog", "j", "diff:nextFile"),
        B("DiffDialog", "k", "diff:previousFile"),
        B("DiffDialog", "pageup", "scroll:pageUp"),
        B("DiffDialog", "pagedown", "scroll:pageDown"),
        B("DiffDialog", "space", "scroll:fullPageDown"),
        B("DiffDialog", "shift+space", "scroll:fullPageUp"),
        B("DiffDialog", "b", "scroll:fullPageUp"),
        B("DiffDialog", "g", "scroll:top"),
        B("DiffDialog", "shift+g", "scroll:bottom"),
        B("DiffDialog", "home", "scroll:top"),
        B("DiffDialog", "end", "scroll:bottom"),
        // ModelPicker
        B("ModelPicker", "left", "modelPicker:decreaseEffort"),
        B("ModelPicker", "right", "modelPicker:increaseEffort"),
        B("ModelPicker", "s", "modelPicker:thisSessionOnly"),
        // EffortSlider
        B("EffortSlider", "s", "effortSlider:thisSessionOnly"),
        // Select
        B("Select", "up", "select:previous"),
        B("Select", "down", "select:next"),
        B("Select", "j", "select:next"),
        B("Select", "k", "select:previous"),
        B("Select", "ctrl+n", "select:next"),
        B("Select", "ctrl+p", "select:previous"),
        B("Select", "pageup", "select:pageUp"),
        B("Select", "pagedown", "select:pageDown"),
        B("Select", "home", "select:first"),
        B("Select", "end", "select:last"),
        B("Select", "enter", "select:accept"),
        B("Select", "escape", "select:cancel"),
        // Plugin
        B("Plugin", "space", "plugin:toggle"),
        B("Plugin", "i", "plugin:install"),
        B("Plugin", "f", "plugin:favorite"),
        // Agents
        B("Agents", "ctrl+s", "agents:switchView"),
        B("Agents", "ctrl+t", "agents:togglePin"),
    ];
}

/// <summary>
/// A resolved binding set: the defaults with the user's blocks appended (a
/// later block wins; a <c>null</c> action unbinds the default), and the two
/// lookups the REPL runs — which action a chord fires in a context, and which
/// chord an action is bound to (for every "ctrl+o to expand" hint).
/// </summary>
internal sealed class KeyMap
{
    private readonly IReadOnlyList<KeyBinding> _bindings;

    public KeyMap(IReadOnlyList<KeyBinding> bindings)
    {
        _bindings = bindings;
    }

    public static KeyMap Default { get; } = new(KeyBindings.Defaults);

    public IReadOnlyList<KeyBinding> Bindings => _bindings;

    /// <summary>
    /// The action bound to a chord sequence in a context, or null. Later
    /// bindings override earlier ones (a user block after the defaults), and a
    /// null action is an explicit unbind. <c>Global</c> is consulted after the
    /// named context, as the reference's dispatch does.
    /// </summary>
    public string? Resolve(string context, IReadOnlyList<string> chords)
    {
        return ResolveIn(context, chords) ?? (context == "Global" ? null : ResolveIn("Global", chords));
    }

    private string? ResolveIn(string context, IReadOnlyList<string> chords)
    {
        string? found = null;
        bool matched = false;
        foreach (var binding in _bindings)
        {
            if (binding.Context != context || !SameChord(binding.Chord, chords))
            {
                continue;
            }

            matched = true;
            found = binding.Action;
        }

        return matched ? found : null;
    }

    /// <summary>True when some binding in the context starts with these chords (a pending two-chord sequence).</summary>
    public bool IsPrefix(string context, IReadOnlyList<string> chords)
    {
        foreach (var binding in _bindings)
        {
            if ((binding.Context == context || binding.Context == "Global") &&
                binding.Chord.Count > chords.Count && binding.Action is not null)
            {
                bool prefix = true;
                for (int i = 0; i < chords.Count; i++)
                {
                    if (binding.Chord[i] != chords[i])
                    {
                        prefix = false;
                        break;
                    }
                }

                if (prefix)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The chord an action is bound to in a context — the last binding wins, as
    /// with dispatch — or the fallback spelling when nothing binds it (the
    /// reference's <c>go</c>/<c>Zd</c> hint lookups).
    /// </summary>
    public string ChordFor(string action, string context, string fallback)
    {
        string? found = null;
        foreach (var binding in _bindings)
        {
            if (binding.Context == context && binding.Action == action)
            {
                found = binding.Spelling;
            }
        }

        return found ?? fallback;
    }

    private static bool SameChord(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
