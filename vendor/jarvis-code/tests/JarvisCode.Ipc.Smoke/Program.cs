using System.Diagnostics;
using JarvisCode.Core.Agent;

if (args.FirstOrDefault() == "child")
{
    LocalSessionMailbox? child = null;
    child = new LocalSessionMailbox(args[1], "child-session", () => "IPC child", (sender, message) =>
    {
        if (message == "idle") child!.NotifyIdle("Finished cross-process probe");
        else if (message == "busy") child!.NotifyBusy();
        else _ = child!.SendAsync("parent-session", "echo:" + message);
    }, _ => { });
    await using (child)
    {
        child.NotifyBusy();
        Console.WriteLine("READY");
        await Console.In.ReadLineAsync();
    }
    return;
}

var profile = Path.Combine(Path.GetTempPath(), "jarvis-ipc-smoke-" + Guid.NewGuid().ToString("N"));
Process? process = null;
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var notice = new TaskCompletionSource<SessionIdleNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var parent = new LocalSessionMailbox(profile, "parent-session", () => "IPC parent",
        (_, message) => reply.TrySetResult(message), message => notice.TrySetResult(message));
    var executable = Environment.ProcessPath!;
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("child"); start.ArgumentList.Add(profile);
    process = Process.Start(start) ?? throw new Exception("The child process could not start.");
    Check(await process.StandardOutput.ReadLineAsync(deadline.Token) == "READY", "child ready");
    Check(parent.ListPeers().Single().ProcessId == process.Id, "discovery names the real second process");
    Check(await parent.SendAsync("child-session", "probe", deadline.Token) is null, "message acknowledged");
    Check(await reply.Task.WaitAsync(deadline.Token) == "echo:probe", "message delivered to and answered by second process");
    Check(await parent.SubscribeAsync("child-session", deadline.Token) is null, "subscription acknowledged");
    Check(await parent.SendAsync("child-session", "idle", deadline.Token) is null, "idle command acknowledged");
    var idle = await notice.Task.WaitAsync(deadline.Token);
    Check(idle.Kind == "idle" && idle.Detail == "Finished cross-process probe", "idle notice carries peer detail");
    await using (var isolated = new LocalSessionMailbox(profile + "-other", "other-session", () => "Other profile", (_, _) => { }, _ => { }))
        Check(isolated.ListPeers().Count == 0, "profile isolation");
    Check(await parent.SendAsync("child-session", "busy", deadline.Token) is null, "busy command acknowledged");
    notice = new TaskCompletionSource<SessionIdleNotice>(TaskCreationOptions.RunContinuationsAsynchronously);
    Check(await parent.SubscribeAsync("child-session", deadline.Token) is null, "second subscription acknowledged");
    // This process was created above by this test. Abrupt death tests the peer
    // liveness sweep rather than a cooperative closing-notice shortcut.
    process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync(deadline.Token);
    Check((await notice.Task.WaitAsync(deadline.Token)).Kind == "exited", "abrupt target exit settles the subscription");
    Check(await parent.SendAsync("child-session", "late", deadline.Token) is not null, "dead target cannot report success");
    Console.WriteLine("IPC_SMOKE_PASS: acknowledged cross-process message, idle/detail, profile isolation, abrupt exit, dead-target refusal");
}
finally
{
    if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
    process?.Dispose();
    foreach (var folder in new[] { profile, profile + "-other" })
        if (Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
            Directory.Delete(folder, true);
}

static void Check(bool condition, string stage)
{
    if (!condition) throw new Exception("IPC smoke failed: " + stage);
}
