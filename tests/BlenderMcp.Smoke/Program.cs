using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

// Explicit opt-in smoke against a dedicated, already-started Blender scene.
// No Agent enrollment, catalog or permission files are read or modified.
if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--public-assets")
{
    Console.Error.WriteLine("Usage: BlenderMcp.Smoke <absolute-mcp-config> <output-directory> [--public-assets]");
    return 2;
}
var configPath = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var publicAssets = args.Length == 3;
Directory.CreateDirectory(output);
using var tools = new BlenderMcpToolSet(configPath);
var context = new AgentExecutionContext("", "blender-smoke", "blender-smoke-" + Guid.NewGuid().ToString("N"));
var steps = new List<object>();
var probe = "JarvisSmoke_" + Guid.NewGuid().ToString("N");
var meshName = probe + "_Mesh";
bool created = false, passed = false;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static string PlainText(ToolReply reply)
{
    try
    {
        var body = JsonNode.Parse(reply.Text);
        // FastMCP emits both text and a structured {result: string} envelope.
        return body?["structuredContent"]?["result"]?.GetValue<string>() ?? body?["text"]?.GetValue<string>() ?? reply.Text;
    }
    catch (JsonException) { return reply.Text; }
    catch (InvalidOperationException) { return reply.Text; }
}
async Task<ToolReply> Invoke(string operation, object arguments, bool expectError = false)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
    var reply = await tools.Tools.Single(t => t.Descriptor.Id == "blender." + operation)
        .ExecuteAsync(WireJson.Element(arguments), context, timeout.Token);
    steps.Add(new { operation, reply.IsError, text = PlainText(reply), images = reply.Images?.Count ?? 0 });
    Require(reply.IsError == expectError, operation + " returned an unexpected error state: " + PlainText(reply));
    return reply;
}
Task<ToolReply> Call(string name, object arguments) => Invoke("call_tool", new { name, arguments });
Task<ToolReply> Code(string code) => Call("execute_blender_code", new { code, user_prompt = "Verify Jarvis Blender integration in a dedicated disposable scene." });

try
{
    var discovery = await Invoke("list_tools", new { pageSize = 50 });
    var catalog = JsonNode.Parse(discovery.Text)!;
    var names = catalog["items"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
    foreach (var required in BlenderMcpCapabilities.Groups.Values.SelectMany(x => x))
        Require(names.Contains(required), "Missing downstream tool: " + required);
    Console.WriteLine("Discovered " + catalog["total"] + " Blender MCP tools.");

    var status = PlainText(await Call("get_addon_status", new { user_prompt = "Check the Blender addon handshake." }));
    Require(!status.StartsWith("Error", StringComparison.OrdinalIgnoreCase), "Addon handshake failed.");
    var handshake = BlenderMcpResult.ParseObjectPrefix(status);
    Require(handshake?["up_to_date"]?.GetValue<bool>() == true, "The addon does not match the server.");
    Require(handshake?["telemetry_consent"]?.GetValue<bool>() == false, "Telemetry must be disabled in the managed runtime.");
    var capabilities = await Invoke("get_capabilities", new { probeAddon = true });
    var audit = JsonNode.Parse(capabilities.Text)!;
    Require(audit["catalogParity"]!.GetValue<bool>() && audit["addonState"]!.GetValue<string>() == "compatible", "Parity audit failed.");
    Require(audit["schemaFeatures"]!["advancedPolyhaven"]!.GetValue<bool>(), "Advanced Poly Haven schema missing.");
    await File.WriteAllTextAsync(Path.Combine(output, "capabilities.json"), capabilities.Text);
    await Call("describe_node_type", new { bl_idname = "ShaderNodeBsdfPrincipled" });
    await Call("bpy_api_lookup", new { query = "bpy.types.Object" });
    await Call("get_tripo_status", new { user_prompt = "Check availability only; do not generate or spend credits." });
    var scene = PlainText(await Call("get_scene_info", new { user_prompt = "Inspect the dedicated test scene without changing it." }));
    Require(!scene.StartsWith("Error", StringComparison.OrdinalIgnoreCase), "Scene query failed.");
    var version = PlainText(await Code("import bpy\nprint('JARVIS_BLENDER_VERSION=' + bpy.app.version_string)"));
    Require(version.Contains("JARVIS_BLENDER_VERSION="), "Python execution marker missing.");

    // The unique prefix ensures cleanup never removes a user's existing object.
    var result = await Code($"import bpy\nmesh = bpy.data.meshes.new('{meshName}')\nmesh.from_pydata([(0,0,0),(1,0,0),(0,1,0)], [], [(0,1,2)])\nobj = bpy.data.objects.new('{probe}', mesh)\nbpy.context.collection.objects.link(obj)\nobj.location = (3,0,0)\nprint('JARVIS_SMOKE_CREATED={probe}')");
    // Treat uncertain mutation outcomes as requiring targeted inspection, not automatic replay.
    created = true;
    Require(PlainText(result).Contains("JARVIS_SMOKE_CREATED="), "Test object creation was not confirmed.");
    var info = PlainText(await Call("get_object_info", new { object_name = probe, user_prompt = "Inspect the temporary Jarvis test object." }));
    Require(info.Contains(probe), "Object query did not return the test object.");

    var screenshot = await Call("get_viewport_screenshot", new { max_size = 640, user_prompt = "Capture this dedicated Blender test viewport." });
    Require(screenshot.Images is { Count: > 0 }, "Viewport screenshot did not return image content.");
    var image = screenshot.Images![0];
    var bytes = Convert.FromBase64String(image.Base64);
    Require(bytes.Length > 100, "Screenshot is empty.");
    bool png = bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    bool jpeg = bytes[0] == 255 && bytes[1] == 216;
    Require(png || jpeg, "Unsupported screenshot image signature.");
    await File.WriteAllBytesAsync(Path.Combine(output, "viewport" + (png ? ".png" : ".jpg")), bytes);

    // Harmless negative fixture: validates rejection of the import, not process execution.
    foreach (var forbidden in new[] { "import subprocess", "import bpy\nbpy.ops.extensions.package_install()", "import bpy\nbpy.context.preferences.filepaths.script_directories.new()" })
    {
        var blocked = PlainText(await Invoke("call_tool", new { name = "execute_blender_code", arguments = new { code = forbidden } }, expectError: true));
        Require(blocked.StartsWith("Rejected by safe mode", StringComparison.OrdinalIgnoreCase), "Safe Mode did not reject the forbidden operation.");
    }
    await Invoke("call_tool", new { name = "get_object_info", arguments = new { object_name = probe + "_missing" } }, expectError: true);
    await Invoke("call_tool", new { name = "search_polyhaven_assets", arguments = new { categories = "legacy" } }, expectError: true);
    foreach (var format in new[] { "glb", "fbx" })
    {
        var exportPath = Path.Combine(output, "smoke." + format);
        await Call("export_scene", new { filepath = exportPath, format, object_names = new[] { probe } });
        Require(File.Exists(exportPath) && new FileInfo(exportPath).Length > 100, "Export output is missing: " + format);
    }
    if (publicAssets)
    {
        // Explicit opt-in, free public metadata/thumbnail requests in this disposable scene only.
        await Code("import bpy\nbpy.context.scene.blendermcp_use_polyhaven = True");
        var search = PlainText(await Call("search_polyhaven_assets", new { query = "wooden chair", asset_type = "models", limit = 2 }));
        var match = Regex.Match(search, @"\(ID: ([a-zA-Z0-9_-]+)\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Require(match.Success, "Public Poly Haven search returned no usable asset identifier.");
        await File.WriteAllTextAsync(Path.Combine(output, "polyhaven-search.txt"), search);
        var preview = await Call("get_polyhaven_asset_preview", new { asset_id = match.Groups[1].Value });
        Require(preview.Images is { Count: > 0 }, "Poly Haven preview did not return an image through the bridge.");
        var assetImage = preview.Images![0];
        var assetBytes = Convert.FromBase64String(assetImage.Base64);
        Require(assetBytes.Length > 100, "Poly Haven thumbnail is empty.");
        bool assetPng = assetBytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool assetJpeg = assetBytes[0] == 255 && assetBytes[1] == 216;
        Require(assetPng || assetJpeg, "Poly Haven preview has an unsupported image signature.");
        await File.WriteAllBytesAsync(Path.Combine(output, "polyhaven-preview." + (assetPng ? "png" : "jpg")), assetBytes);
    }
    passed = true;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
}
finally
{
    // Inspect/remove ONLY this run's exact unique names, even after a partial mutation.
    try
    {
        var cleanup = PlainText(await Code($"import bpy\nobj = bpy.data.objects.get('{probe}')\nif obj is not None:\n    bpy.data.objects.remove(obj, do_unlink=True)\nmesh = bpy.data.meshes.get('{meshName}')\nif mesh is not None and mesh.users == 0:\n    bpy.data.meshes.remove(mesh)\nprint('JARVIS_SMOKE_CLEAN=' + str(bpy.data.objects.get('{probe}') is None and bpy.data.meshes.get('{meshName}') is None))"));
        Require(cleanup.Contains("JARVIS_SMOKE_CLEAN=True"), "Test object cleanup was not confirmed.");
    }
    catch (Exception ex) { passed = false; Console.Error.WriteLine("Cleanup: " + ex.Message); }
    tools.StopSession(context.IsolationScopeId);
    await File.WriteAllTextAsync(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(new
    {
        passed, created, publicAssets, assembly = typeof(BlenderMcpToolSet).Assembly.GetName().Version?.ToString(),
        transport = "real MCP stdio -> loopback Blender addon", probe, steps,
        timestampUtc = DateTimeOffset.UtcNow
    }, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
}
Console.WriteLine(passed ? "BLENDER_SMOKE_PASS" : "BLENDER_SMOKE_FAILED");
return passed ? 0 : 1;
