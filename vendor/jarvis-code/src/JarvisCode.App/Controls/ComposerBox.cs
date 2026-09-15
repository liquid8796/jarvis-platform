using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using JarvisCode.App.Services;

namespace JarvisCode.App.Controls;

/// <summary>
/// A real rich composer with inline skill atoms. The text facade is the command
/// serialization the existing harness consumes; an atom is never a bitmap or
/// an editable substring pretending to be a skill.
/// </summary>
public sealed class ComposerBox : RichTextBox
{
    internal const string ClipboardDocumentFormat = "JarvisCode.ComposerDocument";
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ComposerBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (owner, args) =>
        {
            var editor = (ComposerBox)owner;
            if (!editor._synchronizing) editor.RestoreText((string?)args.NewValue ?? "");
        }));
    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(ComposerBox), new PropertyMetadata(TextWrapping.Wrap));
    public static readonly DependencyProperty ArgumentHintProperty = DependencyProperty.Register(nameof(ArgumentHint), typeof(string), typeof(ComposerBox), new PropertyMetadata(""));
    private bool _synchronizing;
    private bool _composing;
    private bool _ready;
    private IReadOnlyList<ComposerNode> _nodes = [];
    private IReadOnlyList<Fragment> _fragments = [];
    private IReadOnlyList<Fragment> _logicalFragments = [];
    private IReadOnlyList<(int Start, int End)> _skillRanges = [];
    public Func<string, ComposerSkillChip?>? ResolveSkill { get; set; }

    public ComposerBox()
    {
        Document = EmptyDocument();
        RefreshDocumentCache();
        _ready = true;
        DataObject.AddPastingHandler(this, (_, args) =>
        {
            if (args.DataObject.GetData(ClipboardDocumentFormat) is string json)
            {
                try
                {
                    if (System.Text.Json.JsonSerializer.Deserialize<ComposerDocument>(json) is { Nodes: not null } document)
                    {
                        ReplaceSelection(document);
                        args.CancelCommand();
                        return;
                    }
                }
                catch (System.Text.Json.JsonException) { }
            }
            if (args.DataObject.GetDataPresent(DataFormats.UnicodeText)) args.FormatToApply = DataFormats.UnicodeText;
        });
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) => _composing = false);
        CommandManager.AddPreviewExecutedHandler(this, OnClipboardCommand);
    }

    // Reading the text is part of every keystroke path (send state, slash commands,
    // mentions and placeholder). Keep the serialized document from the last edit
    // instead of walking the FlowDocument again for every consumer.
    public string Text { get => (string)GetValue(TextProperty); set => SetCurrentValue(TextProperty, value ?? ""); }
    public TextWrapping TextWrapping { get => (TextWrapping)GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
    public string ArgumentHint => (string)GetValue(ArgumentHintProperty);
    public bool IsComposing => _composing;
    public int CaretIndex { get => Offset(CaretPosition); set => CaretPosition = Position(value, endBias: true); }
    public int SelectionStart => Offset(Selection.Start);
    public int SelectionLength => Math.Max(0, Offset(Selection.End) - SelectionStart);
    public string SelectedText => Text.Substring(SelectionStart, Math.Min(SelectionLength, Text.Length - SelectionStart));
    public void Select(int start, int length) => Selection.Select(Position(start, false), Position(start + length, true));
    public void Clear() => Restore(ComposerDocument.Empty);
    public SpellingError? GetSpellingError(int offset) => base.GetSpellingError(Position(offset, false));
    public Rect GetRectFromCharacterIndex(int index) => Position(index, false).GetCharacterRect(LogicalDirection.Forward);

    public (int Start, int Length, string Query)? SlashQuery()
    {
        if (!Selection.IsEmpty) return null;
        var text = Text;
        var caret = Math.Min(CaretIndex, text.Length);
        for (var index = caret - 1; index >= 0 && text[index] != '\n'; index--)
        {
            if (text[index] != '/' || index > 0 && !char.IsWhiteSpace(text[index - 1])) continue;
            if (_skillRanges.Any(atom => index >= atom.Start && index < atom.End)) return null;
            return (index, caret - index, text[(index + 1)..caret]);
        }
        return null;
    }

    private FlowDocument EmptyDocument() => new(new Paragraph { Margin = new Thickness(0) })
    {
        PagePadding = new Thickness(0), FontFamily = FontFamily, FontSize = FontSize,
        LineHeight = FontSize * 20 / 14, Foreground = Foreground,
    };

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(args);
        if (!_ready) return;
        if (args.Property == FontSizeProperty) { Document.FontSize = FontSize; Document.LineHeight = FontSize * 20 / 14; }
        else if (args.Property == FontFamilyProperty) Document.FontFamily = FontFamily;
        else if (args.Property == ForegroundProperty) Document.Foreground = Foreground;
    }

    protected override void OnTextChanged(TextChangedEventArgs args)
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try
        {
            SetCurrentValue(TextProperty, RefreshDocumentCache());
            RefreshHint(_nodes);
        }
        finally { _synchronizing = false; }
        base.OnTextChanged(args);
    }

    public ComposerDocument Snapshot() => new(_nodes, Offset(CaretPosition));

    public void Restore(ComposerDocument document)
    {
        _synchronizing = true;
        try
        {
            var flow = EmptyDocument();
            var paragraph = (Paragraph)flow.Blocks.FirstBlock!;
            foreach (var node in document.NormalizedNodes())
            {
                if (node.Skill is { } chip) paragraph.Inlines.Add(Chip(chip));
                else if (node.Text is { } text) paragraph.Inlines.Add(new Run(text));
            }
            Document = flow;
            var serializedText = RefreshDocumentCache();
            SetCurrentValue(TextProperty, serializedText);
            CaretPosition = Position(document.Caret ?? serializedText.Length, true);
            RefreshHint(_nodes);
        }
        finally { _synchronizing = false; }
        base.OnTextChanged(new TextChangedEventArgs(TextChangedEvent, UndoAction.None));
    }

    private void RestoreText(string text)
    {
        var existing = _fragments.Where(fragment => fragment.Chip is not null)
            .Select(fragment => fragment.Chip!).GroupBy(chip => chip.SkillId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var nodes = new List<ComposerNode>();
        var plainStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '/' || index > 0 && !char.IsWhiteSpace(text[index - 1])) continue;
            var end = index + 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            var name = text[(index + 1)..end];
            if ((existing.GetValueOrDefault(name) ?? ResolveSkill?.Invoke(name)) is not { } chip) continue;
            if (index > plainStart) nodes.Add(new(text[plainStart..index]));
            nodes.Add(new(Skill: chip));
            plainStart = end;
            index = end - 1;
        }
        if (plainStart < text.Length) nodes.Add(new(text[plainStart..]));
        Restore(new ComposerDocument(nodes));
    }

    public void InsertSkill(ComposerSkillChip chip, int start, int length, string? arguments = null)
    {
        Selection.Select(Position(start, false), Position(start + length, true));
        ReplaceSelection(new ComposerDocument([new(Skill: chip), new(arguments is { Length: > 0 } ? arguments : " ")]));
    }

    internal void ReplaceSelection(ComposerDocument content)
    {
        BeginChange();
        try
        {
            Selection.Text = "";
            var pointer = Selection.Start.GetInsertionPosition(LogicalDirection.Forward);
            var right = new Run("", pointer);
            var inlines = right.Parent is Span span ? span.Inlines : ((Paragraph)right.Parent).Inlines;
            foreach (var node in content.NormalizedNodes())
                inlines.InsertBefore(right, node.Skill is { } chip ? Chip(chip) : new Run(node.Text ?? ""));
            CaretPosition = right.ContentEnd;
        }
        finally { EndChange(); }
    }

    private InlineUIContainer Chip(ComposerSkillChip chip)
    {
        var label = new TextBlock { Text = "/" + chip.DisplayName, IsHitTestVisible = false };
        label.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding(nameof(FontSize)) { Source = this });
        label.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding(nameof(FontFamily)) { Source = this });
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text100Brush");
        var border = new Border { Child = label, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(0, 0, 1, 0),
            ToolTip = chip.Description, Cursor = Cursors.Arrow };
        border.SetResourceReference(Border.BackgroundProperty, "Tint3Brush");
        var atom = new InlineUIContainer(border) { Tag = chip, BaselineAlignment = BaselineAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(border, "/" + chip.SkillId);
        System.Windows.Automation.AutomationProperties.SetHelpText(border, chip.Description);
        border.MouseLeftButtonDown += (_, args) => { Focus(); Selection.Select(atom.ElementStart, atom.ElementEnd); args.Handled = true; };
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) => { Selection.Select(atom.ElementStart, atom.ElementEnd); ReplaceSelection(ComposerDocument.Empty); Focus(); };
        menu.Items.Add(remove);
        border.ContextMenu = menu;
        return atom;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args);
        if (args.Handled || args.Key != Key.Back) return;
        args.Handled = SelectPreviousSkill();
    }

    internal bool SelectPreviousSkill()
    {
        if (!Selection.IsEmpty || _composing) return false;
        var caret = CaretIndex;
        var offset = 0;
        foreach (var fragment in _logicalFragments)
        {
            offset += fragment.Text.Length;
            if (fragment.Chip is null || CaretPosition.CompareTo(fragment.End) < 0) continue;
            if (offset != caret && new TextRange(fragment.End, CaretPosition).Text.Length != 0) continue;
            Selection.Select(fragment.Start, fragment.End);
            return true;
        }
        return false;
    }

    private void OnClipboardCommand(object sender, ExecutedRoutedEventArgs args)
    {
        if (args.Command == EditingCommands.ToggleBold || args.Command == EditingCommands.ToggleItalic || args.Command == EditingCommands.ToggleUnderline)
        { args.Handled = true; return; }
        if (args.Command != ApplicationCommands.Copy && args.Command != ApplicationCommands.Cut) return;
        if (Selection.IsEmpty) return;
        Clipboard.SetDataObject(CopySelection(), true);
        if (args.Command == ApplicationCommands.Cut) ReplaceSelection(ComposerDocument.Empty);
        args.Handled = true;
    }

    internal DataObject CopySelection()
    {
        var start = SelectionStart;
        var end = start + SelectionLength;
        var nodes = new List<ComposerNode>();
        var offset = 0;
        foreach (var fragment in _logicalFragments)
        {
            var from = Math.Max(start, offset);
            var to = Math.Min(end, offset + fragment.Text.Length);
            if (from < to) nodes.Add(fragment.Chip is { } chip ? new(Skill: chip) : new(fragment.Text[(from - offset)..(to - offset)]));
            offset += fragment.Text.Length;
        }
        var document = new ComposerDocument(nodes);
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, document.Text);
        data.SetData(ClipboardDocumentFormat, System.Text.Json.JsonSerializer.Serialize(document));
        return data;
    }

    private sealed record Fragment(TextPointer Start, TextPointer End, string Text, ComposerSkillChip? Chip = null);
    private List<Fragment> ReadFragments()
    {
        var result = new List<Fragment>();
        void Inlines(InlineCollection inlines)
        {
            foreach (var inline in inlines)
                switch (inline)
                {
                    case Run run: result.Add(new(run.ContentStart, run.ContentEnd, run.Text)); break;
                    case LineBreak line: result.Add(new(line.ElementStart, line.ElementEnd, "\n")); break;
                    case InlineUIContainer { Tag: ComposerSkillChip chip } atom: result.Add(new(atom.ElementStart, atom.ElementEnd, "/" + chip.SkillId, chip)); break;
                    case Span span: Inlines(span.Inlines); break;
                }
        }
        for (var block = Document.Blocks.FirstBlock; block is not null; block = block.NextBlock)
        {
            if (block is Paragraph paragraph) Inlines(paragraph.Inlines);
            else result.Add(new(block.ContentStart, block.ContentEnd, new TextRange(block.ContentStart, block.ContentEnd).Text.TrimEnd('\r', '\n')));
            if (block.NextBlock is { } next) result.Add(new(block.ContentEnd, next.ContentStart, "\n"));
        }
        return result;
    }

    private static IReadOnlyList<Fragment> BuildLogicalFragments(IReadOnlyList<Fragment> fragments)
    {
        var result = new List<Fragment>();
        Fragment? previous = null;
        foreach (var fragment in fragments)
        {
            var text = fragment.Text.TrimStart('\u200b');
            if (previous?.Chip is not null && text.Length > 0 && text[0] is not ' ' and not '\u00a0')
                result.Add(new(previous.End, previous.End, " "));
            result.Add(fragment);
            if (fragment.Text.Length > 0) previous = fragment;
        }
        return result;
    }

    private string RefreshDocumentCache()
    {
        _fragments = ReadFragments();
        _logicalFragments = BuildLogicalFragments(_fragments);
        _nodes = _fragments.Select(fragment => fragment.Chip is null
            ? new ComposerNode(fragment.Text)
            : new ComposerNode(Skill: fragment.Chip)).ToArray();

        var skillRanges = new List<(int Start, int End)>();
        var offset = 0;
        foreach (var fragment in _logicalFragments)
        {
            var start = offset;
            offset += fragment.Text.Length;
            if (fragment.Chip is not null) skillRanges.Add((start, offset));
        }
        _skillRanges = skillRanges;

        if (_logicalFragments.Count == 0) return "";
        if (_logicalFragments.Count == 1) return _logicalFragments[0].Text;
        var text = new StringBuilder(offset);
        foreach (var fragment in _logicalFragments) text.Append(fragment.Text);
        return text.ToString();
    }

    private int Offset(TextPointer pointer)
    {
        var offset = 0;
        foreach (var fragment in _logicalFragments)
        {
            if (pointer.CompareTo(fragment.Start) <= 0) return offset;
            if (pointer.CompareTo(fragment.End) < 0)
                return offset + (fragment.Chip is null ? Math.Min(fragment.Text.Length, new TextRange(fragment.Start, pointer).Text.Length) : fragment.Text.Length);
            offset += fragment.Text.Length;
        }
        return offset;
    }

    private TextPointer Position(int index, bool endBias)
    {
        index = Math.Clamp(index, 0, Text.Length);
        var offset = 0;
        foreach (var fragment in _logicalFragments)
        {
            if (index <= offset) return fragment.Start;
            if (index < offset + fragment.Text.Length)
                return fragment.Chip is not null ? endBias ? fragment.End : fragment.Start
                    : fragment.Start.GetPositionAtOffset(index - offset) ?? fragment.End;
            if (index == offset + fragment.Text.Length) return fragment.End;
            offset += fragment.Text.Length;
        }
        return Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    private void RefreshHint(IReadOnlyList<ComposerNode> source)
    {
        var nodes = source.Any(node => node.Skill is not null)
            ? new ComposerDocument(source).NormalizedNodes()
            : source;
        var chips = nodes.Where(node => node.Skill is not null).ToArray();
        var hint = chips.Length == 1 && nodes.Where(node => node.Skill is null).All(node => string.IsNullOrWhiteSpace(node.Text)) &&
            nodes.LastOrDefault()?.Text is { Length: > 0 } && nodes.Sum(node => node.Skill is null ? node.Text?.Length ?? 0 : 1) + 2 <= 128
            ? chips[0].Skill!.ArgumentHint : "";
        if (string.Equals(ArgumentHint, hint, StringComparison.Ordinal)) return;
        SetCurrentValue(ArgumentHintProperty, hint);
        System.Windows.Automation.AutomationProperties.SetHelpText(this, hint);
    }
}
