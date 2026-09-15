using System.IO;
using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>What the composer's history hint is showing.</summary>
public enum ComposerHistoryMode
{
    /// <summary>Nothing: the composer is in its ordinary state.</summary>
    Off,

    /// <summary>Ctrl+R: a reverse search over the history.</summary>
    Search,

    /// <summary>Up/Down: stepping through the history one prompt at a time.</summary>
    Navigate,
}

/// <summary>The hint under the composer, and the text the composer should hold.</summary>
/// <param name="Mode">Which hint, if any.</param>
/// <param name="Query">The search query, in Search mode.</param>
/// <param name="Failed">Whether the query matches nothing.</param>
/// <param name="Index">1-based position in Navigate mode.</param>
/// <param name="Total">How many entries the mode is stepping through.</param>
/// <param name="Text">What the composer should hold, or null to leave it alone.</param>
/// <param name="HasDraft">Whether a draft is being held for the user to come back to.</param>
public readonly record struct ComposerHistoryView(
    ComposerHistoryMode Mode,
    string Query,
    bool Failed,
    int Index,
    int Total,
    string? Text,
    bool HasDraft);

/// <summary>
/// The composer's prompt history: the reverse search Ctrl+R opens and the Up/Down
/// walk, with the reference's own hints (<c>cd089cf92-CPpbZ5h_.js</c>, its
/// <c>id</c> hint renderer — "Search history:", the ↑ ↓ cycle · esc cancel row,
/// "History {index}/{total}" and "↓ to restore your draft").
///
/// The state machine is here rather than in the surface so it can be tested
/// without a window; the surface only moves the caret and paints the hint.
/// </summary>
public sealed class ComposerHistory(IReadOnlyList<string> entries)
{
    public const string SearchHistory = "Search history:";               // lOiJkROzUk
    public const string CycleHint = "cycle";                              // wS5Ru7ip+A
    public const string CancelHint = "cancel";                            // cqZqGKbHSZ
    public const string RestoreDraft = "↓ to restore your draft";        // sYWwXvJhx5

    private readonly List<string> _entries = [.. entries];
    private string _query = "";
    private string? _draft;
    private int _position = -1;
    private ComposerHistoryMode _mode = ComposerHistoryMode.Off;

    /// <summary>"History {index}/{total}".</summary>
    public static string NavigateLabel(int index, int total) => $"History {index}/{total}";   // OL7Tgn9cjy

    /// <summary>
    /// The sentence the reference gives an assistive reader while the search is
    /// open — the query, the match it landed on, and whether there was none.
    /// </summary>
    public static string SearchStatus(string query, int matchIndex, int total, bool failed) =>
        // KyULHmVeJ7
        SearchHistory + " " + query
        + (matchIndex > 0 ? $" — match {matchIndex} of {total}" : "")
        + (failed ? " — no match" : "");

    /// <summary>Opens the reverse search, holding the composer's draft.</summary>
    public ComposerHistoryView BeginSearch(string draft)
    {
        _mode = ComposerHistoryMode.Search;
        _draft = draft;
        _query = "";
        _position = -1;
        return Describe(null);
    }

    /// <summary>Types into the search, landing on the newest match.</summary>
    public ComposerHistoryView Search(string query)
    {
        _mode = ComposerHistoryMode.Search;
        _query = query;
        _position = Matches().Count > 0 ? 0 : -1;
        return Describe(Current());
    }

    /// <summary>Moves to the next (older) or previous (newer) match.</summary>
    public ComposerHistoryView Cycle(int direction)
    {
        var matches = Matches();
        if (matches.Count == 0)
        {
            return Describe(null);
        }

        _position = _position < 0
            ? 0
            : Math.Clamp(_position + direction, 0, matches.Count - 1);
        return Describe(Current());
    }

    /// <summary>
    /// Up/Down through the history with no query. Direction 1 goes back in time.
    /// Stepping past the newest entry restores the draft, which is what the
    /// reference's "↓ to restore your draft" promises.
    /// </summary>
    public ComposerHistoryView Navigate(int direction, string draft)
    {
        if (_entries.Count == 0)
        {
            return Describe(null);
        }

        if (_mode != ComposerHistoryMode.Navigate)
        {
            _mode = ComposerHistoryMode.Navigate;
            _draft = draft;
            _query = "";
            _position = -1;
        }

        var next = _position + direction;
        if (next < 0)
        {
            var draftText = _draft ?? "";
            _mode = ComposerHistoryMode.Off;
            _position = -1;
            return new ComposerHistoryView(ComposerHistoryMode.Off, "", false, 0, 0, draftText, false);
        }

        _position = Math.Min(next, _entries.Count - 1);
        return Describe(_entries[_position]);
    }

    /// <summary>Leaves the search or the walk, putting the draft back.</summary>
    public ComposerHistoryView Cancel()
    {
        var draft = _draft ?? "";
        _mode = ComposerHistoryMode.Off;
        _query = "";
        _position = -1;
        _draft = null;
        return new ComposerHistoryView(ComposerHistoryMode.Off, "", false, 0, 0, draft, false);
    }

    /// <summary>Keeps what the search landed on and closes the hint.</summary>
    public ComposerHistoryView Accept()
    {
        var text = Current();
        _mode = ComposerHistoryMode.Off;
        _query = "";
        _position = -1;
        _draft = null;
        return new ComposerHistoryView(ComposerHistoryMode.Off, "", false, 0, 0, text, false);
    }

    public ComposerHistoryMode Mode => _mode;

    private IReadOnlyList<string> Matches() =>
        _query.Length == 0
            ? _entries
            : [.. _entries.Where(e => e.Contains(_query, StringComparison.OrdinalIgnoreCase))];

    private string? Current()
    {
        var source = _mode == ComposerHistoryMode.Navigate ? _entries : Matches();
        return _position >= 0 && _position < source.Count ? source[_position] : null;
    }

    private ComposerHistoryView Describe(string? text)
    {
        var matches = _mode == ComposerHistoryMode.Navigate ? _entries : Matches();
        var failed = _mode == ComposerHistoryMode.Search && _query.Length > 0 && matches.Count == 0;
        return new ComposerHistoryView(
            _mode,
            _query,
            failed,
            _position + 1,
            matches.Count,
            text,
            _draft is { Length: > 0 });
    }
}

/// <summary>
/// The prompts the user has sent, newest first, kept beside the app's other state.
/// One list serves both surfaces, as the reference's one composer does.
/// </summary>
public sealed class ComposerHistoryStore
{
    /// <summary>How many prompts are kept.</summary>
    public const int Capacity = 200;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly List<string> _entries;

    public ComposerHistoryStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Records a sent prompt, moving a repeat back to the front.</summary>
    public void Add(string prompt)
    {
        var text = prompt.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _entries.RemoveAll(e => string.Equals(e, text, StringComparison.Ordinal));
        _entries.Insert(0, text);
        if (_entries.Count > Capacity)
        {
            _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        }

        Save();
    }

    /// <summary>A history over the stored prompts.</summary>
    public ComposerHistory Open() => new(_entries);

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Options));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; failing to write it must not fail a send.
        }
    }

    private static List<string> Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath)) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt history file reads as an empty one.
        }

        return [];
    }
}
