using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

// Explicit opt-in smoke against a dedicated, already-started Blender scene.
// No Agent enrollment, catalog or permission files are read or modified.
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: BlenderMcp.Smoke <absolute-mcp-config> <output-directory>");
    return 2;
}
var configPath = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
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
async Task<ToolReply> Invoke(string operation, object arguments)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
    var reply = await tools.Tools.Single(t => t.Descriptor.Id == "blender." + operation)
        .ExecuteAsync(WireJson.Element(arguments), context, timeout.Token);
    steps.Add(new { operation, reply.IsError, text = PlainText(reply), images = reply.Images?.Count ?? 0 });
    Require(!reply.IsError, operation + " returned an error: " + PlainText(reply));
    return reply;
}
Task<ToolReply> Call(string name, object arguments) => Invoke("call_tool", new { name, arguments });
Task<ToolReply> Code(string code) => Call("execute_blender_code", new { code, user_prompt = "Verify Jarvis Blender integration in a dedicated disposable scene." });

try
{
    var discovery = await Invoke("list_tools", new { pageSize = 50 });
    var catalog = JsonNode.Parse(discovery.Text)!;
    var names = catalog["items"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
    foreach (var required in new[] { "get_addon_status", "get_scene_info", "get_object_info", "execute_blender_code", "get_viewport_screenshot" })
        Require(names.Contains(required), "Missing downstream tool: " + required);
    Console.WriteLine("Discovered " + catalog["total"] + " Blender MCP tools.");

    var status = PlainText(await Call("get_addon_status", new { user_prompt = "Check the Blender addon handshake." }));
    Require(!status.StartsWith("Error", StringComparison.OrdinalIgnoreCase), "Addon handshake failed.");
    using (JsonDocument.Parse(status)) { } // The pinned status endpoint returns a JSON object.
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
    var blocked = PlainText(await Code("import subprocess"));
    Require(blocked.StartsWith("Rejected by safe mode", StringComparison.OrdinalIgnoreCase),
        "Safe Mode did not reject the forbidden import as expected.");
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
        passed, created, assembly = typeof(BlenderMcpToolSet).Assembly.GetName().Version?.ToString(),
        transport = "real MCP stdio -> loopback Blender addon", probe, steps,
        timestampUtc = DateTimeOffset.UtcNow
    }, new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
}
Console.WriteLine(passed ? "BLENDER_SMOKE_PASS" : "BLENDER_SMOKE_FAILED");
return passed ? 0 : 1;
