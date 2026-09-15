using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.App.Views.Scheduled;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Routines;

namespace JarvisCode.App.Views;

/// <summary>
/// The Scheduled editor — the reference's local routine form
/// (<c>c0243d234-BOJof1xz.js</c>): name, description, instructions, working
/// folder, branch and worktree, permissions, model, and the frequency row with
/// its time, day and cron controls.
/// </summary>
public partial class RoutinesView
{
    private void RenderEditor()
    {
        var editing = _editing;
        var isEdit = editing is not null;
        Host.Children.Add(BuildDetailHeader(isEdit ? "Edit routine" : "New routine", null));

        var form = new StackPanel();
        Host.Children.Add(ScheduledUi.Card(form, new Thickness(16, 4, 16, 16), new Thickness(0)));

        var banner = ScheduledUi.Footnote(
            "Local routines only run while your computer is awake and online.");
        banner.Margin = new Thickness(2, 12, 0, 0);
        form.Children.Add(banner);

        // ---- name, description, instructions ----
        var nameBox = ScheduledUi.Field(form, "Name", "daily-code-review", required: true);
        nameBox.Text = isEdit ? ScheduledTaskPresentation.DisplayName(ToItem(editing!, 0, null)) : "";
        var nameError = ScheduledUi.ErrorLine(form);

        var descriptionBox = ScheduledUi.Field(
            form, "Description", "Review yesterday’s commits and flag anything concerning", required: true);
        descriptionBox.Text = editing?.Description ?? "";

        var promptBox = ScheduledUi.Field(
            form,
            "Instructions",
            "Look at the commits from the last 24 hours. Summarize what changed, call out any risky patterns or missing tests, and note anything worth following up on.",
            multiline: true,
            required: true);
        promptBox.Text = editing?.Instruction ?? "";

        // ---- working folder, branch, worktree ----
        var folderCaption = ScheduledUi.Text("Working folder", 12, FontWeights.Normal, "Text400Brush");
        folderCaption.Margin = new Thickness(2, 12, 0, 4);
        form.Children.Add(folderCaption);

        var folderRow = new Grid();
        folderRow.ColumnDefinitions.Add(new ColumnDefinition());
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderBox = new TextBox { Text = editing?.WorkingDirectory ?? "" };
        folderBox.SetResourceReference(StyleProperty, "InputTextBox");
        folderBox.SetValue(AutomationProperties.NameProperty, "Working folder");
        folderRow.Children.Add(folderBox);
        var browse = ScheduledUi.Button("Choose a different folder…");
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select a folder for this task" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                folderBox.Text = dialog.FolderName;
            }
        };
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(browse);
        form.Children.Add(folderRow);

        var branchBox = ScheduledUi.Field(form, "Branch", "Current branch");
        branchBox.Text = editing?.SourceBranch ?? "";

        var worktree = new CheckBox
        {
            Content = "Worktree",
            IsChecked = editing?.UseWorktree ?? false,
            Margin = new Thickness(2, 12, 0, 0),
        };
        worktree.SetValue(AutomationProperties.NameProperty, "Worktree");
        form.Children.Add(worktree);

        // ---- permissions and model ----
        var permissionCaption = ScheduledUi.Text("Permissions", 12, FontWeights.Normal, "Text400Brush");
        permissionCaption.Margin = new Thickness(2, 12, 0, 4);
        form.Children.Add(permissionCaption);
        var permissionBox = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        permissionBox.SetValue(AutomationProperties.NameProperty, "Permissions");
        permissionBox.Items.Add("Settings default");
        foreach (var mode in Enum.GetNames<PermissionMode>())
        {
            permissionBox.Items.Add(mode);
        }

        permissionBox.SelectedItem = editing?.PermissionModeName is { Length: > 0 } stored &&
                                     permissionBox.Items.Contains(stored)
            ? stored
            : "Settings default";
        form.Children.Add(permissionBox);

        var modelCaption = ScheduledUi.Text("Model", 12, FontWeights.Normal, "Text400Brush");
        modelCaption.Margin = new Thickness(2, 12, 0, 4);
        form.Children.Add(modelCaption);
        var modelBox = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
        modelBox.SetValue(AutomationProperties.NameProperty, "Model");
        modelBox.Items.Add("Default");
        foreach (var id in _modelIds?.Invoke() ?? [])
        {
            modelBox.Items.Add(id);
        }

        modelBox.SelectedItem = editing?.ModelId is { Length: > 0 } modelId && modelBox.Items.Contains(modelId)
            ? modelId
            : "Default";
        form.Children.Add(modelBox);

        // ---- notifications ----
        var notify = new CheckBox
        {
            Content = "Notify me when this routine finishes",
            IsChecked = editing?.NotifyOnCompletion ?? true,
            Margin = new Thickness(2, 14, 0, 0),
        };
        notify.SetValue(AutomationProperties.NameProperty, "Notify me when this routine finishes");
        form.Children.Add(notify);

        // ---- schedule ----
        var scheduleCaption = ScheduledUi.Text("Schedule", 12, FontWeights.Normal, "Text400Brush");
        scheduleCaption.Margin = new Thickness(2, 16, 0, 6);
        form.Children.Add(scheduleCaption);

        var state = new EditorSchedule(editing);
        var frequencyRow = new WrapPanel();
        form.Children.Add(frequencyRow);
        var detailRow = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        form.Children.Add(detailRow);
        var cronError = ScheduledUi.ErrorLine(form);
        var stagger = ScheduledUi.Footnote(
            "Scheduled tasks use a randomized delay of several minutes for server performance.");
        stagger.Margin = new Thickness(2, 8, 0, 0);
        form.Children.Add(stagger);

        var save = new Button { Content = isEdit ? "Save" : "Create" };
        save.SetResourceReference(StyleProperty, "PrimaryButton");

        void Refresh()
        {
            RenderFrequencyRow(frequencyRow, state, Refresh);
            RenderScheduleDetail(detailRow, state, Refresh);
            var problem = state.Problem(editing);
            ScheduledUi.SetError(cronError, ScheduleFrequencies.ProblemMessage(problem));
            stagger.Visibility = ScheduleFrequencies.ShowsStaggerNote(state.Frequency)
                ? Visibility.Visible
                : Visibility.Collapsed;
            save.IsEnabled = Valid();
        }

        bool Valid()
        {
            var name = nameBox.Text.Trim();
            var hasName = isEdit ? name.Length > 0 : ScheduledTaskPresentation.Slug(name).Length > 0;
            return hasName &&
                   descriptionBox.Text.Trim().Length > 0 &&
                   promptBox.Text.Trim().Length > 0 &&
                   nameError.Visibility != Visibility.Visible &&
                   state.IsComplete(editing);
        }

        void RevalidateName()
        {
            var existing = Items()
                .Where(i => !isEdit || i.Id != editing!.Id)
                .Select(static i => i.Id)
                .ToHashSet(StringComparer.Ordinal);
            ScheduledUi.SetError(
                nameError, ScheduledTaskPresentation.NameError(nameBox.Text, existing, isEdit));
            save.IsEnabled = Valid();
        }

        nameBox.TextChanged += (_, _) => RevalidateName();
        descriptionBox.TextChanged += (_, _) => save.IsEnabled = Valid();
        promptBox.TextChanged += (_, _) => save.IsEnabled = Valid();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var cancel = ScheduledUi.Button("Cancel");
        cancel.Click += (_, _) => ShowList();
        buttons.Children.Add(cancel);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => Save(
            editing, nameBox, descriptionBox, promptBox, folderBox, branchBox,
            worktree, permissionBox, modelBox, notify, state);
        buttons.Children.Add(save);
        form.Children.Add(buttons);

        RevalidateName();
        Refresh();
        nameBox.Focus();
    }

    private static void RenderFrequencyRow(WrapPanel row, EditorSchedule state, Action refresh)
    {
        row.Children.Clear();
        foreach (var frequency in ScheduleFrequencies.Offered(state.HasFireAt))
        {
            var button = ScheduledUi.Button(
                ScheduleFrequencies.Label(frequency),
                frequency == state.Frequency ? "SecondaryButton" : "GhostButton");
            button.Margin = new Thickness(0, 0, 6, 6);
            button.SetValue(AutomationProperties.NameProperty, ScheduleFrequencies.Label(frequency));
            var chosen = frequency;
            button.Click += (_, _) =>
            {
                state.Frequency = chosen;
                refresh();
            };
            row.Children.Add(button);
        }
    }

    private static void RenderScheduleDetail(StackPanel host, EditorSchedule state, Action refresh)
    {
        host.Children.Clear();
        if (ScheduleFrequencies.NeedsTimeOfDay(state.Frequency))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var at = ScheduledUi.Text("At", 12, FontWeights.Normal, "Text400Brush");
            at.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(at);

            var time = new TextBox
            {
                Text = $"{state.Hour:D2}:{state.Minute:D2}",
                Width = 84,
                Margin = new Thickness(8, 0, 0, 0),
            };
            time.SetResourceReference(StyleProperty, "InputTextBox");
            time.SetValue(AutomationProperties.NameProperty, "At");
            time.LostFocus += (_, _) =>
            {
                if (TimeSpan.TryParseExact(time.Text.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var parsed) ||
                    TimeSpan.TryParseExact(time.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out parsed))
                {
                    state.Hour = parsed.Hours;
                    state.Minute = parsed.Minutes;
                }

                refresh();
            };
            line.Children.Add(time);

            if (state.Frequency == ScheduleFrequency.Weekly)
            {
                var on = ScheduledUi.Text("On", 12, FontWeights.Normal, "Text400Brush");
                on.VerticalAlignment = VerticalAlignment.Center;
                on.Margin = new Thickness(12, 0, 0, 0);
                line.Children.Add(on);

                var day = new ComboBox { Width = 130, Margin = new Thickness(8, 0, 0, 0) };
                day.SetValue(AutomationProperties.NameProperty, "On");
                for (var i = 0; i < 7; i++)
                {
                    day.Items.Add(ScheduledTaskPresentation.DayName(i));
                }

                day.SelectedIndex = state.DayOfWeek;
                day.SelectionChanged += (_, _) =>
                {
                    state.DayOfWeek = Math.Max(0, day.SelectedIndex);
                    refresh();
                };
                line.Children.Add(day);
            }

            host.Children.Add(line);
        }

        if (state.Frequency == ScheduleFrequency.Custom)
        {
            var caption = ScheduledUi.Text("Cron expression", 12, FontWeights.Normal, "Text400Brush");
            caption.Margin = new Thickness(2, 0, 0, 4);
            host.Children.Add(caption);

            var box = new TextBox { Text = state.CustomCron };
            box.SetResourceReference(StyleProperty, "InputTextBox");
            box.SetResourceReference(TextBox.FontFamilyProperty, "MonoFontFamily");
            box.SetValue(AutomationProperties.NameProperty, "Cron expression");
            box.ToolTip = "0 9 * * *";
            box.TextChanged += (_, _) =>
            {
                state.CustomCron = box.Text;
                refresh();
            };
            host.Children.Add(box);
        }

        if (state.Frequency == ScheduleFrequency.OneTime)
        {
            var when = state.FireAt is { } fireAt
                ? fireAt.LocalDateTime.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture)
                : "—";
            host.Children.Add(ScheduledUi.Footnote(
                $"Runs once on {when}. Changing the frequency will replace this schedule."));
        }
    }

    private void Save(
        Routine? editing,
        TextBox nameBox,
        TextBox descriptionBox,
        TextBox promptBox,
        TextBox folderBox,
        TextBox branchBox,
        CheckBox worktree,
        ComboBox permissionBox,
        ComboBox modelBox,
        CheckBox notify,
        EditorSchedule state)
    {
        if (_runner is null)
        {
            return;
        }

        var all = _runner.Store.Load();
        var routine = editing is null ? new Routine() : all.FirstOrDefault(r => r.Id == editing.Id) ?? editing;
        var name = nameBox.Text.Trim();
        if (editing is null)
        {
            // The reference files a new routine under the slug of its name, which
            // is what the collision check compared against.
            routine.Id = ScheduledTaskPresentation.Slug(name);
        }

        routine.Name = name;
        routine.Description = descriptionBox.Text.Trim();
        routine.Instruction = promptBox.Text.Trim();
        routine.WorkingDirectory = folderBox.Text.Trim() is { Length: > 0 } folder && Directory.Exists(folder)
            ? folder
            : null;
        routine.SourceBranch = branchBox.Text.Trim() is { Length: > 0 } branch ? branch : null;
        routine.UseWorktree = worktree.IsChecked == true;
        routine.PermissionModeName = permissionBox.SelectedItem as string is { } mode && mode != "Settings default"
            ? mode
            : null;
        routine.ModelId = modelBox.SelectedItem as string is { } model && model != "Default" ? model : null;
        routine.NotifyOnCompletion = notify.IsChecked == true;

        var previousCron = routine.CronExpression;
        var previousFireAt = routine.FireAt;
        state.Apply(routine);

        // Editing the schedule is how the user clears an auto-disable, so the
        // reason goes with it — otherwise the row would stay Auto-disabled while
        // reading as fixed. Editing anything else leaves the routine as it was:
        // renaming a paused routine must not start it running.
        var scheduleChanged = routine.CronExpression != previousCron || routine.FireAt != previousFireAt;
        if (scheduleChanged &&
            routine.EndedReason is { } ended && ended != RoutineEndedReasons.RunOnceFired)
        {
            routine.EndedReason = null;
            routine.Enabled = true;
        }

        if (editing is null)
        {
            all.Add(routine);
        }

        _runner.Store.Save(all);
        ShowDetail(routine.Id);
    }

    /// <summary>
    /// The schedule half of the editor's state: which frequency is chosen, the
    /// time and day behind the presets, and the typed cron behind Custom.
    /// </summary>
    internal sealed class EditorSchedule
    {
        public EditorSchedule(Routine? routine)
        {
            FireAt = routine?.FireAt;
            CustomCron = routine?.CronExpression ?? "";
            var parsed = ScheduleFrequencies.ParseCron(routine?.CronExpression);
            Hour = parsed?.Hour ?? 9;
            Minute = parsed?.Minute ?? 0;
            DayOfWeek = parsed?.DayOfWeek ?? 1;
            Frequency = parsed?.Frequency
                        ?? (routine?.CronExpression is { Length: > 0 } ? ScheduleFrequency.Custom
                            : routine?.FireAt is not null ? ScheduleFrequency.OneTime
                            : routine is not null ? ScheduleFrequency.Manual
                            : ScheduleFrequency.Daily);
        }

        public ScheduleFrequency Frequency { get; set; }

        public int Hour { get; set; }

        public int Minute { get; set; }

        public int DayOfWeek { get; set; }

        public string CustomCron { get; set; }

        public DateTimeOffset? FireAt { get; }

        public bool HasFireAt => FireAt is not null;

        /// <summary>
        /// The reference validates the typed cron only when it is the chosen
        /// frequency and the user actually changed it — an existing routine's
        /// stored expression is left alone.
        /// </summary>
        public CronProblem Problem(Routine? editing)
        {
            if (Frequency != ScheduleFrequency.Custom)
            {
                return CronProblem.None;
            }

            if (editing is not null && CustomCron == editing.CronExpression)
            {
                return CronProblem.None;
            }

            return ScheduleFrequencies.Validate(CustomCron);
        }

        /// <summary>
        /// Whether the chosen schedule is filled in. The reference separates
        /// this from validity: an empty cron box shows no error, it just leaves
        /// nothing to save.
        /// </summary>
        public bool IsComplete(Routine? editing) =>
            Frequency != ScheduleFrequency.Custom ||
            (CustomCron.Trim().Length > 0 && Problem(editing) == CronProblem.None);

        /// <summary>Writes the chosen schedule onto the routine, clearing the other kind.</summary>
        public void Apply(Routine routine)
        {
            switch (Frequency)
            {
                case ScheduleFrequency.Manual:
                    routine.CronExpression = null;
                    routine.FireAt = null;
                    routine.Schedule = RoutineSchedule.Manual;
                    break;
                case ScheduleFrequency.OneTime:
                    routine.CronExpression = null;
                    routine.FireAt = FireAt;
                    routine.Schedule = RoutineSchedule.Manual;
                    break;
                case ScheduleFrequency.Custom:
                    routine.CronExpression = CustomCron.Trim();
                    routine.FireAt = null;
                    break;
                default:
                    routine.CronExpression = ScheduleFrequencies.BuildCron(Frequency, Hour, Minute, DayOfWeek);
                    routine.FireAt = null;
                    break;
            }
        }
    }
}
