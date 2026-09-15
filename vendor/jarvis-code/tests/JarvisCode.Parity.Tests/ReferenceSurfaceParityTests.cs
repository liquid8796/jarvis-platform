using System.IO;
using System.Text;
using JarvisCode.Core.Hooks;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Whether this port still offers what the reference offers.
///
/// Every other check here compares text the port already emits, which cannot
/// notice a tool, hook or command that was never built — there is no sentence
/// to fail. These read the reference's own inventories instead and require each
/// entry to be either carried here or declared in
/// <c>Deltas/reference-surface-deltas.tsv</c> with a reason, so "we did not port
/// that" is a recorded decision rather than a silence.
///
/// The extraction is compared against a checked-in baseline first. That order
/// matters: it means a reference release shows up as one failure naming what
/// moved, instead of as a scatter of unrelated failures in the checks below.
/// </summary>
public sealed class ReferenceSurfaceParityTests
{
    [ReferenceCliFact]
    public void Extraction_matches_the_recorded_reference_surface()
    {
        var baseline = ReferenceSurfaceBaseline.Load();
        Assert.True(baseline.Count > 0,
            $"{ReferenceSurfaceBaseline.Path} is missing or empty — set " +
            "JARVIS_APPROVE_REFERENCE_SURFACE=1 to write it from the installed reference");

        var found = ReferenceSurface.Reference;
        Assert.True(found.Count > 0,
            "nothing was extracted from the reference CLI; the declaration shapes this reads " +
            "(the hook-event array, and type:\"local\"/\"local-jsx\"/\"prompt\" objects) have moved");

        var added = found.Except(baseline).ToList();
        var removed = baseline.Except(found).ToList();

        Assert.True(added.Count == 0 && removed.Count == 0, Explain(added, removed));
    }

    [ReferenceCliFact]
    public void Every_reference_command_is_carried_or_declared()
    {
        var unaccounted = ReferenceSurfaceBaseline.Load()
            .Where(static e => e.Kind == SurfaceEntry.Command)
            .Select(static e => e.Name)
            .Where(static name =>
                !ReferenceSurface.AppCommands.Contains(name) &&
                !SurfaceDeltas.Declares(SurfaceEntry.Command, name))
            .ToList();

        Assert.True(unaccounted.Count == 0,
            $"{unaccounted.Count} reference slash command(s) are neither answered by this port nor " +
            "declared in Deltas/reference-surface-deltas.tsv — build one, or write down why not:\n  " +
            string.Join("\n  ", unaccounted));
    }

    [ReferenceCliFact]
    public void Every_reference_hook_event_is_carried_or_declared()
    {
        var unaccounted = ReferenceSurfaceBaseline.Load()
            .Where(static e => e.Kind == SurfaceEntry.Hook)
            .Select(static e => e.Name)
            .Where(static name =>
                HookRunner.ParseEventName(SnakeCase(name)) is null &&
                !SurfaceDeltas.Declares(SurfaceEntry.Hook, name))
            .ToList();

        Assert.True(unaccounted.Count == 0,
            $"{unaccounted.Count} reference hook event(s) are neither parsed by HookRunner nor declared " +
            "in Deltas/reference-surface-deltas.tsv:\n  " + string.Join("\n  ", unaccounted));
    }

    [Fact]
    public void Delta_rows_are_well_formed()
    {
        var rows = SurfaceDeltas.Rows;
        Assert.True(rows.Count > 0, $"{SurfaceDeltas.Path} has no rows");

        var malformed = rows.Where(static r => r.Malformed).Select(static r => r.Kind).ToList();
        Assert.True(malformed.Count == 0,
            "these rows are not four tab-separated fields: " + string.Join(", ", malformed));

        var badKind = rows.Where(static r =>
            r.Kind is not ("tool" or "hook" or "command" or "mcpserver" or "mcptool" or "mcpconfig"
                          or "prompt" or "renderer" or "component")).ToList();
        Assert.True(badKind.Count == 0,
            "unknown kind(s): " + string.Join(", ", badKind.Select(static r => $"{r.Kind} {r.Name}")));

        var badDisposition = rows.Where(static r => !SurfaceDeltas.Dispositions.Contains(r.Disposition)).ToList();
        Assert.True(badDisposition.Count == 0,
            "unknown disposition(s): " +
            string.Join(", ", badDisposition.Select(static r => $"{r.Name} = {r.Disposition}")));

        // Short is fine when the reason points at another row ("as /design"),
        // which is why this asks for a reason rather than for a paragraph. What
        // it refuses is the placeholder a generator leaves behind.
        var unreasoned = rows
            .Where(static r => r.Reason.Trim().Length < 8 ||
                               r.Reason.TrimStart().StartsWith("REVIEW", StringComparison.Ordinal) ||
                               r.Reason.TrimStart().StartsWith("TODO", StringComparison.Ordinal))
            .ToList();
        Assert.True(unreasoned.Count == 0,
            "a row without a real reason is a row nobody will notice is wrong: " +
            string.Join(", ", unreasoned.Select(static r => r.Name)));

        var duplicates = rows
            .GroupBy(static r => $"{r.Kind} {r.Name}", StringComparer.Ordinal)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToList();
        Assert.True(duplicates.Count == 0, "declared twice: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// A row claiming the reference has something it no longer has is worse than
    /// no row: it reads as a considered decision while describing a build that
    /// is gone.
    /// </summary>
    [ReferenceCliFact]
    public void Declared_rows_still_describe_the_installed_reference()
    {
        var corpus = ReferenceCorpora.Cli;
        var desktop = ReferenceCorpora.Desktop;
        var baseline = ReferenceSurfaceBaseline.Load();
        var stale = new List<string>();
        var contradicted = new List<string>();

        foreach (var row in SurfaceDeltas.Rows)
        {
            var inReference = row.Kind switch
            {
                SurfaceEntry.Command or SurfaceEntry.Hook =>
                    baseline.Contains(new SurfaceEntry(row.Kind, row.Name)),

                // Most in-process MCP servers are the desktop's, but the CLI
                // carries a built-in-MCP surface of its own — its inventory of
                // harness server names, its ide tool filter, its sdk transport —
                // whose names appear only in the CLI binary. Either build
                // answering is enough; a name in neither is a stale row.
                // For a row saying the reference HAS something, the probe is the
                // tool half — `mcp__${server}__${tool}` is assembled at runtime,
                // so the joined string is never in the bundle. For a row claiming
                // a name as ours, the joined name is the probe: it is exactly the
                // string the reference would have to contain for the claim to be
                // wrong, and Carried_mcp_tools_are_all_the_reference_s is what
                // actually settles whether a carried tool is the reference's.
                "mcptool" => Either(desktop, corpus,
                    row.Disposition == "addition" ? row.Name : McpToolHalf(row.Name)),
                "mcpserver" or "mcpconfig" => Either(desktop, corpus, row.Name),

                // A prompt section can belong to either half of the reference:
                // the CLI owns the harness prompt, and the desktop appends
                // sections of its own that no claude.exe contains (measured
                // 2026-09-01 — see Services/HostPromptSections.cs). Searching
                // only the CLI would call a real desktop block a stale row.
                "prompt" => Either(desktop, corpus, row.Name),

                // A renderer feature names a library or format the reference's UI
                // draws with. Those live in the desktop bundle, not in claude.exe.
                "renderer" => Either(desktop, corpus, row.Name),

                // A component names a piece of one of the reference's own UI
                // surfaces — a row kind, a node type, a field a card reads. It
                // lives in the desktop bundle for the same reason.
                "component" => Either(desktop, corpus, row.Name),

                _ => corpus.Contains(row.Name),
            };

            if (row.Disposition == "addition" && inReference)
            {
                contradicted.Add($"{row.Kind} {row.Name} is declared as this build's own, and the reference has it");
            }
            else if (row.Disposition != "addition" && !inReference)
            {
                stale.Add($"{row.Kind} {row.Name}");
            }
        }

        Assert.True(stale.Count == 0 && contradicted.Count == 0,
            new StringBuilder()
                .AppendLine($"Deltas/reference-surface-deltas.tsv disagrees with the installed reference " +
                            $"CLI {ReferenceInstall.CliVersion ?? "unknown"}:")
                .AppendLine(stale.Count == 0 ? "" : "  no longer in the reference (remove the row):")
                .AppendLine(string.Join("\n", stale.Select(static s => "    " + s)))
                .AppendLine(contradicted.Count == 0 ? "" : "  claimed as ours:")
                .AppendLine(string.Join("\n", contradicted.Select(static s => "    " + s)))
                .ToString());
    }

    /// <summary>True when either reference build contains the text.</summary>
    private static bool Either(ReferenceCorpus desktop, ReferenceCorpus cli, string text) =>
        desktop.Contains(text) || cli.Contains(text);

    /// <summary>
    /// The tool half of an <c>mcp__server__tool</c> name. A row whose name is not
    /// that shape is returned unchanged, so a malformed row still fails loudly
    /// rather than silently matching nothing.
    /// </summary>
    internal static string McpToolHalf(string wireName)
    {
        const string prefix = "mcp__";
        if (!wireName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return wireName;
        }

        var rest = wireName[prefix.Length..];
        var cut = rest.IndexOf("__", StringComparison.Ordinal);
        return cut > 0 && cut + 2 < rest.Length ? rest[(cut + 2)..] : wireName;
    }

    /// <summary>Rewrites the baseline from the installed reference.</summary>
    [ApprovalFact("JARVIS_APPROVE_REFERENCE_SURFACE")]
    public void Rewrite_reference_surface()
    {
        ReferenceSurfaceBaseline.Write(ReferenceSurface.Reference);
        Assert.True(ReferenceSurfaceBaseline.Load().Count > 0);
    }

    /// <summary>PascalCase event name to the snake_case spelling hooks.json uses.</summary>
    internal static string SnakeCase(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 8);
        foreach (var character in pascal)
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string Explain(IReadOnlyList<SurfaceEntry> added, IReadOnlyList<SurfaceEntry> removed)
    {
        var builder = new StringBuilder()
            .AppendLine($"the installed reference CLI {ReferenceInstall.CliVersion ?? "unknown"} no longer " +
                        "offers what Manifest/reference-surface.tsv records:");
        if (added.Count > 0)
        {
            builder.AppendLine($"  {added.Count} new in the reference:");
            builder.AppendLine(string.Join("\n", added.Take(25).Select(static e => "    " + e)));
        }

        if (removed.Count > 0)
        {
            builder.AppendLine($"  {removed.Count} gone from the reference:");
            builder.AppendLine(string.Join("\n", removed.Take(25).Select(static e => "    " + e)));
        }

        return builder
            .AppendLine("Re-run with JARVIS_APPROVE_REFERENCE_SURFACE=1 to record the new build, then the " +
                        "checks above will name whatever is now unaccounted for.")
            .ToString();
    }
}

/// <summary>The recorded reference surface: one `kind\tname` row per entry.</summary>
internal static class ReferenceSurfaceBaseline
{
    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Manifest", "reference-surface.tsv");

    private static string SourcePath { get; } =
        System.IO.Path.Combine(RepoPaths.ParityTests, "Manifest", "reference-surface.tsv");

    public static IReadOnlySet<SurfaceEntry> Load()
    {
        var entries = new HashSet<SurfaceEntry>();
        if (!File.Exists(Path))
        {
            return entries;
        }

        foreach (var line in File.ReadAllLines(Path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length == 2)
            {
                entries.Add(new SurfaceEntry(fields[0], fields[1]));
            }
        }

        return entries;
    }

    public static void Write(IReadOnlyList<SurfaceEntry> entries)
    {
        var text = new StringBuilder()
            .Append("# The reference's own inventories, extracted from the installed CLI ")
            .Append(ReferenceInstall.CliVersion ?? "unknown")
            .Append(".\n")
            .Append("# Columns: kind (hook|command), name.\n")
            .Append("# Hook events come from the array that declares all of them; commands from the\n")
            .Append("# type:\"local\"/\"local-jsx\"/\"prompt\" objects, read at the name nearest each.\n")
            .Append("# A few entries are minified identifiers the extraction cannot tell from a command.\n")
            .Append("# They are stable, so they are recorded here rather than failing every run — the\n")
            .Append("# point of this file is that a real new command shows up as a new line.\n")
            .Append("# JARVIS_APPROVE_REFERENCE_SURFACE=1 rewrites it.\n");
        foreach (var entry in entries)
        {
            text.Append(entry.Kind).Append('\t').Append(entry.Name).Append('\n');
        }

        foreach (var path in new[] { SourcePath, Path })
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }
    }
}
