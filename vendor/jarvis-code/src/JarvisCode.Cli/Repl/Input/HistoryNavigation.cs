namespace JarvisCode.Cli.Repl.Input;

/// <summary>
/// ↑/↓ through the prompt history: the draft in the box is stashed on the first
/// step back and restored by stepping past the newest entry, so a half-typed
/// prompt survives a look at what came before. The reference walks its
/// project-scoped list newest first and de-duplicates by display text.
/// </summary>
internal sealed class HistoryNavigator(PromptHistory history, string project, string? sessionId)
{
    private IReadOnlyList<HistoryEntry>? _entries;
    private int _index = -1;
    private string? _draft;

    /// <summary>True while the box is showing a history entry rather than the user's own draft.</summary>
    public bool IsBrowsing => _index >= 0;

    private IReadOnlyList<HistoryEntry> Entries =>
        _entries ??= history.Read(HistoryScope.Project, project, sessionId);

    /// <summary>Forgets the cached list and the stashed draft (a submitted prompt starts over).</summary>
    public void Reset()
    {
        _entries = null;
        _index = -1;
        _draft = null;
    }

    /// <summary>Steps back one entry. False when there is nothing older to show.</summary>
    public bool Previous(Composer composer)
    {
        var entries = Entries;
        if (entries.Count == 0 || _index + 1 >= entries.Count)
        {
            return false;
        }

        if (_index < 0)
        {
            _draft = composer.Text;
        }

        _index++;
        Apply(composer, entries[_index]);
        return true;
    }

    /// <summary>Steps forward one entry, restoring the stashed draft past the newest.</summary>
    public bool Next(Composer composer)
    {
        if (_index < 0)
        {
            return false;
        }

        _index--;
        if (_index < 0)
        {
            composer.Set(_draft ?? "");
            _draft = null;
            return true;
        }

        Apply(composer, Entries[_index]);
        return true;
    }

    private static void Apply(Composer composer, HistoryEntry entry)
    {
        composer.Set(entry.Display);
        composer.Pastes.Clear();
        if (entry.PastedContents is { Count: > 0 } pasted)
        {
            composer.Pastes.Restore(pasted);
        }
    }
}

/// <summary>
/// Ctrl+R: the reference's reverse history search. A query filters the scope's
/// entries newest first, ctrl+r walks the matches, ctrl+s cycles the scope
/// (project → session → everywhere), escape and tab accept the match into the
/// box, enter runs it, and ctrl+c leaves the box as it was.
/// </summary>
internal sealed class HistorySearch(PromptHistory history, string project, string? sessionId)
{
    private IReadOnlyList<HistoryEntry> _matches = [];
    private bool _stale = true;

    public string Query { get; private set; } = "";
    public HistoryScope Scope { get; private set; } = HistoryScope.Project;
    public int Index { get; private set; }

    /// <summary>What the box held when the search opened, restored if it is cancelled.</summary>
    public string Restore { get; set; } = "";

    public IReadOnlyList<HistoryEntry> Matches
    {
        get
        {
            if (_stale)
            {
                var all = history.Read(Scope, project, sessionId);
                _matches = Query.Length == 0
                    ? all
                    : [.. all.Where(e => e.Display.Contains(Query, StringComparison.OrdinalIgnoreCase))];
                _stale = false;
                if (Index >= _matches.Count)
                {
                    Index = Math.Max(0, _matches.Count - 1);
                }
            }

            return _matches;
        }
    }

    public HistoryEntry? Current => Matches.Count == 0 ? null : Matches[Math.Clamp(Index, 0, Matches.Count - 1)];

    public void SetQuery(string query)
    {
        Query = query;
        Index = 0;
        _stale = true;
    }

    public void Type(string text) => SetQuery(Query + text);

    public void Backspace()
    {
        if (Query.Length > 0)
        {
            SetQuery(Query[..^1]);
        }
    }

    /// <summary>ctrl+r again: the next older match, stopping at the oldest.</summary>
    public void NextMatch()
    {
        if (Index + 1 < Matches.Count)
        {
            Index++;
        }
    }

    /// <summary>ctrl+s: the reference cycles project → session → everywhere.</summary>
    public void CycleScope()
    {
        Scope = HistoryScopes.Next(Scope);
        Index = 0;
        _stale = true;
    }
}
