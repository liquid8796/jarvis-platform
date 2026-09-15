using System.Security.Principal;
using Jarvis.Agent.Core;
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
            Console.WriteLine("Jarvis Agent 1.0.24\n  configure       Save server, device, project directories and DPAPI-encrypted token\n  connect         Connect interactively; Ctrl+C disconnects and stops owned jobs\n  list-tools      Print the installed CLI tool manifest as JSON\n  browser-install Register the isolated native browser host for this user\n\nSaved per-tool Full permission is configured in the desktop Tool permissions tab; startup still requires local Arm."); return 0;
        }
        using var mutex = new Mutex(true, @"Local\JarvisAgent-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) throw new InvalidOperationException("Jarvis Agent is already running in the GUI or another terminal.");
        if (command == "configure")
        {
            Console.Write("HTTPS server origin: "); var server = Console.ReadLine() ?? "";
            Console.Write("Device ID: "); var device = Console.ReadLine() ?? "";
            Console.Write("Primary working directory: "); var workspace = Console.ReadLine() ?? "";
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
            Console.WriteLine("Load this unpacked extension in your browser's Extensions developer mode:\n" + BrowserIntegration.Install(Environment.ProcessPath!)); return 0;
        }
        if (command == "list-tools")
        {
            var prompts = new ConsolePrompts();
            using var tools = new ToolInventory(prompts, new FileArtifactSink(Environment.CurrentDirectory));
            using var processes = new ProcessToolSet();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(tools.Tools.Concat(processes.Tools).Select(t => t.Descriptor),
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
