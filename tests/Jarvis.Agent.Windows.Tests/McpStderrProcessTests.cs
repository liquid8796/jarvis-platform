using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows.Tests;

public sealed class McpStderrProcessTests
{
    [Fact]
    public async Task Verbose_stderr_without_newlines_cannot_block_initialize_or_repeated_calls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jarvis stderr " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "noisy mcp.ps1");
            await File.WriteAllTextAsync(script, """
                $ErrorActionPreference = 'Stop'
                while ($null -ne ($line = [Console]::ReadLine())) {
                    $request = $line | ConvertFrom-Json
                    if ($null -eq $request.id) { continue }
                    # Larger than a Windows pipe buffer, with no line terminator.
                    [Console]::Error.Write(('private-diagnostic-do-not-echo-' * 40000))
                    [Console]::Error.Flush()
                    if ($request.method -eq 'initialize') {
                        $result = @{ protocolVersion = '2024-11-05'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'noisy-fixture'; version = '1.0' } }
                    } else {
                        $result = @{ content = @(@{ type = 'text'; text = 'ok' }); isError = $false }
                    }
                    [Console]::WriteLine((@{ jsonrpc = '2.0'; id = $request.id; result = $result } | ConvertTo-Json -Depth 10 -Compress))
                }
                """);
            var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var config = new McpServerConfig("stderr-fixture", exe,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script], new Dictionary<string, string>());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var client = await McpClient.StartAsync(config, timeout.Token, TimeSpan.FromSeconds(5));
            for (var call = 0; call < 3; call++)
            {
                var reply = await client.CallToolAsync("echo", new JsonObject(), timeout.Token);
                Assert.False(reply.IsError);
                Assert.Equal("ok", reply.Text);
                Assert.DoesNotContain("private-diagnostic", reply.Text);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
