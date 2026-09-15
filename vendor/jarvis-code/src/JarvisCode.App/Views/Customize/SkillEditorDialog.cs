using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Views.Customize;

/// <summary>
/// The reference's skill editor (c5e558aae <c>mi</c>): a name that is sanitized
/// per keystroke and frozen once saved, a description with its 1024-character
/// counter and its two refusals, the instructions under them, and a footer that
/// says which version is on screen. Saving over an existing skill snapshots what
/// was there first, which is what makes "Save version" mean something here.
/// </summary>
public sealed class SkillEditorDialog : Window
{
    private readonly SkillRow? _editing;
    private readonly string _skillsDirectory;
    private readonly SkillVersions _versions;

    private readonly TextBox _name;
    private readonly TextBox _description;
    private readonly TextBox _instructions;
    private readonly TextBlock _nameError;
    private readonly TextBlock _descriptionError;
    private readonly TextBlock _counter;
    private readonly TextBlock _versionLabel;
    private readonly TextBlock _alert;
    private readonly Button _save;

    private readonly string _originalDescription;
    private readonly string _originalInstructions;

    public SkillEditorDialog(SkillRow? editing, string skillsDirectory, SkillVersions versions)
    {
        _editing = editing;
        _skillsDirectory = skillsDirectory;
        _versions = versions;

        Title = editing is null ? "Create a skill" : "Edit skill";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg100Brush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFontFamily");

        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

        var heading = CustomizeUi.Text(Title, 17, "Text100Brush", semibold: true);
        heading.Margin = new Thickness(0, 0, 0, 6);
        body.Children.Add(heading);

        _alert = CustomizeUi.Text("", 12.5, "Danger100Brush", wrap: true);
        _alert.Margin = new Thickness(0, 6, 0, 0);
        _alert.Visibility = Visibility.Collapsed;
        body.Children.Add(_alert);

        var currentMd = ReadCurrent();
        var (parsedName, parsedDescription) = currentMd is null
            ? (null, null)
            : SkillLibrary.NameAndDescription(currentMd);

        _name = CustomizeUi.Field(body, "Skill name", editing?.Name ?? parsedName ?? "");
        _name.MaxLength = SkillEditorRules.MaxNameLength;
        _name.IsEnabled = editing is null;
        if (editing is null)
        {
            var placeholder = CustomizeUi.Text("weekly-status-report", 12.5, "Text500Brush");
            placeholder.Margin = new Thickness(2, 3, 0, 0);
            body.Children.Add(placeholder);
        }
        _nameError = CustomizeUi.FieldError(body);

        var descriptionHeader = new Grid { Margin = new Thickness(2, 10, 0, 4) };
        descriptionHeader.ColumnDefinitions.Add(new ColumnDefinition());
        descriptionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        descriptionHeader.Children.Add(CustomizeUi.Text("Description", 12, "Text400Brush"));
        _counter = CustomizeUi.Text("", 12, "Text500Brush");
        Grid.SetColumn(_counter, 1);
        descriptionHeader.Children.Add(_counter);
        body.Children.Add(descriptionHeader);

        _originalDescription = editing?.Skill.Description ?? parsedDescription ?? "";
        _description = new TextBox
        {
            Text = _originalDescription,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 62,
            MaxHeight = 124,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _description.SetResourceReference(StyleProperty, "InputTextBox");
        System.Windows.Automation.AutomationProperties.SetName(_description, "Description");
        body.Children.Add(_description);
        _descriptionError = CustomizeUi.FieldError(body);

        _originalInstructions = currentMd is null ? "" : SkillEditorRules.BodyOf(currentMd);
        _instructions = CustomizeUi.Field(body, "Instructions", _originalInstructions, multiline: true);
        _instructions.MinHeight = 200;
        if (_originalInstructions.Length == 0)
        {
            var placeholder = CustomizeUi.Text(
                "Summarize my recent work in three sections: wins, blockers, and next steps. Keep the tone professional but not stiff...",
                11.5, "Text500Brush", wrap: true);
            placeholder.Margin = new Thickness(2, 4, 0, 0);
            body.Children.Add(placeholder);
        }

        if (editing is not null && SkillLibrary.HasExtraFiles(editing.Skill.FilePath))
        {
            var notice = CustomizeUi.Text(
                "This skill contains additional files or metadata that can’t be edited here. Use Replace to upload a new version.",
                12, "Warning100Brush", wrap: true);
            notice.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(notice);
        }

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _versionLabel = CustomizeUi.Text("", 12, "Text500Brush");
        _versionLabel.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_versionLabel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = CustomizeUi.Ghost("Cancel", Close);
        buttons.Children.Add(cancel);
        _save = CustomizeUi.Primary(editing is null ? "Create" : "Save version", Save);
        _save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_save);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        body.Children.Add(footer);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _name.TextChanged += (_, _) => OnNameChanged();
        _description.TextChanged += (_, _) => Validate();
        _instructions.TextChanged += (_, _) => Validate();
        PreviewKeyDown += OnKeyDown;
        Validate();
        CustomizeUi.FocusWhenLoaded(editing is null ? _name : _description);
    }

    private string? ReadCurrent()
    {
        if (_editing is null || _editing.Skill.FilePath.Length == 0)
            return null;
        try
        {
            return File.Exists(_editing.Skill.FilePath) ? File.ReadAllText(_editing.Skill.FilePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnNameChanged()
    {
        var sanitized = SkillEditorRules.SanitizeName(_name.Text);
        if (!string.Equals(sanitized, _name.Text, StringComparison.Ordinal))
        {
            var caret = _name.CaretIndex;
            _name.Text = sanitized;
            _name.CaretIndex = Math.Min(caret, sanitized.Length);
        }
        Validate();
    }

    private bool Dirty =>
        !string.Equals(_description.Text, _originalDescription, StringComparison.Ordinal) ||
        !string.Equals(_instructions.Text, _originalInstructions, StringComparison.Ordinal);

    private void Validate()
    {
        var reserved = SkillEditorRules.ReservedWordIn(_name.Text);
        CustomizeUi.SetError(_nameError, reserved is null ? null : SkillEditorRules.ReservedWordError(reserved));

        var description = _description.Text;
        CustomizeUi.SetError(_descriptionError,
            SkillEditorRules.DescriptionTooLong(description) ? "Description must be under 1024 characters"
            : SkillEditorRules.DescriptionHasXml(description) ? "Description cannot contain XML tags"
            : null);

        _counter.Text = SkillEditorRules.ShowsCounter(description)
            ? $"{description.Length}/{SkillEditorRules.MaxDescriptionLength}"
            : "";
        _counter.SetResourceReference(TextBlock.ForegroundProperty,
            SkillEditorRules.DescriptionTooLong(description) ? "Danger100Brush" : "Text500Brush");

        _versionLabel.Text = SkillEditorRules.VersionLabel(
            _editing is not null, Dirty, _versions.Head(_editing?.Skill.Name ?? ""), _editing?.UpdatedAt);

        _save.IsEnabled =
            !SkillEditorRules.NameAndDescriptionInvalid(_name.Text, description) &&
            (_editing is not null || _instructions.Text.Trim().Length > 0);
    }

    private void Alert(string message)
    {
        _alert.Text = message;
        _alert.Visibility = Visibility.Visible;
    }

    private void Save()
    {
        _alert.Visibility = Visibility.Collapsed;
        var name = _name.Text.Trim();
        var description = _description.Text.Trim();
        var instructions = _instructions.Text;

        if (SkillEditorRules.NameAndDescriptionInvalid(name, description))
        {
            Alert("Add a valid name and description before saving.");
            return;
        }

        if (_editing is null)
        {
            if (SkillEditorRules.StartsWithFrontmatter(instructions))
            {
                Alert("Your instructions start with a --- frontmatter block. Remove it — the name and description fields above become the frontmatter.");
                return;
            }
            if (!SkillEditorRules.ValidName.IsMatch(name))
            {
                Alert("Names can only contain lowercase letters, numbers, and hyphens.");
                return;
            }
            if (SkillLibrary.Exists(_skillsDirectory, name))
            {
                Alert($"“{name}” wasn’t uploaded: a skill with that name already exists.");
                return;
            }
            try
            {
                SkillLibrary.Create(_skillsDirectory, name, description, instructions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Alert(ex.Message);
                return;
            }
            DialogResult = true;
            return;
        }

        var path = _editing.Skill.FilePath;
        if (path.Length == 0 || !File.Exists(path))
        {
            Alert("Skill files couldn’t be loaded. Close this dialog and try again.");
            return;
        }
        try
        {
            var current = File.ReadAllText(path);
            if (SkillEditorRules.WithDescription(current, description) is null)
            {
                Alert("SKILL.md is missing its frontmatter, so the description can’t be updated. Fix the file’s leading --- block and try again.");
                return;
            }
            SkillLibrary.SaveVersion(path, _editing.Skill.Name, description, instructions, _versions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Alert(ex.Message);
            return;
        }
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                 _save.IsEnabled)
        {
            e.Handled = true;
            Save();
        }
    }
}
