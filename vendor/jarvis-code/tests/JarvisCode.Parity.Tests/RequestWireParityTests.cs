using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers;
using JarvisCode.Providers.Anthropic;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// What the reference CLI actually put on the wire, recorded once per model
/// class, against what this app's provider builds for the same request.
///
/// Everything else in this suite reads the reference's *code*; this reads its
/// behaviour. The distinction matters because the wire is decided by feature
/// gates and model classification rather than by a literal anyone can grep for:
/// a build that changed which betas it advertises, or which models get an
/// effort dial, looks identical in the binary and different on the wire.
///
/// The fixtures under <c>Captures/</c> hold the protocol only. They were taken
/// by pointing the CLI's endpoint at a local listener with a dummy key, so no
/// request ever left the machine and no credential is in them; the identity
/// headers, the metadata and the prompt were dropped before writing.
/// </summary>
public sealed class RequestWireParityTests
{
    public static TheoryData<string> Fixtures => [.. WireFixture.All().Select(static f => f.Name)];

    /// <summary>
    /// Fields the reference sends and this port deliberately does not, with the
    /// reason. They are asserted to be *absent* from ours: a delta that quietly
    /// stops being a delta is a decision nobody made.
    /// </summary>
    private static readonly (string Path, string Reason)[] DeclaredBodyDeltas =
    [
        ("thinking.display",
            "\"omitted\" would suppress the thinking stream this app renders; " +
            "confirmed still sent by CLI 2.1.257, in a captured post-tool-result request"),
        ("metadata", "device/account identity; opt-in here and off by default; " +
            "confirmed still sent by CLI 2.1.257, carrying device_id/account_uuid/session_id"),
    ];

    /// <summary>
    /// Betas the reference advertises for features this build does not have.
    /// Empty since the 2.1.257 round: <c>advisor-tool</c> is no longer sent by
    /// any model, and <c>afk-mode</c> / <c>fallback-credit</c> — once declared
    /// here as account features — turned out to be part of the per-model beta
    /// list the reference sends every request, so they are sent rather than
    /// declared. The mechanism stays so the next such beta has a home.
    /// </summary>
    private static readonly (string Beta, string Reason)[] DeclaredBetaDeltas = [];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Our_request_matches_the_recorded_reference(string name)
    {
        var fixture = WireFixture.All().Single(f => f.Name == name);
        var preview = Build(fixture);

        Assert.EndsWith(fixture.Path, preview.Url, StringComparison.Ordinal);

        // A dimension with a recorded gap is checked by
        // Known_wire_gaps_are_still_exactly_these instead, which fails when the
        // gap closes so the row has to be removed. Everything else is asserted
        // here, so a fixture with one recorded difference still covers the rest.
        if (WireGaps.For(name, WireGapDimension.Body) is null)
        {
            foreach (var field in new[] { "model", "max_tokens", "stream", "output_config", "context_management" })
            {
                AssertField(fixture, preview.Body, field);
            }

            AssertThinking(fixture, preview.Body);
        }
        else
        {
            AssertField(fixture, preview.Body, "model");
            AssertField(fixture, preview.Body, "stream");
        }

        if (WireGaps.For(name, WireGapDimension.Betas) is null)
        {
            AssertBetas(fixture, preview);
        }
    }

    /// <summary>
    /// The differences this port knows about and has not closed, asserted to be
    /// exactly what was measured. Removing a gap from the file is how a fix is
    /// recorded; a gap that changed shape fails here rather than passing under
    /// its old description.
    /// </summary>
    [Fact]
    public void Known_wire_gaps_are_still_exactly_these()
    {
        var wrong = new List<string>();
        foreach (var fixture in WireFixture.All())
        {
            var preview = Build(fixture);
            foreach (var dimension in new[] { WireGapDimension.Body, WireGapDimension.Betas })
            {
                if (WireGaps.For(fixture.Name, dimension) is not { } expected)
                {
                    continue;
                }

                var actual = dimension == WireGapDimension.Body
                    ? Describe(preview.Body)
                    : BetaHeader(preview);
                if (actual != expected)
                {
                    wrong.Add($"{fixture.Name} ({dimension}):\n    recorded gap: {expected}\n" +
                              $"    ours now:     {actual}");
                }
            }
        }

        Assert.True(wrong.Count == 0,
            "a recorded wire gap changed. If it was fixed, delete its row from Captures/known-wire-gaps.tsv; " +
            "if it moved, re-measure it:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>Every fixture must be one the suite reads, and every gap must name a fixture.</summary>
    [Fact]
    public void Recorded_gaps_name_fixtures_that_exist()
    {
        var names = WireFixture.All().Select(static f => f.Name).ToHashSet(StringComparer.Ordinal);
        var orphans = WireGaps.Names.Where(name => !names.Contains(name)).ToList();
        Assert.True(orphans.Count == 0,
            "Captures/known-wire-gaps.tsv names fixtures that no longer exist: " + string.Join(", ", orphans));
        Assert.True(names.Count > 0, "no wire fixtures were found next to the test assembly");
    }

    private static ProviderRequestPreview Build(WireFixture fixture)
    {
        var provider = new AnthropicProvider(new HttpClient(), new NoKeys());
        return provider.PreviewRequest(new LlmRequest
        {
            ModelId = fixture.RequestedModel,
            SystemPrompt = "system",
            Messages = [ChatMessage.FromUserText("say ok")],
            ThinkingEffort = fixture.Effort,
        });
    }

    /// <summary>The body fields that decide how the model is asked to think.</summary>
    private static string Describe(JsonObject body)
    {
        string Field(string name) => body[name] is { } value ? value.ToJsonString() : "(absent)";
        return $"max_tokens={Field("max_tokens")} thinking={Field("thinking")} " +
               $"output_config={Field("output_config")}";
    }

    private static void AssertField(WireFixture fixture, JsonObject ours, string field)
    {
        var reference = fixture.Body[field];
        var mine = ours[field];
        if (reference is null && mine is null)
        {
            return;
        }

        Assert.True(reference is not null,
            $"{fixture.Name}: we send {field}={mine?.ToJsonString()}, the reference " +
            $"{fixture.CapturedFrom} sends none.");
        Assert.True(mine is not null,
            $"{fixture.Name}: the reference {fixture.CapturedFrom} sends {field}=" +
            $"{reference!.ToJsonString()}, we send none.");
        Assert.True(JsonNode.DeepEquals(reference, mine),
            $"{fixture.Name}: {field} is {mine!.ToJsonString()} here and " +
            $"{reference!.ToJsonString()} in the reference {fixture.CapturedFrom}.");
    }

    /// <summary>
    /// Thinking is compared field by field so the one part we deliberately drop
    /// — <c>display</c> — is asserted absent rather than silently ignored.
    /// </summary>
    private static void AssertThinking(WireFixture fixture, JsonObject ours)
    {
        var reference = fixture.Body["thinking"] as JsonObject;
        var mine = ours["thinking"] as JsonObject;
        if (reference is null)
        {
            Assert.True(mine is null,
                $"{fixture.Name}: we send thinking={mine?.ToJsonString()}, the reference sends none.");
            return;
        }

        Assert.True(mine is not null, $"{fixture.Name}: the reference sends thinking, we send none.");
        foreach (var property in reference)
        {
            if (DeclaredBodyDeltas.Any(d => d.Path == $"thinking.{property.Key}"))
            {
                Assert.True(mine![property.Key] is null,
                    $"{fixture.Name}: thinking.{property.Key} is declared as a deliberate omission " +
                    $"({DeclaredBodyDeltas.First(d => d.Path == $"thinking.{property.Key}").Reason}), " +
                    "but we now send it — remove the declaration or the field.");
                continue;
            }

            Assert.True(JsonNode.DeepEquals(property.Value, mine![property.Key]),
                $"{fixture.Name}: thinking.{property.Key} is {mine[property.Key]?.ToJsonString()} here and " +
                $"{property.Value?.ToJsonString()} in the reference {fixture.CapturedFrom}.");
        }

        foreach (var property in mine!)
        {
            Assert.True(reference[property.Key] is not null,
                $"{fixture.Name}: we send thinking.{property.Key}, which the reference does not.");
        }
    }

    /// <summary>
    /// The beta list is a subset check in one direction and a declaration in the
    /// other: we may never advertise a beta the reference does not, and every
    /// one we drop must be named with its reason.
    /// </summary>
    private static string BetaHeader(ProviderRequestPreview preview) =>
        preview.Headers
            .FirstOrDefault(h => string.Equals(h.Key, "anthropic-beta", StringComparison.OrdinalIgnoreCase))
            .Value ?? "";

    private static void AssertBetas(WireFixture fixture, ProviderRequestPreview preview)
    {
        var reference = Split(fixture.Headers.GetValueOrDefault("anthropic-beta", ""));
        var mine = Split(BetaHeader(preview));

        var invented = mine.Except(reference).ToList();
        Assert.True(invented.Count == 0,
            $"{fixture.Name}: we advertise beta(s) the reference {fixture.CapturedFrom} does not send: " +
            string.Join(", ", invented));

        var missing = reference.Except(mine).ToList();
        var undeclared = missing.Where(beta => !DeclaredBetaDeltas.Any(d => d.Beta == beta)).ToList();
        Assert.True(undeclared.Count == 0,
            $"{fixture.Name}: the reference {fixture.CapturedFrom} sends beta(s) we do not, and they are not " +
            $"declared in DeclaredBetaDeltas: {string.Join(", ", undeclared)}. Either send them or say why not.");

        // Order is part of the header the reference sends, so the ones we do
        // send must appear in its order.
        var ordered = reference.Where(mine.Contains).ToList();
        Assert.True(ordered.SequenceEqual(mine),
            $"{fixture.Name}: our beta order is [{string.Join(", ", mine)}], the reference's is " +
            $"[{string.Join(", ", ordered)}] for the same set.");
    }

    private static List<string> Split(string header) =>
        [.. header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private sealed class NoKeys : IApiKeySource
    {
        public string? GetKey(string providerId) => "not-a-real-key";
    }
}

/// <summary>One recorded reference request: the protocol half of it.</summary>
internal sealed record WireFixture(
    string Name,
    string CapturedFrom,
    string RequestedModel,
    ThinkingEffort Effort,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    JsonObject Body)
{
    private static readonly Lazy<IReadOnlyList<WireFixture>> Loaded = new(Load);

    public static IReadOnlyList<WireFixture> All() => Loaded.Value;

    private static IReadOnlyList<WireFixture> Load()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "Captures");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var fixtures = new List<WireFixture>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(static f => f, StringComparer.Ordinal))
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in json["headers"]!.AsObject())
            {
                headers[property.Key] = property.Value!.GetValue<string>();
            }

            fixtures.Add(new WireFixture(
                System.IO.Path.GetFileNameWithoutExtension(file),
                json["capturedFrom"]!.GetValue<string>(),
                json["requestedModel"]!.GetValue<string>(),
                ParseEffort(json["requestedEffort"]!.GetValue<string>()),
                json["path"]!.GetValue<string>(),
                headers,
                json["body"]!.AsObject()));
        }

        return fixtures;
    }

    private static ThinkingEffort ParseEffort(string wire) => wire switch
    {
        "low" => ThinkingEffort.Low,
        "medium" => ThinkingEffort.Medium,
        "xhigh" => ThinkingEffort.XHigh,
        "max" => ThinkingEffort.Max,
        _ => ThinkingEffort.High,
    };
}

/// <summary>
/// The wire differences this port has measured and not yet closed: one row per
/// fixture, holding what our provider builds today. They are not approvals —
/// the test that reads them fails when the shape changes, including when it is
/// fixed, so closing one is a deliberate edit rather than a silent pass.
/// </summary>
internal static class WireGaps
{
    private static readonly Lazy<IReadOnlyDictionary<(string Fixture, WireGapDimension Dimension), string>>
        Entries = new(Load);

    public static IEnumerable<string> Names => Entries.Value.Keys.Select(static key => key.Fixture).Distinct();

    public static string? For(string fixture, WireGapDimension dimension) =>
        Entries.Value.TryGetValue((fixture, dimension), out var gap) ? gap : null;

    private static IReadOnlyDictionary<(string, WireGapDimension), string> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Captures", "known-wire-gaps.tsv");
        var entries = new Dictionary<(string, WireGapDimension), string>();
        if (!File.Exists(path))
        {
            return entries;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length >= 4 &&
                Enum.TryParse<WireGapDimension>(fields[1], ignoreCase: true, out var dimension))
            {
                entries[(fields[0], dimension)] = fields[3];
            }
        }

        return entries;
    }
}

/// <summary>Which part of a request a recorded gap is about.</summary>
internal enum WireGapDimension
{
    /// <summary>The thinking, effort and token-cap fields of the body.</summary>
    Body,

    /// <summary>The anthropic-beta list this app advertises for the model.</summary>
    Betas,
}
