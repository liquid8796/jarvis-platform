using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using JarvisCode.App.Services;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The slash-command menu's ordering, replayed against what the reference's own
/// code produced.
///
/// Every other check in this suite compares text; this one compares a *ranking*,
/// which no amount of reading proves. The recording under <c>Captures/</c> was
/// made by running the desktop's shipped Fuse.js and the verbatim
/// <c>ix</c>/<c>sx</c>/<c>rx</c>/<c>lx</c>/<c>cx</c>/<c>ux</c>/<c>px</c> region
/// of its menu module over a fixture that exercises each of the sort's tiers —
/// exact label, exact alias, label prefix, shorter label, alias prefix,
/// qualified prefix, the <c>floor(10 * score)</c> bucket — plus the
/// description-only rejection, the plugin- and repo-qualified forms, the
/// arguments-taking button shortcut and the fifty-character cap.
///
/// The generator is <c>Captures/SlashMenu/gen-slash-menu-filter.mjs</c> and carries
/// its own refresh recipe. Like the wire fixtures the recording is checked in
/// rather than produced at test time, because reproducing it needs the
/// installed app.
/// </summary>
public sealed class SlashMenuFilterParityTests
{
    private const string CasesFile = "slash-menu-cases.json";
    private const string ResultsFile = "slash-menu-filter.json";

    private static string CapturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Captures", "SlashMenu", name);

    private static (List<SlashMenuItem> Items, List<string> Queries) ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CapturePath(CasesFile)));
        var root = document.RootElement;

        var items = new List<SlashMenuItem>();
        foreach (var element in root.GetProperty("items").EnumerateArray())
        {
            items.Add(new SlashMenuItem
            {
                Kind = element.GetProperty("type").GetString() switch
                {
                    "skill" => SlashMenuItemKind.Skill,
                    "button" => SlashMenuItemKind.Button,
                    "separator" => SlashMenuItemKind.Separator,
                    "section-header" => SlashMenuItemKind.SectionHeader,
                    "loading" => SlashMenuItemKind.Loading,
                    var other => throw new InvalidDataException($"Unmodelled item type '{other}' in {CasesFile}."),
                },
                Label = String(element, "label") ?? "",
                SkillId = String(element, "skillId") ?? "",
                SkillDescription = String(element, "skillDescription") ?? "",
                SourcePluginName = String(element, "sourcePluginName"),
                SourceRepo = String(element, "sourceRepo"),
                AcceptsArgs = element.TryGetProperty("acceptsArgs", out var accepts) && accepts.GetBoolean(),
                Aliases = element.TryGetProperty("aliases", out var aliases)
                    ? [.. aliases.EnumerateArray().Select(alias => alias.GetString() ?? "")]
                    : [],
            });
        }

        var queries = root.GetProperty("queries").EnumerateArray().Select(query => query.GetString() ?? "").ToList();
        return (items, queries);

        static string? String(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static Dictionary<string, List<string>> ReadRecordedResults()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CapturePath(ResultsFile)));
        var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.GetProperty("results").EnumerateObject())
        {
            results[property.Name] = [.. property.Value.EnumerateArray().Select(label => label.GetString() ?? "")];
        }

        return results;
    }

    [Fact]
    public void FilterReproducesTheReferenceOrderingForEveryRecordedQuery()
    {
        var (items, queries) = ReadCases();
        var recorded = ReadRecordedResults();

        var mismatches = new List<string>();
        foreach (var query in queries)
        {
            Assert.True(recorded.ContainsKey(query), $"No recorded result for query '{query}'; re-run gen-slash-menu-filter.mjs.");

            var actual = SlashMenuFilter.Filter(items, query).Select(item => item.Label).ToList();
            if (!actual.SequenceEqual(recorded[query], StringComparer.Ordinal))
            {
                mismatches.Add(
                    $"query '{query}'{Environment.NewLine}"
                    + $"  reference: [{string.Join(", ", recorded[query])}]{Environment.NewLine}"
                    + $"  ours:      [{string.Join(", ", actual)}]");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// The ordering above is decided by the sort's earlier tiers for most pairs,
    /// so it does not on its own pin the scorer: a wrong field norm or a wrong
    /// zero-score epsilon can leave every recorded order intact. This compares
    /// Fuse's raw numbers.
    ///
    /// <c>Math.Pow</c> is not required to be bit-identical across runtimes, so
    /// the comparison is relative rather than exact — wide enough for a
    /// last-place difference, far too tight for a wrong constant, which moves
    /// these by orders of magnitude.
    /// </summary>
    [Fact]
    public void FuzzyScoresReproduceTheReferenceNumbers()
    {
        const double RelativeTolerance = 1e-9;

        var (items, _) = ReadCases();
        using var document = JsonDocument.Parse(File.ReadAllText(CapturePath(ResultsFile)));
        var scored = document.RootElement.GetProperty("scores");

        var records = items.Where(item => !SlashMenuFilter.IsNonSelectable(item)).Select(SlashMenuFilter.Describe).ToList();
        var mismatches = new List<string>();

        foreach (var property in scored.EnumerateObject())
        {
            var index = new FuzzyIndex<SlashMenuFilter.Indexed>(
                records,
                SlashMenuFilter.Keys,
                SlashMenuFilter.ValuesFor,
                SlashMenuFilter.Options);
            var ours = index.Search(property.Name.Trim().ToLowerInvariant());
            var reference = property.Value.EnumerateArray().ToList();

            if (ours.Count != reference.Count)
            {
                mismatches.Add($"query '{property.Name}': {reference.Count} reference results, {ours.Count} ours");
                continue;
            }

            for (var i = 0; i < ours.Count; i++)
            {
                var expectedLabel = reference[i].GetProperty("label").GetString() ?? "";
                var expectedScore = reference[i].GetProperty("score").GetDouble();
                var actualLabel = ours[i].Item.Item.Label;
                var actualScore = ours[i].Score;

                if (!string.Equals(expectedLabel, actualLabel, StringComparison.Ordinal))
                {
                    mismatches.Add($"query '{property.Name}' position {i}: reference '{expectedLabel}', ours '{actualLabel}'");
                    continue;
                }

                var apart = Math.Abs(expectedScore - actualScore);
                if (apart > RelativeTolerance * Math.Max(Math.Abs(expectedScore), 1e-300))
                {
                    mismatches.Add(
                        $"query '{property.Name}' '{expectedLabel}': reference score {expectedScore:R}, ours {actualScore:R}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void EveryRecordedQueryIsStillExercised()
    {
        var (_, queries) = ReadCases();
        var recorded = ReadRecordedResults();

        var orphaned = recorded.Keys.Except(queries, StringComparer.Ordinal).ToList();
        Assert.True(
            orphaned.Count == 0,
            $"{ResultsFile} records queries {CasesFile} no longer asks for: {string.Join(", ", orphaned)}.");
    }
}
