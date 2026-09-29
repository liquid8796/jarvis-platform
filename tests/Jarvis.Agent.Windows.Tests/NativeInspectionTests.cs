using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;

namespace Jarvis.Agent.Windows.Tests;

public sealed class NativeInspectionTests
{
    private static AgentExecutionContext Context() => new(Path.GetTempPath(), "inspect-call",
        "js_" + new string('d', 32)) { OwnerId = "owner", AgentDeviceId = "device", FullPermission = true };

    [Fact]
    public void Catalog_exposes_only_read_only_sensitive_native_diagnostics()
    {
        var tools = new NativeInspectionToolSet().Tools;
        Assert.Equal(["diagnostics.binary_inspect", "diagnostics.process_list", "diagnostics.process_inspect"],
            tools.Select(tool => tool.Descriptor.Id).ToArray());
        Assert.All(tools, tool =>
        {
            Assert.True(tool.Descriptor.ReadOnly);
            Assert.True(tool.Descriptor.Sensitive);
        });
    }

    [Fact]
    public async Task Binary_inspection_returns_hash_pe_metadata_and_bounded_strings_without_execution()
    {
        var tools = new NativeInspectionToolSet().Tools;
        var tool = tools.Single(item => item.Descriptor.Id == "diagnostics.binary_inspect");
        var path = typeof(NativeInspectionToolSet).Assembly.Location;
        var reply = await tool.ExecuteAsync(WireJson.Element(new
        {
            file_path = path,
            max_strings = 20,
            max_scan_bytes = 1024 * 1024
        }), Context(), CancellationToken.None);
        Assert.False(reply.IsError, reply.Text);
        using var json = JsonDocument.Parse(reply.Text);
        Assert.Equal(64, json.RootElement.GetProperty("sha256").GetString()!.Length);
        Assert.True(json.RootElement.GetProperty("size").GetInt64() > 0);
        Assert.NotEqual(JsonValueKind.Null, json.RootElement.GetProperty("pe").ValueKind);
        Assert.InRange(json.RootElement.GetProperty("strings").GetArrayLength(), 0, 20);
    }

    [Fact]
    public async Task Process_list_and_inspect_query_the_current_process_without_memory_access()
    {
        if (!OperatingSystem.IsWindows()) return;
        var tools = new NativeInspectionToolSet().Tools;
        var current = Process.GetCurrentProcess();
        var list = await tools.Single(item => item.Descriptor.Id == "diagnostics.process_list").ExecuteAsync(
            WireJson.Element(new { name = current.ProcessName, limit = 20 }), Context(), CancellationToken.None);
        Assert.False(list.IsError, list.Text);
        using (var json = JsonDocument.Parse(list.Text))
            Assert.Contains(json.RootElement.GetProperty("processes").EnumerateArray(),
                item => item.GetProperty("process_id").GetInt32() == current.Id);

        var inspect = await tools.Single(item => item.Descriptor.Id == "diagnostics.process_inspect").ExecuteAsync(
            WireJson.Element(new { process_id = current.Id, include_modules = true, max_modules = 20 }),
            Context(), CancellationToken.None);
        Assert.False(inspect.IsError, inspect.Text);
        using var inspected = JsonDocument.Parse(inspect.Text);
        Assert.Equal(current.Id, inspected.RootElement.GetProperty("process_id").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, inspected.RootElement.GetProperty("architecture").ValueKind);
        Assert.True(inspected.RootElement.GetProperty("modules").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Binary_inspection_preserves_the_agent_private_profile_boundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-native-private-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "private.bin");
            await File.WriteAllBytesAsync(file, [0x4d, 0x5a, 0, 0]);
            var tool = new NativeInspectionToolSet(root).Tools.Single(item =>
                item.Descriptor.Id == "diagnostics.binary_inspect");
            var reply = await tool.ExecuteAsync(WireJson.Element(new { file_path = file }), Context(), CancellationToken.None);
            Assert.True(reply.IsError);
            Assert.Contains("private profile", reply.Text, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
