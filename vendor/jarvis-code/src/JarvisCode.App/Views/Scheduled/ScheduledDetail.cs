using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Scheduled;

namespace JarvisCode.App.Views;

/// <summary>
/// The Scheduled detail view — the reference's local routine detail
/// (<c>cfc18e0f4-DP8WK7zq.js</c>): Status, Folder, Repeats, Always allowed,
/// Instructions and History, with Run now, Edit, Delete and the schedule switch
/// in its header.
/// </summary>
public partial class RoutinesView
{
    /// <summary>How many History rows are shown before "Show more" adds another page.</summary>
    private const int HistoryPage = 100;

    private int _historyShown = HistoryPage;

    private void RenderDetail(ScheduledItem item)
    {
        var now = DateTimeOffset.Now;
        var status = ScheduledTaskPresentation.Status(item);
        Host.Children.Add(BuildDetailHeader(
            ScheduledTaskPresentation.DisplayName(item),
            item.Description is { Length: > 0 } ? item.Description : null));

        Host.Children.Add(BuildDetailActions(item, status, now));

        // ---- Status ----
        var statusPanel = new StackPanel();
        statusPanel.Children.Add(ScheduledUi.SectionHeading("Status"));
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(StatusChip(status));
        if (ScheduledTaskPresentation.NextRunText(item, now) is { } next)
        {
            statusRow.Children.Add(ScheduledUi.Separator());
            var label = ScheduledUi.Text(
                item.FireAt is not null ? $"Runs at: {next}" : $"Next run: {next}",
                12,
                FontWeights.Normal,
                "Text400Brush");
            label.VerticalAlignment = VerticalAlignment.Center;
            statusRow.Children.Add(label);
        }

        statusPanel.Children.Add(statusRow);
        if (ScheduledTaskPresentation.EndedReasonMessage(item.EndedReason) is { } ended)
        {
            var explanation = ScheduledUi.Footnote(ended);
            explanation.Margin = new Thickness(0, 8, 0, 0);
            statusPanel.Children.Add(explanation);
        }

        Host.Children.Add(ScheduledUi.Card(statusPanel));

        // ---- Folder ----
        if (item.WorkingDirectory is { Length: > 0 } folder)
        {
            var panel = new StackPanel();
            panel.Children.Add(ScheduledUi.SectionHeading("Folder"));
            var path = ScheduledUi.Text(folder, 12.5, FontWeights.Normal, "Text200Brush");
            path.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(path);
            Host.Children.Add(ScheduledUi.Card(panel));
        }

        // ---- Repeats ----
        var repeats = new StackPanel();
        repeats.Children.Add(ScheduledUi.SectionHeading(item.FireAt is not null ? "Runs" : "Repeats"));
        repeats.Children.Add(ScheduledUi.Text(
            ScheduledTaskPresentation.Describe(item, now) ?? "Manual only",
            12.5,
            FontWeights.Normal,
            "Text200Brush"));
        Host.Children.Add(ScheduledUi.Card(repeats));

        // ---- Always allowed ----
        Host.Children.Add(ScheduledUi.Card(BuildAlwaysAllowed(item)));

        // ---- Instructions ----
        var instructions = new StackPanel();
        instructions.Children.Add(ScheduledUi.SectionHeading("Instructions"));
        if (item.Prompt is { Length: > 0 })
        {
            var body = ScheduledUi.Text(item.Prompt, 12.5, FontWeights.Normal, "Text200Brush");
            body.TextWrapping = TextWrapping.Wrap;
            body.MaxHeight = 320;
            instructions.Children.Add(body);
        }
        else
        {
            instructions.Children.Add(
                ScheduledUi.Footnote("Task file not found or has unexpected format."));
        }

        Host.Children.Add(ScheduledUi.Card(instructions));

        Host.Children.Add(BuildHistory(item));
    }

    /// <summary>
    /// The reference's Always-allowed section: an unattended routine names the mode
    /// that stops it asking, otherwise the grants its runs collected appear as chips
    /// — the Browser row first, then one per stored tool rule with a "Remove
    /// approval" ×; a routine that has collected none gets the empty footnote.
    /// </summary>
    private StackPanel BuildAlwaysAllowed(ScheduledItem item)
    {
        var permissions = new StackPanel();
        permissions.Children.Add(ScheduledUi.SectionHeading(RoutineApprovals.SectionHeading));

        if (item.PermissionModeName is { Length: > 0 } mode)
        {
            var button = ScheduledUi.Button(PermissionModeSentence(mode));
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Click += (_, _) => OpenEditor(item.Id);
            permissions.Children.Add(button);
            return permissions;
        }

        var hasGrants = item.ApprovedPermissions.Count > 0 || item.ChromePermissionMode is { Length: > 0 };
        if (!hasGrants)
        {
            permissions.Children.Add(ScheduledUi.Footnote(RoutineApprovals.Empty));
            return permissions;
        }

        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        if (item.ChromePermissionMode is { Length: > 0 })
        {
            var browser = ScheduledUi.Chip(
                $"{RoutineApprovals.BrowserRow}  " +
                RoutineApprovals.BrowserValue(item.ChromePermissionMode, item.ChromeAllowedDomains));
            browser.Margin = new Thickness(0, 0, 6, 6);
            chips.Children.Add(GrantChip(item, browser, null, null));
        }

        foreach (var key in item.ApprovedPermissions)
        {
            var toolName = RoutineApprovals.ToolNameOf(key);
            var ruleContent = RoutineApprovals.RuleContentOf(key);
            var chip = ScheduledUi.Chip(RoutineApprovals.ToolLabel(toolName, ruleContent));
            chip.Margin = new Thickness(0, 0, 6, 6);
            chips.Children.Add(GrantChip(item, chip, toolName, ruleContent));
        }

        permissions.Children.Add(chips);
        return permissions;
    }

    /// <summary>One chip with the reference's "Remove approval" × beside it.</summary>
    private FrameworkElement GrantChip(
        ScheduledItem item, FrameworkElement chip, string? toolName, string? ruleContent)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 6, 6),
        };
        chip.Margin = new Thickness(0);
        row.Children.Add(chip);

        var remove = ScheduledUi.IconButton("", RoutineApprovals.RemoveApproval);
        remove.ToolTip = RoutineApprovals.RemoveApproval;
        remove.Margin = new Thickness(2, 0, 0, 0);
        remove.Click += (_, _) => RemoveApproval(item.Id, toolName, ruleContent);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>
    /// The reference's <c>removeApprovedPermission</c> and
    /// <c>clearChromePermissions</c>: a null tool name clears the browser grant.
    /// </summary>
    private void RemoveApproval(string routineId, string? toolName, string? ruleContent)
    {
        if (_runner is null)
        {
            return;
        }

        var routines = _runner.Store.Load();
        if (routines.FirstOrDefault(r => r.Id == routineId) is not { } routine)
        {
            return;
        }

        if (toolName is null)
        {
            routine.ChromePermissionMode = null;
            routine.ChromeAllowedDomains = [];
        }
        else
        {
            routine.ApprovedPermissions =
                [.. RoutineApprovals.Remove(routine.ApprovedPermissions, toolName, ruleContent)];
        }

        _runner.Store.Save(routines);
        Render();
    }

    /// <summary>
    /// The reference names the two unattended modes by what they do rather than
    /// by their setting name, which is what its detail page prints here.
    /// </summary>
    internal static string PermissionModeSentence(string mode) =>
        string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase)
            ? "Automatically approve"
            : "Skip all approvals";

    private FrameworkElement BuildDetailActions(ScheduledItem item, ScheduledStatus status, DateTimeOffset now)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var run = new Button { Content = "Run now" };
        run.SetResourceReference(StyleProperty, "PrimaryButton");
        run.IsEnabled = item.Prompt.Length > 0;
        run.Click += async (_, _) =>
        {
            if (_runner is null)
            {
                return;
            }

            run.IsEnabled = false;
            run.Content = "In progress";
            try
            {
                if (item.IsScheduledTask)
                {
                    if (_runner.ScheduledTasks.Find(item.Id) is { } task)
                    {
                        await _runner.RunScheduledTaskAsync(task);
                    }
                }
                else if (FindRoutine(item.Id) is { } routine)
                {
                    await _runner.RunAsync(routine);
                }
            }
            finally
            {
                Render();
            }
        };
        row.Children.Add(run);

        var edit = ScheduledUi.Button("Edit");
        edit.Margin = new Thickness(8, 0, 0, 0);
        edit.IsEnabled = !item.IsScheduledTask;
        edit.Click += (_, _) => OpenEditor(item.Id);
        row.Children.Add(edit);

        var delete = ScheduledUi.Button("Delete");
        delete.Margin = new Thickness(8, 0, 0, 0);
        delete.Click += (_, _) => Delete(item);
        row.Children.Add(delete);

        if (ScheduledTaskPresentation.CanToggleEnabled(item, now))
        {
            var toggle = new CheckBox
            {
                IsChecked = item.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            toggle.SetResourceReference(StyleProperty, "SwitchToggle");
            toggle.SetValue(
                AutomationProperties.NameProperty, item.Enabled ? "Pause routine" : "Enable routine");
            toggle.Click += (_, _) => SetEnabled(item, toggle.IsChecked == true);
            row.Children.Add(toggle);

            var label = ScheduledUi.Text("Enable schedule", 12, FontWeights.Normal, "Text400Brush");
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(label);
        }

        return row;
    }

    private void OpenEditor(string id)
    {
        if (FindRoutine(id) is { } routine)
        {
            ShowEditor(routine);
        }
    }

    private void SetEnabled(ScheduledItem item, bool enabled)
    {
        if (_runner is null)
        {
            return;
        }

        if (item.IsScheduledTask)
        {
            if (_runner.ScheduledTasks.Find(item.Id) is { } task)
            {
                task.Enabled = enabled;
                _runner.ScheduledTasks.Save(task);
            }
        }
        else
        {
            var all = _runner.Store.Load();
            if (all.FirstOrDefault(r => r.Id == item.Id) is { } routine)
            {
                routine.Enabled = enabled;
                if (enabled)
                {
                    // Switching a routine back on clears whatever stopped it —
                    // otherwise it would read Auto-disabled while running.
                    routine.EndedReason = null;
                }

                _runner.Store.Save(all);
            }
        }

        Render();
    }

    private void Delete(ScheduledItem item)
    {
        if (_runner is null)
        {
            return;
        }

        var name = ScheduledTaskPresentation.DisplayName(item);
        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                "Delete routine?",
                $"Delete “{name}”? Any sessions from this routine will be archived.",
                "Delete"))
        {
            return;
        }

        if (item.IsScheduledTask)
        {
            _runner.ScheduledTasks.Delete(item.Id);
        }
        else
        {
            var all = _runner.Store.Load();
            all.RemoveAll(r => r.Id == item.Id);
            _runner.Store.Save(all);
        }

        _runner.Runs.Delete(item.Id);
        ShowList();
    }

    private FrameworkElement BuildHistory(ScheduledItem item)
    {
        var panel = new StackPanel();
        panel.Children.Add(ScheduledUi.SectionHeading("History"));
        var entries = _runner?.Runs.Load(item.Id) ?? [];
        if (entries.Count == 0)
        {
            panel.Children.Add(ScheduledUi.Footnote("No runs yet"));
            return ScheduledUi.Card(panel);
        }

        foreach (var entry in entries.Take(_historyShown))
        {
            panel.Children.Add(BuildHistoryRow(entry));
        }

        if (entries.Count > _historyShown)
        {
            var more = ScheduledUi.Button("Show more");
            more.HorizontalAlignment = HorizontalAlignment.Left;
            more.Margin = new Thickness(0, 8, 0, 0);
            more.Click += (_, _) =>
            {
                _historyShown += HistoryPage;
                Render();
            };
            panel.Children.Add(more);
        }

        return ScheduledUi.Card(panel);
    }

    private FrameworkElement BuildHistoryRow(RoutineRunEntry entry)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var when = ScheduledTaskPresentation.Relative(
            entry.Time, DateTimeOffset.Now, approximate: false,
            ScheduledTaskPresentation.RelativeDirection.Past);
        var label = ScheduledUi.Text(when, 12.5, FontWeights.Normal, "Text200Brush");
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);

        FrameworkElement trailing;
        if (entry.IsMissed)
        {
            var skipped = ScheduledUi.Text("Skipped", 11.5, FontWeights.Normal, "Text500Brush");
            skipped.ToolTip = SkipTooltip(entry.Reason);
            skipped.VerticalAlignment = VerticalAlignment.Center;
            trailing = skipped;
        }
        else if (entry.Failed)
        {
            var failed = ScheduledUi.Text("Error", 11.5, FontWeights.Normal, "Danger100Brush");
            failed.VerticalAlignment = VerticalAlignment.Center;
            trailing = failed;
        }
        else
        {
            var open = ScheduledUi.Button("View");
            open.IsEnabled = entry.SessionId is { Length: > 0 };
            if (entry.Summary is { Length: > 0 } summary)
            {
                open.ToolTip = $"Run reported: {summary}";
            }

            open.Click += (_, _) =>
            {
                if (entry.SessionId is { Length: > 0 } sessionId)
                {
                    SessionRequested?.Invoke(this, sessionId);
                }
            };
            trailing = open;
        }

        Grid.SetColumn(trailing, 1);
        row.Children.Add(trailing);
        return row;
    }

    /// <summary>The three sentences the reference explains a skipped slot with.</summary>
    internal static string SkipTooltip(string? reason) => reason switch
    {
        RoutineSkipReasons.PerTaskLimit => "The previous run was still in progress.",
        RoutineSkipReasons.GlobalLimit => "Other routines were already running.",
        _ => "Routines only run while your computer is awake and online.",
    };
}
