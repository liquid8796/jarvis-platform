using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Server.Tests;

public sealed class AgentTaskExecutionTests
{
    private static RemoteTaskStep Command(string id, string command, string stage = "EXECUTE", int timeout = 30) =>
        new() { Id = id, ToolId = "process.start", Stage = stage, TimeoutSeconds = timeout,
            Arguments = WireJson.Element(new { command, timeoutSeconds = timeout }) };
    private static RemoteTaskPlan Plan(params RemoteTaskStep[] steps) => new() { Goal = "Task integration fixture", Steps = steps };

    [Fact]
    public async Task Goal_only_waits_for_plan_and_plan_submission_is_idempotent()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var counter = new CountingTool();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, counter);
        var draft = await peer.CreateAsync(admin, Plan());
        Assert.Equal("NEEDS_PLAN", draft.Status); Assert.Equal(0, counter.Calls);
        var plan = Plan(new RemoteTaskStep { Id = "read", ToolId = counter.Descriptor.Id });
        var submitted = await admin.PostAsJsonAsync($"/api/agent/tasks/{draft.TaskId}/plan", peer.Input(plan));
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        Assert.Equal("COMPLETED", (await peer.TerminalAsync(admin, draft.TaskId)).Status);
        var repeated = await admin.PostAsJsonAsync($"/api/agent/tasks/{draft.TaskId}/plan", peer.Input(plan));
        repeated.EnsureSuccessStatusCode(); Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Client_plan_runs_real_patch_build_test_package_verify()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        Directory.CreateDirectory(Path.Combine(peer.Workspace, "feed"));
        await File.WriteAllTextAsync(Path.Combine(peer.Workspace, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(peer.Workspace, "Program.cs"),
            "static int Add(int a, int b) => a - b; if (Add(2, 3) != 5) return 1; Console.WriteLine(\"FIXTURE_TEST_OK\"); return 0;");
        var windows = OperatingSystem.IsWindows();
        var patch = windows ? "[IO.File]::WriteAllText((Join-Path (Get-Location) 'Program.cs'), [IO.File]::ReadAllText((Join-Path (Get-Location) 'Program.cs')).Replace('a - b','a + b')); Write-Output PATCH_OK"
            : "sed -i 's/a - b/a + b/' Program.cs; echo PATCH_OK";
        var build = windows ? "dotnet restore Fixture.csproj --source ./feed -p:NuGetAudit=false; if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}; dotnet build Fixture.csproj -c Release --no-restore; exit $LASTEXITCODE"
            : "dotnet restore Fixture.csproj --source ./feed -p:NuGetAudit=false && dotnet build Fixture.csproj -c Release --no-restore";
        var test = windows ? "dotnet bin/Release/net10.0/Fixture.dll; exit $LASTEXITCODE" : "dotnet bin/Release/net10.0/Fixture.dll";
        var package = windows ? "Compress-Archive -Path bin/Release/net10.0/* -DestinationPath fixture.zip; Write-Output PACKAGE_OK"
            : "tar -czf fixture.tar.gz -C bin/Release/net10.0 .; echo PACKAGE_OK";
        var verify = windows ? "Add-Type -AssemblyName System.IO.Compression.FileSystem; $z=[IO.Compression.ZipFile]::OpenRead((Join-Path (Get-Location) 'fixture.zip')); try { if(-not ($z.Entries | Where-Object FullName -eq 'Fixture.dll')){exit 8}; Write-Output VERIFIED } finally { $z.Dispose() }"
            : "tar -tzf fixture.tar.gz | grep Fixture.dll && echo VERIFIED";
        var plan = Plan(Command("patch", patch), Command("build", build, "BUILD", 60),
            Command("test", test, "TEST"), Command("package", package, "PACKAGE"),
            Command("verify", verify, "VERIFY") with { ExpectedText = "VERIFIED" });
        var created = await peer.CreateAsync(admin, plan);
        var result = await peer.TerminalAsync(admin, created.TaskId);
        Assert.True(result.Status == "COMPLETED", JsonSerializer.Serialize(result));
        Assert.Equal(5, result.CompletedSteps); Assert.Equal(5, peer.Approval.Calls);
        var artifacts = await admin.GetFromJsonAsync<JsonElement>(peer.Url(created.TaskId, "/artifacts"));
        Assert.Equal(5, artifacts.GetProperty("artifacts").GetArrayLength());
        foreach (var artifact in artifacts.GetProperty("artifacts").EnumerateArray())
        { Assert.True(artifact.GetProperty("success").GetBoolean()); Assert.Equal(0, artifact.GetProperty("exitCode").GetInt32()); }
        Assert.Contains("FIXTURE_TEST_OK", artifacts.GetRawText());
        Assert.True(File.Exists(Path.Combine(peer.Workspace, windows ? "fixture.zip" : "fixture.tar.gz")));
    }

    [Fact]
    public async Task Process_exit_failure_stops_later_stages_and_duplicate_create_does_not_replay()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        var plan = Plan(Command("fail", "echo expected-failure; exit 9", "BUILD"), Command("never", "echo NEVER", "TEST"));
        var id = Guid.NewGuid().ToString("N");
        var created = await peer.CreateAsync(admin, plan, id);
        var result = await peer.TerminalAsync(admin, created.TaskId);
        Assert.Equal("FAILED", result.Status); Assert.Equal(0, result.CompletedSteps);
        var artifacts = await admin.GetFromJsonAsync<JsonElement>(peer.Url(id, "/artifacts"));
        var item = Assert.Single(artifacts.GetProperty("artifacts").EnumerateArray());
        Assert.Equal(9, item.GetProperty("exitCode").GetInt32()); Assert.DoesNotContain("NEVER", artifacts.GetRawText());
        Assert.Equal("FAILED", (await peer.CreateAsync(admin, plan, id)).Status);
        Assert.Equal(1, peer.Approval.Calls);
        var conflict = await admin.PostAsJsonAsync("/api/agent/tasks", peer.Input(Plan(Command("different", "echo x")), id));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Safe_read_retry_records_attempts_but_mutating_retry_is_rejected()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var counter = new CountingTool(failFirst: true);
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, counter);
        var task = await peer.CreateAsync(admin, Plan(new RemoteTaskStep { Id = "read", ToolId = counter.Descriptor.Id, MaxAttempts = 2 }));
        Assert.Equal("COMPLETED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
        Assert.Equal(2, counter.Calls);
        var artifacts = await admin.GetFromJsonAsync<JsonElement>(peer.Url(task.TaskId, "/artifacts") + "&limit=1");
        Assert.Single(artifacts.GetProperty("artifacts").EnumerateArray()); Assert.Equal(1, artifacts.GetProperty("nextOffset").GetInt32());
        var invalid = await admin.PostAsJsonAsync("/api/agent/tasks", peer.Input(Plan(Command("mutate", "echo x") with { MaxAttempts = 2 })));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Autonomous_mode_cannot_bypass_local_pause_or_denied_approval(bool arm, bool approve)
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin, arm: arm, approve: approve);
        var response = await admin.PostAsJsonAsync("/api/agent/tasks", peer.Input(Plan(Command("check", "echo x")) with { ExecutionMode = "AUTONOMOUS" }));
        if (!arm) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        else
        {
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<RemoteTaskSnapshot>(WireJson.Options);
            Assert.Equal("FAILED", (await peer.TerminalAsync(admin, created!.TaskId)).Status);
        }
        Assert.Null(peer.StartedJobId);
    }

    [Fact]
    public async Task Cancel_stops_owned_process_and_status_remains_readable_while_paused()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        var command = OperatingSystem.IsWindows() ? "Write-Output STARTED; Start-Sleep -Seconds 30; Write-Output MUST_NOT_FINISH"
            : "echo STARTED; sleep 30; echo MUST_NOT_FINISH";
        var task = await peer.CreateAsync(admin, Plan(Command("long", command, timeout: 60)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (peer.StartedJobId is null) await Task.Delay(50, timeout.Token);
        (await admin.PostAsJsonAsync(peer.Url(task.TaskId, "/cancel"), new { })).EnsureSuccessStatusCode();
        Assert.Equal("CANCELLED", (await peer.TerminalAsync(admin, task.TaskId)).Status);
        while (!(await peer.JobAsync()).GetProperty("done").GetBoolean()) await Task.Delay(50, timeout.Token);
        Assert.DoesNotContain("MUST_NOT_FINISH", (await peer.JobAsync()).GetProperty("output").GetString());
        peer.Connection.Pause();
        Assert.Equal("CANCELLED", (await admin.GetFromJsonAsync<RemoteTaskSnapshot>(peer.Url(task.TaskId), WireJson.Options))!.Status);
    }

    [Fact]
    public async Task Completed_state_survives_agent_restart_without_reexecution()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        var counter = new CountingTool();
        var first = await TaskAgentPeer.ConnectAsync(app, admin, counter);
        var plan = Plan(new RemoteTaskStep { Id = "read", ToolId = counter.Descriptor.Id });
        var task = await first.CreateAsync(admin, plan);
        Assert.Equal("COMPLETED", (await first.TerminalAsync(admin, task.TaskId)).Status);
        await first.DisposeAsync();
        await using var second = await TaskAgentPeer.ConnectAsync(app, admin, counter, deviceId: first.DeviceId, token: first.Token);
        Assert.Equal("COMPLETED", (await second.CreateAsync(admin, plan, task.TaskId)).Status);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Task_queries_cannot_cross_owner_or_device()
    {
        using var app = new ServerFixture(); using var admin = await app.Admin();
        await using var peer = await TaskAgentPeer.ConnectAsync(app, admin);
        var task = await peer.CreateAsync(admin, Plan());
        await using var other = await TaskAgentPeer.ConnectAsync(app, admin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(other.Url(task.TaskId))).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/users", new { email = "taskmember@example.test", displayName = "Task member", password = ServerFixture.Password, role = "user", status = "active" })).EnsureSuccessStatusCode();
        using var member = app.Client(); await ServerFixture.Csrf(member);
        (await member.PostAsJsonAsync("/api/auth/login", new { email = "taskmember@example.test", password = ServerFixture.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(peer.Url(task.TaskId))).StatusCode);
    }

    private sealed class CountingTool(bool failFirst = false) : IAgentTool
    {
        public int Calls;
        public ToolDescriptor Descriptor => new("test.counter", "test__counter", "test", "Count real invocations",
            WireJson.Element(new { type = "object", additionalProperties = false }), true);
        public Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref Calls);
            return Task.FromResult(failFirst && attempt == 1 ? ToolReply.Error("transient read failure") : new ToolReply("count=" + attempt));
        }
    }
}
