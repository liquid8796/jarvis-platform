using System.IO;
using System.Text.Json.Nodes;

namespace JarvisCode.Parity.Tests;

/// <summary>
/// The recorded reference surface of the desktop's in-process MCP shell:
/// <c>Captures/Mcp/internal-servers-1.46388.2.0.json</c>, written by
/// <c>Captures/Mcp/gen-internal-servers.js</c> from the extracted app.asar.
///
/// The names alone were a hand-written table here, which could say a tool
/// exists but nothing about what it advertises. The recording carries each
/// tool's description and its declared input schema, so the checks below can
/// compare field by field the way <see cref="ToolDocsParityTests"/> does for
/// the CLI's tools.
///
/// One measured caveat rides with it: what the desktop *declares* and what its
/// model *receives* are not the same JSON. The declarations are plain JSON
/// Schema; the shell hands them to the Agent SDK's <c>tool()</c> through a
/// JSON-Schema-to-zod converter, and that round trip drops the descriptions of
/// optional properties, the bounds, and type unions before the schema reaches
/// the wire (measured on a live ccd session: <c>read_page</c> arrives with
/// <c>filter</c>/<c>depth</c>/<c>ref_id</c>/<c>max_chars</c> undescribed and
/// <c>required</c> cut to <c>["tabId"]</c>). This port advertises the
/// declaration, which is the surface the reference authored and the one its own
/// <c>getAppServersInfo</c> reports.
/// </summary>
public static class McpReferenceFixture
{
    /// <summary>The build the recording was taken from.</summary>
    public const string Build = "1.46388.2.0";

    public sealed record Tool(string Name, string Description, JsonObject InputSchema);

    public sealed record Server(string Name, string WireName, bool AlwaysLoad, IReadOnlyList<Tool> Tools);

    private static readonly Lazy<IReadOnlyList<Server>> Loaded = new(Load);

    /// <summary>
    /// The Android-emulator server, recorded on its own by
    /// <c>Captures/Mcp/gen-android-emulator.js</c>. The reference gates it behind
    /// a remote flag that ships off, so no live session exposes it and its
    /// declaration had to be read out of the bundle rather than executed — which
    /// is why it is a second file rather than an eleventh entry in the first.
    /// </summary>
    public const string AndroidWireName = "Claude_Code_Android_Emulator";

    public static IReadOnlyList<Server> Servers => Loaded.Value;

    /// <summary>Every tool of every server, keyed by its wire name.</summary>
    public static readonly IReadOnlyDictionary<string, Tool> ByWireName =
        Servers
            .SelectMany(s => s.Tools.Select(t => (Key: $"mcp__{s.WireName}__{t.Name}", Tool: t)))
            .ToDictionary(p => p.Key, p => p.Tool, StringComparer.Ordinal);

    private static IReadOnlyList<Server> Load() => [.. LoadShell(), LoadAndroid()];

    /// <summary>The Android recording, whose file holds one server rather than a list.</summary>
    public static Server Android
    {
        get
        {
            var root = Read($"android-emulator-{Build}.json", "gen-android-emulator.js");
            List<Tool> tools = [];
            foreach (var toolNode in root["tools"]!.AsArray())
            {
                var tool = toolNode!.AsObject();
                tools.Add(new Tool(
                    (string)tool["name"]!,
                    (string)tool["description"]!,
                    (JsonObject)tool["inputSchema"]!.DeepClone()));
            }

            // Its alwaysLoad is declared per tool, and its one tool sets it.
            return new Server(
                (string)root["serverName"]!,
                AndroidWireName,
                (bool)root["tools"]![0]!["alwaysLoad"]!,
                tools);
        }
    }

    /// <summary>The ten actions the recorded <c>control</c> declares, in declaration order.</summary>
    public static IReadOnlyList<string> AndroidActions =>
        [.. Read($"android-emulator-{Build}.json", "gen-android-emulator.js")["actions"]!
            .AsArray().Select(static n => (string)n!)];

    /// <summary>The four hardware buttons it enumerates.</summary>
    public static IReadOnlyList<string> AndroidButtons =>
        [.. Read($"android-emulator-{Build}.json", "gen-android-emulator.js")["buttons"]!
            .AsArray().Select(static n => (string)n!)];

    /// <summary>The reference's own <c>isEnabled</c> source, as recorded.</summary>
    public static string AndroidIsEnabledSource =>
        (string)Read($"android-emulator-{Build}.json", "gen-android-emulator.js")["isEnabledSource"]!;

    /// <summary>The wire name the recording says the one tool wears.</summary>
    public static string AndroidToolWireName =>
        (string)Read($"android-emulator-{Build}.json", "gen-android-emulator.js")["wireName"]!;

    private static Server LoadAndroid() => Android;

    private static JsonObject Read(string fileName, string generator)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Captures", "Mcp", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The reference MCP recording is missing: {path}. Regenerate it with " +
                $"Captures/Mcp/{generator} against an extracted app.asar.",
                path);
        }

        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static IReadOnlyList<Server> LoadShell()
    {
        var root = Read($"internal-servers-{Build}.json", "gen-internal-servers.js");
        List<Server> servers = [];
        foreach (var node in root["servers"]!.AsArray())
        {
            var server = node!.AsObject();
            List<Tool> tools = [];
            foreach (var toolNode in server["tools"]!.AsArray())
            {
                var tool = toolNode!.AsObject();
                tools.Add(new Tool(
                    (string)tool["name"]!,
                    (string)tool["description"]!,
                    (JsonObject)tool["inputSchema"]!.DeepClone()));
            }

            servers.Add(new Server(
                (string)server["name"]!,
                (string)server["wireName"]!,
                (bool)server["alwaysLoad"]!,
                tools));
        }

        return servers;
    }
}
