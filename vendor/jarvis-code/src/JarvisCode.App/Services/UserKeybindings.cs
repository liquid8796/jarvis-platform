using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;

namespace JarvisCode.App.Services;

/// <summary>One parsed user binding: an action id and the gesture that fires it.</summary>
public sealed record UserKeybinding(string Action, ModifierKeys Modifiers, Key Key);

/// <summary>
/// keybindings.json, the desktop port: {"bindings": {"command-palette": "ctrl+p"}}.
/// User gestures run in addition to the built-in chords — an override adds a way
/// in, it never removes the default. Unknown actions and unparsable gestures are
/// skipped so a broken file can't take the keyboard down.
/// </summary>
public static class UserKeybindings
{
    /// <summary>Action ids the window knows how to execute.</summary>
    public static readonly IReadOnlyList<string> KnownActions =
    [
        "command-palette", "theme-picker", "settings", "new-session", "find",
        "model-menu", "mode-menu", "effort-menu", "shortcuts", "focus-composer",
        "transcript-view",
        // Every pane command the default keymap declares, so a rebind names the same
        // thing the built-in chord does. Taken from the enum rather than retyped: two
        // lists would let a new command exist with no way to bind it.
        .. Enum.GetNames<PaneCommand>(),
    ];

    public const string Template =
        """
        {
          // Extra keyboard chords, in addition to the built-in ones.
          // Actions: command-palette, theme-picker, settings, new-session, find,
          //          model-menu, mode-menu, effort-menu, shortcuts, focus-composer,
          //          transcript-view, and every pane command the keymap declares
          //          (TogglePreview, ToggleDiff, ToggleTerminal, ToggleFileBrowser,
          //           ClosePane, ToggleSideChat, BackgroundTasks, ToggleSelectionMode,
          //           NewPreviewTab, ...)
          // Gestures: modifiers ctrl/shift/alt joined with '+', then a key
          //           (a letter, digit, f1-f24, comma, period, slash...).
          "bindings": {
            // "command-palette": "ctrl+p"
          }
        }
        """;

    public static IReadOnlyList<UserKeybinding> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (root?["bindings"] is not JsonObject bindings)
                return [];

            var result = new List<UserKeybinding>();
            foreach (var (action, value) in bindings)
            {
                // The canonical spelling is kept rather than a lowercased one: the pane
                // actions carry the reference's own camelCase command names, and the
                // window dispatches on those.
                var known = KnownActions.FirstOrDefault(
                    a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));
                if (known is null)
                    continue;
                if (value is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? gesture))
                    continue;
                if (TryParseGesture(gesture, out var modifiers, out var key))
                    result.Add(new UserKeybinding(known, modifiers, key));
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>"ctrl+shift+p" → (Control|Shift, P). False when any part is unknown.</summary>
    public static bool TryParseGesture(string gesture, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "win" or "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    return false;
            }
        }

        var keyName = parts[^1].ToLowerInvariant() switch
        {
            "comma" or "," => "OemComma",
            "period" or "." => "OemPeriod",
            "slash" or "/" => "OemQuestion",
            "backslash" => "Oem5",
            "minus" or "-" => "OemMinus",
            "plus" or "=" => "OemPlus",
            "space" => "Space",
            var digit when digit.Length == 1 && char.IsAsciiDigit(digit[0]) => "D" + digit,
            var other => other,
        };
        return Enum.TryParse(keyName, ignoreCase: true, out key) && key != Key.None;
    }

    /// <summary>The action for a pressed gesture, or null.</summary>
    public static string? Match(IReadOnlyList<UserKeybinding> bindings, ModifierKeys modifiers, Key key) =>
        bindings.FirstOrDefault(b => b.Modifiers == modifiers && b.Key == key)?.Action;
}
