using System.IO;
using System.Text;
using JarvisCode.Cli;

namespace JarvisCode.Cli.Tests;

public sealed class BackgroundLogTests
{
    [Fact]
    public async Task A_running_host_can_append_while_attach_reads_its_log()
    {
        var path = Path.Combine(Path.GetTempPath(), "jarvis-background-log-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync("{\"type\":\"system\"}");
            Assert.Contains("\"system\"", await CliBackground.ReadLogAsync(path, CancellationToken.None));
            await writer.WriteLineAsync("{\"type\":\"result\"}");
            Assert.Contains("\"result\"", await CliBackground.ReadLogAsync(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }
}
