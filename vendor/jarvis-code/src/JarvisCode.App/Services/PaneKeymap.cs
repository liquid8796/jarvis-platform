using System.Windows.Input;

namespace JarvisCode.App.Services;

/// <summary>
/// The chords the Code surface answers to, ported row for row from the reference
/// desktop's own keymap (<c>LI</c> in <c>shared-16-DFDNRrwQ.js</c>).
///
/// Its rows carry a <c>when</c> gate — <c>isClaudeApp</c> or <c>!isClaudeApp</c>
/// — which decides whether a chord belongs to the desktop app or the web client.
/// This app is the desktop one, so the web-only rows are dropped here rather
/// than carried and never matched.
/// </summary>
public enum PaneCommand
{
    TogglePreview,
    ToggleDiff,
    ToggleTerminal,
    ToggleFileBrowser,
    ClosePane,
    ToggleSideChat,
    CycleTranscriptMode,
    BackgroundTasks,
    OpenModeMenu,
    OpenModelMenu,
    OpenEffortMenu,
    CycleChipLevel,
    ToggleSelectionMode,
    NewPreviewTab,
    JumpPrevPrompt,
    JumpNextPrompt,
    AttachTerminalOutput,
    ToggleDiffFileList,
}

/// <summary>One keymap row: the command, and the chord that fires it here.</summary>
public sealed record PaneChord(PaneCommand Command, ModifierKeys Modifiers, Key Key);

public static class PaneKeymap
{
    private const ModifierKeys Ctrl = ModifierKeys.Control;
    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
    private const ModifierKeys CtrlAlt = ModifierKeys.Control | ModifierKeys.Alt;

    /// <summary>
    /// The reference's rows in its order, with its <c>cmd</c> read as Ctrl on
    /// this platform. The first row whose chord matches wins, which is how the
    /// reference resolves <c>cmd+shift+b</c> before <c>cmd+shift+p</c>.
    /// </summary>
    public static IReadOnlyList<PaneChord> Chords { get; } =
    [
        new(PaneCommand.TogglePreview, CtrlShift, Key.B),
        new(PaneCommand.TogglePreview, CtrlShift, Key.P),
        new(PaneCommand.ToggleDiff, CtrlShift, Key.D),
        new(PaneCommand.ToggleTerminal, Ctrl, Key.OemTilde),
        new(PaneCommand.ToggleFileBrowser, CtrlShift, Key.F),
        new(PaneCommand.ClosePane, Ctrl, Key.Oem5),
        new(PaneCommand.ToggleSideChat, Ctrl, Key.OemSemicolon),
        new(PaneCommand.CycleTranscriptMode, Ctrl, Key.O),
        new(PaneCommand.BackgroundTasks, Ctrl, Key.B),
        new(PaneCommand.OpenModeMenu, CtrlShift, Key.M),
        new(PaneCommand.OpenModeMenu, CtrlAlt, Key.M),
        new(PaneCommand.OpenModelMenu, CtrlShift, Key.I),
        new(PaneCommand.OpenEffortMenu, CtrlShift, Key.E),
        new(PaneCommand.CycleChipLevel, ModifierKeys.Shift, Key.Tab),
        new(PaneCommand.ToggleSelectionMode, CtrlShift, Key.S),
        new(PaneCommand.NewPreviewTab, Ctrl, Key.T),
        // The reference's non-Mac prompt jumps, which its Mac rows spell
        // cmd+alt+up/down. Moving these off Ctrl+arrow is the reference's own
        // change; this app used to carry the older pair.
        new(PaneCommand.JumpPrevPrompt, ModifierKeys.Alt, Key.Up),
        new(PaneCommand.JumpNextPrompt, ModifierKeys.Alt, Key.Down),

        // The reference's row sits here, between the prompt jumps and the
        // terminal grab, and spells its non-Mac chord ctrl+shift+y.
        new(PaneCommand.ToggleDiffFileList, CtrlShift, Key.Y),
        new(PaneCommand.AttachTerminalOutput, CtrlShift, Key.L),
    ];

    /// <summary>The command a chord fires, or null when nothing claims it.</summary>
    public static PaneCommand? Match(ModifierKeys modifiers, Key key)
    {
        foreach (var chord in Chords)
        {
            if (chord.Modifiers == modifiers && chord.Key == key)
            {
                return chord.Command;
            }
        }

        return null;
    }
}
