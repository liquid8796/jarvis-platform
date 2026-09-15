using JarvisCode.Cli.Repl.Keys;
using JarvisCode.Cli.Repl.Render;
using JarvisCode.Cli.Repl.Terminal;
using JarvisCode.Core.Sessions;

namespace JarvisCode.Cli.Repl.Dialogs;

/// <summary>One row of the picker.</summary>
internal sealed record ResumeRow(SessionSummary Session, string Title, string Detail, string Branch)
{
    public string? Repository { get; init; }
    public bool Worktree { get; init; }
}

/// <summary>
/// The reference's resume picker (CLI 2.1.257, its session-list component):
/// "Resume session" with the count, a type-to-search box, the sessions grouped
/// and dated, and its filters — ctrl+a across projects, ctrl+b by branch,
/// ctrl+w by worktree, ctrl+r to rename, space to preview.
/// </summary>
internal sealed class ResumePicker(IReadOnlyList<ResumeRow> rows, string project, Ansi ansi, string initialQuery = "")
{
    public const string Title = "Resume session";
    public const string Refreshing = " · Refreshing…";
    public const string NoneHere = "No conversations found in this project.";
    public const string NoneAnywhere = "No conversations found.";
    public const string RenameTitle = "Rename session:";
    public const string RenamePlaceholder = "Enter new session name";
    public const string SearchHint = "Type to Search";
    public const string ListHint = "Type to search";
    public const string ShowAllProjects = "show all projects";
    public const string OnlyThisRepo = "only show current repo";
    public const string ShowAllBranches = "show all branches";
    public const string OnlyThisBranch = "only show current branch";
    public const string ShowAllWorktrees = "show all worktrees";
    public const string OnlyThisWorktree = "only show current worktree";

    /// <summary>The reference's own message when a query matches nothing.</summary>
    public static string NoMatch(string query) => $"No sessions match \"{query}\".";

    /// <summary>The chord style the picker's hints use: title-cased modifiers, upper-cased keys.</summary>
    public static readonly ChordStyle HintStyle = new(ModCase: "title", CharCase: "upper");

    /// <summary>How many rows the reference shows at once.</summary>
    public const int VisibleRows = 10;

    private readonly List<ResumeRow> _all = [.. rows];

    public string Query { get; private set; } = initialQuery;
    public bool AllProjects { get; private set; }
    public bool AllBranches { get; private set; } = true;
    public bool AllWorktrees { get; private set; } = true;
    public bool Renaming { get; private set; }
    public string RenameText { get; private set; } = "";
    public int Index { get; private set; }

    public string Context => "Select";

    /// <summary>The rows the filters and the query leave, newest first.</summary>
    public IReadOnlyList<ResumeRow> Visible
    {
        get
        {
            IEnumerable<ResumeRow> rows = _all;
            if (!AllProjects)
            {
                var currentRepository = CliGitLocation.Read(project).Repository;
                rows = rows.Where(r => PathsEqual(r.Repository ?? r.Session.WorkingDirectory, currentRepository));
            }
            if (!AllBranches)
            {
                var currentBranch = CliGitLocation.Read(project).Branch;
                rows = rows.Where(row => row.Branch == currentBranch);
            }
            if (!AllWorktrees) rows = rows.Where(row => PathsEqual(row.Session.WorkingDirectory, project));

            if (Query.Length > 0)
            {
                rows = rows.Where(r =>
                    r.Title.Contains(Query, StringComparison.OrdinalIgnoreCase) ||
                    r.Detail.Contains(Query, StringComparison.OrdinalIgnoreCase) ||
                    r.Branch.Contains(Query, StringComparison.OrdinalIgnoreCase));
            }

            return [.. rows.OrderByDescending(r => r.Session.UpdatedAt)];
        }
    }

    public ResumeRow? Current
    {
        get
        {
            var visible = Visible;
            return visible.Count == 0 ? null : visible[Math.Clamp(Index, 0, visible.Count - 1)];
        }
    }

    public IReadOnlyList<string> Render(int columns)
    {
        var visible = Visible;
        var header = Title + (visible.Count > VisibleRows ? $" ({Math.Min(VisibleRows, visible.Count)} of {visible.Count})" : "");
        var lines = new List<string> { "", ansi.Color("suggestion", ansi.Bold(header)) };
        lines.Add("  " + (Query.Length > 0 ? Query : ansi.Dim(ListHint)));
        lines.Add("");

        if (Renaming && Current is { } renaming)
        {
            lines.Add("  " + ansi.Bold(RenameTitle));
            lines.Add("  " + (RenameText.Length > 0
                ? RenameText
                : ansi.Dim(renaming.Title.Length > 0 ? renaming.Title : RenamePlaceholder)));
            lines.Add("");
            lines.Add("  " + ansi.Dim("Enter to save · Esc to cancel"));
            return lines;
        }

        if (visible.Count == 0)
        {
            lines.Add("  " + (Query.Trim().Length > 0
                ? NoMatch(Query)
                : AllProjects ? NoneAnywhere : NoneHere));
            lines.Add("");
            lines.AddRange(Hints());
            return lines;
        }

        int start = Math.Max(0, Math.Min(Index - VisibleRows / 2, visible.Count - VisibleRows));
        for (int i = start; i < Math.Min(visible.Count, start + VisibleRows); i++)
        {
            var row = visible[i];
            var pointer = i == Index ? ansi.Color("suggestion", Glyphs.Pointer) : " ";
            var title = TextWidth.Truncate(row.Title, Math.Max(10, columns - 30), "…");
            lines.Add($"{pointer} {title}  {ansi.Dim(row.Detail)}");
        }

        lines.Add("");
        lines.AddRange(Hints());
        return lines;
    }

    private IEnumerable<string> Hints()
    {
        var hints = new List<string>
        {
            ChordFormat.Hint("ctrl+a", AllProjects ? OnlyThisRepo : ShowAllProjects, HintStyle),
            ChordFormat.Hint("ctrl+b", AllBranches ? OnlyThisBranch : ShowAllBranches, HintStyle),
            ChordFormat.Hint("ctrl+w", AllWorktrees ? OnlyThisWorktree : ShowAllWorktrees, HintStyle),
            ChordFormat.Hint("ctrl+r", "rename", HintStyle),
            ChordFormat.Hint("space", "preview", HintStyle),
            ListHint,
            ChordFormat.Hint("escape", "cancel", ChordStyle.LowerKeys),
        };
        yield return "  " + ansi.Dim(string.Join(Footer.Separator, hints));
    }

    public DialogResult Handle(string? action, KeyPress press)
    {
        if (Renaming)
        {
            switch (press.Key)
            {
                case "enter":
                    Renaming = false;
                    return DialogResult.Accept("rename", RenameText);
                case "escape":
                    Renaming = false;
                    RenameText = "";
                    return DialogResult.Handled;
                case "backspace":
                    RenameText = RenameText.Length > 0 ? RenameText[..^1] : "";
                    return DialogResult.Handled;
            }

            if (press.IsPrintable && press.Text is { Length: > 0 } typed)
            {
                RenameText += typed;
            }

            return DialogResult.Handled;
        }

        switch (press.Chord)
        {
            case "ctrl+a":
                AllProjects = !AllProjects;
                Index = 0;
                return DialogResult.Handled;
            case "ctrl+b":
                AllBranches = !AllBranches;
                Index = 0;
                return DialogResult.Handled;
            case "ctrl+w":
                AllWorktrees = !AllWorktrees;
                Index = 0;
                return DialogResult.Handled;
            case "ctrl+r":
                Renaming = Current is not null;
                RenameText = Current?.Title ?? "";
                return DialogResult.Handled;
            case "space":
                return Current is { } previewed
                    ? DialogResult.Accept("preview", previewed.Session.Id)
                    : DialogResult.Handled;
        }

        switch (action)
        {
            case "select:next":
                Index = Math.Min(Index + 1, Math.Max(0, Visible.Count - 1));
                return DialogResult.Handled;
            case "select:previous":
                Index = Math.Max(0, Index - 1);
                return DialogResult.Handled;
            case "select:first":
                Index = 0;
                return DialogResult.Handled;
            case "select:last":
                Index = Math.Max(0, Visible.Count - 1);
                return DialogResult.Handled;
            case "select:accept":
                return Current is { } chosen
                    ? DialogResult.Accept("resume", chosen.Session.Id)
                    : DialogResult.Handled;
            case "select:cancel":
                if (Query.Length > 0)
                {
                    Query = "";
                    Index = 0;
                    return DialogResult.Handled;
                }

                return DialogResult.Cancelled;
        }

        if (press.Key == "backspace")
        {
            Query = Query.Length > 0 ? Query[..^1] : "";
            Index = 0;
            return DialogResult.Handled;
        }

        if (press.IsPrintable && press.Text is { Length: > 0 } text)
        {
            Query += text;
            Index = 0;
            return DialogResult.Handled;
        }

        return DialogResult.Ignored;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(a),
            System.IO.Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}
