using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Exercises the actual published managed/native binaries, not the development output or live agent.</summary>
internal static class PublishedPtySmoke
{
    public static async Task Run(string publish, string report)
    {
        var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(publish, "Jarvis.Agent.Core.dll"));
        if (!Path.GetFullPath(core.Location).Equals(Path.Combine(publish, "Jarvis.Agent.Core.dll"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The PTY smoke must use the published assembly.");
        // Do not let a development-output or system DLL make an incomplete package pass.
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(core, (name, _, _) =>
            name.Equals("conpty.dll", StringComparison.OrdinalIgnoreCase)
                ? System.Runtime.InteropServices.NativeLibrary.Load(Path.Combine(publish, "conpty.dll"))
                : IntPtr.Zero);
        using var tools = (IDisposable)Activator.CreateInstance(core.GetType("Jarvis.Agent.Core.ProcessToolSet", true)!, new object?[] { null })!;
        var toolInterface = core.GetType("Jarvis.Agent.Core.IAgentTool", true)!;
        var execute = toolInterface.GetMethod("ExecuteAsync")!;
        var descriptor = toolInterface.GetProperty("Descriptor")!;
        var entries = ((IEnumerable)tools.GetType().GetProperty("Tools")!.GetValue(tools)!).Cast<object>().ToArray();
        object Tool(string id) => entries.Single(entry => (string)descriptor.GetValue(entry)!.GetType().GetProperty("Id")!.GetValue(descriptor.GetValue(entry))! == id);
        var context = Activator.CreateInstance(core.GetType("Jarvis.Agent.Core.AgentExecutionContext", true)!, new object[] { report, "published-pty-smoke", "isolated-test" })!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<JsonDocument> Invoke(string id, object arguments)
        {
            var task = (Task)execute.Invoke(Tool(id), new[] { (object)JsonSerializer.SerializeToElement(arguments), context, deadline.Token })!;
            await task.ConfigureAwait(false);
            var reply = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var text = (string)reply.GetType().GetProperty("Text")!.GetValue(reply)!;
            if ((bool)reply.GetType().GetProperty("IsError")!.GetValue(reply)!) throw new InvalidOperationException(text);
            return JsonDocument.Parse(text);
        }
        using var started = await Invoke("process.spawn", new { argv = new[] { "cmd.exe", "/d", "/c", "echo jarvis-published-pty-ok" }, pty = true, timeoutSeconds = 10 });
        var id = started.RootElement.GetProperty("jobId").GetString()!;
        while (true)
        {
            using var status = await Invoke("process.read", new { jobId = id, cursor = 0 });
            if (status.RootElement.GetProperty("done").GetBoolean())
            {
                if (status.RootElement.GetProperty("exitCode").GetInt32() != 0 ||
                    !status.RootElement.GetProperty("output").GetString()!.Contains("jarvis-published-pty-ok"))
                    throw new InvalidOperationException("Published PTY did not return final output and successful exit: " + status.RootElement);
                break;
            }
            await Task.Delay(30, deadline.Token).ConfigureAwait(false);
        }
        File.WriteAllText(Path.Combine(report, "published-pty-smoke.json"), JsonSerializer.Serialize(new {
            version = core.GetName().Version!.ToString(), publishedNativeHostStarted = true, finalOutputCaptured = true,
            exitCode = 0, liveAgentConnected = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
