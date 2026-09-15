using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Panels;

namespace JarvisCode.App.Views;

/// <summary>
/// The Code surface's chrome around the transcript, ported from the reference
/// desktop (1.40609.1.0): the session header's in-place rename, the home view's
/// action center, the PR bar, the working-directory row and the composer's "+"
/// menu and context ring. It lives beside <c>ChatSurface.xaml.cs</c> so those
/// regions can be read on their own.
/// </summary>
public partial class ChatSurface
{
    private HomeView? _homeView;
    private GitBarView? _gitBar;
    private GitBarInput _gitBarInput = new();
    private bool _headerRenaming;

    // ---- session header: the reference's `_h` title and `jh` rename affordance ----

    private void OnSessionTitleClick(object sender, RoutedEventArgs e) => BeginHeaderRename();

    /// <summary>
    /// The reference's own key guard on the title button
    /// (<c>e.key === "Enter" &amp;&amp; e.repeat &amp;&amp; e.preventDefault()</c>): holding
    /// Enter down must not re-enter the editor once per repeat.
    /// </summary>
    private void OnSessionTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.IsRepeat)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The reference hands the title to its session menu as the <c>contextMenuTrigger</c>,
    /// so right-clicking the name opens the same menu the ⋮ does.
    /// </summary>
    private void OnSessionTitleContextMenu(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowSessionMenu(SessionTitleButton);
    }

    /// <summary>
    /// Swaps the header title for the reference's in-place editor. Enter commits,
    /// Escape cancels, and losing focus commits — <see cref="InlineRenameBox"/>'s
    /// port of its `Fe`/`Pe` handlers, in the bare shape 1.44121.4.0 gives the titlebar.
    /// </summary>
    public void BeginHeaderRename()
    {
        if (_vm is null || _headerRenaming)
        {
            return;
        }

        _headerRenaming = true;
        SessionTitleButton.Visibility = Visibility.Collapsed;
        SessionTitleEditorHost.Visibility = Visibility.Visible;
        SessionTitleEditorHost.Content = new InlineRenameBox(
            _vm.Session.Title,
            name => _ = RenameSessionAsync(name),
            () =>
            {
                _headerRenaming = false;
                SessionTitleEditorHost.Content = null;
                SessionTitleEditorHost.Visibility = Visibility.Collapsed;
                SessionTitleButton.Visibility = Visibility.Visible;
                SessionTitleHost.InvalidateMeasure();
            },
            InlineRenameLook.TitleBar);
        SessionTitleHost.InvalidateMeasure();
    }

    /// <summary>
    /// The reference's `Qp`: while the artifact pane is in the layout but is not the tile
    /// filling the host, the titlebar offers a way to expand it.
    /// </summary>
    private void OnArtifactExpandClick(object sender, RoutedEventArgs e)
        => ArtifactExpandRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The reference's "Close pane" ×, which it shows only where the pane can be removed.</summary>
    private void OnClosePaneClick(object sender, RoutedEventArgs e)
        => ClosePaneRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Dev/verification hook behind <c>--open=titlebar[:loading|:agent|:panes]</c>. A posed
    /// profile has no long title, no half-read session, no agent and one pane, so the bar's
    /// other states have to be put there by hand to be looked at.
    /// </summary>
    internal void PoseTitleBar(string state)
    {
        if (_vm is not null)
        {
            _vm.Session.Title = "Reference titlebar parity round, a title long enough to truncate";
            UpdateSessionHeader();
        }

        switch (state)
        {
            case "loading":
                TitleLoading = true;
                break;
            case "agent":
                RunningAsAgent = "reviewer";
                break;
            case "panes":
                SetPaneControls(canClose: true, canExpandArtifact: true);
                break;
        }
    }

    private async Task RenameSessionAsync(string title)
    {
        if (_vm is null || title == _vm.Session.Title)
        {
            return;
        }

        await _vm.RenameAsync(title);
        UpdateSessionHeader();
        SessionPersisted?.Invoke(this, EventArgs.Empty);
    }

    // ---- home view: the reference's action center ----

    /// <summary>
    /// Rebuilds the home view. The reference shows its usage stats card only while
    /// every section is empty (its `landingClear`), and the three sections otherwise.
    /// </summary>
    private void BuildHomeActionCenter(IReadOnlyList<Core.Sessions.SessionSummary> sessions)
    {
        if (_services is null)
        {
            return;
        }

        _homeView ??= CreateHomeView();

        var groups = _services.SessionGroups;
        var inputs = sessions.Select(s => new HomeSessionInput(
                s.Id,
                s.Title,
                s.UpdatedAt,
                groups.IsArchived(s.Id),
                groups.IsPinned(s.Id),
                IsSessionRunning(s.Id),
                NeedsInput: false,
                groups.IsUnread(s.Id),
                groups.DismissedAt(s.Id),
                RepoName: SidebarPresentation.FolderName(s.WorkingDirectory)))
            .ToList();

        var rows = HomeViewPresentation.AttentionRows(inputs);
        var folders = SidebarPresentation.RecentFolders(
            [.. sessions.Select(s => new SidebarSessionInput(s.Id, s.Title, s.WorkingDirectory, s.UpdatedAt, s.UpdatedAt))]);
        var clearable = rows.Any(r => r.Kind == HomeSessionKind.Unread);
        var height = Window.GetWindow(this)?.ActualHeight ?? 900;

        _homeView.Show(rows, folders, _homePrRows, clearable, height);

        var name = HomeViewPresentation.GreetingName(
            SystemReminders.UserEmail(_vm?.Session.WorkingDirectory ?? ""),
            _services.UiSettings.Current.UserDisplayName);
        HomeGreetingText.Text = HomeViewPresentation.Greeting(_homeView.IsClear, name);
        HomeActionCenterHost.Content = _homeView.IsClear ? null : _homeView;
        StatsCardHost.Visibility = _homeView.IsClear ? Visibility.Visible : Visibility.Collapsed;
    }

    private IReadOnlyList<HomePrRow> _homePrRows = [];

    /// <summary>
    /// Lists the sessions the home view reads, then its pull requests: one `gh pr view`
    /// per distinct folder, off the UI thread, since the reference's own list is one PR
    /// per repository rather than per session.
    /// </summary>
    private async Task RefreshHomeActionCenterAsync()
    {
        if (_services is null || _vm is null || !_vm.IsCodeSurface)
        {
            return;
        }

        IReadOnlyList<Core.Sessions.SessionSummary> sessions;
        try
        {
            sessions = await _services.Sessions.ListAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            return;
        }

        BuildHomeActionCenter(sessions);

        var folders = sessions
            .Where(static s => s.WorkingDirectory.Length > 0)
            .GroupBy(static s => s.WorkingDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.OrderByDescending(static s => s.UpdatedAt).First())
            .Take(8)
            .ToList();

        await _services.PullRequests.RefreshAsync(folders.Select(static s => s.WorkingDirectory));

        var entries = new List<HomePrInput>();
        foreach (var session in folders)
        {
            if (_services.PullRequests.For(session.WorkingDirectory) is not { } pr)
            {
                continue;
            }

            entries.Add(new HomePrInput(
                session.Id,
                session.Title,
                session.UpdatedAt,
                pr,
                SidebarPresentation.FolderName(session.WorkingDirectory),
                ""));
        }

        _homePrRows = HomeViewPresentation.PrRows(entries);
        BuildHomeActionCenter(sessions);
    }

    private HomeView CreateHomeView()
    {
        var view = new HomeView();
        view.SessionActivated += (_, id) => HomeSessionRequested?.Invoke(this, id);
        view.FolderActivated += (_, folder) => StartNew(folder);
        view.MarkAllRead += (_, _) =>
        {
            if (_services is null)
            {
                return;
            }

            foreach (var id in _services.SessionGroups.Data.UnreadSessionIds.ToList())
            {
                _services.SessionGroups.SetUnread(id, false);
            }

            SessionPersisted?.Invoke(this, EventArgs.Empty);
        };
        view.SessionDismissed += (_, id) =>
        {
            _services?.SessionGroups.Dismiss(id, DateTimeOffset.Now);
            SessionPersisted?.Invoke(this, EventArgs.Empty);
        };
        view.PrActivated += (_, row) => OpenUrl(row.Entry.Pr.Url);
        return view;
    }

    /// <summary>A home row was activated: the shell opens that session.</summary>
    public event EventHandler<string>? HomeSessionRequested;

    private bool IsSessionRunning(string sessionId) => RunningSessionIds.Contains(sessionId);

    // ---- the PR bar ----

    private GitBarView GitBar()
    {
        if (_gitBar is not null)
        {
            return _gitBar;
        }

        _gitBar = new GitBarView();
        _gitBar.OpenUrlRequested += (_, url) => OpenUrl(url);
        _gitBar.DiffRequested += (_, _) => PanelRequested?.Invoke(this, "changes");
        _gitBar.CiRequested += (_, _) => ShowPrAutomationMenu();
        _gitBar.Dismissed += (_, _) =>
        {
            _gitBannerDismissed = true;
            _gitBar.Visibility = Visibility.Collapsed;
        };
        _gitBar.CommitRequested += (_, prompt) => _ = _vm?.SendAsync(prompt);
        _gitBar.PrActionRequested += (_, mode) => _ = CreatePullRequestAsync(mode);
        _gitBar.CreateModeChanged += (_, mode) =>
        {
            if (_services is null)
            {
                return;
            }

            _services.UiSettings.Current.PrCreateMode = mode;
            _services.UiSettings.Save();
            _ = RefreshGitBarAsync();
        };
        _gitBar.WorkingDirectoryAction += (_, action) => RunWorkingDirectoryAction(action);
        GitBarHost.Content = _gitBar;
        return _gitBar;
    }

    /// <summary>Re-reads the working copy and repaints the PR bar.</summary>
    private async Task RefreshGitBarAsync()
    {
        if (_vm is null || !_vm.IsCodeSurface)
        {
            GitBarHost.Content = null;
            return;
        }

        if (_gitBannerDismissed)
        {
            GitBar().Show(_gitBarInput with { Dismissed = true });
            return;
        }

        var cwd = _vm.Session.WorkingDirectory;
        var createMode = _services?.UiSettings.Current.PrCreateMode ?? "create";
        var responding = _vm.IsRunning;
        var input = await Task.Run(() => GitStatusProbe.Read(cwd, createMode));
        _services?.PullRequests.Record(cwd, input.Pr);
        _gitBarInput = input with { IsResponding = responding };
        GitBar().Show(_gitBarInput, await RelatedAndStackedAsync(input));
        OfferGitHubCliInstall(input);
        if (input.HasGithubRepo)
        {
            _ghAuthenticated = await Task.Run(() => GitStatusProbe.GhAvailable(cwd));
        }
    }

    /// <summary>
    /// The rows the reference draws beside the branch's own pull request: the ones
    /// this session has already been bound to, and the stack above the current PR —
    /// each parent found by following its <c>baseRefName</c>.
    /// </summary>
    private async Task<IReadOnlyList<RelatedPr>> RelatedAndStackedAsync(GitBarInput input)
    {
        if (_services is null || _vm is null || !input.IsGitRepo)
        {
            return [];
        }

        var settings = _services.UiSettings;
        var sessionId = _vm.Session.Id;
        if (!settings.Current.SessionPullRequests.TryGetValue(sessionId, out var stored))
        {
            stored = [];
        }

        if (input.Pr is { } pr &&
            SessionPullRequestLog.Record(stored, pr, input.RepoName, input.BranchName))
        {
            settings.Current.SessionPullRequests[sessionId] = stored;
            settings.Save();
        }

        var related = SessionPullRequestLog.Related(stored, input.RepoName);
        var cwd = input.Cwd;
        var current = input.Pr;
        var branch = input.BranchName;
        var baseBranch = input.BaseBranch;
        var stacked = await Task.Run(() => GitStatusProbe.ReadStack(cwd, current, branch, baseBranch));
        return [.. stacked, .. related];
    }

    /// <summary>
    /// The reference's one-time prompt when the GitHub CLI is missing (`Km`/`zm` at
    /// `ca80fca8d`@149600): offered once a session is in a repository that has a GitHub
    /// remote — where the bar's PR actions need `gh` — and remembered under the
    /// reference's own dismissal key however it is answered.
    /// </summary>
    private void OfferGitHubCliInstall(GitBarInput input)
    {
        if (_services is null || _services.UiSettings.Current.GhInstallDismissed ||
            !input.IsGitRepo || !input.HasGithubRepo || _ghInstallAsked)
        {
            return;
        }

        _ghInstallAsked = true;
        var cwd = input.Cwd;
        _ = Task.Run(() => GitStatusProbe.GhAvailable(cwd)).ContinueWith(
            available =>
            {
                if (available.Result || _services is null)
                {
                    return;
                }

                // Answered either way, the reference does not ask again.
                _services.UiSettings.Current.GhInstallDismissed = true;
                _services.UiSettings.Save();
                // The reference focuses its cancel button and labels it "Skip".
                if (ConfirmDialog.Ask(
                        Window.GetWindow(this),
                        GitHubCliPrompt.Title,
                        GitHubCliPrompt.Description,
                        GitHubCliPrompt.Confirm,
                        focusCancel: true,
                        cancelLabel: GitHubCliPrompt.Cancel))
                {
                    OpenUrl(GitHubCliPrompt.Url);
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool _ghInstallAsked;

    /// <summary>The bar's create modes, each `gh pr create` with the flag the mode names.</summary>
    private async Task CreatePullRequestAsync(PrMode mode)
    {
        if (_vm is null)
        {
            return;
        }

        var cwd = _vm.Session.WorkingDirectory;
        _gitBarInput = _gitBarInput with { Busy = true };
        GitBar().Show(_gitBarInput);
        var arguments = mode switch
        {
            PrMode.Draft => "pr create --draft --fill",
            PrMode.Compose => "pr create --web",
            _ => "pr create --fill",
        };

        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("gh", arguments)
                {
                    WorkingDirectory = cwd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                process?.WaitForExit(120000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                           or IOException)
            {
                // The bar reports the outcome by re-reading the repository below.
            }
        });

        _gitBarInput = _gitBarInput with { Busy = false };
        await RefreshGitBarAsync();
    }

    private void RunWorkingDirectoryAction(WorkingDirectoryAction action)
    {
        var cwd = _vm?.Session.WorkingDirectory ?? "";
        switch (action)
        {
            case WorkingDirectoryAction.OpenInVsCode:
                SessionMenu.OpenFolder(Window.GetWindow(this), cwd, "code");
                break;
            case WorkingDirectoryAction.OpenInExplorer:
                SessionMenu.OpenFolder(Window.GetWindow(this), cwd, "explorer");
                break;
            case WorkingDirectoryAction.CopyPath:
                CopyToClipboard(cwd);
                break;
            case WorkingDirectoryAction.ChangeDirectory:
                ChangeWorkingDirectory();
                break;
            case WorkingDirectoryAction.OpenInTerminal:
                PanelRequested?.Invoke(this, "terminal");
                break;
            case WorkingDirectoryAction.CopyBranchName:
                CopyToClipboard(_gitBarInput.BranchName);
                break;
            case WorkingDirectoryAction.OpenRepoOnGithub:
                OpenUrl(_gitBarInput.RepoUrl ?? "");
                break;
        }
    }

    /// <summary>The reference's Change directory row: a folder picker, then the session moves.</summary>
    private void ChangeWorkingDirectory()
    {
        if (_vm is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = WorkingDirectoryMenu.ChangeDirectoryTitle,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var chosen = dialog.FolderName;
        if (Core.Utilities.NetworkPaths.IsNetworkPath(chosen))
        {
            MessageBox.Show(Window.GetWindow(this), WorkingDirectoryMenu.NetworkPathRefused, "Jarvis Code");
            return;
        }

        if (string.Equals(chosen, _vm.Session.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _vm.SetWorkingDirectory(chosen);
        _gitBannerDismissed = false;
        UpdateContextBar();
        _ = RefreshGitBarAsync();
    }

    private static void CopyToClipboard(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another process owns the clipboard; the reference is silent here too.
        }
    }

    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Nothing to open with; the reference silently no-ops here too.
        }
    }

    // ---- the working-directory row above the composer ----

    /// <summary>
    /// The row the reference shows above the composer. Before the session starts it is
    /// its landing (`fA`): a folder chip, then a branch picker and a worktree switch
    /// joined into one pill, then the extra folders and a FolderPlus. Once the session
    /// has messages the reference shows the repo chip alone, whose menu is the
    /// "Working directory" group — so the standalone branch and worktree chips this
    /// port used to keep are gone.
    /// </summary>
    private void BuildContextRow()
    {
        if (_vm is null || !_vm.IsCodeSurface)
        {
            return;
        }

        ContextChips.Children.Clear();
        var cwd = _vm.Session.WorkingDirectory;
        var landing = _vm.Session.Messages.Count == 0;

        var folderChip = Chip("", LandingPresentation.FolderLabel(cwd), FolderTooltip(cwd));
        folderChip.ContextMenu = BuildWorkingDirectoryMenu(cwd);
        folderChip.Click += (_, _) =>
        {
            folderChip.ContextMenu.PlacementTarget = folderChip;
            folderChip.ContextMenu.IsOpen = true;
        };
        ContextChips.Children.Add(folderChip);

        if (landing && Core.Utilities.GitInfo.GetBranch(cwd) is { Length: > 0 } branch)
        {
            ContextChips.Children.Add(BuildBranchChip(cwd, branch));
            ContextChips.Children.Add(BuildWorktreeToggle(cwd));
        }

        foreach (var directory in _vm.Session.AdditionalDirectories.ToList())
        {
            var label = SidebarPresentation.FolderName(directory);
            var chip = Chip("", label, directory, removable: true);
            chip.Click += (_, _) =>
            {
                _vm.RemoveAdditionalDirectory(directory);
                UpdateContextBar();
            };
            ContextChips.Children.Add(chip);
        }

        var add = Chip("", "", LandingPresentation.AddAnotherFolder);
        add.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            LandingPresentation.AddAnotherFolder);
        add.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = LandingPresentation.AddFolderDialogTitle };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                _vm.AddAdditionalDirectory(dialog.FolderName);
                UpdateContextBar();
            }
        };
        ContextChips.Children.Add(add);
    }

    /// <summary>The repo chip's tooltip: the reference's "{cwd} · {repos}".</summary>
    private string? FolderTooltip(string cwd) =>
        WorkingDirectoryMenu.ChipTooltip(cwd, [SidebarPresentation.FolderName(cwd)],
            LandingPresentation.FolderLabel(cwd));

    private ContextMenu BuildWorkingDirectoryMenu(string cwd)
    {
        var menu = new ContextMenu { MinWidth = 220 };
        var context = new WorkingDirectoryContext
        {
            RepoName = SidebarPresentation.FolderName(cwd),
            Cwd = cwd,
            Branch = Core.Utilities.GitInfo.GetBranch(cwd),
            RepoUrl = _gitBarInput.RepoUrl,
            CanChangeDirectory = true,
            CanOpenInTerminal = true,
        };
        menu.Items.Add(SessionMenu.SectionHeader(WorkingDirectoryMenu.Label(context)));
        foreach (var row in WorkingDirectoryMenu.Build(context))
        {
            if (row.SeparatorBefore)
            {
                menu.Items.Add(new Separator());
            }

            var item = new MenuItem { Header = row.Label };
            var action = row.Action;
            item.Click += (_, _) => RunWorkingDirectoryAction(action);
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>The landing's branch picker: the reference's `Nk` with its own ordering.</summary>
    private FrameworkElement BuildBranchChip(string cwd, string branch)
    {
        var chip = Chip("", branch, LandingPresentation.BranchTooltip);
        var menu = new ContextMenu { MinWidth = 200, MaxHeight = 320 };
        var branches = ListBranches(cwd);
        if (branches.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = LandingPresentation.NoBranchesMatch, IsEnabled = false });
        }
        else
        {
            foreach (var name in LandingPresentation.Order(branches, GitStatusProbe.ReadBaseBranch(cwd)))
            {
                var item = new MenuItem
                {
                    Header = name,
                    IsCheckable = true,
                    IsChecked = name == branch,
                };
                var target = name;
                item.Click += (_, _) => CheckoutBranch(cwd, target);
                menu.Items.Add(item);
            }
        }

        chip.ContextMenu = menu;
        chip.Click += (_, _) =>
        {
            menu.PlacementTarget = chip;
            menu.IsOpen = true;
        };
        return chip;
    }

    private static IReadOnlyList<string> ListBranches(string cwd)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "for-each-ref --format=%(refname:short) refs/heads")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => l.Trim())
                .Where(static l => l.Length > 0)];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// The reference's branch pick: a switch that would collide with the working
    /// tree raises its dirty-tree dialog first, and only the answer the user picks
    /// there — stash, WIP commit or discard — clears the way for the checkout.
    /// </summary>
    private void CheckoutBranch(string cwd, string branch)
    {
        var current = Core.Utilities.GitInfo.GetBranch(cwd) ?? "";
        if (current.Length > 0 && branch != current)
        {
            var status = Services.BranchSwitch.ReadStatus(cwd);
            if (Services.BranchSwitch.WouldConflict(cwd, branch, status))
            {
                var answer = Views.BranchSwitchDialog.Ask(
                    Window.GetWindow(this), current, branch, OtherSessionsInFolder(cwd), status);
                if (answer is null)
                {
                    return;
                }

                if (Services.BranchSwitch.Resolve(cwd, answer.Value, current) is { } failure)
                {
                    Services.ToastQueue.Current?.AddDanger(failure);
                    return;
                }
            }
        }

        var checkout = Services.BranchSwitch.Run(cwd, ["checkout", branch]);
        if (checkout.ExitCode != 0)
        {
            var (title, _) = Services.ErrorCards.Describe(
                Services.ErrorCards.SessionErrorKind.GitCheckoutFailed);
            Services.ToastQueue.Current?.AddDanger(title);
            return;
        }

        UpdateContextBar();
        _ = RefreshGitBarAsync();
    }

    /// <summary>
    /// How many other sessions share this folder — the count the dialog's
    /// "{n} other sessions are using this folder" line reads.
    /// </summary>
    private int OtherSessionsInFolder(string cwd) =>
        OtherSessionWorkingDirectories?.Invoke()
            .Count(d => string.Equals(d, cwd, StringComparison.OrdinalIgnoreCase)) ?? 0;

    /// <summary>The window supplies the other open sessions' folders; null outside one.</summary>
    public Func<IReadOnlyList<string>>? OtherSessionWorkingDirectories { get; set; }

    // ---- Import GitHub issue ----

    /// <summary>
    /// Whether `gh` can answer for this machine, which is what decides between the
    /// reference's two unavailable reasons for the Import GitHub issue row.
    /// </summary>
    private bool _ghAuthenticated;

    /// <summary>
    /// The reference's Import GitHub issue picker: the chosen issue's context block
    /// is appended to the composer, which is where its own chip's contextSuffix goes.
    /// </summary>
    private void ImportGithubIssue()
    {
        if (_vm is null)
        {
            return;
        }

        var context = IssuePickerDialog.Pick(Window.GetWindow(this), _vm.Session.WorkingDirectory);
        if (context is null)
        {
            return;
        }

        InputBox.Text = InputBox.Text.Length > 0 ? $"{InputBox.Text}\n\n{context}" : context;
        InputBox.CaretIndex = InputBox.Text.Length;
        FocusInput();
    }

    /// <summary>The landing's Worktree switch, with the reference's two tooltips.</summary>
    private FrameworkElement BuildWorktreeToggle(string cwd)
    {
        var isWorktree = cwd.Contains(".jarvis-worktrees", StringComparison.OrdinalIgnoreCase);
        var chip = Chip("", LandingPresentation.WorktreeLabel, LandingPresentation.WorktreeTooltip);
        chip.Click += (_, _) => ToggleWorktree(!isWorktree);
        return chip;
    }

    // ---- the composer's "+" menu ----

    private void ShowPlusMenu()
    {
        var context = new PlusMenuContext
        {
            SupportsFileAttachments = true,
            AddFilesShortcut = "Ctrl+U",
            CanAddFolder = _vm?.IsCodeSurface == true,
            IsDraft = _vm is { } vm && vm.Session.Messages.Count == 0,
            GithubIssueAvailable = _gitBarInput.HasGithubRepo && _ghAuthenticated,
            GithubIssueUnavailableReason = _gitBarInput.HasGithubRepo
                ? (_ghAuthenticated ? null : "not-authenticated")
                : "no-github-remote",
            LinearIssueAvailable = false,
            Connectors = [.. ListConnectorNames().Select(n => new PlusMenuConnector(n, true, true, false, 1))],
            Plugins = ListPluginMenus(),
        };

        var menu = new ContextMenu
        {
            PlacementTarget = AttachButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            MinWidth = 220,
        };
        FillPlusMenu(menu.Items, PlusMenuModel.Build(context), menu);
        menu.IsOpen = true;
    }

    private void FillPlusMenu(ItemCollection items, IReadOnlyList<PlusMenuRow> rows, ContextMenu owner)
    {
        foreach (var row in rows)
        {
            if (row.SeparatorBefore)
            {
                items.Add(new Separator());
            }

            var item = new MenuItem
            {
                Header = row.Label,
                IsEnabled = !row.Disabled,
                InputGestureText = row.Shortcut ?? "",
                ToolTip = row.Tooltip,
            };
            if (row.Checked is { } isChecked)
            {
                item.IsCheckable = true;
                item.IsChecked = isChecked;
            }

            if (row.IsSubmenu)
            {
                FillPlusMenu(item.Items, row.Children!, owner);
            }
            else
            {
                var action = row.Action;
                var argument = row.Argument;
                item.Click += (_, _) =>
                {
                    owner.IsOpen = false;
                    RunPlusMenuAction(action, argument);
                };
            }

            items.Add(item);
        }
    }

    private void RunPlusMenuAction(PlusMenuAction action, string? argument)
    {
        switch (action)
        {
            case PlusMenuAction.AddFiles:
                AddFiles();
                break;
            case PlusMenuAction.AddFolder:
                AddFolder();
                break;
            case PlusMenuAction.ImportGithubIssue:
                ImportGithubIssue();
                break;
            case PlusMenuAction.SlashCommands:
                InputBox.Text = "/";
                InputBox.CaretIndex = InputBox.Text.Length;
                FocusInput();
                break;
            case PlusMenuAction.InsertSkill when argument is { Length: > 0 }:
                InputBox.Text = argument + " ";
                InputBox.CaretIndex = InputBox.Text.Length;
                FocusInput();
                break;
            case PlusMenuAction.ManageConnectors:
            case PlusMenuAction.BrowseConnectors:
            case PlusMenuAction.AddConnectors:
                CustomizeNavigateRequested?.Invoke(this, "connectors");
                break;
            case PlusMenuAction.ManagePlugins:
            case PlusMenuAction.BrowsePlugins:
            case PlusMenuAction.AddPlugins:
                CustomizeNavigateRequested?.Invoke(this, "plugins");
                break;
        }
    }

    /// <summary>
    /// Dev/verification hook: poses the home view with one row of every kind the
    /// reference lists, or the empty state it shows the usage stats card in.
    /// </summary>
    public void PoseHomeView(bool withRows)
    {
        _homeView ??= CreateHomeView();
        var now = DateTimeOffset.Now;
        if (!withRows)
        {
            _homeView.Show([], [], [], false, 900);
        }
        else
        {
            _homeView.Show(
                [
                    new HomeSessionRow(
                        new HomeSessionInput("s1", "Port the session sidebar", now.AddMinutes(-3),
                            false, false, false, true, false, RepoName: "jarvis-code",
                            Summary: "waiting on a permission prompt"),
                        HomeSessionKind.Blocked),
                    new HomeSessionRow(
                        new HomeSessionInput("s2", "Measure the PR bar", now.AddHours(-2),
                            false, false, false, false, true, RepoName: "jarvis-code"),
                        HomeSessionKind.Unread),
                ],
                [Environment.CurrentDirectory],
                [
                    new HomePrRow(
                        new HomePrInput("s3", "Add the context ring", now.AddHours(-5),
                            new PrInfo(214, "https://example.test/pr/214", PrDisplayState.Approved,
                                "Add the context ring", "master", new CiCounts(4, 0, 0)),
                            "owner/jarvis-code", "feature"),
                        HomePrCategory.ReadyToMerge,
                        new HomePrPill(HomePrTone.Green, "Ready to merge")),
                    new HomePrRow(
                        new HomePrInput("s4", "Rename in place", now.AddDays(-2),
                            new PrInfo(211, "https://example.test/pr/211", PrDisplayState.Open,
                                "Rename in place", "master", new CiCounts(2, 1, 0)),
                            "owner/jarvis-code", "rename"),
                        HomePrCategory.NeedsAttention,
                        new HomePrPill(HomePrTone.Red, "CI failing")),
                ],
                true,
                Window.GetWindow(this)?.ActualHeight ?? 900);
        }

        HomeGreetingText.Text = HomeViewPresentation.Greeting(_homeView.IsClear, "Sam");
        HomeActionCenterHost.Content = _homeView.IsClear ? null : _homeView;
        StatsCardHost.Visibility = _homeView.IsClear ? Visibility.Visible : Visibility.Collapsed;
        HomeScroll.Visibility = Visibility.Visible;
        TranscriptScroll.Visibility = Visibility.Collapsed;
    }

    /// <summary>Dev/verification hook: the PR bar posed in one of its modes.</summary>
    public void PoseGitBar(string mode)
    {
        var input = new GitBarInput
        {
            IsGitRepo = true,
            HasGithubRepo = true,
            RepoName = "jarvis-code",
            Cwd = Environment.CurrentDirectory,
            BranchName = "wp/sidebar-composer",
            BaseBranch = "master",
            RepoUrl = "https://example.test/owner/jarvis-code",
            BranchUrl = "https://example.test/owner/jarvis-code/tree/wp",
            Added = 512,
            Removed = 91,
            HasChanges = true,
            Behind = 3,
        };

        input = mode switch
        {
            "draft" => input with { CreateMode = "draft" },
            "commit" => input with { HasGithubRepo = false, LocalOnly = true },
            "view" => input with
            {
                Pr = new PrInfo(214, "https://example.test/pr/214", PrDisplayState.Open, "Port the sidebar",
                    "master", new CiCounts(6, 0, 0)),
            },
            "failing" => input with
            {
                Pr = new PrInfo(214, "https://example.test/pr/214", PrDisplayState.ChangesRequested,
                    "Port the sidebar", "master", new CiCounts(4, 2, 1)),
            },
            "merged" => input with
            {
                Pr = new PrInfo(214, "https://example.test/pr/214", PrDisplayState.Merged, "Port the sidebar"),
            },
            "queued" => input with
            {
                Pr = new PrInfo(214, "https://example.test/pr/214", PrDisplayState.Queued, "Port the sidebar"),
            },
            "stacked" => input with
            {
                Pr = new PrInfo(214, "https://example.test/pr/214", PrDisplayState.Open, "Port the sidebar",
                    "wp/sidebar-base", new CiCounts(6, 0, 0)),
            },
            _ => input,
        };

        // "--open=gitbar:stacked" poses the reference's extra rows: the stack above
        // this pull request, and one the session opened earlier.
        IReadOnlyList<RelatedPr> extra = mode == "stacked"
            ?
            [
                new RelatedPr(211, "https://example.test/pr/211", PrDisplayState.Open, "wp/sidebar-base",
                    "master", Stacked: true),
                new RelatedPr(198, "https://example.test/pr/198", PrDisplayState.Merged, "wp/panes", "master"),
            ]
            : [];

        _gitBarInput = input;
        GitBar().Show(input, extra);
    }

    /// <summary>Dev/verification hook: the session-not-found card over an empty transcript.</summary>
    public void PoseSessionNotFound()
    {
        HomeScroll.Visibility = Visibility.Collapsed;
        TranscriptScroll.Visibility = Visibility.Visible;
        _vm?.Transcript.Clear();
        ShowSessionNotFound(_vm?.Session.Id ?? "missing");
    }

    /// <summary>
    /// The installed plugins and the skills each contributes, for the "+" menu's
    /// Plugins submenu. A plugin namespaces everything it ships as "{plugin}:{name}",
    /// which is how a skill is matched back to the plugin that brought it.
    /// </summary>
    private IReadOnlyList<PlusMenuPlugin> ListPluginMenus()
    {
        if (_services is null)
        {
            return [];
        }

        try
        {
            var content = Core.Customization.Plugins.Load(_services.Paths.PluginsDirectory);
            var menus = new List<PlusMenuPlugin>();
            foreach (var plugin in content.Installed)
            {
                var prefix = plugin.Name + ":";
                var skills = content.Skills
                    .Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(s => new PlusMenuSkill("/" + s.Name, s.Description))
                    .ToList();
                skills.AddRange(content.Commands
                    .Where(command => command.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .Where(command => command.DescriptionDeclared)
                    .Select(command => new PlusMenuSkill("/" + command.Name, command.Description)));
                menus.Add(new PlusMenuPlugin(plugin.Name, skills));
            }

            return menus;
        }
        catch (IOException)
        {
            return [];
        }
    }

    // ---- the context ring ----

    /// <summary>Repaints the chin's ring and its tooltip from the session's context use.</summary>
    private void UpdateContextRing()
    {
        if (_vm is null)
        {
            return;
        }

        if (_services is null)
        {
            return;
        }

        var breakdown = ContextBreakdown.Compute(_services, _vm);
        var (summary, pct) = ContextRing.Summarize(breakdown.UsedTokens, breakdown.Cap);
        ContextRingGlyph.SetPercent(pct ?? 100);
        ContextPill.ToolTip = ContextRing.Tooltip(summary);
        ContextPill.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            ContextRing.AccessibleName(summary));
    }
}
