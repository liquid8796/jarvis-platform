using System.Text.Json;

namespace JarvisCode.App.Services;

/// <summary>
/// The badge a pull request wears, in the reference's own nine states (ion-dist chunk
/// shared-16-DFDNRrwQ.js, its yC icon/colour map beside the bC label map).
/// </summary>
public enum PrBadge
{
    None,
    Open,
    Draft,
    Approved,
    ChangesRequested,
    Conflicting,
    Queued,
    Merged,
    Closed,
}

/// <summary>One changed file of a pull request.</summary>
public sealed record PrFile(string Path, int Additions, int Deletions);

/// <summary>What the Pull request pane draws.</summary>
public sealed record PullRequestView(
    int Number,
    string Title,
    string Author,
    string? Body,
    PrBadge Badge,
    DateTimeOffset? UpdatedAt,
    string Url,
    string HeadSha,
    int Additions,
    int Deletions,
    IReadOnlyList<PrFile> Files,
    IReadOnlyList<string> Reviews,
    int ChecksPassed,
    int ChecksFailed,
    int ChecksPending);

/// <summary>
/// The Pull request pane's model. Its state resolution is the reference's own wC/vC:
/// an open request whose review decision asks for changes reads Changes requested, one
/// with conflicts reads Merge conflicts, one that is approved reads Approved, and
/// anything else keeps the state GitHub reported.
/// </summary>
public static class PullRequestPresentation
{
    public static string BadgeLabel(PrBadge badge) => badge switch
    {
        PrBadge.None => "No pull request",
        PrBadge.Open => "Open",
        PrBadge.Draft => "Draft",
        PrBadge.Approved => "Approved",
        PrBadge.ChangesRequested => "Changes requested",
        PrBadge.Conflicting => "Merge conflicts",
        PrBadge.Queued => "Queued",
        PrBadge.Merged => "Merged",
        _ => "Closed",
    };

    /// <summary>The theme brush a badge takes, following the reference's git colour tokens.</summary>
    public static string BadgeBrushKey(PrBadge badge) => badge switch
    {
        PrBadge.Merged => "Accent100Brush",
        PrBadge.Closed or PrBadge.ChangesRequested => "Danger100Brush",
        PrBadge.Conflicting => "Danger100Brush",
        PrBadge.Approved or PrBadge.Open => "Success100Brush",
        _ => "Text400Brush",
    };

    /// <summary>The reference's wC over vC: the badge a request's own fields resolve to.</summary>
    public static PrBadge Resolve(string? state, bool isDraft, string? reviewDecision, bool conflicting)
    {
        var normalized = (state ?? "").ToUpperInvariant() switch
        {
            "MERGED" => PrBadge.Merged,
            "CLOSED" => PrBadge.Closed,
            "OPEN" => PrBadge.Open,
            _ => PrBadge.None,
        };
        if (normalized != PrBadge.Open)
        {
            return normalized;
        }

        if (isDraft)
        {
            return PrBadge.Draft;
        }

        if (string.Equals(reviewDecision, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase))
        {
            return PrBadge.ChangesRequested;
        }

        if (conflicting)
        {
            return PrBadge.Conflicting;
        }

        if (string.Equals(reviewDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return PrBadge.Approved;
        }

        return PrBadge.Open;
    }

    /// <summary>The file count line, in the reference's plural form.</summary>
    public static string FileCountLabel(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>
    /// Reads the JSON `gh pr view` prints. Anything missing simply reads as absent — this
    /// pane must never take a session down because a field moved.
    /// </summary>
    public static PullRequestView? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("number", out var number))
            {
                return null;
            }

            var files = new List<PrFile>();
            if (root.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in fileArray.EnumerateArray())
                {
                    files.Add(new PrFile(
                        Text(file, "path"),
                        Int(file, "additions"),
                        Int(file, "deletions")));
                }
            }

            var reviews = new List<string>();
            if (root.TryGetProperty("reviews", out var reviewArray) && reviewArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var review in reviewArray.EnumerateArray())
                {
                    var reviewer = review.TryGetProperty("author", out var who) ? Text(who, "login") : "";
                    var state = Text(review, "state");
                    reviews.Add(reviewer.Length > 0 ? $"{reviewer} · {state}" : state);
                }
            }

            int passed = 0, failed = 0, pending = 0;
            if (root.TryGetProperty("statusCheckRollup", out var checks) && checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    var conclusion = Text(check, "conclusion").ToUpperInvariant();
                    var status = Text(check, "status").ToUpperInvariant();
                    if (status is "IN_PROGRESS" or "QUEUED" or "PENDING")
                    {
                        pending++;
                    }
                    else if (conclusion is "SUCCESS" or "NEUTRAL" or "SKIPPED")
                    {
                        passed++;
                    }
                    else if (conclusion.Length > 0)
                    {
                        failed++;
                    }
                }
            }

            var mergeable = Text(root, "mergeable").ToUpperInvariant();
            var badge = Resolve(
                Text(root, "state"),
                root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                Text(root, "reviewDecision"),
                mergeable == "CONFLICTING");

            DateTimeOffset? updated = null;
            if (root.TryGetProperty("updatedAt", out var updatedAt) &&
                updatedAt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(updatedAt.GetString(), out var parsed))
            {
                updated = parsed;
            }

            return new PullRequestView(
                number.GetInt32(),
                Text(root, "title"),
                root.TryGetProperty("author", out var author) ? Text(author, "login") : "",
                Text(root, "body"),
                badge,
                updated,
                Text(root, "url"),
                Text(root, "headRefOid"),
                Int(root, "additions"),
                Int(root, "deletions"),
                files,
                reviews,
                passed,
                failed,
                pending);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
