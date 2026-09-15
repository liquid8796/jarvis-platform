using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Dialogs;

internal sealed class ChoiceDialog(string title, IReadOnlyList<SelectOption> choices,
    string context = "Select", int selected = 0)
{
    public string Context { get; } = context;
    public SelectList List { get; } = new(choices, Math.Max(0, selected));
    public int EffortIndex { get; set; } = context == "EffortSlider" ? selected : 2;
    public bool SessionOnly { get; private set; }
    public bool HasEffort => Context is "ModelPicker" or "EffortSlider";

    public DialogResult Handle(string? action, KeyPress press)
    {
        if (HasEffort && (action is "modelPicker:thisSessionOnly" or "effortSlider:thisSessionOnly" || press.Text == "s"))
        { SessionOnly = !SessionOnly; return DialogResult.Handled; }
        if (HasEffort && (action == "modelPicker:decreaseEffort" || press.Key == "left"))
        { EffortIndex = Math.Max(0, EffortIndex - 1); if (Context == "EffortSlider") List.MoveTo(EffortIndex); return DialogResult.Handled; }
        if (HasEffort && (action == "modelPicker:increaseEffort" || press.Key == "right"))
        { EffortIndex = Math.Min(RootOptions.EffortChoices.Length - 1, EffortIndex + 1); if (Context == "EffortSlider") List.MoveTo(EffortIndex); return DialogResult.Handled; }
        var result = List.Handle(press.Key switch
        {
            "up" => "select:previous", "down" => "select:next", "enter" => "select:accept",
            "escape" => "select:cancel", _ => action,
        }, press);
        if (Context == "EffortSlider") EffortIndex = List.Index;
        return result;
    }

    public IReadOnlyList<string> Render(Ansi ansi, int columns)
    {
        var lines = new List<string> { "", ansi.Bold(title) };
        var first = Math.Max(0, List.Index - 5);
        for (var index = first; index < Math.Min(List.Options.Count, first + 10); index++)
        {
            var item = List.Options[index];
            var text = $"{(index == List.Index ? Glyphs.Pointer : " ")} {index + 1}. {item.Label}";
            lines.Add(TextWidth.Truncate(index == List.Index ? ansi.Color("suggestion", text) : text, columns, "…"));
            if (index == List.Index && item.Description is { Length: > 0 } description)
                lines.Add(ansi.Dim("   " + TextWidth.Truncate(description, Math.Max(1, columns - 3), "…")));
        }
        if (HasEffort) lines.Add("Effort: " + RootOptions.EffortChoices[EffortIndex] +
            (SessionOnly ? " · this session only" : " · saved") + "  (←/→ effort, s scope)");
        lines.Add(ansi.Dim("↑/↓ select · Enter confirm · Esc cancel"));
        return lines;
    }
}
