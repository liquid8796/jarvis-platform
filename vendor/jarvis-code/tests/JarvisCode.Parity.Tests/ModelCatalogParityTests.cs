using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JarvisCode.App.Services;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// Keeps <see cref="ReferenceModelCatalog"/> honest against the installed CLI.
/// </summary>
/// <remarks>
/// <para>
/// Which prompt a model receives is not derivable from its name: the reference
/// reads a <c>capabilities</c> array off its own embedded model catalog, and
/// <c>lean_prompt</c> is the flag that picks the lean <c># Harness</c> document
/// over the classic <c># System</c> one. Two rows prove a name cannot stand in
/// for the table — <c>claude-sonnet-5</c> is a frontier Claude 5 model with no
/// <c>lean_prompt</c>, and <c>claude-mythos-5</c> ships an empty capability
/// list while <c>claude-mythos-5-1</c> beside it carries nine.
/// </para>
/// <para>
/// The catalog is a literal array in the packed bundle, so it is re-read here
/// and compared row for row against the generated table. A reference release
/// that adds a model, moves a capability or changes a cutoff lands as one
/// failure naming what moved, rather than as a prompt that quietly stops
/// matching. Regenerate with
/// <c>Captures/Models/gen-model-catalog.py</c>.
/// </para>
/// </remarks>
public sealed class ModelCatalogParityTests
{
    private const string Anchor = "{id:\"claude-opus-5\",family:\"opus\"";
    private const string ArrayEnd = "],aliases:{";

    private static readonly Regex EntryStart =
        new("\\{id:\"(claude-[a-z0-9.\\-]+)\",family:\"([a-z]+)\"", RegexOptions.Compiled);

    private sealed record Row(string? Cutoff, IReadOnlyList<string> Capabilities);

    /// <summary>
    /// Every model the installed CLI's catalog carries, its knowledge cutoff and
    /// its capability list, read out of the packed bundle.
    /// </summary>
    private static IReadOnlyDictionary<string, Row> ReadReferenceCatalog()
    {
        var bytes = File.ReadAllBytes(ReferenceInstall.CliPath!);
        // The bundle is UTF-8 script inside a packed executable; decoding the
        // whole file with replacement keeps every byte offset addressable.
        var text = Encoding.UTF8.GetString(bytes);

        var anchor = text.IndexOf(Anchor, StringComparison.Ordinal);
        Assert.True(anchor >= 0, "the model catalog's anchor entry (claude-opus-5) has moved");

        var end = text.IndexOf(ArrayEnd, anchor, StringComparison.Ordinal);
        Assert.True(end >= 0, "the model catalog's end (the aliases key) has moved");

        var start = text.LastIndexOf("[{id:\"claude-", anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, "the model catalog's opening bracket has moved");

        var array = text[start..end];
        var starts = EntryStart.Matches(array).Select(m => m.Index).ToList();
        Assert.NotEmpty(starts);
        starts.Add(array.Length);

        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        for (var i = 0; i < starts.Count - 1; i++)
        {
            var entry = array[starts[i]..starts[i + 1]];
            var id = EntryStart.Match(entry).Groups[1].Value;
            var capabilities = Regex.Match(entry, "capabilities:\\[([^\\]]*)\\]");
            Assert.True(capabilities.Success, $"{id}: the entry carries no capabilities key");

            var cutoff = Regex.Match(entry, "knowledge_cutoff:\"([^\"]*)\"");
            rows[id] = new Row(
                cutoff.Success ? cutoff.Groups[1].Value : null,
                [.. Regex.Matches(capabilities.Groups[1].Value, "\"([a-z0-9_]+)\"")
                    .Select(m => m.Groups[1].Value)]);
        }

        return rows;
    }

    [ReferenceCliFact]
    public void The_generated_catalog_matches_the_installed_build()
    {
        var reference = ReadReferenceCatalog();
        var ours = ReferenceModelCatalog.Models;

        var missing = reference.Keys.Where(id => !ours.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal);
        var extra = ours.Keys.Where(id => !reference.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal);

        var report = new StringBuilder();
        foreach (var id in missing)
        {
            report.AppendLine($"  {id}: in CLI {ReferenceInstall.CliVersion} and not in the generated table");
        }

        foreach (var id in extra)
        {
            report.AppendLine($"  {id}: in the generated table and not in CLI {ReferenceInstall.CliVersion}");
        }

        foreach (var (id, row) in reference.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!ours.TryGetValue(id, out var mine))
            {
                continue;
            }

            if (row.Cutoff != mine.KnowledgeCutoff)
            {
                report.AppendLine(
                    $"  {id}: knowledge cutoff is \"{row.Cutoff}\", the table says \"{mine.KnowledgeCutoff}\"");
            }

            // Order matters as little as duplication does, but the set is what
            // the prompt reads, so compare it as a set and name both directions.
            var theirs = row.Capabilities.ToHashSet(StringComparer.Ordinal);
            var here = (mine.Capabilities ?? []).ToHashSet(StringComparer.Ordinal);
            foreach (var capability in theirs.Except(here).OrderBy(c => c, StringComparer.Ordinal))
            {
                report.AppendLine($"  {id}: declares \"{capability}\", the table does not");
            }

            foreach (var capability in here.Except(theirs).OrderBy(c => c, StringComparer.Ordinal))
            {
                report.AppendLine($"  {id}: the table declares \"{capability}\", the build does not");
            }
        }

        Assert.True(
            report.Length == 0,
            $"the model catalog moved between CLI {ReferenceModelCatalog.MeasuredAgainst} (generated) and " +
            $"CLI {ReferenceInstall.CliVersion} (installed):\n{report}" +
            "\nRegenerate with tests/JarvisCode.Parity.Tests/Captures/Models/gen-model-catalog.py");
    }

    /// <summary>
    /// The <c>lean_prompt</c> capability looks like the prompt gate and is not
    /// one, and this pins the single model that proves it.
    /// </summary>
    /// <remarks>
    /// Captured on CLI 2.1.257: <c>claude-mythos-5</c> declares
    /// <c>capabilities:[]</c> — no <c>lean_prompt</c> — and is nonetheless sent
    /// the lean <c># Harness</c> document and
    /// <c># Communicating with the user</c>, exactly like <c>claude-fable-5</c>
    /// beside it. Every other row agrees with the capability, so this asserts
    /// the agreement <i>and</i> the one exception: a build that fixed its
    /// catalog, or that moved another model, lands here and says to re-measure
    /// rather than letting the port drift on either reading.
    /// </remarks>
    [ReferenceCliFact]
    public void The_lean_prompt_capability_agrees_everywhere_but_mythos_5()
    {
        var diverging = new List<string>();
        foreach (var (id, row) in ReadReferenceCatalog().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (row.Capabilities.Contains("lean_prompt") != PromptModelProfile.For(id).Lean)
            {
                diverging.Add(id);
            }
        }

        Assert.True(
            diverging is ["claude-mythos-5"],
            "the models whose prompt form disagrees with their lean_prompt capability changed — " +
            $"expected exactly claude-mythos-5, found [{string.Join(", ", diverging)}]. " +
            "Re-capture the affected models against the installed CLI before moving the gate: " +
            "the capability is not what the client reads, and the captures are the authority.");
    }

    /// <summary>
    /// The generated table names the build it was read from, so a failure above
    /// can say whether the reference moved or the table was always wrong.
    /// </summary>
    [ReferenceCliFact]
    public void The_generated_table_names_the_installed_build()
    {
        Assert.Equal(ReferenceInstall.CliVersion, ReferenceModelCatalog.MeasuredAgainst);
    }
}
