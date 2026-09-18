using System.IO;
using System.IO.Pipes;
using System.Text;

using JarvisCode.App.Services;
namespace Jarvis.Agent.Windows;

/// <summary>
/// The process the browser launches for native messaging. It shuttles frames
/// between the extension (length-prefixed JSON on stdio) and the running app
/// (newline-delimited JSON on the BrowserBridge pipe), reconnecting to the
/// pipe. If the app is unavailable or exits, the relay exits too so the
/// extension's existing reconnect backoff creates a fresh host and replays ready.
/// </summary>
public static class BrowserHostRelay
{
    public static void Run()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        RunCore(stdin, stdout, BrowserIntegration.ExtensionPipeName);
    }

    internal static void RunCore(Stream stdin, Stream stdout, string pipeName)
    {
        NamedPipeClientStream? pipe = null;
        StreamWriter? pipeWriter = null;
        var stdoutLock = new object();
        using var exit = new CancellationTokenSource();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void WriteToExtension(string json)
        {
            lock (stdoutLock)
            {
                var frame = NativeMessageCodec.Encode(json);
                stdout.Write(frame, 0, frame.Length);
                stdout.Flush();
            }
        }

        // Pipe → extension pump; restarted whenever the pipe reconnects.
        void StartPipeReader(NamedPipeClientStream stream)
        {
            var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            _ = Task.Run(() =>
            {
                try
                {
                    while (!exit.IsCancellationRequested)
                    {
                        var line = reader.ReadLine();
                        if (line is null)
                        {
                            break;
                        }

                        WriteToExtension(line);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }
                finally { disconnected.TrySetResult(); }
            });
        }

        bool EnsurePipe()
        {
            if (pipe is { IsConnected: true })
            {
                return true;
            }

            pipe?.Dispose();
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(timeout: 800);
                pipeWriter = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                StartPipeReader(pipe);
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                pipe.Dispose();
                pipe = null;
                pipeWriter = null;
                return false;
            }
        }

        if (!EnsurePipe()) return;

        // Extension → pipe pump (main loop; EOF means the browser closed us).
        while (!exit.IsCancellationRequested)
        {
            var frame = Task.Run(() =>
            {
                try { return NativeMessageCodec.ReadFrame(stdin); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { return null; }
            });
            if (Task.WaitAny(frame, disconnected.Task) != 0 || frame.GetAwaiter().GetResult() is not { } json) break;
            if (!EnsurePipe())
            {
                break;
            }

            try
            {
                pipeWriter!.WriteLine(json);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                pipe?.Dispose();
                pipe = null;
                pipeWriter = null;
                break;
            }
        }

        exit.Cancel();
        pipe?.Dispose();
    }
}
