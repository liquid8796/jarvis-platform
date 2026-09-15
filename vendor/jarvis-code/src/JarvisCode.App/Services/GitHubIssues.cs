using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>One row of the picker's list.</summary>
public sealed record GitHubIssue(
    int Number,
    string Title,
    string State,
    string Repo,
    string Url,
    IReadOnlyList<string> Labels)
{
    /// <summary>The reference's row key: <c>{repo}#{number}</c>.</summary>
    public string Key => $"{Repo}#{Number}";
}

/// <summary>The issue with the body the picker fetches after the row is chosen.</summary>
public sealed record GitHubIssueDetail(string Body, string Author, IReadOnlyList<string> Labels);

/// <summary>
/// The reference's Import GitHub issue flow (its picker <c>jA</c> and the context
/// builder <c>E_</c> in the ccd chunk <c>c11959232-DM8o5ho4.js</c>, over the main
/// process's <c>listGhIssues</c> / <c>getGhIssue</c> in
/// <c>index2.chunk-DTg3UwuF.js</c>).
///
/// The reference calls GitHub's REST API through its own signed-in client; this
/// build makes the same two calls through <c>gh api</c>, which is the user's own
/// GitHub CLI credential. The query, the fields and the composed context block are
/// the reference's.
/// </summary>
public static class GitHubIssues
{
    // ---- the picker's own strings ----

    public const string Title = "Import issue";

    public const string SearchPlaceholder = "Search by issue number or title";

    public const string Loading = "Loading...";

    public const string Failed = "Something went wrong. You can try again.";

    public const string SelectARepository = "Select a repository to see issues.";

    public const string NoIssues = "No issues you’re involved with here. Search to find any issue.";

    public const string NoMatches = "No issues match your search.";

    /// <summary>The reference debounces the search box by 300ms before it queries.</summary>
    public const int SearchDebounceMs = 300;

    /// <summary>The reference asks GitHub for 50 rows.</summary>
    public const int PageSize = 50;

    /// <summary>The reference's default query: the issues the signed-in user is involved with.</summary>
    public const string DefaultQuery = "involves:@me";

    /// <summary>
    /// The reference's search: this repository's open issues, narrowed by whatever
    /// was typed, or by <c>involves:@me</c> when nothing was.
    /// </summary>
    public static string SearchQuery(string repoSlug, string? typed)
    {
        var query = string.IsNullOrWhiteSpace(typed) ? DefaultQuery : typed.Trim();
        return $"repo:{repoSlug} is:issue is:open {query}";
    }

    /// <summary>The REST path the reference's list call makes.</summary>
    public static string SearchPath(string repoSlug, string? typed) =>
        $"search/issues?per_page={PageSize}&q={Uri.EscapeDataString(SearchQuery(repoSlug, typed))}";

    /// <summary>The REST path its detail call makes.</summary>
    public static string IssuePath(string repoSlug, int number) => $"repos/{repoSlug}/issues/{number}";

    // ---- the context block the chosen issue puts on the composer ----

    /// <summary>The reference's body cap, past which it appends its truncation note.</summary>
    public const int MaxBodyChars = 16000;

    /// <summary>
    /// The reference's <c>R_</c>: format and default-ignorable code points and the
    /// C0/C1 controls are stripped, apart from the Arabic marks it allows through.
    /// </summary>
    public static string StripControls(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsAllowedMark(rune.Value) || !IsStripped(rune.Value))
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    private static bool IsAllowedMark(int value) =>
        value is >= 0x0600 and <= 0x0605 or 0x06DD or 0x070F or 0x0890 or 0x0891 or 0x08E2
            or 0x110BD or 0x110CD;

    private static bool IsStripped(int value)
    {
        if (value is (>= 0x00 and <= 0x08) or 0x0B or 0x0C or (>= 0x0E and <= 0x1F) or (>= 0x7F and <= 0x9F))
        {
            return true;
        }

        return System.Globalization.CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(value), 0)
            == System.Globalization.UnicodeCategory.Format;
    }

    /// <summary>
    /// The context the reference puts on the composer for a fetched issue. The
    /// tagged form is used once the body has loaded; without it the reference falls
    /// back to naming the URL alone.
    /// </summary>
    public static string ContextBlock(GitHubIssue issue, GitHubIssueDetail? detail)
    {
        if (detail is null)
        {
            return $"GitHub issue: {issue.Url}";
        }

        var body = detail.Body.Length > MaxBodyChars
            ? detail.Body[..MaxBodyChars] + $"\n\n[... truncated; full issue at {issue.Url}]"
            : detail.Body;
        var author = detail.Author.Length > 0 ? $"\nAuthor: @{detail.Author}" : "";
        var labels = detail.Labels.Count > 0 ? $"\nLabels: {string.Join(", ", detail.Labels)}" : "";
        var inner = string.Join("\n",
        [
            $"Repository: {issue.Repo}",
            $"Issue: #{issue.Number} — {issue.Title}",
            $"URL: {issue.Url}{author}{labels}",
            "",
            body.Length > 0 ? body : "(no description)",
        ]);
        return string.Join("\n", ["<github_issue>", StripControls(inner), "</github_issue>"]);
    }

    // ---- the two gh calls ----

    /// <summary>The <c>owner/name</c> slug of the folder's origin remote, or null.</summary>
    public static string? RepoSlug(string workingDirectory)
    {
        var url = GitStatusProbe.NormalizeRemote(
            Gh(workingDirectory, "git", "remote get-url origin").Output.Trim());
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// <summary>The picker's list, or an empty list when gh cannot answer.</summary>
    public static IReadOnlyList<GitHubIssue> Search(string workingDirectory, string repoSlug, string? typed)
    {
        var result = Gh(workingDirectory, "gh", $"api \"{SearchPath(repoSlug, typed)}\"");
        if (result.Exit != 0 || result.Output.Trim().Length == 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return ParseSearch(document.RootElement, repoSlug);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Reads a <c>search/issues</c> response; separated out so it is unit-testable.</summary>
    public static IReadOnlyList<GitHubIssue> ParseSearch(JsonElement root, string repoSlug)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<GitHubIssue>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("number", out var n) || !n.TryGetInt32(out var number))
            {
                continue;
            }

            rows.Add(new GitHubIssue(
                number,
                item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                (item.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "").ToLowerInvariant(),
                repoSlug,
                item.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "",
                Labels(item)));
        }

        return rows;
    }

    /// <summary>The chosen issue's body and author, or null when gh cannot answer.</summary>
    public static GitHubIssueDetail? Fetch(string workingDirectory, string repoSlug, int number)
    {
        var result = Gh(workingDirectory, "gh", $"api \"{IssuePath(repoSlug, number)}\"");
        if (result.Exit != 0 || result.Output.Trim().Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Output);
            return ParseDetail(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a <c>repos/…/issues/{n}</c> response.</summary>
    public static GitHubIssueDetail? ParseDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var author = root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object &&
                     user.TryGetProperty("login", out var login)
            ? login.GetString() ?? ""
            : "";
        return new GitHubIssueDetail(
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            author,
            Labels(root));
    }

    private static IReadOnlyList<string> Labels(JsonElement element)
    {
        if (!element.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. labels.EnumerateArray()
                .Select(static l => l.ValueKind == JsonValueKind.Object && l.TryGetProperty("name", out var name)
                    ? name.GetString()
                    : l.GetString())
                .Where(static n => !string.IsNullOrEmpty(n))
                .Select(static n => n!),
        ];
    }

    private static (int Exit, string Output) Gh(string workingDirectory, string file, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (1, "");
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(30000);
            return (process.HasExited ? process.ExitCode : 1, output);
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (1, "");
        }
    }
}
