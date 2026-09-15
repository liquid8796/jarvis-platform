using System.IO;
using System.Windows;
using System.Windows.Controls;
using JarvisCode.App.Services;
using JarvisCode.Core.Customization;
using Path = System.IO.Path;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// Customize › Skills, at the shape of the reference's tabbed list
/// (c5e558aae): "Your skills" over "Discover", a search across skills and
/// plugins, the Filter by / Sort by facets, the Needs attention and Created by
/// you sections, and the Add skill menu.
/// </summary>
public partial class CustomizeSurface
{
    private string _skillQuery = "";
    private SkillSort _skillSort = SkillSort.Updated;
    private SkillFilter _skillFilter = SkillFilter.None;
    private bool _skillsBrowseTab;

    public void ShowSkills()
    {
        if (_services is null)
            return;
        _skillsBrowseTab = false;
        RenderSkills();
    }

    /// <summary>The Discover tab, which is the Directory scoped to skills.</summary>
    public void ShowSkillDirectory()
    {
        if (_services is null)
            return;
        _skillsBrowseTab = true;
        RenderSkills();
    }

    private void RenderSkills()
    {
        var page = NewPage();
        page.Children.Add(CustomizeUi.PageHeader(
            "Skills",
            "Search skills and plugins",
            query =>
            {
                _skillQuery = query;
                RenderSkillList();
            },
            _skillQuery,
            AddSkillButton()));

        page.Children.Add(CustomizeUi.Toolbar(
            CustomizeUi.Tabs("Skills",
            [
                ("Your skills", !_skillsBrowseTab, () => { _skillsBrowseTab = false; RenderSkills(); }),
                ("Discover", _skillsBrowseTab, () => { _skillsBrowseTab = true; RenderSkills(); }),
            ]),
            _skillsBrowseTab ? null : SkillFilterPicker(),
            _skillsBrowseTab ? null : SkillSortPicker(),
            _skillsBrowseTab || (_skillFilter.ActiveCount == 0 && _skillSort == SkillSort.Updated)
                ? null
                : CustomizeUi.ResetLink(() =>
                {
                    _skillFilter = SkillFilter.None;
                    _skillSort = SkillSort.Updated;
                    RenderSkills();
                })));

        _skillListHost = new StackPanel();
        page.Children.Add(_skillListHost);
        RenderSkillList();
        Show(page);
    }

    private StackPanel? _skillListHost;

    /// <summary>Every skill the session can invoke, as rows the list can sort.</summary>
    private IReadOnlyList<SkillRow> SkillRows()
    {
        var now = DateTimeOffset.Now;
        var ui = Services.UiSettings.Current;
        SkillCatalog.InvalidateCache();
        return [.. SkillCatalog.LoadAll(Cwd, Services.Paths)
            .Select(skill => SkillListPresentation.ToRow(skill, ui, now))];
    }

    private void RenderSkillList()
    {
        if (_skillListHost is null)
            return;
        _skillListHost.Children.Clear();

        if (_skillsBrowseTab)
        {
            RenderDirectory(_skillListHost, DirectorySection.Skills, _skillQuery);
            return;
        }

        var all = SkillRows();
        if (all.Count == 0)
        {
            _skillListHost.Children.Add(CustomizeUi.EmptyState(
                "Add your first skills",
                "Skills teach Jarvis how you work. Add them from Discover, or create your own.",
                "Discover",
                () => { _skillsBrowseTab = true; RenderSkills(); }));
            return;
        }

        var matched = SkillListPresentation.Search(all, _skillQuery)
            .Where(row => SkillListPresentation.Matches(_skillFilter, row))
            .ToList();
        var havePlugins = matched.Any(r => r.Kind == SkillRowKind.Plugin);
        var haveUses = matched.Any(r => r.OwnUses90d is not null);
        var sort = SkillListPresentation.EffectiveSort(_skillSort, havePlugins, haveUses);
        var sections = SkillListPresentation.Split(matched, row => !row.Enabled);

        if (matched.Count == 0)
        {
            _skillListHost.Children.Add(CustomizeUi.Notice(
                _skillQuery.Trim().Length > 0 ? "No skills match your search" : "No skills match your filters."));
            if (_skillQuery.Trim().Length > 0)
            {
                _skillListHost.Children.Add(CustomizeUi.Notice(
                    "Try a different term, or browse the Directory to find new skills."));
            }
            return;
        }

        void AddRows(IReadOnlyList<SkillRow> rows)
        {
            foreach (var row in rows.OrderBy(r => r, Comparer<SkillRow>.Create((a, b) =>
                         SkillListPresentation.Compare(sort, a, b))))
            {
                _skillListHost.Children.Add(SkillListRow(row, sort == SkillSort.MostUsedByMe));
            }
        }

        if (sections.Attention.Count > 0)
        {
            _skillListHost.Children.Add(
                CustomizeUi.SectionHeader("Needs attention", sections.Attention.Count, attention: true));
            AddRows(sections.Attention);
        }
        AddRows(sections.Main);
        if (sections.CreatedByYou.Count > 0)
        {
            _skillListHost.Children.Add(
                CustomizeUi.SectionHeader("Created by you", sections.CreatedByYou.Count));
            AddRows(sections.CreatedByYou);
        }
    }

    private FrameworkElement SkillFilterPicker()
    {
        var options = new List<(SkillCreator? Value, string Label)>
        {
            (null, "All"),
            (SkillCreator.You, "You"),
            (SkillCreator.Anthropic, "Anthropic"),
            (SkillCreator.Partners, "Partners"),
            (SkillCreator.Org, "Your organization"),
        };
        return CustomizeUi.Picker(
            "Filter by", options, _skillFilter.CreatedBy,
            value =>
            {
                _skillFilter = _skillFilter with { CreatedBy = value };
                RenderSkills();
            },
            _skillFilter.ActiveCount > 0);
    }

    private FrameworkElement SkillSortPicker()
    {
        var rows = SkillRows();
        var options = SkillListPresentation.SortOptions(
            rows.Any(r => r.Kind == SkillRowKind.Plugin), rows.Any(r => r.OwnUses90d is not null));
        return CustomizeUi.Picker(
            "Sort by", options, _skillSort,
            value => { _skillSort = value; RenderSkills(); },
            _skillSort != SkillSort.Updated);
    }

    /// <summary>The "Add skill" menu: Upload skill, Create a skill, Create with Jarvis.</summary>
    private FrameworkElement AddSkillButton()
    {
        var button = CustomizeUi.Primary("Add skill", () => { });
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var upload = new MenuItem { Header = "Upload skill" };
        upload.Click += (_, _) => UploadSkill();
        menu.Items.Add(upload);
        var create = new MenuItem { Header = "Create a skill" };
        create.Click += (_, _) => OpenSkillEditor(null);
        menu.Items.Add(create);
        var withJarvis = new MenuItem { Header = "Create with Jarvis" };
        withJarvis.Click += (_, _) => CreateSkillWithJarvis();
        menu.Items.Add(withJarvis);
        // Not the reference's: this app can copy the skills Claude Code keeps in
        // the user's own ~/.claude/skills, which it also reads live.
        var import = new MenuItem { Header = "Import from Claude Code" };
        import.Click += (_, _) => ImportClaudeCodeSkills();
        menu.Items.Add(import);
        button.Click += (_, _) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>
    /// "Create with Jarvis" opens a Code session on the reference's own task:
    /// the skill-creator skill this build ships is what carries the method, so
    /// the prompt invokes it rather than restating it.
    /// </summary>
    private void CreateSkillWithJarvis() =>
        CodeSessionRequested?.Invoke(this, "/anthropic:skill-creator Create a new skill with me.");

    /// <summary>Copies the skills in %USERPROFILE%\.claude\skills into this profile.</summary>
    private void ImportClaudeCodeSkills()
    {
        var source = SkillCatalog.ClaudeUserSkillsDirectory;
        if (!Directory.Exists(source))
        {
            Warn($"No Claude Code skills found at {source}.");
            return;
        }

        var target = Services.Paths.UserSkillsDirectory;
        Directory.CreateDirectory(target);
        int imported = 0, skipped = 0, failed = 0;

        try
        {
            // Folder skills (SKILL.md + resources) are copied whole.
            foreach (var directory in Directory.GetDirectories(source))
            {
                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    !File.Exists(Path.Combine(directory, "SKILL.md")))
                    continue;
                if (SkillLibrary.Exists(target, info.Name))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    PluginLibrary.CopyDirectory(directory, Path.Combine(target, info.Name));
                    imported++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                }
            }

            // Flat markdown skills copy across directly.
            foreach (var file in Directory.GetFiles(source, "*.md"))
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                if (File.Exists(destination))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    File.Copy(file, destination);
                    imported++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"Import failed: {ex.Message}");
            return;
        }

        var summary = $"Imported {imported} skill{(imported == 1 ? "" : "s")}.";
        if (skipped > 0)
            summary += $" {skipped} already existed.";
        if (failed > 0)
            summary += $" {failed} could not be copied.";
        Warn(summary);
        SkillsWereChanged();
        RenderSkills();
    }

    private FrameworkElement SkillListRow(SkillRow row, bool showRuns)
    {
        var chips = new List<FrameworkElement?>();
        if (!row.Enabled)
            chips.Add(CustomizeUi.Chip("Disabled"));
        if (row.Kind == SkillRowKind.BuiltIn)
            chips.Add(CustomizeUi.Chip("built-in"));
        if (row.Kind == SkillRowKind.Plugin && row.PluginName is { Length: > 0 } plugin)
            chips.Add(CustomizeUi.Chip(plugin));

        var meta = new List<string>();
        if (SkillListPresentation.Credit(row) is { } credit)
            meta.Add(credit);
        if (showRuns && row.OwnUses90d is { } runs)
            meta.Add(SkillListPresentation.RunsLabel(runs));
        else if (row.UpdatedAt is { } at)
            meta.Add(ViewModels.TranscriptTime.Relative(DateTimeOffset.Now - at));

        var body = CustomizeUi.Body(
            row.Name, row.Skill.Description, meta.Count > 0 ? string.Join(" · ", meta) : null, [.. chips]);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(CustomizeUi.Switch(
            row.Enabled, enabled => SetSkillEnabled(row, enabled), $"Enable skill {row.Name}"));
        controls.Children.Add(SkillKebab(row));

        return CustomizeUi.ListRow(body, controls, () => ShowSkillDetail(row), row.Name);
    }

    private void SetSkillEnabled(SkillRow row, bool enabled)
    {
        var overrides = Services.UiSettings.Current.SkillOverrides;
        if (enabled)
            overrides.Remove(row.Skill.Name);
        else
            overrides[row.Skill.Name] = SkillCatalog.OverrideOff;
        Services.UiSettings.Save();
        SkillsWereChanged();
        RenderSkillList();
    }

    private FrameworkElement SkillKebab(SkillRow row)
    {
        var items = new List<(string, bool, Action)>();
        if (row.IsEditable)
        {
            items.Add(("Edit files", false, () => OpenSkillEditor(row)));
            items.Add(("Edit with Jarvis", false, () => CodeSessionRequested?.Invoke(
                this, $"Edit the skill at {row.Skill.FilePath} with me.")));
            items.Add(("Rename", false, () => RenameSkill(row)));
            items.Add(("Duplicate", false, () => DuplicateSkill(row)));
        }
        if (row.Skill.Directory is { Length: > 0 } || row.Skill.FilePath is { Length: > 0 })
            items.Add(("Open folder", false, () => OpenSkillFolder(row)));
        if (row.IsEditable)
            items.Add(("Remove", true, () => RemoveSkill(row)));
        return CustomizeUi.Kebab($"More options for {row.Name}", items);
    }

    private void OpenSkillFolder(SkillRow row)
    {
        var directory = row.Skill.Directory is { Length: > 0 } dir
            ? dir
            : Path.GetDirectoryName(row.Skill.FilePath);
        if (directory is { Length: > 0 } && Directory.Exists(directory))
            OpenInShell(directory);
        else if (row.Skill.FilePath.Length > 0 && File.Exists(row.Skill.FilePath))
            RevealInExplorer(row.Skill.FilePath);
    }

    private string SkillsRootFor(SkillRow row) =>
        row.Skill.Source == Skills.ProjectSource
            ? InstallScopes.SkillsRoot(InstallScope.Project, Services.Paths, Cwd)
            : Services.Paths.UserSkillsDirectory;

    private void RenameSkill(SkillRow row)
    {
        var typed = InputDialog.Prompt(Window.GetWindow(this), "Rename", row.Name, "Rename");
        if (typed is null)
            return;
        var name = SkillEditorRules.SanitizeName(typed);
        if (name.Length == 0 || !SkillEditorRules.ValidName.IsMatch(name))
        {
            Warn("Names can only contain lowercase letters, numbers, and hyphens.");
            return;
        }
        if (SkillEditorRules.ReservedWordIn(name) is { } reserved)
        {
            Warn(SkillEditorRules.ReservedWordError(reserved));
            return;
        }
        var root = SkillsRootFor(row);
        if (SkillLibrary.Exists(root, name))
        {
            Warn($"“{name}” wasn’t uploaded: a skill with that name already exists.");
            return;
        }
        try
        {
            SkillLibrary.Rename(root, row.Name, name);
            Versions.Copy(row.Name, name);
            Versions.Remove(row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            Warn(ex.Message);
            return;
        }
        SkillsWereChanged();
        RenderSkills();
    }

    private void DuplicateSkill(SkillRow row)
    {
        try
        {
            var copy = SkillLibrary.Duplicate(SkillsRootFor(row), row.Name);
            Versions.Copy(row.Name, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            Warn(ex.Message);
            return;
        }
        SkillsWereChanged();
        RenderSkills();
    }

    private void RemoveSkill(SkillRow row)
    {
        if (!Confirm("Remove skill?", "This removes the skill from this device.", "Remove", row.Name))
            return;
        try
        {
            SkillLibrary.Remove(row.Skill.FilePath);
            Versions.Remove(row.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex.Message);
            return;
        }
        Services.UiSettings.Current.SkillOverrides.Remove(row.Skill.Name);
        Services.UiSettings.Save();
        SkillsWereChanged();
        RenderSkills();
    }

    // ---------- upload ----------

    private void UploadSkill()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Upload a skill",
            Filter = "Skill files (*.zip;*.skill;*.md)|*.zip;*.skill;*.md",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        if (dialog.FileNames.Length > 1)
        {
            Warn("Upload one skill file at a time.");
            return;
        }
        AddSkillFile(dialog.FileName);
    }

    /// <summary>The reference's review step: what the file holds, then Add to library.</summary>
    private void AddSkillFile(string filePath)
    {
        if (!SkillLibrary.HasUploadExtension(filePath))
        {
            Warn("Skill files must have a .skill, .zip, or .md file extension.");
            return;
        }
        var preview = SkillLibrary.Preview(filePath);
        if (preview is null)
        {
            Warn(filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? ".md file must contain skill name and description formatted in YAML"
                : ".zip or .skill file must include a SKILL.md file");
            return;
        }

        var name = preview.Name ?? Path.GetFileNameWithoutExtension(filePath);
        var files = string.Join("\n", preview.Files.Take(12));
        if (preview.Files.Count > 12)
            files += $"\n… and {preview.Files.Count - 12} more";
        if (!ConfirmDialog.Ask(
                Window.GetWindow(this),
                $"Add “{name}” to your library?",
                "Review the skill contents before adding to your library.",
                "Add to library",
                files))
            return;

        var root = Services.Paths.UserSkillsDirectory;
        var outcome = SkillLibrary.Upload(root, filePath, overwrite: false, Versions);
        if (outcome.Kind == SkillUploadOutcome.Conflict)
        {
            if (!Confirm(
                    $"Replace {outcome.SkillName}",
                    "Upload a file to replace this skill’s contents.",
                    "Save",
                    outcome.SkillName))
                return;
            outcome = SkillLibrary.Upload(root, filePath, overwrite: true, Versions);
        }
        if (outcome.Kind != SkillUploadOutcome.Ok)
        {
            Warn(outcome.Messages.Count > 0
                ? string.Join("\n", outcome.Messages)
                : "Skill upload failed. Check the file format and try again.");
            return;
        }
        SkillsWereChanged();
        RenderSkills();
    }

    // ---------- editor ----------

    private void OpenSkillEditor(SkillRow? row)
    {
        var root = row is null ? Services.Paths.UserSkillsDirectory : SkillsRootFor(row);
        var dialog = new SkillEditorDialog(row, root, Versions) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            SkillsWereChanged();
            RenderSkills();
        }
    }

    // ---------- detail ----------

    /// <summary>
    /// A row's detail page: what the skill is, where it came from, its files and
    /// its saved versions. A skill this app cannot edit opens read-only.
    /// </summary>
    private void ShowSkillDetail(SkillRow row)
    {
        var page = NewPage();
        page.Children.Add(CustomizeUi.BackRow("Skills", ShowSkills));

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(CustomizeUi.Text(row.Name, 22, "Text100Brush", semibold: true));
        if (SkillListPresentation.Credit(row) is { } credit)
        {
            var creditBlock = CustomizeUi.Text(credit, 12.5, "Text400Brush");
            creditBlock.Margin = new Thickness(0, 4, 0, 0);
            title.Children.Add(creditBlock);
        }
        header.Children.Add(title);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(CustomizeUi.Switch(
            row.Enabled, enabled => SetSkillEnabled(row, enabled), $"Enable skill {row.Name}"));
        if (row.IsEditable)
        {
            var edit = CustomizeUi.Primary("Edit files", () => OpenSkillEditor(row));
            edit.Margin = new Thickness(10, 0, 0, 0);
            actions.Children.Add(edit);
        }
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        page.Children.Add(header);

        var description = CustomizeUi.Text(row.Skill.Description, 13, "Text400Brush", wrap: true);
        description.Margin = new Thickness(0, 10, 0, 6);
        page.Children.Add(description);

        var meta = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        meta.Children.Add(CustomizeUi.MetaCell("Source", row.Kind switch
        {
            SkillRowKind.BuiltIn => "Built in",
            SkillRowKind.Plugin => row.PluginName is { Length: > 0 } p ? $"Plugin ({p})" : "Plugin",
            _ => row.Skill.Source,
        }));
        meta.Children.Add(CustomizeUi.MetaCell("Author", SkillListPresentation.AuthorCell(row)));
        if (row.UpdatedAt is { } updated)
            meta.Children.Add(CustomizeUi.MetaCell("Last updated", updated.ToString("d")));
        if (row.Skill.Model is { Length: > 0 } model)
            meta.Children.Add(CustomizeUi.MetaCell("Model", model));
        page.Children.Add(meta);

        var files = SkillLibrary.Files(row.Skill.FilePath);
        if (files.Count > 0)
        {
            page.Children.Add(CustomizeUi.SectionHeader("Contents", files.Count));
            foreach (var file in files)
            {
                page.Children.Add(CustomizeUi.ListRow(
                    CustomizeUi.Body(file, null, null), null, null, file));
            }
        }

        var versions = Versions.List(row.Skill.Name);
        if (versions.Count > 0)
        {
            page.Children.Add(CustomizeUi.SectionHeader("Versions", versions.Count));
            foreach (var version in versions)
            {
                var restore = CustomizeUi.Ghost("Restore", () => RestoreSkillVersion(row, version));
                page.Children.Add(CustomizeUi.ListRow(
                    CustomizeUi.Body(version.Label, null, null),
                    row.IsEditable ? restore : null, null, version.Label));
            }
        }

        Show(page);
    }

    private void RestoreSkillVersion(SkillRow row, SkillVersionEntry version)
    {
        if (Versions.Read(row.Skill.Name, version.Version) is not { } content)
        {
            Warn("Skill files couldn’t be loaded. Close this dialog and try again.");
            return;
        }
        if (!Confirm("Restore version", $"This replaces the skill with {version.Label}.", "Save", row.Name))
            return;
        try
        {
            Versions.Save(row.Skill.Name, File.ReadAllText(row.Skill.FilePath));
            File.WriteAllText(row.Skill.FilePath, content, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(ex.Message);
            return;
        }
        SkillsWereChanged();
        RenderSkills();
    }
}
