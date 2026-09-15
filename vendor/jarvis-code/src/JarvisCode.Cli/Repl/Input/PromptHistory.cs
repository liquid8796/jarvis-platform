using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JarvisCode.Cli.Repl.Input;

/// <summary>One line of history.jsonl.</summary>
internal sealed record HistoryEntry(string Display, long Timestamp, string Project, string? SessionId,
    IReadOnlyDictionary<string, string>? PastedContents = null);

/// <summary>
/// The reference's prompt history: an append-only <c>history.jsonl</c> under the
/// profile with <c>{display, pastedContents, timestamp, project, sessionId}</c>
/// per line, read newest first, de-duplicated by display, and filtered by scope
/// — this project, this session, or everywhere (Ctrl+S cycles in the search).
/// A prompt that carried a paste is not recorded when it was a slash command,
/// and a bare paste is not recorded at all, as the reference's <c>addEntry</c> refuses.
/// </summary>
internal sealed class PromptHistory
{
    private readonly string _path;
    private readonly List<HistoryEntry> _entries = [];

    /// <summary>The most entries one project scope yields; this build's own figure (the reference's cap is not in the binary as a literal).</summary>
    public const int MaxEntriesPerProject = 1000;

    public PromptHistory(string path)
    {
        _path = path;
        Load();
    }

    public static string DefaultPath(string profileRoot) => Path.Combine(profileRoot, "history.jsonl");

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(line) is JsonObject obj && obj["display"] is JsonValue display &&
                        display.TryGetValue<string>(out var text))
                    {
                        var pasted = obj["pastedContents"] as JsonObject;
                        Dictionary<string, string>? contents = null;
                        if (pasted is not null && pasted.Count > 0)
                        {
                            contents = [];
                            foreach (var (key, value) in pasted)
                            {
                                if (value is JsonObject entry && entry["content"] is JsonValue c && c.TryGetValue<string>(out var content))
                                {
                                    contents[key] = content;
                                }
                            }
                        }

                        _entries.Add(new HistoryEntry(
                            text,
                            obj["timestamp"] is JsonValue ts && ts.TryGetValue<long>(out var stamp) ? stamp : 0,
                            obj["project"]?.GetValue<string>() ?? "",
                            obj["sessionId"]?.GetValue<string>(),
                            contents));
                    }
                }
                catch (JsonException)
                {
                    // A broken line is skipped; the reference counts it and moves on.
                }
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Records a submitted prompt. Returns false when the reference would not record it.</summary>
    public bool Add(string display, string project, string? sessionId, IReadOnlyDictionary<string, string>? pasted = null,
        long? timestamp = null)
    {
        var trimmed = display.Trim();
        bool hadPaste = pasted is { Count: > 0 };
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (hadPaste && trimmed.StartsWith('/'))
        {
            return false;
        }

        // The reference's HGr: an immediate repeat of the same prompt in the
        // same project and session is not stored again, unless the entry it
        // would repeat carried pastes of its own.
        if (_entries.Count > 0)
        {
            var previous = _entries[^1];
            if (previous.Display == display && PathsEqual(previous.Project, project) &&
                previous.SessionId == sessionId && previous.PastedContents is not { Count: > 0 })
            {
                return false;
            }
        }

        var entry = new HistoryEntry(display, timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), project, sessionId, pasted);
        _entries.Add(entry);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var obj = new JsonObject
            {
                ["display"] = display,
                ["pastedContents"] = PastedJson(pasted),
                ["timestamp"] = entry.Timestamp,
                ["project"] = project,
                ["sessionId"] = sessionId,
            };
            File.AppendAllText(_path, obj.ToJsonString() + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return true;
    }

    private static JsonObject PastedJson(IReadOnlyDictionary<string, string>? pasted)
    {
        var obj = new JsonObject();
        if (pasted is null)
        {
            return obj;
        }

        foreach (var (key, content) in pasted)
        {
            obj[key] = new JsonObject { ["id"] = int.TryParse(key, out var id) ? id : 0, ["type"] = "text", ["content"] = content };
        }

        return obj;
    }

    /// <summary>Newest first, de-duplicated by display text, filtered by scope.</summary>
    public IReadOnlyList<HistoryEntry> Read(HistoryScope scope, string project, string? sessionId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<HistoryEntry>();
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (scope == HistoryScope.Project && !PathsEqual(entry.Project, project))
            {
                continue;
            }

            if (scope == HistoryScope.Session && entry.SessionId != sessionId)
            {
                continue;
            }

            if (!seen.Add(entry.Display))
            {
                continue;
            }

            result.Add(entry);
            if (result.Count >= MaxEntriesPerProject)
            {
                break;
            }
        }

        return result;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>The three scopes Ctrl+S cycles through in the history search.</summary>
internal enum HistoryScope
{
    Project,
    Session,
    Everywhere,
}

internal static class HistoryScopes
{
    public static HistoryScope Next(HistoryScope scope) => scope switch
    {
        HistoryScope.Project => HistoryScope.Session,
        HistoryScope.Session => HistoryScope.Everywhere,
        _ => HistoryScope.Project,
    };

    /// <summary>The reference's scope word in the search prompt.</summary>
    public static string Label(HistoryScope scope) => scope switch
    {
        HistoryScope.Project => "project",
        HistoryScope.Session => "session",
        _ => "everywhere",
    };
}
