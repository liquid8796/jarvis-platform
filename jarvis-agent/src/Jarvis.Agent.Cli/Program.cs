using System.Security.Principal;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Diagnostics;
using Jarvis.Agent.Core.Threads;
using Jarvis.Protocol;
using Jarvis.Agent.Windows;
namespace Jarvis.Agent.Cli;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Native messaging stdout must contain only framed protocol messages.
        if (BrowserIntegration.IsNativeHostInvocation(args)) { BrowserHostRelay.Run(); return 0; }
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine("Jarvis Agent: " + ex.Message); return 1; }
    }
    private static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This agent uses Windows computer-use tools.");
        var command = args.FirstOrDefault() ?? "help";
        if (command == "help")
        {
            Console.WriteLine($"Jarvis Agent {typeof(Program).Assembly.GetName().Version?.ToString(3)}\n  configure       Save server, device, project directories and DPAPI-encrypted token\n  connect         Connect interactively; Ctrl+C disconnects and stops owned jobs\n  list-tools      Print the installed CLI tool manifest as JSON\n  doctor --json   Print redacted runtime/protocol/catalog health as JSON\n  browser-install Register the isolated native browser host for this user\n\nSaved per-tool Full permission is configured in the desktop Tool permissions tab; persistent process.start/process.spawn approval is granted from their local prompt and revoked there. Startup still requires local Arm."); return 0;
        }
        if (command == "doctor")
        {
            if (args.Skip(1).Any(arg => !StringComparer.Ordinal.Equals(arg, "--json")))
                throw new ArgumentException("Usage: jarvis-agent doctor --json");
            return await DoctorAsync();
        }
        using var mutex = new Mutex(true, @"Local\JarvisAgent-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) throw new InvalidOperationException("Jarvis Agent is already running in the GUI or another terminal.");
        if (command == "configure")
        {
            Console.Write("HTTPS server origin: "); var server = Console.ReadLine() ?? "";
            Console.Write("Device ID: "); var device = Console.ReadLine() ?? "";
            Console.Write("Default workspace directory (Enter for none): "); var workspace = Console.ReadLine() ?? "";
            var additional = new List<string>();
            while (true)
            {
                Console.Write("Additional project directory (Enter to finish): ");
                var directory = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(directory)) break;
                additional.Add(directory);
            }
            Console.Write("Allow HTTP only for localhost development? [y/N]: "); var local = Console.ReadLine()?.Equals("y", StringComparison.OrdinalIgnoreCase) == true;
            Console.Write("Enrollment token (hidden): "); var token = Secret();
            AgentProfile.Save(new AgentOptions(server, device, workspace, local, additional), token);
            Console.WriteLine("Saved for this Windows user. The token is protected with DPAPI."); return 0;
        }
        if (command == "browser-install")
        {
            var host = BrowserIntegration.ResolveCompanionExecutable(BrowserIntegration.BrowserHostExeName);
            Console.WriteLine("Load this unpacked extension in your browser's Extensions developer mode:\n" + BrowserIntegration.Install(host)); return 0;
        }
        if (command == "list-tools")
        {
            var prompts = new ConsolePrompts();
            using var tools = new ToolInventory(prompts, new FileArtifactSink(Environment.CurrentDirectory));
            using var processes = new ProcessToolSet();
            using var threads = new ThreadRuntimeToolSet(Path.Combine(AgentProfile.Root, "thread-runtime.db"));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(tools.Tools.Concat(processes.Tools).Concat(threads.Tools).Select(t => t.Descriptor).Concat(AgentCoreHostTools.Descriptors).DistinctBy(t => t.Id),
                new System.Text.Json.JsonSerializerOptions(Jarvis.Protocol.WireJson.Options) { WriteIndented = true })); return 0;
        }
        if (command != "connect") throw new ArgumentException("Unknown command. Run jarvis-agent help.");
        if (Console.IsInputRedirected) throw new InvalidOperationException("Interactive stdin is required for local Arm and any tools not preapproved in Tool permissions.");
        var saved = AgentProfile.Load() ?? throw new InvalidOperationException("Run configure first.");
        var ui = new ConsolePrompts();
        await using var runtime = new AgentRuntime(ui, ui, new FileArtifactSink(saved.Workspace));
        runtime.Connection.Activity += e => Console.Error.WriteLine($"[{e.Time:HH:mm:ss}] {e.Kind}: {e.Message}");
        Console.WriteLine("Remote tools may read files outside project folders and, according to your saved tool permissions, execute commands or control your desktop.\nTerminal/browser/GUI actions are not OS-sandboxed. Ctrl+C always disconnects. Temporary reconnects keep your arm choice; interrupted actions are never replayed.\nArm control until you stop it (no automatic expiry)? [y/N]");
        if (Console.ReadLine()?.Equals("y", StringComparison.OrdinalIgnoreCase) != true) return 0;
        runtime.Arm();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; runtime.Pause(); runtime.Connection.Disconnect(); };
        Console.CancelKeyPress += cancel;
        try { await runtime.StartAsync(saved.ToOptions(), AgentProfile.GetToken(saved)); }
        finally { Console.CancelKeyPress -= cancel; }
        return 0;
    }
    private static async Task<int> DoctorAsync()
    {
        var prompts = new ConsolePrompts();
        using var tools = new ToolInventory(prompts, new FileArtifactSink(Environment.CurrentDirectory));
        using var processes = new ProcessToolSet();
        ToolPermissionPolicy permissions;
        var permissionHealthy = true;
        var permissionStatus = "ok";
        try
        {
            var permissionSettings = new ToolPermissionStore(
                Path.Combine(AgentProfile.Root, "tool-permissions.json")).LoadSettings();
            permissions = new ToolPermissionPolicy(permissionSettings.FullPermissionTools, permissionSettings.AlwaysApprovedConstrainedTools);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            permissions = new ToolPermissionPolicy();
            permissionHealthy = false;
            permissionStatus = "error:" + ex.GetType().Name;
        }

        using var threads = new ThreadRuntimeToolSet(Path.Combine(AgentProfile.Root, "thread-runtime.db"));
        var registry = new DynamicToolRegistry(tools.Tools.Concat(processes.Tools).Concat(threads.Tools));
        var gate = new LocalControlGate();
        await using var connection = new AgentConnection(registry, prompts, gate, permissions);
        var assembly = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        var package = assembly.ToString(3);
        var snapshot = AgentDoctor.Capture(new AgentDoctorInput(
            package,
            assembly,
            connection.ToolRegistry,
            permissions,
            Path.Combine(AgentProfile.Root, "plugins"),
            [],
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JarvisAgent", "TaskRuns"),
            0,
            OperatingSystem.IsWindows(),
            File.Exists(Path.Combine(AgentProfile.Root, "browser-host.json")),
            permissionHealthy,
            permissionStatus));
        Console.WriteLine(JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions(WireJson.Options) { WriteIndented = true }));
        return snapshot.VersionDrift || !snapshot.PluginHealthy || !snapshot.PermissionStoreHealthy || !snapshot.TaskStoreHealthy ? 2 : 0;
    }

    private static string Secret()
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine(); return text.ToString();
    }
}
