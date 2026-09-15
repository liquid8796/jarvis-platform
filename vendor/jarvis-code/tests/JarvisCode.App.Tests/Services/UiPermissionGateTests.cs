using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

public class UiPermissionGateTests : IDisposable
{
    private sealed class FakeTool(string name, bool isReadOnly) : ITool
    {
        public string Name => name;
        public string Description => "test";
        public JsonObject InputSchema => [];
        public bool IsReadOnly => isReadOnly;
        public string DescribeCall(JsonObject arguments) => name;
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("ok"));
    }

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "jarvis-gate-" + Guid.NewGuid().ToString("N")[..8]);
    private int _promptCount;
    private PermissionDecision _promptAnswer = PermissionDecision.Allow;
    private PermissionPrompt? _lastPrompt;
    private readonly UiPermissionGate _gate;

    public UiPermissionGateTests()
    {
        Directory.CreateDirectory(_workspace);
        _gate = new UiPermissionGate
        {
            WorkingDirectory = _workspace,
            PromptAsync = (prompt, _) =>
            {
                _promptCount++;
                _lastPrompt = prompt;
                return Task.FromResult(_promptAnswer);
            },
        };
    }

    private static PermissionRequest Request(string tool, bool readOnly, params (string Key, string Value)[] args)
    {
        var arguments = new JsonObject();
        foreach (var (key, value) in args)
        {
            arguments[key] = value;
        }

        return new PermissionRequest(new FakeTool(tool, readOnly), arguments, tool);
    }

    [Fact]
    public async Task ReadOnlyCallInsideWorkspaceRunsSilently()
    {
        var decision = await _gate.RequestAsync(Request("Read", true, ("file_path", "notes.txt")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(0, _promptCount);
    }

    [Fact]
    public async Task MutatingShellCommandPromptsInAutoMode()
    {
        var decision = await _gate.RequestAsync(Request("PowerShell", false, ("command", "dotnet build")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(1, _promptCount);
        Assert.Equal("dotnet build", _lastPrompt!.SubjectText);
    }

    [Fact]
    public async Task AFilePromptShowsTheRelativePathAndResolvesTheBoxToTheRealOne()
    {
        await _gate.RequestAsync(Request("Write", false, ("file_path", "src/app.cs")), CancellationToken.None);

        Assert.Equal(Path.Combine(_workspace, "src", "app.cs"), _lastPrompt!.SubjectText);
        Assert.Equal(Path.Combine("src", "app.cs"), _lastPrompt.Detail);
    }

    [Fact]
    public async Task APathThatEscapesTheWorkspaceIsShownInFullOnBothLines()
    {
        var outside = Path.Combine(Path.GetTempPath(), "jarvis-outside.txt");

        await _gate.RequestAsync(Request("Write", false, ("file_path", outside)), CancellationToken.None);

        Assert.Equal(outside, _lastPrompt!.SubjectText);
        Assert.Equal(outside, _lastPrompt.Detail);
        Assert.NotNull(_lastPrompt.Warning);
    }

    [Fact]
    public async Task DenyRuleWinsWithoutPrompting()
    {
        _gate.GlobalRuleLines = ["deny PowerShell *"];
        var decision = await _gate.RequestAsync(Request("PowerShell", false, ("command", "echo hi")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(0, _promptCount);
    }

    [Theory]
    [InlineData("deny", false)]
    [InlineData("ask", true)]
    public async Task Global_restriction_outranks_an_earlier_session_allow(string restriction, bool prompts)
    {
        _gate.ExtraRuleLines = ["allow Read notes.txt"];
        _gate.GlobalRuleLines = [restriction + " Read *.txt"];
        _promptAnswer = PermissionDecision.Deny;
        var decision = await _gate.RequestAsync(Request("Read", true, ("file_path", "notes.txt")), default);
        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(prompts ? 1 : 0, _promptCount);
    }

    [Fact]
    public async Task Sdk_allow_cannot_override_a_configured_deny()
    {
        _gate.GlobalRuleLines = ["deny Read *.txt"];
        _gate.SdkRuleLines = ["allow Read notes.txt"];
        Assert.Equal(PermissionDecision.Deny, await _gate.RequestAsync(Request("Read", true, ("file_path", "notes.txt")), default));
        Assert.Equal(0, _promptCount);
    }

    [Fact]
    public async Task AllowRuleSkipsPromptForStandardCalls()
    {
        _gate.GlobalRuleLines = ["allow PowerShell git *"];
        var decision = await _gate.RequestAsync(Request("PowerShell", false, ("command", "git commit -m x")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(0, _promptCount);
    }

    [Fact]
    public async Task AllowRuleDoesNotSilenceEscalatedCalls()
    {
        _gate.GlobalRuleLines = ["allow PowerShell *"];
        _promptAnswer = PermissionDecision.Deny;
        var decision = await _gate.RequestAsync(
            Request("PowerShell", false, ("command", $"rm -rf {Path.GetTempPath()}other")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(1, _promptCount);
        Assert.NotNull(_lastPrompt!.Warning);
    }

    [Fact]
    public async Task AllowAlwaysIsRememberedForTheSession()
    {
        _promptAnswer = PermissionDecision.AllowAlways;
        var request = Request("PowerShell", false, ("command", "npm run dev"));
        Assert.Equal(PermissionDecision.AllowAlways, await _gate.RequestAsync(request, CancellationToken.None));
        Assert.Equal(1, _promptCount);

        // Identical command: no second prompt.
        Assert.Equal(PermissionDecision.Allow, await _gate.RequestAsync(request, CancellationToken.None));
        Assert.Equal(1, _promptCount);

        // A different command still prompts.
        await _gate.RequestAsync(Request("PowerShell", false, ("command", "npm test")), CancellationToken.None);
        Assert.Equal(2, _promptCount);

        _gate.ResetSessionGrants();
        await _gate.RequestAsync(request, CancellationToken.None);
        Assert.Equal(3, _promptCount);
    }

    [Fact]
    public async Task ManualModeIgnoresAllowRulesAndGrants()
    {
        _gate.Mode = PermissionMode.Manual;
        _gate.GlobalRuleLines = ["allow Write *"];
        await _gate.RequestAsync(Request("Write", false, ("file_path", "a.txt"), ("content", "x")), CancellationToken.None);
        Assert.Equal(1, _promptCount);

        // Read-only calls stay silent even in Manual.
        await _gate.RequestAsync(Request("Grep", true, ("pattern", "foo")), CancellationToken.None);
        Assert.Equal(1, _promptCount);
    }

    [Fact]
    public async Task AcceptEditsWavesInWorkspaceEditsThrough()
    {
        _gate.Mode = PermissionMode.AcceptEdits;
        var decision = await _gate.RequestAsync(
            Request("Edit", false, ("file_path", "src.cs"), ("old_string", "a"), ("new_string", "b")),
            CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(0, _promptCount);

        // But an edit escaping the workspace still prompts.
        await _gate.RequestAsync(
            Request("Edit", false, ("file_path", @"C:\Windows\hosts"), ("old_string", "a"), ("new_string", "b")),
            CancellationToken.None);
        Assert.Equal(1, _promptCount);
    }

    [Fact]
    public async Task BypassAllowsEverythingButRespectsDenyRules()
    {
        _gate.Mode = PermissionMode.Bypass;
        Assert.Equal(
            PermissionDecision.Allow,
            await _gate.RequestAsync(Request("PowerShell", false, ("command", "format c:")), CancellationToken.None));
        Assert.Equal(0, _promptCount);

        _gate.GlobalRuleLines = ["deny PowerShell *"];
        Assert.Equal(
            PermissionDecision.Deny,
            await _gate.RequestAsync(Request("PowerShell", false, ("command", "echo x")), CancellationToken.None));
    }

    [Fact]
    public async Task NoPromptHandlerMeansDeny()
    {
        _gate.PromptAsync = null;
        var decision = await _gate.RequestAsync(Request("PowerShell", false, ("command", "npm i")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, decision);
    }

    [Fact]
    public async Task EditPromptCarriesADiffPreview()
    {
        var file = Path.Combine(_workspace, "hello.txt");
        await File.WriteAllTextAsync(file, "line one\nline two\n");
        await _gate.RequestAsync(
            Request("Edit", false, ("file_path", "hello.txt"), ("old_string", "line two"), ("new_string", "line 2")),
            CancellationToken.None);
        Assert.Equal(1, _promptCount);
        Assert.NotNull(_lastPrompt!.DiffPreview);
        Assert.Contains(_lastPrompt.DiffPreview!.Lines, static l => l.Kind == JarvisCode.Core.Utilities.DiffKind.Added);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }
    /// <summary>
    /// The reference's plan-mode rule: a non-read-only call that is not the plan
    /// file is asked about — "Cannot write to {path} while in plan mode." for a
    /// file, "Cannot call {tool} while in plan mode." for anything else — and
    /// nothing is remembered from the answer.
    /// </summary>
    [Fact]
    public async Task PlanModeAsksBeforeAnyWriteThatIsNotThePlanFile()
    {
        _gate.Mode = PermissionMode.Plan;
        _gate.PlanFilePath = Path.Combine(_workspace, ".jarvis", "plans", "s1.md");
        _promptAnswer = PermissionDecision.Deny;

        var decision = await _gate.RequestAsync(Request("Write", false, ("file_path", "src/app.cs")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(1, _promptCount);
        Assert.StartsWith("Cannot write to ", _lastPrompt!.Warning, StringComparison.Ordinal);
        Assert.EndsWith(" while in plan mode.", _lastPrompt.Warning, StringComparison.Ordinal);
        Assert.Equal(CallRisk.Escalated, _lastPrompt.Risk);
    }

    [Fact]
    public async Task PlanModeLetsThePlanFileThrough()
    {
        _gate.Mode = PermissionMode.Plan;
        _gate.PlanFilePath = Path.Combine(_workspace, ".jarvis", "plans", "s1.md");

        var decision = await _gate.RequestAsync(
            Request("Write", false, ("file_path", Path.Combine(".jarvis", "plans", "s1.md"))), CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(0, _promptCount);
    }

    [Fact]
    public async Task PlanModeAsksBeforeAnyOtherMutatingTool()
    {
        _gate.Mode = PermissionMode.Plan;
        _promptAnswer = PermissionDecision.Allow;

        var decision = await _gate.RequestAsync(Request("PowerShell", false, ("command", "git status")), CancellationToken.None);
        Assert.Equal(PermissionDecision.Allow, decision);
        Assert.Equal(1, _promptCount);
        Assert.Equal("Cannot call PowerShell while in plan mode.", _lastPrompt!.Warning);

        // The answer is for that call alone: the same call asks again.
        await _gate.RequestAsync(Request("PowerShell", false, ("command", "git status")), CancellationToken.None);
        Assert.Equal(2, _promptCount);
    }

    [Fact]
    public async Task PlanModeLeavesReadOnlyAndPlanModeToolsAlone()
    {
        _gate.Mode = PermissionMode.Plan;
        Assert.Equal(PermissionDecision.Allow,
            await _gate.RequestAsync(Request("Read", true, ("file_path", "notes.txt")), CancellationToken.None));
        // ExitPlanMode is read-only in this build, as the reference's is.
        Assert.Equal(PermissionDecision.Allow,
            await _gate.RequestAsync(Request("ExitPlanMode", true), CancellationToken.None));
        Assert.Equal(0, _promptCount);
    }
    private static PermissionRequest CallRequest(string tool, string callId, params (string Key, string Value)[] args)
    {
        var arguments = new JsonObject();
        foreach (var (key, value) in args)
        {
            arguments[key] = value;
        }

        return new PermissionRequest(new FakeTool(tool, false), arguments, tool, callId);
    }

    /// <summary>
    /// A rule denial is configuration, not the user saying no. Measured on CLI
    /// 2.1.257: a settings deny rule answered "Permission to use Bash with
    /// command echo hi has been denied." and both post-tool-result reminders
    /// still rode the turn, so the batching reminder must not stand down.
    /// </summary>
    [Fact]
    public async Task A_rule_denial_is_not_a_user_refusal()
    {
        _gate.GlobalRuleLines = ["deny PowerShell *"];
        var decision = await _gate.RequestAsync(
            CallRequest("PowerShell", "call-rule", ("command", "echo hi")), CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(0, _promptCount);
        Assert.False(_gate.TakeDenialWasUserRefusal("call-rule"));
    }

    /// <summary>The user was asked and said no — the case the reference does suppress on.</summary>
    [Fact]
    public async Task The_card_answering_no_is_a_user_refusal()
    {
        _promptAnswer = PermissionDecision.Deny;
        var decision = await _gate.RequestAsync(
            CallRequest("PowerShell", "call-user", ("command", "dotnet build")), CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.Equal(1, _promptCount);
        Assert.True(_gate.TakeDenialWasUserRefusal("call-user"));
        // Consumed on read, like the reason beside it.
        Assert.False(_gate.TakeDenialWasUserRefusal("call-user"));
    }

    /// <summary>A run with nobody to ask denies, and nobody refused.</summary>
    [Fact]
    public async Task A_denial_with_no_one_to_ask_is_not_a_user_refusal()
    {
        var gate = new UiPermissionGate { WorkingDirectory = _workspace };
        var decision = await gate.RequestAsync(
            CallRequest("PowerShell", "call-headless", ("command", "dotnet build")), CancellationToken.None);

        Assert.Equal(PermissionDecision.Deny, decision);
        Assert.False(gate.TakeDenialWasUserRefusal("call-headless"));
    }

}
