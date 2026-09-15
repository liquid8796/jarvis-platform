using System.IO;
using JarvisCode.App.Services;
using JarvisCode.Core.BackgroundTasks;

namespace JarvisCode.App.Tests.Services;

public sealed class PreviewToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("preview-").FullName;
    private readonly BackgroundTaskManager _tasks = new();
    private readonly List<string> _opened = [];

    private PreviewServers Servers() => new(_tasks, _opened.Add);

    public void Dispose()
    {
        _tasks.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Parses_launch_configurations()
    {
        var configurations = PreviewServers.ParseLaunchFile("""
            {
              "version": "0.0.1",
              "configurations": [
                { "name": "web", "runtimeExecutable": "npm", "runtimeArgs": ["run", "dev"], "port": 3000 },
                { "name": "attached", "url": "http://localhost:8443", "port": 8443 }
              ]
            }
            """);

        Assert.Equal(2, configurations.Count);
        Assert.Equal("npm run dev", configurations[0].CommandLine);
        Assert.Equal("http://localhost:3000", configurations[0].PreviewUrl);
        Assert.False(configurations[0].AttachOnly);
        Assert.True(configurations[1].AttachOnly);
        Assert.Equal("http://localhost:8443", configurations[1].PreviewUrl);
    }

    [Fact]
    public void Url_only_preview_opens_the_browser_without_a_server()
    {
        var servers = Servers();
        var result = servers.Start(name: null, url: "http://localhost:5000", _dir);
        Assert.False(result.IsError);
        Assert.Contains("preview-1", result.Content);
        Assert.Equal("http://localhost:5000", Assert.Single(_opened));
        Assert.Contains("url-only", servers.ListServers().Content);
    }

    [Fact]
    public void EachWayALaunchFileCanFailIsItsOwnDiagnostic()
    {
        // The reference tells these five apart because each is a different
        // thing for the caller to do; this used to answer three of them with
        // one "could not read it".
        var servers = Servers();
        Assert.Contains("No ", servers.Start("web", null, _dir).Content);
        Assert.Contains("Then call preview_start with the server name.",
            servers.Start("web", null, _dir).Content);

        var config = Path.Combine(_dir, ".jarvis", "launch.json");
        Directory.CreateDirectory(Path.Combine(_dir, ".jarvis"));

        File.WriteAllText(config, "{ this is not json");
        Assert.Contains("but it could not be parsed:", servers.Start("web", null, _dir).Content);

        File.WriteAllText(config, """{"configurations":[]}""");
        Assert.Contains("but it contains no configurations.", servers.Start("web", null, _dir).Content);

        File.WriteAllText(config,
            """{"configurations":[{"name":"other","runtimeExecutable":"cmd","port":1}]}""");
        var unknown = servers.Start("web", null, _dir);
        Assert.True(unknown.IsError);
        Assert.Contains("No matching server in", unknown.Content);
        Assert.Contains("Available servers: other.", unknown.Content);
    }

    [Fact]
    public void OneConfigurationNeedsNoNameAndSeveralDo()
    {
        var servers = Servers();
        var config = Path.Combine(_dir, ".jarvis", "launch.json");
        Directory.CreateDirectory(Path.Combine(_dir, ".jarvis"));

        // Several: the caller is told the names rather than asked to guess.
        File.WriteAllText(config, """
            {"configurations":[
              {"name":"frontend","runtimeExecutable":"cmd","port":1},
              {"name":"backend","runtimeExecutable":"cmd","port":2}]}
            """);
        var ambiguous = servers.Start(null, null, _dir);
        Assert.True(ambiguous.IsError);
        Assert.Contains("Multiple server configurations found: frontend, backend.", ambiguous.Content);
        Assert.Contains("To start all servers, call preview_start separately for each.", ambiguous.Content);

        // One: it is the one meant, so no name is needed.
        File.WriteAllText(config, """{"configurations":[{"name":"only","url":"http://localhost:1"}]}""");
        Assert.False(servers.Start(null, null, _dir).IsError);
    }

    [Fact]
    public void ASessionWithNoFolderSaysSoRatherThanLookingForAFile()
    {
        Assert.Equal(
            PreviewServers.NoWorkingDirectory,
            Servers().Start("web", null, Path.Combine(_dir, "does-not-exist")).Content);
    }

    [Fact]
    public void Claude_launch_json_is_the_compat_fallback()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".claude"));
        File.WriteAllText(Path.Combine(_dir, ".claude", "launch.json"), "{}");
        Assert.EndsWith(Path.Combine(".claude", "launch.json"), PreviewServers.FindLaunchFile(_dir)!);
    }

    [Fact]
    public void Started_server_runs_logs_filter_and_stop_kills()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".jarvis"));
        File.WriteAllText(Path.Combine(_dir, ".jarvis", "launch.json"), """
            {"configurations":[{"name":"echo","runtimeExecutable":"powershell",
             "runtimeArgs":["-NoProfile","-Command","Write-Output 'server ready'; Write-Output 'ERROR: oops'; Start-Sleep -Seconds 30"],
             "port": 4321}]}
            """.ReplaceLineEndings(" "));

        var servers = Servers();
        var start = servers.Start("echo", null, _dir);
        Assert.False(start.IsError);
        Assert.Contains("http://localhost:4321", start.Content);
        Assert.Equal("http://localhost:4321", Assert.Single(_opened));

        // Reuse instead of double-starting.
        Assert.Contains("already running", servers.Start("echo", null, _dir).Content);

        // Wait for the line rather than for a guessed duration: powershell's startup runs
        // well past a couple of seconds on a busy machine.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var logs = servers.Logs("preview-1", lines: 50, level: "error", search: null);
        while (!logs.Content.Contains("ERROR: oops") && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            logs = servers.Logs("preview-1", lines: 50, level: "error", search: null);
        }

        Assert.Contains("ERROR: oops", logs.Content);
        Assert.DoesNotContain("server ready", logs.Content);

        var stop = servers.Stop("preview-1");
        Assert.False(stop.IsError);
        Assert.Contains("Stopped", stop.Content);
    }

    [Fact]
    public void Parses_autoPort_as_a_tri_state()
    {
        var configurations = PreviewServers.ParseLaunchFile("""
            {
              "configurations": [
                { "name": "on",  "runtimeExecutable": "npm", "port": 3000, "autoPort": true },
                { "name": "off", "runtimeExecutable": "npm", "port": 3001, "autoPort": false },
                { "name": "unset", "runtimeExecutable": "npm", "port": 3002 }
              ]
            }
            """);

        Assert.True(configurations[0].AutoPort);
        Assert.False(configurations[1].AutoPort);
        // Absent is its own answer, not false.
        Assert.Null(configurations[2].AutoPort);
    }

    [Fact]
    public void Parses_env_and_ignores_non_string_values()
    {
        var configurations = PreviewServers.ParseLaunchFile("""
            {
              "configurations": [
                { "name": "web", "runtimeExecutable": "npm",
                  "env": { "API": "http://x", "COUNT": 3 } }
              ]
            }
            """);

        Assert.Equal("http://x", configurations[0].Env["API"]);
        Assert.False(configurations[0].Env.ContainsKey("COUNT"));
    }

    [Fact]
    public void Reassigned_port_moves_a_localhost_url_with_it()
    {
        var entry = new LaunchConfiguration("web", "npm", [], 3000, "http://localhost:3000/app");

        var moved = entry.OnPort(51234);

        Assert.Equal("http://localhost:51234/app", moved.PreviewUrl);
    }

    [Fact]
    public void Reassigned_port_leaves_a_remote_url_alone()
    {
        var entry = new LaunchConfiguration("web", "npm", [], 3000, "https://staging.example.com/app");

        var moved = entry.OnPort(51234);

        Assert.Equal("https://staging.example.com/app", moved.PreviewUrl);
    }
}
