using System.Collections.Concurrent;
using JarvisCode.Core.Models;

namespace JarvisCode.Cli;

/// <summary>Owns print-session wakeups and background reports after their initiating turn ends.</summary>
internal sealed class CliPendingWork(CancellationToken lifetime, Action<ChatMessage>? delivery = null) : IDisposable
{
    private readonly ConcurrentQueue<CliInputMessage> _ready = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _timers = new();
    private readonly SemaphoreSlim _changed = new(0);
    private volatile bool _inputComplete;
    private Exception? _inputFailure;
    public bool HasReady => !_ready.IsEmpty;
    public bool HasBackgroundReady => _ready.Any(message => message.Message.IsMeta);

    public void Post(CliInputMessage message)
    {
        if (delivery is not null) delivery(message.Message);
        else { _ready.Enqueue(message); _changed.Release(); }
    }
    public void Post(ChatMessage message) => Post(new CliInputMessage(message, Guid.NewGuid().ToString()));
    public void CompleteInput(Exception? failure = null)
    { _inputFailure = failure; _inputComplete = true; _changed.Release(); }

    public void Schedule(TimeSpan delay, string prompt, bool noop)
    {
        StopWakeups();
        var id = Guid.NewGuid().ToString();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _timers[id] = stop;
        _ = FireAsync(id, delay, prompt, noop, stop);
    }

    private async Task FireAsync(string id, TimeSpan delay, string prompt, bool noop, CancellationTokenSource stop)
    {
        try
        {
            await Task.Delay(delay, stop.Token);
            Post(ChatMessage.FromUserText(prompt) with { IsMeta = true });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { _timers.TryRemove(id, out _); stop.Dispose(); _changed.Release(); }
        _ = noop; // The flag describes the previous tick; it does not cancel this wakeup.
    }

    public int StopWakeups()
    {
        var stopped = 0;
        foreach (var (id, timer) in _timers)
            if (_timers.TryRemove(id, out _))
            {
                try { timer.Cancel(); } catch (ObjectDisposedException) { }
                stopped++;
            }
        _changed.Release();
        return stopped;
    }

    public async Task<CliInputMessage?> NextAsync(Func<bool> backgroundRunning, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_inputFailure is { } failure) throw new CliError("stream-json input failed: " + failure.Message);
            if (_ready.TryDequeue(out var message)) return message;
            if (_inputComplete && _timers.IsEmpty && !backgroundRunning())
            {
                // A producer posts before dropping its pending-work marker.
                // Re-check after observing that marker clear, or a completion
                // racing the first dequeue could otherwise be lost at EOF.
                if (!_ready.IsEmpty) continue;
                return null;
            }
            await _changed.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    public void Dispose() => StopWakeups();
}
