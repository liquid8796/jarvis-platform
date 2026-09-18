using System.Diagnostics;
using System.Security.Principal;
using Jarvis.Agent.Windows;

namespace Jarvis.Agent.BrowserService;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        var servicePipe = Value(args, "--service-pipe") ?? BrowserIntegration.ServicePipeName;
        var extensionPipe = Value(args, "--extension-pipe") ?? BrowserIntegration.ExtensionPipeName;
        var parentText = Value(args, "--parent");
        using var mutex = new Mutex(true,
            @"Local\JarvisBrowserService-" + WindowsIdentity.GetCurrent().User!.Value, out var created);
        if (!created) return 0;

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        Task? parentWatch = null;
        if (int.TryParse(parentText, out var parentId))
        {
            parentWatch = Task.Run(async () =>
            {
                try
                {
                    using var parent = Process.GetProcessById(parentId);
                    await parent.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                    stop.Cancel();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
                {
                    if (ex is not OperationCanceledException) stop.Cancel();
                }
            });
        }

        try
        {
            using var server = new BrowserServiceServer(servicePipe, extensionPipe);
            await server.RunAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            stop.Cancel();
            if (parentWatch is not null) try { await parentWatch.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
