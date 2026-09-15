using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Composition;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Panels;

/// <summary>How much of a turn the transcript shows.</summary>
public enum TranscriptViewMode
{
    Normal,
    Thinking,
    Verbose,
    Summary,
}

/// <summary>What the session menu needs from whoever hosts the session.</summary>
public sealed class SessionMenuHost
{
    public required AppServices Services { get; init; }

    public required ChatSurface Chat { get; init; }

    /// <summary>Toggles a side pane by key ("artifacts", "files", "plan"…).</summary>
    public required Action<string> ShowPanel { get; init; }

    /// <summary>Whether a pane is currently open — the pane rows carry a tick when it is.</summary>
    public Func<string, bool> IsPaneOpen { get; init; } = _ => false;

    public required Func<TranscriptViewMode> GetTranscriptView { get; init; }

    public required Action<TranscriptViewMode> SetTranscriptView { get; init; }

    /// <summary>Whether the transcript holds any thinking — gates the Thinking mode row.</summary>
    public required Func<bool> SessionHasThinking { get; init; }

    /// <summary>The session list changed (renamed, forked, archived, deleted).</summary>
    public required Action SessionsChanged { get; init; }

    /// <summary>Starts the header's in-place rename editor; null falls back to the dialog.</summary>
    public Action? StartRename { get; init; }

}

/// <summary>
/// The session header's overflow menu, drawn from <see cref="SessionMenuModel.ForHeader"/>
/// so it stays row for row the reference's (`ly` in the ccd chunk `ca80fca8d`@301k):
/// the pane checkbox rows, Go to routine, Open in, then Rename · Color ▸ ·
/// Transcript view ▸ · Export · Copy link · Fork, and Archive · Delete under a rule.
/// </summary>
public static class SessionMenu
{
    public static void Show(UIElement target, SessionMenuHost host)
    {
        var rows = SessionMenuModel.ForHeader(BuildContext(host));
        var menu = SessionMenuRenderer.Build(rows, row => Invoke(host, row));
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = -186;
        menu.VerticalOffset = 6;
        menu.IsOpen = true;
    }

    /// <summary>The header's own gates, ready for <see cref="SessionMenuModel.ForHeader"/>.</summary>
    public static SessionMenuContext BuildContext(SessionMenuHost host)
    {
        var session = host.Chat.ViewModel.Session;
        var groups = host.Services.SessionGroups;
        return new SessionMenuContext
        {
            IsArchived = groups.IsArchived(session.Id),
            HasLocalPath = Directory.Exists(session.WorkingDirectory),
            HasRoutine = session.RoutineId is not null,
            Panes = PaneRows(host),
            Colors = ColorChoices(groups.ColorOf(session.Id)),
            TranscriptViews = TranscriptViewChoices(host),
            OutputStyles =
            [
                new("", "Default", !host.Services.UiSettings.Current.SessionOutputStyles.ContainsKey(session.Id)),
                .. OutputStyles.Available(session.WorkingDirectory, host.Services.Paths.Root).Select(style =>
                    new SessionMenuChoice(style.Name, style.Name,
                        host.Services.UiSettings.Current.SessionOutputStyles.GetValueOrDefault(session.Id) == style.Name)),
            ],
        };
    }

    /// <summary>
    /// The pane checkbox rows the menu leads with. Whatever the header rail could not
    /// fit comes first — the reference's own `f.slice(0, hiddenCount)` — then its pane
    /// group in its order: Artifacts, Files, Background tasks, Plan, Runs.
    /// </summary>
    public static IReadOnlyList<SessionMenuPane> PaneRows(SessionMenuHost host)
    {
        var rows = new List<SessionMenuPane>();
        foreach (var spec in host.Chat.HeaderRail.OverflowSpecs)
        {
            rows.Add(new SessionMenuPane(spec.Pane, spec.Tooltip, spec.Active, spec.Shortcut, spec.Icon));
        }

        foreach (var (key, gesture) in new (string Key, string? Gesture)[]
                 {
                     (Services.SidePanes.Artifact, null),
                     (Services.SidePanes.FileBrowser,
                         Services.PaneShortcuts.Display(Services.PaneCommand.ToggleFileBrowser)),
                     (Services.SidePanes.BackgroundTasks,
                         Services.PaneShortcuts.Display(Services.PaneCommand.BackgroundTasks)),
                     (Services.SidePanes.Plan, null),
                     (Services.SidePanes.RunHistory, null),
                 })
        {
            rows.Add(new SessionMenuPane(
                key, Services.SidePanes.Title(key), host.IsPaneOpen(key), gesture, Services.SidePanes.Icon(key)));
        }

        return rows;
    }

    /// <summary>The colour picker's rows: the reference's "Default" first, then its eight colours.</summary>
    public static IReadOnlyList<SessionMenuChoice> ColorChoices(string? current)
    {
        var rows = new List<SessionMenuChoice>
        {
            new("", SessionColors.DefaultLabel, current is null),
        };
        rows.AddRange(SessionColors.All.Select(c => new SessionMenuChoice(c.Key, c.Label, c.Key == current)));
        return rows;
    }

    /// <summary>The transcript-view rows: Thinking appears only once the session has thinking.</summary>
    public static IReadOnlyList<SessionMenuChoice> TranscriptViewChoices(SessionMenuHost host)
    {
        var hasThinking = host.SessionHasThinking();
        var current = host.GetTranscriptView();
        if (current == TranscriptViewMode.Thinking && !hasThinking)
        {
            current = TranscriptViewMode.Normal;
        }

        var rows = new List<SessionMenuChoice>();
        foreach (var mode in Enum.GetValues<TranscriptViewMode>())
        {
            if (mode == TranscriptViewMode.Thinking && !hasThinking)
            {
                continue;
            }

            rows.Add(new SessionMenuChoice(mode.ToString(), mode.ToString(), mode == current));
        }

        return rows;
    }

    /// <summary>Runs a picked row against the hosting surface.</summary>
    public static void Invoke(SessionMenuHost host, SessionMenuRow row)
    {
        var session = host.Chat.ViewModel.Session;
        var groups = host.Services.SessionGroups;
        switch (row.Action)
        {
            case SessionMenuAction.TogglePane when row.Argument is { } pane:
                host.ShowPanel(pane);
                break;
            case SessionMenuAction.Rename:
                if (host.StartRename is { } start)
                {
                    start();
                }
                else
                {
                    Rename(host);
                }

                break;
            case SessionMenuAction.SetColor:
                groups.SetColor(session.Id, string.IsNullOrEmpty(row.Argument) ? null : row.Argument);
                host.SessionsChanged();
                break;
            case SessionMenuAction.SetTranscriptView when
                Enum.TryParse<TranscriptViewMode>(row.Argument, out var mode):
                host.SetTranscriptView(mode);
                break;
            case SessionMenuAction.Export:
                _ = SessionActions.ExportAsync(host.Services, session.Id, Window.GetWindow(host.Chat));
                break;
            case SessionMenuAction.SetOutputStyle:
                if (string.IsNullOrEmpty(row.Argument)) host.Services.UiSettings.Current.SessionOutputStyles.Remove(session.Id);
                else host.Services.UiSettings.Current.SessionOutputStyles[session.Id] = row.Argument;
                host.Services.UiSettings.Save();
                break;
            case SessionMenuAction.EditOutputStyles:
                var editor = new OutputStyleEditor(host.Services, session.WorkingDirectory) { Owner = Window.GetWindow(host.Chat) };
                if (editor.ShowDialog() == true)
                {
                    host.Services.UiSettings.Current.SessionOutputStyles[session.Id] = editor.SavedName!;
                    host.Services.UiSettings.Save();
                }
                break;
            case SessionMenuAction.CopyLink:
                CopyLink(host, session.Id);
                break;
            case SessionMenuAction.GoToRoutine when session.RoutineId is { } routineId:
                (Window.GetWindow(host.Chat) as MainWindow)?.OpenRoutines(routineId: routineId);
                break;
            case SessionMenuAction.Fork:
                _ = ForkAsync(host);
                break;
            case SessionMenuAction.NewWindow:
                OpenInNewWindow(host);
                break;
            case SessionMenuAction.OpenInVsCode:
                OpenWorkingDirectory(host, "code");
                break;
            case SessionMenuAction.OpenInExplorer:
                OpenWorkingDirectory(host, "explorer");
                break;
            case SessionMenuAction.Archive:
                groups.SetArchived(session.Id, true);
                host.SessionsChanged();
                break;
            case SessionMenuAction.Unarchive:
                groups.SetArchived(session.Id, false);
                host.SessionsChanged();
                break;
            case SessionMenuAction.Delete:
                _ = DeleteAsync(host);
                break;
        }
    }

    // ---- actions ----

    private static void CopyLink(SessionMenuHost host, string sessionId)
    {
        try
        {
            Clipboard.SetText(DeepLinks.ForSession(sessionId));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; the reference is silent here too.
        }
    }

    private static void Rename(SessionMenuHost host)
    {
        var vm = host.Chat.ViewModel;
        if (InputDialog.Prompt(Window.GetWindow(host.Chat), "Rename session", vm.Session.Title, "Rename") is not { } title)
        {
            return;
        }

        _ = RenameAsync(host, title);
    }

    private static async Task RenameAsync(SessionMenuHost host, string title)
    {
        await host.Chat.ViewModel.RenameAsync(title);
        host.SessionsChanged();
    }

    private static async Task ForkAsync(SessionMenuHost host)
    {
        host.Services.Toasts.AddSuccess(Services.ToastText.Forking);
        var (fork, _) = await Services.SessionActions.ForkAsync(host.Services, host.Chat.ViewModel.Session);
        if (fork is null)
        {
            host.Services.Toasts.AddError(Services.ToastText.ForkUnavailable);
            return;
        }

        host.Chat.LoadSession(fork);
        host.SessionsChanged();
    }

    private static async Task DeleteAsync(SessionMenuHost host)
    {
        var session = host.Chat.ViewModel.Session;
        var changes = await Services.WorktreeChanges.UncommittedAsync(session.WorkingDirectory);
        var confirmed = changes.Count > 0
            ? ConfirmDialog.Ask(
                Window.GetWindow(host.Chat),
                Services.SessionDialogs.Uncommitted(
                    Services.SessionDialogAction.Delete, changes, moreSessionsFollow: false))
            : ConfirmDialog.Ask(
                Window.GetWindow(host.Chat),
                Services.SessionDialogs.DeleteTitle(1),
                Services.SessionDialogs.DeleteBody(session.Title),
                Services.SessionDialogs.Delete,
                focusCancel: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await host.Services.Sessions.DeleteAsync(session.Id);
            // The session's team goes with it, worktrees that still hold
            // nothing included; one with work in it is kept.
            host.Chat.ViewModel.Teams.CleanupSession(session.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            host.Services.Toasts.AddError(Services.ToastText.DeleteFailed);
            return;
        }

        host.Chat.ForgetSession(session.Id);
        host.Chat.StartNew(session.WorkingDirectory);
        host.SessionsChanged();
    }

    private static void OpenInNewWindow(SessionMenuHost host)
    {
        var surface = new ChatSurface();
        var window = new Window
        {
            Title = host.Chat.ViewModel.Session.Title,
            Width = 1080,
            Height = 760,
            Content = surface,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Bg100Brush");
        window.Show();
        surface.Initialize(host.Services, isCodeSurface: true);
        surface.LoadSession(host.Chat.ViewModel.Session);
    }

    private static void OpenWorkingDirectory(SessionMenuHost host, string target)
    {
        OpenFolder(Window.GetWindow(host.Chat), host.Chat.ViewModel.Session.WorkingDirectory, target);
    }

    /// <summary>Opens a folder in VS Code or Explorer; shared with the working-directory menu.</summary>
    public static void OpenFolder(Window? owner, string cwd, string target)
    {
        if (!Directory.Exists(cwd))
        {
            MessageBox.Show(owner, $"The project folder no longer exists:\n{cwd}", "Jarvis Code");
            return;
        }

        try
        {
            var psi = target == "code"
                // code is a .cmd shim, so it needs the shell to resolve it on PATH.
                ? new ProcessStartInfo("cmd.exe", $"/c code \"{cwd}\"") { CreateNoWindow = true, UseShellExecute = false }
                : new ProcessStartInfo("explorer.exe", $"\"{cwd}\"") { UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            MessageBox.Show(
                owner,
                target == "code"
                    ? $"Could not start VS Code. Is code on PATH?\n{ex.Message}"
                    : $"Could not open the folder: {ex.Message}",
                "Jarvis Code");
        }
    }

    /// <summary>A dimmed group label ("Open in", "Layout") — shared with the panel menu.</summary>
    public static MenuItem SectionHeader(string text)
    {
        var item = new MenuItem { Header = text };
        item.SetResourceReference(FrameworkElement.StyleProperty, "MenuSectionHeader");
        return item;
    }
}
