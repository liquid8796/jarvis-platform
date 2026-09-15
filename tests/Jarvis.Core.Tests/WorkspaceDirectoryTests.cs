using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;
namespace Jarvis.Core.Tests;

public sealed class WorkspaceDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-folders-" + Guid.NewGuid());
    private string Primary => Path.Combine(_root, "primary");
    private string Secondary => Path.Combine(_root, "secondary");
    public WorkspaceDirectoryTests() { Directory.CreateDirectory(Primary); Directory.CreateDirectory(Secondary); }
    [Fact] public void Folder_list_is_normalized_deduplicated_and_primary_stays_first()
    {
        var folders = new WorkspaceDirectories(Primary, [Secondary, Primary + Path.DirectorySeparatorChar, Secondary]);
        Assert.Equal(new[] { Primary, Secondary }, folders.Directories);
        Assert.Equal(new[] { Secondary }, folders.Additional);
    }
    [Fact] public void Directory_context_is_an_immutable_snapshot()
    {
        var additional = new List<string> { Secondary };
        var folders = new WorkspaceDirectories(Primary, additional);
        additional.Clear(); Assert.Single(folders.Additional);
    }
    [Fact] public void Missing_additional_directory_is_rejected_before_connect()
    {
        var options = new AgentOptions("https://jarvis.example", Guid.NewGuid().ToString(), Primary, false, [Path.Combine(_root, "missing")]);
        Assert.Throws<DirectoryNotFoundException>(() => options.ValidateAndGetWebSocketUri());
    }
    [Fact] public void Working_directory_resolution_is_not_a_shell_sandbox()
    {
        var folders = new WorkspaceDirectories(Primary, [Secondary]);
        Assert.Equal(Path.Combine(_root, "other.txt"), folders.Resolve("../other.txt"));
        Assert.Equal(Secondary, folders.Resolve(Secondary));
    }
    [Fact] public void Explicit_secondary_folder_is_accepted_by_file_tools()
    {
        var boundary = new WorkspaceDirectories(Primary, [Secondary]);
        Assert.Equal(Path.Combine(Secondary, "file.txt"), boundary.Resolve(Path.Combine(Secondary, "file.txt")));
        Assert.Equal(Path.Combine(Primary, "file.txt"), boundary.Resolve("file.txt"));
    }
    [Fact] public void Unselected_sibling_and_other_projects_are_not_blocked()
    {
        var boundary = new WorkspaceDirectories(Primary, [Secondary]);
        Assert.Equal(Path.Combine(_root, "unselected", "file.txt"), boundary.Resolve(Path.Combine(_root, "unselected", "file.txt")));
        Assert.Equal(Path.GetFullPath(Secondary + "-sibling/file.txt"), boundary.Resolve(Secondary + "-sibling/file.txt"));
    }
    [Fact] public void Legacy_single_directory_options_load_without_migration()
    {
        var json = JsonSerializer.Serialize(new { serverUrl = "https://jarvis.example", deviceId = Guid.NewGuid().ToString(), workspace = Primary, allowLoopbackHttp = false });
        var options = JsonSerializer.Deserialize<AgentOptions>(json, WireJson.Options)!;
        Assert.Equal(Primary, options.Workspace); Assert.Null(options.AdditionalDirectories);
        Assert.Equal("wss", options.ValidateAndGetWebSocketUri().Scheme);
    }
    [Fact] public void Multiple_directories_round_trip_in_options()
    {
        var options = new AgentOptions("https://jarvis.example", Guid.NewGuid().ToString(), Primary, false, [Secondary]);
        var copy = JsonSerializer.Deserialize<AgentOptions>(JsonSerializer.Serialize(options, WireJson.Options), WireJson.Options)!;
        Assert.Equal(options.Workspace, copy.Workspace); Assert.Equal(options.AdditionalDirectories, copy.AdditionalDirectories);
    }
    [Fact] public async Task Managed_job_can_choose_an_explicit_starting_directory()
    {
        using var tools = new ProcessToolSet();
        var context = new AgentExecutionContext(Primary, "cwd-test", "test");
        var start = tools.Tools.Single(t => t.Descriptor.Id == "process.start");
        var command = OperatingSystem.IsWindows() ? "(Get-Location).Path" : "pwd";
        var reply = await start.ExecuteAsync(WireJson.Element(new { command, workingDirectory = Secondary, timeoutSeconds = 10 }), context, CancellationToken.None);
        using var initial = JsonDocument.Parse(reply.Text);
        var id = initial.RootElement.GetProperty("jobId").GetString();
        var reader = tools.Tools.Single(t => t.Descriptor.Id == "process.read");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            using var result = JsonDocument.Parse((await reader.ExecuteAsync(WireJson.Element(new { jobId = id, cursor = 0 }), context, timeout.Token)).Text);
            if (result.RootElement.GetProperty("done").GetBoolean())
            {
                Assert.Contains(Secondary, result.RootElement.GetProperty("output").GetString());
                Assert.Equal(0, result.RootElement.GetProperty("exitCode").GetInt32()); break;
            }
            await Task.Delay(50, timeout.Token);
        }
    }
    [Fact] public void Filesystem_root_selection_accepts_descendants()
    {
        var drive = Path.GetPathRoot(Primary)!;
        Assert.Equal(Primary, new WorkspaceDirectories(drive).Resolve(Primary));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
