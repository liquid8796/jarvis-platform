using System.Text;
using System.Text.Json.Nodes;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// What each in-process MCP tool advertises, compared against the reference's
/// own declaration field by field — the same bar
/// <see cref="ToolDocsParityTests"/> holds the CLI's tools to.
///
/// The name checks in <see cref="InternalMcpSurfaceParityTests"/> can only say
/// that a tool exists. A description that drifted a sentence, a missing
/// <c>required</c> entry, an absent <c>minItems</c> or a property order the
/// reference does not use are all invisible to them, and all of them change
/// what the model is told it may send.
/// </summary>
public sealed class InternalMcpDocsParityTests
{
    /// <summary>
    /// The places a tool's text may legitimately differ, each with the reason it
    /// does. Applied to the *reference* side before comparing, so a row that
    /// stops matching fails rather than quietly widening the licence: the
    /// reference text on the left has to still be there for the row to do
    /// anything at all.
    ///
    /// Every one of these is a fact about this build, not a rewording. A doc
    /// that named .claude/launch.json here would be telling the model to write a
    /// file this app never reads.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Reference, string Port, string Why)[]> Adaptations =
        new Dictionary<string, (string, string, string)[]>(StringComparer.Ordinal)
        {
            ["mcp__Claude_Browser__preview_start"] =
            [
                (".claude/launch.json", ".jarvis/launch.json",
                    "preview_start reads this app's own launch.json; the reference path is the compat fallback"),
            ],
            ["mcp__computer-use__request_teach_access"] =
            [
                ("the main Claude window hides", "the main Jarvis window hides",
                    "the window it names is this app's, and calling it Claude's would be false"),
            ],
            ["mcp__ccd_session_mgmt__list_sessions"] =
            [
                (" To change either, use the ccd_sidebar tools when they are available (move_sessions, set_pinned, create_group, …).",
                    "",
                    "this build has no ccd_sidebar server, so naming its tools would point the model at a family the session does not carry"),
            ],
            ["mcp__ccd_session_mgmt__get_session"] =
            [
                (", which the ccd_sidebar tools change when available",
                    "",
                    "this build has no ccd_sidebar server, so naming its tools would point the model at a family the session does not carry"),
            ],
            ["mcp__ccd_session_mgmt__archive_session"] =
            [
                ("The app asks the user to approve each call (in auto mode it may approve without asking, so the call itself can take effect at once). Archiving a session also archives its side sessions that share its checkout or have finished. A session that is still working (mid-turn, or with live background work) is not archived and the call says so; the same goes for one whose side session sharing its checkout is still working, and for one that is pinned or open on screen (or whose side session is). ",
                    "This tool ALWAYS prompts the user for confirmation. ",
                    "the approval this build raises is the ordinary permission card, and it archives neither side sessions nor refuses a working, pinned or open one - so the reference sentences would describe behaviour nothing here implements"),
            ],
            ["mcp__ccd_session_mgmt__set_session_title"] =
            [
                ("If the user set the current title themselves, the app first asks them to approve the new one (and in unattended sessions, where nobody can approve, declines it); titles the app generated are replaced without asking. W",
                    "An explicit rename here overwrites the existing title even if the user set it by hand, so w",
                    "the consent step behind the reference _consent flag is not built here, so a rename always takes effect and the doc says which"),
            ],
            ["mcp__scheduled-tasks__create_scheduled_task"] =
            [
                ("${g}", InternalMcpSurfaceParityTests.ScheduledTasksRoot,
                    "the reference interpolates its own task directory here; this is ours"),
            ],
        };

    /// <summary>
    /// The reference's text with this build's adaptations applied, so the
    /// comparison is against what the reference would say if it were this app.
    /// </summary>
    private static string Adapted(string wireName, string referenceText)
    {
        if (!Adaptations.TryGetValue(wireName, out var rows))
        {
            return referenceText;
        }

        foreach (var (reference, port, _) in rows)
        {
            referenceText = referenceText.Replace(reference, port, StringComparison.Ordinal);
        }

        return referenceText;
    }

    /// <summary>
    /// A licence that no longer describes anything is a licence to drift, so
    /// each row has to still match somewhere in the tool's reference text. This
    /// is what turns the table above from a list of excuses into a set of
    /// measured facts about the installed build.
    /// </summary>
    [Fact]
    public void Every_declared_adaptation_still_matches_the_reference()
    {
        foreach (var (wireName, rows) in Adaptations)
        {
            Assert.True(
                McpReferenceFixture.ByWireName.TryGetValue(wireName, out var reference),
                $"{wireName} carries adaptations but the reference recording has no such tool.");

            var haystack = reference!.Description + "\n" + reference.InputSchema.ToJsonString();
            foreach (var (from, to, why) in rows)
            {
                Assert.True(
                    haystack.Contains(from, StringComparison.Ordinal),
                    $"{wireName}: the adaptation \"{from}\" → \"{to}\" ({why}) no longer matches anything in " +
                    "the reference's text. Re-measure it against the installed build.");
            }
        }
    }

    [Fact]
    public void Every_carried_tool_advertises_the_reference_description()
    {
        List<string> drift = [];
        foreach (var tool in InternalMcpSurfaceParityTests.Composed())
        {
            if (!McpReferenceFixture.ByWireName.TryGetValue(tool.Name, out var reference))
            {
                continue;
            }

            var expected = Adapted(tool.Name, reference.Description);
            if (!string.Equals(expected, tool.Description, StringComparison.Ordinal))
            {
                drift.Add($"{tool.Name}\n  reference: {Show(expected)}\n  this build: {Show(tool.Description)}");
            }
        }

        Assert.True(drift.Count == 0, new StringBuilder()
            .AppendLine("These in-process MCP tools do not advertise the reference's description:")
            .AppendLine(string.Join("\n\n", drift))
            .ToString());
    }

    [Fact]
    public void Every_carried_tool_advertises_the_reference_input_schema()
    {
        List<string> drift = [];
        foreach (var tool in InternalMcpSurfaceParityTests.Composed())
        {
            if (!McpReferenceFixture.ByWireName.TryGetValue(tool.Name, out var reference))
            {
                continue;
            }

            var expected = (JsonObject)reference.InputSchema.DeepClone();
            Adapt(tool.Name, expected);

            var ours = (JsonObject)tool.InputSchema.DeepClone();
            DropInstalledApps(ours);

            List<string> diffs = [];
            Compare(expected, ours, "", diffs);
            if (diffs.Count > 0)
            {
                drift.Add($"{tool.Name}\n{string.Join("\n", diffs.Select(static d => "    " + d))}");
            }
        }

        Assert.True(drift.Count == 0, new StringBuilder()
            .AppendLine("These in-process MCP tools do not advertise the reference's input schema.")
            .AppendLine("Property order is part of the comparison: the reference declares its keys in a")
            .AppendLine("fixed order and the model reads them in that order.")
            .AppendLine()
            .AppendLine(string.Join("\n\n", drift))
            .ToString());
    }

    /// <summary>
    /// The reference declares the short <c>apps</c> description and its server
    /// appends the machine's installed applications when its enumeration has
    /// answered. The recording therefore holds the short form, and this cuts the
    /// live list back off before comparing — the block itself is checked by
    /// ComputerUseExtrasTests, where the list can be supplied rather than read
    /// off whatever machine the suite runs on.
    /// </summary>
    private const string InstalledAppsBlock =
        "\n\nApplications currently installed on this machine are listed below.";

    private static void DropInstalledApps(JsonObject schema)
    {
        if (schema["properties"]?["apps"] is not JsonObject apps ||
            apps["description"] is not JsonValue value ||
            !value.TryGetValue<string>(out var text))
        {
            return;
        }

        var cut = text.IndexOf(InstalledAppsBlock, StringComparison.Ordinal);
        if (cut >= 0)
        {
            apps["description"] = text[..cut];
        }
    }

    /// <summary>Rewrites every string in a schema through the same adaptation table.</summary>
    private static void Adapt(string wireName, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(static p => p.Key).ToList())
                {
                    if (o[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        o[key] = Adapted(wireName, text);
                    }
                    else
                    {
                        Adapt(wireName, o[key]);
                    }
                }

                return;
            case JsonArray a:
                foreach (var item in a)
                {
                    Adapt(wireName, item);
                }

                return;
        }
    }

    /// <summary>
    /// A structural diff that keeps property order. Object members are compared
    /// pairwise in declaration order, so a reordered schema reports as a
    /// mismatch rather than passing on set equality.
    /// </summary>
    private static void Compare(JsonNode? reference, JsonNode? ours, string path, List<string> diffs)
    {
        var at = path.Length == 0 ? "(root)" : path;
        switch (reference)
        {
            case JsonObject referenceObject:
            {
                if (ours is not JsonObject ourObject)
                {
                    diffs.Add($"{at}: expected an object, found {Kind(ours)}");
                    return;
                }

                var referenceKeys = referenceObject.Select(static p => p.Key).ToList();
                var ourKeys = ourObject.Select(static p => p.Key).ToList();
                if (!referenceKeys.SequenceEqual(ourKeys, StringComparer.Ordinal))
                {
                    diffs.Add(
                        $"{at}: keys are [{string.Join(", ", ourKeys)}], " +
                        $"reference declares [{string.Join(", ", referenceKeys)}]");
                }

                foreach (var key in referenceKeys)
                {
                    if (ourObject.TryGetPropertyValue(key, out var mine))
                    {
                        Compare(referenceObject[key], mine, path.Length == 0 ? key : $"{path}.{key}", diffs);
                    }
                }

                return;
            }

            case JsonArray referenceArray:
            {
                if (ours is not JsonArray ourArray)
                {
                    diffs.Add($"{at}: expected an array, found {Kind(ours)}");
                    return;
                }

                if (referenceArray.Count != ourArray.Count)
                {
                    diffs.Add($"{at}: {ourArray.Count} items, reference declares {referenceArray.Count} " +
                        $"([{string.Join(", ", referenceArray.Select(static n => n?.ToJsonString()))}])");
                    return;
                }

                for (var i = 0; i < referenceArray.Count; i++)
                {
                    Compare(referenceArray[i], ourArray[i], $"{path}[{i}]", diffs);
                }

                return;
            }

            default:
            {
                var expected = reference?.ToJsonString() ?? "null";
                var actual = ours?.ToJsonString() ?? "null";
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    diffs.Add($"{at}: {Show(actual)}, reference declares {Show(expected)}");
                }

                return;
            }
        }
    }

    private static string Kind(JsonNode? node) => node switch
    {
        null => "nothing",
        JsonObject => "an object",
        JsonArray => "an array",
        _ => node.ToJsonString(),
    };

    private static string Show(string text) =>
        text.Length <= 400 ? text : text[..400] + "… (" + text.Length + " chars)";
}
