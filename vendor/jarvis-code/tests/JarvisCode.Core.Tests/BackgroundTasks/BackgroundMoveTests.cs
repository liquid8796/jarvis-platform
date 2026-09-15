using System.Text.Json.Nodes;
using JarvisCode.Core.BackgroundTasks;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;

namespace JarvisCode.Core.Tests.BackgroundTasks;

/// <summary>
/// "Run in background": a running tool call is handed to the background task
/// manager and keeps going there (the reference's onBackgroundTools).
/// </summary>
public sealed class BackgroundMoveTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_call_can_be_moved_only_while_it_is_registered()
    {
        var moves = new BackgroundMoveRequests();

        Assert.False(moves.CanMove("call-1"));
        Assert.False(moves.Request("call-1"));

        var token = moves.Register("call-1");
        Assert.True(moves.CanMove("call-1"));
        Assert.False(token.IsCancellationRequested);

        Assert.True(moves.Request("call-1"));
        Assert.True(token.IsCancellationRequested);

        moves.Release("call-1");
        Assert.False(moves.CanMove("call-1"));
        Assert.False(moves.Request("call-1"));
    }

    [Fact]
    public void Registering_the_same_call_twice_hands_out_a_fresh_signal()
    {
        var moves = new BackgroundMoveRequests();

        var first = moves.Register("call-1");
        var second = moves.Register("call-1");
        moves.Request("call-1");

        Assert.True(second.IsCancellationRequested);
        // The stale token's source is disposed, so only the live one is signalled.
        Assert.False(first.IsCancellationRequested);
    }

    [Fact]
    public void An_adopted_task_is_listed_running_and_settles_on_its_owners_word()
    {
        using var manager = new BackgroundTaskManager();
        BackgroundTaskInfo? exited = null;
        manager.TaskExited += info => exited = info;

        var adopted = manager.Adopt("npm run dev", "session-1", "Start the dev server", () => { });
        adopted.Append("listening on 5173\n");

        var running = Assert.Single(manager.List());
        Assert.Equal(BackgroundTaskStatus.Running, running.Status);
        Assert.Equal("npm run dev", running.Command);
        Assert.Equal("Start the dev server", running.Description);
        Assert.Equal("session-1", running.SessionId);
        Assert.Contains("listening on 5173", running.Output);
        Assert.Null(exited);

        adopted.Complete(0);

        var settled = Assert.Single(manager.List());
        Assert.Equal(BackgroundTaskStatus.Completed, settled.Status);
        Assert.Equal(0, settled.ExitCode);
        Assert.NotNull(settled.CompletedAt);
        Assert.Equal(settled.Id, exited?.Id);
    }

    [Fact]
    public void An_adopted_task_that_ends_without_an_exit_code_reads_as_killed()
    {
        using var manager = new BackgroundTaskManager();

        var adopted = manager.Adopt("sleep 100", null, null, () => { });
        adopted.Complete(null);

        Assert.Equal(BackgroundTaskStatus.Killed, Assert.Single(manager.List()).Status);
    }

    [Fact]
    public void Killing_an_adopted_task_asks_its_owner_to_stop()
    {
        using var manager = new BackgroundTaskManager();
        var asked = false;

        var adopted = manager.Adopt("sleep 100", null, null, () => asked = true);

        Assert.True(manager.Kill(adopted.Id));
        Assert.True(asked);
        Assert.Equal(BackgroundTaskStatus.Killed, Assert.Single(manager.List()).Status);
        // A task that has already stopped cannot be stopped again.
        Assert.False(manager.Kill(adopted.Id));
    }

    [Fact]
    public async Task A_running_shell_call_moved_to_the_background_keeps_going_there()
    {
        using var tasks = new BackgroundTaskManager();
        var moves = new BackgroundMoveRequests();
        var context = new ToolExecutionContext
        {
            WorkingDirectory = _temp.Path,
            SessionId = "session-1",
            CallId = "call-1",
            BackgroundTasks = tasks,
            BackgroundMoves = moves,
        };

        var call = new ShellTool().ExecuteAsync(
            new JsonObject
            {
                ["command"] = "Write-Output 'before-move'; Start-Sleep -Milliseconds 1500; Write-Output 'after-move'",
                ["description"] = "Print two lines",
            },
            context,
            default);

        // The call registers itself as movable as soon as its process is up.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!moves.CanMove("call-1") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Give the first line time to be read into the call's own buffer, so the
        // move has to carry existing output across rather than only what follows.
        await Task.Delay(500);
        Assert.True(moves.Request("call-1"));

        var result = await call;
        Assert.False(result.IsError);
        Assert.Contains("Moved to the background as task-1", result.Content);
        Assert.Contains("TaskOutput", result.Content);
        // The call has let go of its registration.
        Assert.False(moves.CanMove("call-1"));

        var moved = Assert.Single(tasks.List());
        Assert.Equal("Print two lines", moved.Description);
        Assert.Equal("session-1", moved.SessionId);

        // The output it had already produced came across, and the rest follows.
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (tasks.Get(moved.Id) is { Status: BackgroundTaskStatus.Running } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var settled = tasks.Get(moved.Id);
        Assert.NotNull(settled);
        Assert.Equal(BackgroundTaskStatus.Completed, settled.Status);
        Assert.Equal(0, settled.ExitCode);
        Assert.Contains("before-move", settled.Output);
        Assert.Contains("after-move", settled.Output);
    }

    [Fact]
    public async Task A_shell_call_that_is_never_moved_releases_its_registration()
    {
        using var tasks = new BackgroundTaskManager();
        var moves = new BackgroundMoveRequests();
        var context = new ToolExecutionContext
        {
            WorkingDirectory = _temp.Path,
            CallId = "call-1",
            BackgroundTasks = tasks,
            BackgroundMoves = moves,
        };

        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Write-Output 'done'" }, context, default);

        Assert.False(result.IsError);
        Assert.Contains("done", result.Content);
        Assert.False(moves.CanMove("call-1"));
        Assert.Empty(tasks.List());
    }

    [Fact]
    public async Task A_moved_call_settles_when_its_process_exits_even_with_a_detached_child()
    {
        using var tasks = new BackgroundTaskManager();
        var moves = new BackgroundMoveRequests();
        var context = new ToolExecutionContext
        {
            WorkingDirectory = _temp.Path,
            CallId = "call-1",
            BackgroundTasks = tasks,
            BackgroundMoves = moves,
        };

        // The grandchild outlives the command and holds the inherited pipe, so an
        // adopted task that waited for the pipe would never report its exit.
        var call = new ShellTool().ExecuteAsync(
            new JsonObject
            {
                ["command"] =
                    "Write-Output 'before-move'; Start-Sleep -Milliseconds 1500; " +
                    "Start-Process -NoNewWindow -FilePath cmd.exe -WorkingDirectory $env:TEMP " +
                    "-ArgumentList '/c','ping -n 20 127.0.0.1 >NUL'; Write-Output 'after-move'",
            },
            context,
            default);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!moves.CanMove("call-1") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(moves.Request("call-1"));
        Assert.Contains("Moved to the background", (await call).Content);

        var moved = Assert.Single(tasks.List());
        deadline = DateTime.UtcNow.AddSeconds(12);
        while (tasks.Get(moved.Id) is { Status: BackgroundTaskStatus.Running } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var settled = tasks.Get(moved.Id);
        Assert.NotNull(settled);
        Assert.Equal(BackgroundTaskStatus.Completed, settled.Status);
        Assert.Equal(0, settled.ExitCode);
        Assert.Contains("after-move", settled.Output);
    }
}
