using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace JarvisCode.App.Services;

/// <summary>One unsolicited message from the engine: an event name and its body.</summary>
public sealed record ElectronPaneEvent(string Name, JsonObject Body);

/// <summary>
/// Owns the Browser pane's Electron process and the named pipe it speaks over.
///
/// The transport is a pipe rather than the child's stdio because Electron's main
/// process on Windows sees stdin at EOF the moment it starts: a sidecar reading
/// commands from stdin exits before <c>app.whenReady()</c> fires, silently and
/// with no output at all. The pipe also gives shutdown for free — the engine
/// treats losing it as the signal that the app it renders for is gone, so no
/// orphan Chromium survives this process.
///
/// Requests are answered by id, exactly once; everything without an id is an
/// event and is raised on <see cref="EventReceived"/>. The reader runs on its
/// own task and never touches the UI thread.
/// </summary>
public sealed class ElectronPaneHost : IAsyncDisposable
{
    private readonly ElectronRuntime _runtime;
    private readonly string _appDirectory;
    private readonly string _userDataDirectory;
    private readonly IReadOnlyList<string> _chromiumSwitches;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Lock _startGate = new();
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// The longest this waits for one answer when the caller's token has no
    /// deadline of its own. The pane's tools race their own calls at 30s (45s
    /// for javascript_tool), so this sits above those and exists only so a lost
    /// answer surfaces as an error instead of hanging the pane forever.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private Task? _reader;
    private Task? _starting;
    private int _nextId;
    private volatile bool _disposed;

    /// <param name="chromiumSwitches">
    /// Switches this engine's Chromium starts with. A switch has to be set
    /// before the app is ready, so it travels on argv rather than as a command
    /// — which is also why a surface needing its own switches needs its own
    /// engine process rather than a window in the shared one.
    /// </param>
    public ElectronPaneHost(
        ElectronRuntime runtime,
        string appDirectory,
        string userDataDirectory,
        IReadOnlyList<string>? chromiumSwitches = null)
    {
        _runtime = runtime;
        _appDirectory = appDirectory;
        _userDataDirectory = userDataDirectory;
        _chromiumSwitches = chromiumSwitches ?? [];
    }

    /// <summary>Where the engine app lives beside the built app.</summary>
    public static string DefaultAppDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ElectronHost");

    /// <summary>Raised for every event the engine sends. Not on the UI thread.</summary>
    public event Action<ElectronPaneEvent>? EventReceived;

    /// <summary>Raised once when the engine process ends without being disposed.</summary>
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false } && _pipe is { IsConnected: true };

    /// <summary>The engine process this host started, or null before it has one.</summary>
    public int? ProcessId => _process?.Id;

    /// <summary>The engine's own reported versions, once it has connected.</summary>
    public string? ElectronVersion { get; private set; }

    public string? ChromiumVersion { get; private set; }

    /// <summary>
    /// Starts the engine if it is not already running. Concurrent callers share
    /// one start; a failed start is not cached, so the next call retries.
    /// </summary>
    public Task StartAsync(IProgress<ElectronRuntimeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        Task launch;
        lock (_startGate)
        {
            if (_starting is { IsCompleted: true, IsCompletedSuccessfully: false })
            {
                _starting = null;
            }

            // The launch runs on the host's own lifetime, not the caller's: a
            // tool call that times out after 30s must not cancel a first-run
            // download every other caller is waiting on. The caller only stops
            // waiting.
            launch = _starting ??= LaunchAsync(progress, _lifetime.Token);
        }

        return launch.WaitAsync(cancellationToken);
    }

    private async Task LaunchAsync(IProgress<ElectronRuntimeProgress>? progress, CancellationToken cancellationToken)
    {
        var executable = await _runtime.EnsureAsync(progress, cancellationToken).ConfigureAwait(false);

        if (!Directory.Exists(_appDirectory))
        {
            throw new ElectronRuntimeUnavailableException(
                $"The Browser pane's engine app is missing from {_appDirectory}. Reinstall Jarvis Code.");
        }

        // A per-run name: two app instances (or two --profile runs) must not
        // race for the same pipe, and a stale server from a crashed run must
        // not be connected to by mistake.
        var pipeName = "JarvisCode-pane-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(_appDirectory);
        start.ArgumentList.Add("--pipe=" + pipeName);
        start.ArgumentList.Add("--user-data-dir=" + _userDataDirectory);
        foreach (var value in _chromiumSwitches)
        {
            start.ArgumentList.Add("--chromium-switch=" + value);
        }

        Process process;
        try
        {
            process = Process.Start(start)
                ?? throw new ElectronRuntimeUnavailableException("The Browser pane's engine did not start.");
        }
        catch (Exception ex) when (ex is not ElectronRuntimeUnavailableException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new ElectronRuntimeUnavailableException(
                $"The Browser pane's engine could not be launched: {ex.Message}", ex);
        }

        // Chromium writes to both streams; draining them keeps a chatty build
        // from filling its pipe buffer and blocking the engine.
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            FailAllPending(new InvalidOperationException("The Browser pane's engine stopped."));
            if (!_disposed)
            {
                Exited?.Invoke(process.ExitCode);
            }
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new ElectronRuntimeUnavailableException(
                "The Browser pane's engine started but never connected back. It may have been blocked by security software.");
        }

        _process = process;
        _pipe = pipe;
        _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        _reader = Task.Run(() => ReadLoopAsync(pipe), CancellationToken.None);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // Chromium's own logging. Nothing here is protocol.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonObject? message;
                try
                {
                    message = line.Length == 0 ? null : JsonNode.Parse(line) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    // A line this process cannot read is one message lost, not a
                    // reason to stop reading the rest of the session.
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                if (message["event"]?.GetValue<string>() is { } name)
                {
                    if (name == "ready")
                    {
                        ElectronVersion = message["electron"]?.GetValue<string>();
                        ChromiumVersion = message["chrome"]?.GetValue<string>();
                    }

                    EventReceived?.Invoke(new ElectronPaneEvent(name, message));
                    continue;
                }

                if (message["id"]?.GetValue<int>() is { } id && _pending.TryRemove(id, out var waiter))
                {
                    waiter.TrySetResult(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            FailAllPending(new InvalidOperationException("The Browser pane's engine connection dropped."));
        }
    }

    private void FailAllPending(Exception error)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
            {
                waiter.TrySetException(error);
            }
        }
    }

    /// <summary>
    /// Sends one command and awaits its answer. An engine-side failure comes
    /// back as an <see cref="InvalidOperationException"/> carrying the engine's
    /// own sentence, which is what the pane's result strings are built from.
    /// </summary>
    public async Task<JsonObject> RequestAsync(string cmd, JsonObject? args, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var writer = _writer ?? throw new InvalidOperationException("The Browser pane's engine is not connected.");
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        var request = new JsonObject { ["id"] = id, ["cmd"] = cmd };
        if (args is not null)
        {
            request["args"] = args;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("The Browser pane's engine connection dropped mid-request.", ex);
        }
        finally
        {
            _writeLock.Release();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        await using var registration = deadline.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetException(cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : new TimeoutException(
                        $"The Browser pane's engine did not answer {cmd} within " +
                        $"{RequestTimeout.TotalSeconds:0}s."));
            }
        });

        var response = await waiter.Task.ConfigureAwait(false);
        if (response["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException(
                response["error"]?.GetValue<string>() ?? $"{cmd} failed in the Browser pane's engine.");
        }

        return response["result"] as JsonObject ?? [];
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        FailAllPending(new ObjectDisposedException(nameof(ElectronPaneHost)));

        // Closing the pipe is the engine's own shutdown signal; killing it is
        // the fallback for one that did not take the hint.
        try
        {
            _writer?.Dispose();
            _pipe?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        if (_process is { } process)
        {
            try
            {
                if (!process.WaitForExit(2000))
                {
                    Kill(process);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or SystemException)
            {
            }

            process.Dispose();
        }

        if (_reader is { } reader)
        {
            try
            {
                await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
            {
            }
        }

        _lifetime.Dispose();
    }
}
