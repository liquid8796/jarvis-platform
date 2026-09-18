using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace Jarvis.Agent.BrowserHost;

public static class Program
{
    private const string ExtensionPipeName = "JarvisAgent-browser-extension-v2";
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    [STAThread]
    public static int Main()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            Relay(stdin, stdout);
            return 0;
        }
        catch
        {
            // Native-messaging stdout is protocol-only. Chrome treats process exit as disconnect.
            return 1;
        }
    }

    internal static void Relay(Stream stdin, Stream stdout, string pipeName = ExtensionPipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(timeout: 800); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return; }

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var stop = new CancellationTokenSource();
        var stdoutLock = new object();

        var pipeToExtension = Task.Run(() =>
        {
            var prefix = new byte[4];
            try
            {
                while (!stop.IsCancellationRequested && reader.ReadLine() is { } line)
                {
                    var payload = Encoding.UTF8.GetBytes(line);
                    if (payload.Length > MaxFrameBytes) break;
                    BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
                    lock (stdoutLock)
                    {
                        stdout.Write(prefix);
                        stdout.Write(payload);
                        stdout.Flush();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally { stop.Cancel(); }
        });

        var extensionToPipe = Task.Run(() =>
        {
            try
            {
                var prefix = new byte[4];
                while (!stop.IsCancellationRequested)
                {
                    if (!ReadExact(stdin, prefix)) break;
                    var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                    if (length <= 0 || length > MaxFrameBytes) break;
                    var payload = new byte[length];
                    if (!ReadExact(stdin, payload)) break;
                    writer.WriteLine(Encoding.UTF8.GetString(payload));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            finally { stop.Cancel(); }
        });

        try
        {
            Task.WaitAny(pipeToExtension, extensionToPipe);
        }
        finally
        {
            stop.Cancel();
            try { pipe.Dispose(); } catch { }
            try { Task.WaitAll([pipeToExtension, extensionToPipe], TimeSpan.FromMilliseconds(250)); } catch { }
            try { reader.Dispose(); } catch { }
            try { writer.Dispose(); } catch { }
        }
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0) return false;
            read += count;
        }
        return true;
    }
}
