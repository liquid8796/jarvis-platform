using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JarvisCode.App.Services;

/// <summary>One version and the lines its notes carry.</summary>
public sealed record ReleaseNote(string Version, IReadOnlyList<string> Bullets);

/// <summary>
/// <c>/release-notes</c>: the reference's own picker (CLI 2.1.257, the module at
/// 210560000 exporting <c>ReleaseNotesPicker</c>/<c>formatAll</c>/<c>formatVersion</c>,
/// over the changelog store at 192815000).
///
/// The mechanism is carried whole — fetch the product's release notes, parse the
/// <c>## version</c> sections, offer "Show all" over one row per version newest
/// first, and print the chosen one as a notice — and the source is swapped for
/// this product's own: the GitHub releases of <c>liquid8796/jarvis-code</c>,
/// which is the same feed <see cref="AppUpdateService"/> already reads to decide
/// there is an update. Showing the reference's cached CHANGELOG instead would be
/// presenting another product's notes as ours, and this repository publishes no
/// releases today, so what the command normally renders is the reference's own
/// empty state.
/// </summary>
public static partial class ReleaseNotes
{
    /// <summary>The description both command tables carry.</summary>
    public const string Description = "View release notes";

    /// <summary>The dialog's title.</summary>
    public const string Title = "Release notes";

    /// <summary>The line above the list.</summary>
    public const string SelectHint = "Select a version to view its notes.";

    /// <summary>The first row, which prints every version's notes at once.</summary>
    public const string ShowAll = "Show all";

    /// <summary>How many rows the reference's picker shows at once.</summary>
    public const int VisibleOptionCount = 10;

    /// <summary>Where the notes come from: this product's releases.</summary>
    public const string FeedUrl = $"https://api.github.com/repos/{AppUpdateService.Repository}/releases";

    /// <summary>The "Show all" row's description.</summary>
    public static string VersionCount(int count) => $"{count} versions";

    /// <summary>
    /// The reference's answer when the changelog holds nothing: one notice
    /// pointing at where the full list lives.
    /// </summary>
    public static string NothingToShow() => $"See the full changelog at: {AppUpdateService.ReleasesPage}";

    /// <summary>The reference's <c>formatVersion</c>.</summary>
    public static string FormatVersion(ReleaseNote note) =>
        $"Version {note.Version}:\n" + string.Join('\n', note.Bullets.Select(static b => "· " + b));

    /// <summary>
    /// The reference's <c>formatAll</c>: every version, oldest first — the
    /// reverse of the order the picker lists them in — separated by a blank line.
    /// </summary>
    public static string FormatAll(IEnumerable<ReleaseNote> notes) =>
        string.Join(
            "\n\n",
            notes.OrderBy(static n => n.Version, VersionOrder.Instance).Select(FormatVersion));

    /// <summary>The order the picker lists in: newest first.</summary>
    public static IReadOnlyList<ReleaseNote> Newest(IEnumerable<ReleaseNote> notes) =>
        [.. notes.OrderByDescending(static n => n.Version, VersionOrder.Instance)];

    [GeneratedRegex(@"^## ", RegexOptions.Multiline)]
    private static partial Regex SectionHeading();

    /// <summary>
    /// The reference's changelog parser: split on <c>## </c> headings, take the
    /// heading up to the first <c>" - "</c> as the version, and keep the
    /// <c>- </c> lines under it. A section with no bullets is dropped.
    /// </summary>
    public static IReadOnlyList<ReleaseNote> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var notes = new List<ReleaseNote>();
        var sections = SectionHeading().Split(markdown).Skip(1);
        foreach (var section in sections)
        {
            var lines = section.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (lines.Length == 0 || lines[0].Length == 0)
            {
                continue;
            }

            var version = Before(lines[0], " - ").Trim();
            if (version.Length == 0)
            {
                continue;
            }

            var bullets = lines.Skip(1)
                .Select(static l => l.Trim())
                .Where(static l => l.StartsWith("- ", StringComparison.Ordinal))
                .Select(static l => l[2..].Trim())
                .Where(static l => l.Length > 0)
                .ToList();
            if (bullets.Count > 0)
            {
                notes.Add(new ReleaseNote(version, bullets));
            }
        }

        return notes;
    }

    /// <summary>The reference's <c>gt(text, separator)</c>: everything before the first match.</summary>
    private static string Before(string text, string separator)
    {
        var index = text.IndexOf(separator, StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    /// <summary>
    /// One release of this product, read the way the changelog parser reads a
    /// section: the tag names the version and the body's <c>- </c> lines are the
    /// notes. A release whose body is itself written as <c>## version</c>
    /// sections is parsed as those instead, so either shape renders.
    /// </summary>
    public static IReadOnlyList<ReleaseNote> FromFeed(string json)
    {
        List<ReleaseNote> notes = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True))
                {
                    continue;
                }

                var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                var body = release.TryGetProperty("body", out var b) ? b.GetString() : null;
                var version = ReleaseVersion.FromTag(tag);
                if (version is null)
                {
                    continue;
                }

                var sections = Parse(body);
                if (sections.Count > 0)
                {
                    notes.AddRange(sections);
                    continue;
                }

                var bullets = (body ?? "")
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n')
                    .Select(static l => l.Trim())
                    .Where(static l => l.StartsWith("- ", StringComparison.Ordinal) ||
                                       l.StartsWith("* ", StringComparison.Ordinal))
                    .Select(static l => l[2..].Trim())
                    .Where(static l => l.Length > 0)
                    .ToList();
                if (bullets.Count > 0)
                {
                    notes.Add(new ReleaseNote(version, bullets));
                }
            }
        }

        return notes;
    }

    /// <summary>How long the feed is waited for before the command answers without it.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fetches the feed, caching a successful read for the life of the process.
    /// A feed that cannot be read reads as "no notes" — the same answer an empty
    /// one gives, since the command says where the full list is either way — but
    /// is not cached, so a run that was offline can still find them later. The
    /// reference caches its changelog on disk and races the fetch against 500ms;
    /// with no cached copy to fall back on, this waits for the answer instead.
    /// </summary>
    public static async Task<IReadOnlyList<ReleaseNote>> FetchAsync(
        HttpClient http, CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FetchTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            request.Headers.UserAgent.ParseAdd("JarvisCode");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            return _cache = Newest(FromFeed(json));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<ReleaseNote>? _cache;

    /// <summary>Orders versions the way the reference's semver comparison does.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(string? x, string? y) => ReleaseVersion.Compare(x ?? "", y ?? "");
    }
}
