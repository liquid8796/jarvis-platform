using System.IO;
using System.IO.Pipes;

namespace JarvisCode.App.Services;

/// <summary>
/// One app instance per profile, with a named-pipe command channel so a second
/// launch (e.g. "jarvis --toggle") reaches the running instance in milliseconds.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ShowCommand = "show";
    public const string ToggleQuickEntryCommand = "toggle-quick-entry";
    public const string NewChatCommand = "new-chat";

    /// <summary>Prefix command: open a Code session in the directory that follows.</summary>
    public const string CodeDirCommandPrefix = "code-dir:";

    /// <summary>Prefix command: act on the jarvis-code:// link that follows.</summary>
    public const string DeepLinkCommandPrefix = "deep-link:";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();

    public bool IsPrimary { get; }

    public event Action<string>? CommandReceived;

    public SingleInstance(string instanceKey)
    {
        _pipeName = $"{instanceKey}-cmd";
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{instanceKey}-instance", out var createdNew);
        IsPrimary = createdNew;
        if (IsPrimary)
        {
            _ = Task.Run(ListenLoopAsync);
        }
    }

    /// <summary>Sends a command to the primary instance. Returns false when none is listening.</summary>
    public bool SendToPrimary(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    CommandReceived?.Invoke(command.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Client dropped mid-handshake; keep listening.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
