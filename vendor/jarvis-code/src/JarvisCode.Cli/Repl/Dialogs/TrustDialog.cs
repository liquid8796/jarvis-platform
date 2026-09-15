using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;

namespace JarvisCode.Cli.Repl.Dialogs;

/// <summary>
/// The workspace trust dialog the reference shows the first time a folder is
/// opened (CLI 2.1.257, its onboarding trust component): the folder, the safety
/// question, what accepting allows, whatever the folder pre-approves, and two
/// buttons with the refusing one focused first.
/// </summary>
internal sealed class TrustDialog(string workingDirectory, Ansi ansi)
{
    public const string Header = "Accessing workspace:";

    public const string Question =
        "Quick safety check: Is this a project you created or one you trust? (Like your own code, a well-known " +
        "open source project, or work from your team). If not, take a moment to review what's in this folder first.";

    public const string Consequence = "Jarvis Code'll be able to read, edit, and execute files here.";

    public const string ConfirmLabel = "Yes, I trust this folder";
    public const string CancelLabel = "No, exit";

    /// <summary>The reference's footnote under a folder that pre-approves rules.</summary>
    public const string PreApprovedNotice =
        "These will apply without asking. Only proceed if you trust this configuration.";

    /// <summary>The line it writes for a folder that pre-approves permission rules.</summary>
    public static string PreApprovedRules(int count, string sources) =>
        $"{Glyphs.Warning} This folder pre-approves {count} {Format.Plural(count, "tool permission")} in {sources}:";

    /// <summary>Its line for a folder that adds directories to the workspace.</summary>
    public static string PreApprovedDirectories(int count, string sources) =>
        $"{Glyphs.Warning} This folder adds {count} {Format.Plural(count, "directory", "directories")} " +
        $"to the workspace in {sources}:";

    private readonly SelectList _list = new(
    [
        new SelectOption("no", CancelLabel),
        new SelectOption("yes", ConfirmLabel),
    ])
    { HideIndexes = true };

    public string Context => "Confirmation";

    public IReadOnlyList<string> Render(
        int columns, IReadOnlyList<string>? preApprovedRules = null, IReadOnlyList<string>? addedDirectories = null)
    {
        var lines = new List<string> { "", ansi.Color("warning", Header), ansi.Bold(workingDirectory), "" };
        foreach (var row in TextWidth.Wrap(Question, Math.Max(20, columns - 2)))
        {
            lines.Add(row);
        }

        lines.Add("");
        lines.Add(Consequence);
        if (preApprovedRules is { Count: > 0 } rules)
        {
            lines.Add("");
            lines.Add(ansi.Color("warning", PreApprovedRules(rules.Count, ".jarvis/settings.json")));
            lines.Add("  " + string.Join(", ", rules.Take(8)));
        }

        if (addedDirectories is { Count: > 0 } directories)
        {
            lines.Add("");
            lines.Add(ansi.Color("warning", PreApprovedDirectories(directories.Count, ".jarvis/settings.json")));
            lines.Add("  " + string.Join(", ", directories.Take(6)));
        }

        if (preApprovedRules is { Count: > 0 } || addedDirectories is { Count: > 0 })
        {
            lines.Add(ansi.Dim(PreApprovedNotice));
        }

        lines.Add("");
        lines.AddRange(_list.Render(ansi));
        return lines;
    }

    public DialogResult Handle(string? action, KeyPress press) => _list.Handle(action, press);
}
