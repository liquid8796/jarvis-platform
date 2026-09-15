using System.Text.Json.Nodes;
using JarvisCode.Core.Ide;

var bridge = new IdeBridge(args[0]);
var folder = args[1];
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var editors = bridge.Discover(folder);
Check(editors.Count == 1, "one live editor discovered");
var selection = await bridge.CallAsync(folder, "getSelection", [], timeout.Token);
Check(selection["text"]?.ToString() == "value", "actual editor selection");
var file = Path.Combine(folder, "probe.js");
var diagnostics = await bridge.CallAsync(folder, "getDiagnostics", new JsonObject { ["uri"] = new Uri(file).AbsoluteUri }, timeout.Token);
Check(diagnostics.AsArray().Any(item => item?["diagnostics"] is JsonArray { Count: > 0 }), "live language diagnostics");
var opened = await bridge.CallAsync(folder, "openDiff", new JsonObject
{
    ["old_file_path"] = file, ["new_file_path"] = file, ["new_file_contents"] = "const value = 42;\n",
    ["tab_name"] = "Read-only probe", ["wait_for_decision"] = false, ["read_only"] = true,
}, timeout.Token);
Check(opened["opened"]?.ToString().Length > 0, "diff opened");
var closed = await bridge.CallAsync(folder, "closeDiff", new JsonObject { ["id"] = opened["opened"]!.ToString() }, timeout.Token);
Check(closed["closed"]?.GetValue<bool>() == true, "diff closed");
var accepted = await bridge.CallAsync(folder, "openDiff", new JsonObject
{
    ["old_file_path"] = file, ["new_file_path"] = file, ["new_file_contents"] = "const value = 42;\n",
    ["tab_name"] = "Acceptance probe",
}, timeout.Token);
Check(accepted[0]?["text"]?.ToString() == "DIFF_ACCEPTED", "editor acceptance returned to caller");
Check(await File.ReadAllTextAsync(file) == "const value = 42;\n", "accepted edit saved to the file");
try
{
    await bridge.CallAsync(folder, "openFile", new JsonObject { ["file_path"] = args[2] }, timeout.Token);
    throw new Exception("Outside-workspace request was accepted.");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("outside this editor workspace")) { }
await bridge.CallAsync(folder, "closeAllDiffTabs", [], timeout.Token);
Console.WriteLine("IDE_SMOKE_PASS: live editor discovery, selection, language diagnostics, open/close diff, accepted edit, workspace boundary");

static void Check(bool condition, string stage)
{
    if (!condition) throw new Exception("IDE smoke failed: " + stage);
}
