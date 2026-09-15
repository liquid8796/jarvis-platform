using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using JarvisCode.App.Services;

namespace JarvisCode.App.Tests.Services;

public sealed class BrowserHostRelayTests
{
    [Fact]
    public async Task Absent_desktop_exits_so_extension_can_restart_and_replay_its_ready_frame()
    {
        var elapsed = Stopwatch.StartNew();
        await Task.Run(() => BrowserHostRelay.RunCore(Stream.Null, Stream.Null,
            "jarvis-missing-browser-" + Guid.NewGuid().ToString("N"))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Desktop_disconnect_exits_even_while_extension_input_remains_open()
    {
        var name = "jarvis-relay-lifecycle-" + Guid.NewGuid().ToString("N");
        using var desktop = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var extensionWriter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var extensionInput = new AnonymousPipeClientStream(PipeDirection.In, extensionWriter.GetClientHandleAsString());
        var relay = Task.Run(() => BrowserHostRelay.RunCore(extensionInput, Stream.Null, name));
        await desktop.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        desktop.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
