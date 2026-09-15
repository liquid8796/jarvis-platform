using System.Text;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The Android-emulator server, compared against the recording of the
/// reference's own declaration
/// (<c>Captures/Mcp/android-emulator-1.44121.2.0.json</c>, written by
/// <c>Captures/Mcp/gen-android-emulator.js</c>).
///
/// The reference feature-flags this server off in the installed build, so it
/// reaches no live session and its declaration cannot be read off the wire the
/// way the other ten were. The recording is therefore the whole contract:
/// the server name, the single tool, that tool's description and input schema
/// field by field and in declaration order, its <c>alwaysLoad</c>, its ten
/// actions and its four buttons.
///
/// <see cref="InternalMcpDocsParityTests"/> already compares the description
/// and schema of every composed tool against the same fixture, since the
/// fixture loader carries this server too; the checks here are the ones that
/// are specific to it — the wire name, the enums this port's own code reads,
/// and the aliases the four replaced tools left behind.
/// </summary>
public sealed class AndroidEmulatorParityTests
{
    /// <summary>The server as this build composes it, with adb faked so the machine does not decide.</summary>
    private static IReadOnlyList<ITool> Composed() =>
        InternalMcpServers.Compose(
            [
                AndroidEmulatorTools.Server(
                    new UiSettings { AndroidToolsEnabled = true },
                    panel: null,
                    findAdb: static () => "adb.exe")!,
            ],
            new InternalMcpSessionContext());

    [Fact]
    public void The_server_advertises_exactly_the_reference_s_one_tool()
    {
        var advertised = Composed().Select(static t => t.Name).ToList();
        Assert.Equal([McpReferenceFixture.AndroidToolWireName], advertised);
        Assert.Equal("mcp__Claude_Code_Android_Emulator__control", McpReferenceFixture.AndroidToolWireName);
    }

    [Fact]
    public void The_tool_advertises_the_reference_description()
    {
        var tool = Assert.Single(Composed());
        Assert.Equal(McpReferenceFixture.Android.Tools[0].Description, tool.Description);
    }

    /// <summary>
    /// The schema, member for member and in declaration order. A reordered
    /// <c>properties</c> block or a dropped <c>required</c> entry changes what
    /// the model is told it may send, and neither is visible to a name check.
    /// </summary>
    [Fact]
    public void The_tool_advertises_the_reference_input_schema()
    {
        var tool = Assert.Single(Composed());
        List<string> diffs = [];
        Compare(McpReferenceFixture.Android.Tools[0].InputSchema, tool.InputSchema, "", diffs);

        Assert.True(diffs.Count == 0, new StringBuilder()
            .AppendLine("The control tool does not advertise the reference's declared input schema:")
            .AppendLine(string.Join("\n", diffs.Select(static d => "    " + d)))
            .ToString());
    }

    [Fact]
    public void The_tool_is_exempt_from_deferral_as_the_reference_declares()
    {
        Assert.True(McpReferenceFixture.Android.AlwaysLoad);
        Assert.True(InternalMcpServers.IsAlwaysLoad(Assert.Single(Composed())));
    }

    /// <summary>
    /// The ten action names this port's handler switches on, against the ten the
    /// recording carries — including their order, which is the order the schema's
    /// own enum declares and the order the "'action' must be one of" refusal
    /// lists them in.
    /// </summary>
    [Fact]
    public void The_actions_are_the_reference_s_in_its_order()
    {
        Assert.Equal(McpReferenceFixture.AndroidActions, AndroidEmulatorMessages.Actions);

        var schema = McpReferenceFixture.Android.Tools[0].InputSchema;
        var declared = schema["properties"]!["action"]!["enum"]!.AsArray().Select(static n => (string)n!);
        Assert.Equal(declared, AndroidEmulatorMessages.Actions);
    }

    [Fact]
    public void The_buttons_are_the_reference_s_in_its_order()
    {
        Assert.Equal(McpReferenceFixture.AndroidButtons, AndroidEmulator.Buttons);

        var schema = McpReferenceFixture.Android.Tools[0].InputSchema;
        var declared = schema["properties"]!["name"]!["enum"]!.AsArray().Select(static n => (string)n!);
        Assert.Equal(declared, AndroidEmulator.Buttons);

        // Every one of them has to reach a keycode, or 'button' would answer
        // "not supported by this tool" for a name its own schema enumerates.
        Assert.All(AndroidEmulator.Buttons, name => Assert.True(AndroidEmulator.ButtonKeycodes.ContainsKey(name)));
    }

    [Fact]
    public void The_schema_property_order_is_the_reference_s()
    {
        var declared = McpReferenceFixture.Android.Tools[0].InputSchema["properties"]!
            .AsObject().Select(static p => p.Key).ToList();
        Assert.Equal(declared, AndroidEmulatorMessages.Properties);
    }

    /// <summary>
    /// The four bare tools <c>control</c> replaced still resolve, and none of
    /// them is advertised — the same rule the claude-in-chrome rename follows.
    /// <c>android_logcat</c> is deliberately not among them: it stays a tool of
    /// this app's own, since <c>control</c> has no action for it.
    /// </summary>
    [Fact]
    public void The_replaced_bare_names_resolve_but_are_never_advertised()
    {
        var composed = Composed();
        var registry = new ToolRegistry(composed);

        Assert.Equal(
            ["mcp__Claude_Code_Android_Emulator__control"],
            composed.Select(static t => t.Name));

        foreach (var legacy in AndroidEmulatorTools.LegacyToolNames)
        {
            Assert.NotNull(registry.Find(legacy));
            Assert.DoesNotContain(legacy, composed.Select(static t => t.Name));
        }

        Assert.Equal(
            ["android_devices", "android_screenshot", "android_input", "android_install"],
            AndroidEmulatorTools.LegacyToolNames);
        Assert.DoesNotContain("android_logcat", AndroidEmulatorTools.LegacyToolNames);

        // The bare current name is not an alias: "control" says nothing about
        // which subsystem answers, and no stored session ever used it.
        Assert.Null(registry.Find("control"));
    }

    /// <summary>
    /// The reference gates the server on a remote flag
    /// (<c>claudeAndroidEmulatorAccessEnabled</c>) with no channel here, so this
    /// build gates it on adb and the Settings switch instead. Both halves are
    /// asserted, because a server that answered with adb missing would name a
    /// binary that is not there in every one of its results.
    /// </summary>
    [Fact]
    public void The_gate_is_adb_and_the_settings_switch()
    {
        Assert.Contains("claudeAndroidEmulatorAccessEnabled", McpReferenceFixture.AndroidIsEnabledSource);

        Assert.Null(AndroidEmulatorTools.Server(
            new UiSettings { AndroidToolsEnabled = true }, panel: null, findAdb: static () => null));

        var server = AndroidEmulatorTools.Server(
            new UiSettings { AndroidToolsEnabled = false }, panel: null, findAdb: static () => "adb.exe");
        Assert.NotNull(server);
        Assert.False(server!.IsEnabled!(new InternalMcpSessionContext()));

        Assert.Empty(InternalMcpServers.Compose([server], new InternalMcpSessionContext()));
    }

    /// <summary>Order-sensitive structural comparison, the same one the sibling docs check uses.</summary>
    private static void Compare(JsonNode? reference, JsonNode? ours, string path, List<string> diffs)
    {
        var at = path.Length == 0 ? "(root)" : path;
        switch (reference)
        {
            case JsonObject referenceObject:
            {
                if (ours is not JsonObject ourObject)
                {
                    diffs.Add($"{at}: expected an object, found {ours?.ToJsonString() ?? "nothing"}");
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
                    diffs.Add($"{at}: expected an array, found {ours?.ToJsonString() ?? "nothing"}");
                    return;
                }

                if (referenceArray.Count != ourArray.Count)
                {
                    diffs.Add($"{at}: {ourArray.Count} items, reference declares {referenceArray.Count}");
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
                    diffs.Add($"{at}: {actual}, reference declares {expected}");
                }

                return;
            }
        }
    }
}
