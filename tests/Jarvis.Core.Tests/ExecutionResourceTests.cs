using System.Reflection;
using Jarvis.Agent.Core;
using Jarvis.Agent.Core.Execution;

namespace Jarvis.Core.Tests;

public sealed class ExecutionResourceTests
{
    [Fact]
    public async Task Reads_share_a_path_but_writes_wait_and_other_paths_remain_available()
    {
        using var coordinator = Create();
        var a = await Acquire(coordinator, "a", "fs|C:/repo/a.txt", false);
        var b = await Acquire(coordinator, "b", "fs|C:/repo/a.txt", false);
        var write = Acquire(coordinator, "c", "fs|C:/repo/a.txt", true);
        Assert.False(write.IsCompleted);
        using (await Acquire(coordinator, "d", "fs|C:/repo/b.txt", true)) { }
        a.Dispose(); Assert.False(write.IsCompleted);
        b.Dispose(); (await write.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task Directory_claims_cover_children_and_waiting_writers_are_not_starved_by_new_readers()
    {
        using var coordinator = Create();
        var first = await Acquire(coordinator, "a", "fs|C:/repo", false);
        var writer = Acquire(coordinator, "b", "fs|C:/repo/file.cs", true);
        var laterRead = Acquire(coordinator, "c", "fs|C:/repo/file.cs", false);
        Assert.False(laterRead.IsCompleted);
        first.Dispose();
        var held = await writer.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(laterRead.IsCompleted);
        held.Dispose(); (await laterRead.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task Cancelled_resource_waits_do_not_steal_the_resource_when_it_is_released()
    {
        using var coordinator = Create();
        var first = await Acquire(coordinator, "a", "desktop", true);
        using var stop = new CancellationTokenSource();
        var cancelled = Acquire(coordinator, "b", "desktop", true, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        first.Dispose();
        using var next = await Acquire(coordinator, "c", "desktop", true).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelled_browser_call_releases_desktop_lease_even_if_old_session_never_disposes_it()
    {
        using var coordinator = Create();
        using var oldSessionStop = new CancellationTokenSource();
        var oldRaw = (IExecutionResourceLease)await Acquire(
            coordinator, "old-session", ["browser|old-session", "desktop"], true, CancellationToken.None);
        var oldSession = CancellationBoundResourceLease.Bind(oldRaw, oldSessionStop.Token);
        var newSession = Acquire(
            coordinator, "new-session", ["browser|new-session", "desktop"], true, CancellationToken.None);

        Assert.False(newSession.IsCompleted);

        oldSessionStop.Cancel();
        using var acquired = await newSession.WaitAsync(TimeSpan.FromSeconds(2));
        oldSession.Dispose();
    }

    [Fact]
    public async Task A_background_job_can_retain_resources_after_its_start_call_returns()
    {
        using var coordinator = Create();
        var call = await Acquire(coordinator, "a", "fs|C:/output", true);
        var retain = call.GetType().GetMethod("Retain", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(retain);
        var job = (IDisposable)retain.Invoke(call, null)!;
        call.Dispose();
        var other = Acquire(coordinator, "b", "fs|C:/output/a.dll", true);
        Assert.False(other.IsCompleted);
        job.Dispose(); (await other.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task Unknown_shell_effects_exclude_files_and_desktop_but_not_owned_job_control()
    {
        using var coordinator = Create();
        var command = await Acquire(coordinator, "a", "*", true);
        var read = Acquire(coordinator, "b", "fs|C:/repo", false);
        var desktop = Acquire(coordinator, "c", "desktop", true);
        Assert.False(read.IsCompleted); Assert.False(desktop.IsCompleted);
        using (await Acquire(coordinator, "a", "job|owned-id", true).WaitAsync(TimeSpan.FromSeconds(2))) { }
        command.Dispose();
        (await read.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
        (await desktop.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    private static IDisposable Create()
    {
        var type = typeof(AgentConnection).Assembly.GetType("Jarvis.Agent.Core.Execution.ExecutionResourceCoordinator");
        Assert.NotNull(type);
        return (IDisposable)Activator.CreateInstance(type, 100)!;
    }
    private static Task<IDisposable> Acquire(object coordinator, string owner, string resource, bool exclusive, CancellationToken ct = default) =>
        Acquire(coordinator, owner, [resource], exclusive, ct);

    private static Task<IDisposable> Acquire(object coordinator, string owner, string[] resources, bool exclusive,
        CancellationToken cancellationToken) =>
        (Task<IDisposable>)coordinator.GetType().GetMethod("AcquireAsync")!.Invoke(
            coordinator, [owner, resources, exclusive, cancellationToken])!;
}
