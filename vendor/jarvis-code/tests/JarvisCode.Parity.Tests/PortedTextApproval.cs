using System.IO;
using System.Text;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Rewrites <c>Deltas/ported-text-deltas.tsv</c> from the installed reference.
///
/// It writes a first guess at *why* each line differs, and the guess is the
/// point of the review that follows: a row reading "REVIEW" is a line nobody
/// has explained yet, and shipping one of those turns this suite from a parity
/// check into a list of things it agreed not to look at.
/// </summary>
public sealed class PortedTextApproval
{
    [ApprovalFact("JARVIS_APPROVE_PORTED_TEXT")]
    public void Rewrite_declared_deltas()
    {
        var path = Path.Combine(RepoPaths.ParityTests, "Deltas", "ported-text-deltas.tsv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existing = PortedTextDeltas.Files
            .SelectMany(file => PortedTextDeltas.For(file).Select(row => (file, row.Key, row.Value)))
            .ToDictionary(static e => (e.file, e.Key), static e => e.Value);

        var builder = new StringBuilder()
            .AppendLine("# Lines this port deliberately does not share with the reference build.")
            .AppendLine("# Columns: source file, reason, the line (\\t and \\n escaped).")
            .AppendLine($"# Regenerated against CLI {ReferenceInstall.CliVersion ?? "?"} " +
                        $"and desktop app {ReferenceInstall.AppVersion ?? "?"}.")
            .AppendLine("# JARVIS_APPROVE_PORTED_TEXT=1 rewrites it; every REVIEW row needs a real reason.");

        int rows = 0;
        foreach (var source in PortedTextParityTests.Sources)
        {
            if (source.Side == ReferenceSide.Desktop && ReferenceInstall.AppDirectory is null)
            {
                continue;
            }

            var corpus = source.Side == ReferenceSide.Cli ? ReferenceCorpora.Cli : ReferenceCorpora.Desktop;
            var missing = corpus.Missing(PortedTextParityTests.ProseRuns(source.Path));
            foreach (var line in missing)
            {
                // A reason already written by hand outlives the regeneration.
                var reason = existing.TryGetValue((source.Path, line), out var kept) && !kept.StartsWith("REVIEW")
                    ? kept
                    : Suggest(line);
                builder.Append(source.Path).Append('\t').Append(reason).Append('\t')
                    .AppendLine(PortedTextDeltas.Encode(line));
                rows++;
            }
        }

        File.WriteAllText(path, builder.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));
        Assert.Fail($"wrote {rows} declared deltas to {path}. Review every row — especially the REVIEW " +
                    "ones — then re-run without the variable. This run proved nothing.");
    }

    /// <summary>
    /// A first classification of why a line is not in the reference. Only the
    /// mechanical, checkable reasons are guessed; anything else is left for a
    /// human to explain rather than given a plausible-sounding excuse.
    /// </summary>
    private static string Suggest(string line)
    {
        if (line.Contains("Jarvis", StringComparison.Ordinal) ||
            line.Contains("jarvis", StringComparison.Ordinal))
        {
            return "our brand";
        }

        string[] toolNames =
        [
            "Read", "Edit", "Write", "todo_write", "Agent", "WebSearch", "WebFetch",
            "list_directory", "NotebookEdit", "TaskStop", "TaskOutput", "SendMessage", "ScheduleWakeup",
            "TaskCreate", "TaskUpdate", "TaskList", "TaskGet", "preview_start", "computer_batch",
        ];
        if (toolNames.Any(name => line.Contains(name, StringComparison.Ordinal)))
        {
            return "our tool names";
        }

        if (line.Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
        {
            return "this app's shell is PowerShell";
        }

        if (line.Contains("mcp__Claude_Browser__", StringComparison.Ordinal) ||
            line.Contains("browser_", StringComparison.Ordinal))
        {
            return "our two browser surfaces";
        }

        return "REVIEW: not in the reference build";
    }
}
