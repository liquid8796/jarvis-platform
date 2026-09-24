using System.IO;
using JarvisCode.Core.Mcp;

namespace Jarvis.Agent.Windows.Tests;

public sealed class UnityMcpStdioProcessTests
{
    [Fact]
    public async Task Native_executable_preserves_version_requirements_and_shell_metacharacters()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "jarvis stdio " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "mcp fixture.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Requirement, [string]$Literal)
                $ErrorActionPreference = 'Stop'
                while ($null -ne ($line = [Console]::ReadLine())) {
                    $request = $line | ConvertFrom-Json
                    if ($null -eq $request.id) { continue }
                    if ($request.method -eq 'initialize') {
                        $result = @{ protocolVersion = '2024-11-05'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'fixture'; version = '1.0' } }
                    } elseif ($request.method -eq 'tools/list') {
                        $result = @{ tools = @(@{ name = 'echo'; description = "$Requirement|$Literal"; inputSchema = @{ type = 'object' } }) }
                    } else { $result = @{} }
                    [Console]::WriteLine((@{ jsonrpc = '2.0'; id = $request.id; result = $result } | ConvertTo-Json -Depth 10 -Compress))
                }
                """);
            const string requirement = "mcpforunityserver>=0.0.0a0";
            const string literal = "a&b|c<d>e^f%JARVIS_LITERAL%";
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var config = new McpServerConfig("unityMCP", executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, requirement, literal],
                new Dictionary<string, string> { ["JARVIS_LITERAL"] = "must-not-expand" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = await McpClient.StartAsync(config, timeout.Token, TimeSpan.FromSeconds(10));
            var tool = Assert.Single(await client.ListToolsAsync(timeout.Token));
            Assert.Equal(requirement + "|" + literal, tool.Description);
            Assert.Equal("echo", tool.Name);
        }
        finally { Directory.Delete(directory, true); }
    }
}
