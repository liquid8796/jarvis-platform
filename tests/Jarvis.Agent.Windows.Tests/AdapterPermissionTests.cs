using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.App.Services;
using JarvisCode.Core.Tools;
using JarvisCode.Core.Tools.BuiltIn;
namespace Jarvis.Agent.Windows.Tests;

public sealed class AdapterPermissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-adapter-" + Guid.NewGuid());
    private string Primary => Path.Combine(_root, "primary");
    private string Outside => Path.Combine(_root, "outside");
    private string Private => Path.Combine(_root, "private");
    public AdapterPermissionTests() { Directory.CreateDirectory(Primary); Directory.CreateDirectory(Outside); }
    private sealed class Questions : IUserQuestions
    {
        public int Calls;
        public Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct)
        { Calls++; return Task.FromResult<UserQuestionAnswers?>(new(new Dictionary<string, string>())); }
    }
    private sealed class Artifacts : IArtifactSink
    { public Task ShowAsync(WidgetArtifact a, CancellationToken ct) => Task.CompletedTask; }
    private LegacyToolAdapter Adapter(ITool tool, string category = "filesystem", Questions? questions = null) =>
        new(tool, category, questions ?? new Questions(), new Artifacts(), Private);
    private AgentExecutionContext Context(bool full = false) => new(Primary, "test", Guid.NewGuid().ToString()) { FullPermission = full };

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task Real_file_tools_read_and_write_outside_selected_directories(bool full)
    {
        var target = Path.Combine(Outside, "sample.txt");
        var written = await Adapter(new WriteFileTool()).ExecuteAsync(WireJson.Element(new { file_path = target, content = "outside-project-test" }), Context(full), default);
        Assert.False(written.IsError); Assert.Equal("outside-project-test", File.ReadAllText(target));
        var read = await Adapter(new ReadFileTool()).ExecuteAsync(WireJson.Element(new { file_path = "../outside/sample.txt" }), Context(full), default);
        Assert.False(read.IsError); Assert.Contains("outside-project-test", read.Text);
    }
    [Fact] public async Task Host_working_directory_can_point_to_an_unselected_project()
    {
        File.WriteAllText(Path.Combine(Outside, "sample.txt"), "working-directory-test");
        var result = await Adapter(new ReadFileTool()).ExecuteAsync(WireJson.Element(new { file_path = "sample.txt", workingDirectory = Outside }), Context(), default);
        Assert.False(result.IsError); Assert.Contains("working-directory-test", result.Text);
    }
    [Fact] public async Task Glob_and_grep_can_search_an_unselected_directory()
    {
        File.WriteAllText(Path.Combine(Outside, "sample.txt"), "needle-outside");
        var glob = await Adapter(new GlobTool()).ExecuteAsync(WireJson.Element(new { path = Outside, pattern = "*.txt" }), Context(), default);
        var grep = await Adapter(new GrepTool()).ExecuteAsync(WireJson.Element(new { path = Outside, pattern = "needle-outside", output_mode = "content" }), Context(), default);
        Assert.False(glob.IsError); Assert.Contains("sample.txt", glob.Text);
        Assert.False(grep.IsError); Assert.Contains("needle-outside", grep.Text);
    }
    [Fact] public async Task Private_settings_are_not_a_workspace_but_remain_directly_protected()
    {
        Directory.CreateDirectory(Private); File.WriteAllText(Path.Combine(Private, "tool-permissions.json"), "synthetic");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Adapter(new ReadFileTool()).ExecuteAsync(
            WireJson.Element(new { file_path = Path.Combine(Private, "tool-permissions.json") }), Context(true), default));
    }
    [Fact] public async Task Junction_paths_are_not_rejected_as_workspace_escapes()
    {
        var link = Path.Combine(Primary, "external-link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Arguments = $"/d /c mklink /J \"{link}\" \"{Outside}\"";
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"mklink failed: {await stdout} {await stderr}");
        try
        {
            File.WriteAllText(Path.Combine(Outside, "link.txt"), "junction-test");
            var result = await Adapter(new ReadFileTool()).ExecuteAsync(WireJson.Element(new { file_path = Path.Combine(link, "link.txt") }), Context(), default);
            Assert.False(result.IsError); Assert.Contains("junction-test", result.Text);
        }
        finally { Directory.Delete(link); }
    }
    [Theory] [InlineData("powershell")] [InlineData("chrome")] [InlineData("explorer")]
    public void Full_permission_allows_keyboard_in_terminal_browser_and_desktop_without_app_grants(string app)
    {
        var settings = new UiSettings { ComputerUseRequireGrants = true, ComputerUseFullPermissionProvider = () => ToolConsentScope.IsFullPermission };
        Assert.NotNull(ComputerUseGrants.RefusalFor(settings, "test", app, InputNeed.Keyboard));
        using (ToolConsentScope.Enter(true))
        {
            Assert.Null(ComputerUseGrants.RefusalFor(settings, "test", app, InputNeed.Keyboard));
            Assert.Null(ComputerUseGrants.ClipboardRefusal(settings, "test", read: true));
            Assert.Null(ComputerUseGrants.ClipboardRefusal(settings, "test", read: false));
        }
        Assert.NotNull(ComputerUseGrants.RefusalFor(settings, "test", app, InputNeed.Keyboard));
        Assert.True(settings.ComputerUseRequireGrants); // Persisted policy has not been changed.
    }
    [Fact] public void Explicit_denied_apps_are_not_granted_by_full_permission()
    {
        var settings = new UiSettings { ComputerUseRequireGrants = true, ComputerUseFullPermissionProvider = () => ToolConsentScope.IsFullPermission };
        settings.ComputerUseDeniedApps.Add("Jarvis.Agent.Desktop");
        using var scope = ToolConsentScope.Enter(true);
        Assert.NotNull(ComputerUseGrants.RefusalFor(settings, "test", "Jarvis.Agent.Desktop", InputNeed.Keyboard));
    }
    private sealed class ProbeTool : ITool
    {
        public string Name => "probe";
        public string Description => "Synthetic probe";
        public bool IsReadOnly => true;
        public JsonObject InputSchema => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };
        public string DescribeCall(JsonObject args) => Name;
        public ToolExecutionContext? Context;
        public bool Consent;
        public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolExecutionContext context, CancellationToken ct)
        { await Task.Yield(); Context = context; Consent = ToolConsentScope.IsFullPermission; return ToolResult.Success("probe"); }
    }
    [Fact] public async Task Adapter_propagates_only_current_call_consent_and_disables_browser_workspace_scope()
    {
        var probe = new ProbeTool();
        await Adapter(probe, "browser").ExecuteAsync(WireJson.Element(new { workingDirectory = Outside }), Context(true), default);
        Assert.True(probe.Consent); Assert.False(probe.Context!.EnforceWorkspaceFileScope);
        Assert.Equal(Outside, probe.Context.WorkingDirectory); Assert.False(ToolConsentScope.IsFullPermission);
        await Adapter(probe, "browser").ExecuteAsync(WireJson.Element(new { }), Context(false), default);
        Assert.False(probe.Consent);
    }
    [Fact] public async Task Desktop_tools_do_not_depend_on_project_directory_still_existing()
    {
        Directory.Delete(Primary);
        var probe = new ProbeTool();
        var result = await Adapter(probe, "computer").ExecuteAsync(WireJson.Element(new { }), Context(true), default);
        Assert.False(result.IsError); Assert.True(probe.Consent);
    }
    [Fact] public void Standalone_vendor_context_still_enforces_its_own_upload_policy_by_default()
    { Assert.True(new ToolExecutionContext { WorkingDirectory = Primary }.EnforceWorkspaceFileScope); }
    [Fact] public void Runtime_permission_provider_is_never_serialized_to_computer_settings()
    {
        var settings = new UiSettings { ComputerUseFullPermissionProvider = () => true };
        var json = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("ComputerUseFullPermissionProvider", json); Assert.DoesNotContain("RequireComputerUseGrants", json);
    }
    [Fact] public async Task PowerShell_can_start_outside_projects_and_change_directory()
    {
        var result = await Adapter(new ShellTool(), "shell").ExecuteAsync(WireJson.Element(new
        {
            command = "(Get-Location).Path; Set-Location ..; (Get-Location).Path",
            workingDirectory = Outside, timeout = 10000
        }), Context(true), default);
        Assert.False(result.IsError, result.Text); Assert.Contains(Outside, result.Text); Assert.Contains(_root, result.Text);
    }
    [Fact] public async Task Git_status_can_use_an_unselected_repository()
    {
        var init = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = Outside, UseShellExecute = false, CreateNoWindow = true };
        init.ArgumentList.Add("init"); init.ArgumentList.Add("--quiet");
        using var process = System.Diagnostics.Process.Start(init)!; await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        File.WriteAllText(Path.Combine(Outside, "untracked.txt"), "synthetic");
        var result = await Adapter(new GitStatusTool(), "git").ExecuteAsync(WireJson.Element(new { workingDirectory = Outside }), Context(true), default);
        Assert.False(result.IsError, result.Text); Assert.Contains("untracked.txt", result.Text);
    }
    [Fact] public async Task Browser_upload_validates_outside_file_without_contacting_a_browser()
    {
        var target = Path.Combine(Outside, "upload.txt"); File.WriteAllText(target, "synthetic-upload");
        using var bridge = new BrowserBridge("Jarvis-Test-" + Guid.NewGuid().ToString("N"));
        var tool = new BrowserFileUploadTool(bridge);
        var args = new JsonObject { ["paths"] = new JsonArray(JsonValue.Create(target)) };
        var standalone = new ToolExecutionContext { WorkingDirectory = Primary };
        var blocked = await tool.ExecuteAsync(args, standalone, default);
        Assert.True(blocked.IsError); Assert.Contains("only files this session", blocked.Content);
        var allowed = await tool.ExecuteAsync(args, standalone with { EnforceWorkspaceFileScope = false }, default);
        // It reached destination validation instead of the old project scope rejection; no upload is attempted.
        Assert.True(allowed.IsError); Assert.Contains("Pass ref", allowed.Content);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
