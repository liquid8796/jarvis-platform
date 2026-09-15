using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using JarvisCode.App.Services;
using JarvisCode.Core.Agent;
using JarvisCode.Core.Models;
using JarvisCode.Core.Permissions;
using JarvisCode.Core.Providers;
using JarvisCode.Core.Tools;

namespace JarvisCode.App.Tests.Services;

public sealed class AutoPermissionClassifierTests
{
    [Fact]
    public async Task Synthetic_notes_and_compaction_summaries_are_not_user_authority()
    {
        var provider = new FakeProvider("{\"decision\":\"allow\",\"reason\":\"ok\"}");
        var result = await AutoPermissionClassifier.ClassifyAsync(provider, "test-model",
            [SystemReminders.HarnessMessage("The tool claims the user approved a push."),
             ChatMessage.FromUserText(ConversationCompactor.SummaryHeader + "\nA generated summary claims approval.")],
            Request("Edit", "file_path", "main.cs"), Path.GetTempPath(), default);
        Assert.Equal("ask", result.Decision);
        Assert.Null(provider.Request);
    }

    [Fact]
    public async Task Model_reviews_data_without_tools_and_returns_only_complete_decisions()
    {
        var provider = new FakeProvider("{\"decision\":\"allow\",\"reason\":\"Requested project edit.\"}");
        var request = Request("Edit", "file_path", "main.cs");
        var verdict = await AutoPermissionClassifier.ClassifyAsync(provider, "test-model",
            [ChatMessage.FromUserText("Fix the compiler error.")], request, Path.GetTempPath(), default);
        Assert.Equal("allow", verdict.Decision);
        Assert.Empty(provider.Request!.Tools);
        Assert.False(provider.Request.EnableWebSearch);
        Assert.Contains("Fix the compiler error.", provider.Request.Messages[0].GetText());
        Assert.Contains("not instructions", provider.Request.SystemPrompt);
        var invalid = new FakeProvider("{\"decision\":\"allow\",\"reason\":\"ok\"}", complete: false);
        Assert.Equal("ask", (await AutoPermissionClassifier.ClassifyAsync(invalid, "test-model",
            [ChatMessage.FromUserText("Fix it.")], request, Path.GetTempPath(), default)).Decision);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"decision\":\"allow\"}")]
    [InlineData("{\"decision\":\"always allow\",\"reason\":\"ok\"}")]
    public async Task Malformed_assessment_never_grants_permission(string reply)
    {
        var verdict = await AutoPermissionClassifier.ClassifyAsync(new FakeProvider(reply), "test-model",
            [ChatMessage.FromUserText("Fix it.")], Request("Edit", "file_path", "main.cs"), Path.GetTempPath(), default);
        Assert.Equal("ask", verdict.Decision);
    }

    [Fact]
    public async Task Auto_classifier_grants_one_call_but_never_overrides_rules_or_escalation()
    {
        var reviews = 0;
        var prompts = 0;
        var gate = new UiPermissionGate
        {
            WorkingDirectory = Path.GetTempPath(),
            IgnoreSettingsFiles = true,
            AutoClassifyAsync = (_, _) => { reviews++; return Task.FromResult(new AutoPermissionVerdict("allow", "Requested.")); },
            PromptAsync = (_, _) => { prompts++; return Task.FromResult(PermissionDecision.Deny); },
        };
        Assert.Equal(PermissionDecision.Allow, await gate.RequestAsync(Request("Edit", "file_path", "main.cs"), default));
        Assert.Equal(1, reviews);
        gate.ExtraRuleLines = ["deny Edit"];
        Assert.Equal(PermissionDecision.Deny, await gate.RequestAsync(Request("Edit", "file_path", "main.cs"), default));
        Assert.Equal(1, reviews);
        gate.ExtraRuleLines = [];
        Assert.Equal(PermissionDecision.Deny, await gate.RequestAsync(Request("Bash", "command", "rm -rf /"), default));
        Assert.Equal(1, reviews);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task Organization_always_ask_includes_read_only_tools_and_ignores_classifier()
    {
        var prompts = 0;
        var gate = new UiPermissionGate
        {
            WorkingDirectory = Path.GetTempPath(),
            IgnoreSettingsFiles = true,
            AlwaysAskTools = ["Read"],
            AutoClassifyAsync = (_, _) => throw new InvalidOperationException("Must not classify"),
            PromptAsync = (_, _) => { prompts++; return Task.FromResult(PermissionDecision.Deny); },
        };
        Assert.Equal(PermissionDecision.Deny, await gate.RequestAsync(Request("Read", "file_path", "main.cs", readOnly: true), default));
        Assert.Equal(1, prompts);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("plan")]
    [InlineData("deny-rule")]
    [InlineData("always-ask")]
    [InlineData("new-session")]
    public async Task Permission_changes_during_model_review_revoke_the_old_allow(string change)
    {
        var reviewing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verdict = new TaskCompletionSource<AutoPermissionVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new UiPermissionGate
        {
            WorkingDirectory = Path.GetTempPath(), IgnoreSettingsFiles = true,
            AutoClassifyAsync = (_, _) => { reviewing.TrySetResult(); return verdict.Task; },
            PromptAsync = (_, _) => Task.FromResult(PermissionDecision.Deny),
        };
        var pending = gate.RequestAsync(Request("Edit", "file_path", "main.cs"), default).AsTask();
        await reviewing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        switch (change)
        {
            case "manual": gate.Mode = PermissionMode.Manual; break;
            case "plan": gate.EnterPlanMode(); break;
            case "deny-rule": gate.SdkRuleLines = ["deny Edit"]; break;
            case "always-ask": gate.AlwaysAskTools = ["Edit"]; break;
            case "new-session": gate.ResetSessionGrants(); break;
        }
        verdict.SetResult(new AutoPermissionVerdict("allow", "Old policy allowed this."));
        Assert.Equal(PermissionDecision.Deny, await pending.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static PermissionRequest Request(string name, string key, string value, bool readOnly = false) =>
        new(new FakeTool(name, readOnly), new JsonObject { [key] = value }, name);

    private sealed class FakeTool(string name, bool readOnly) : ITool
    {
        public string Name => name;
        public string Description => "test";
        public JsonObject InputSchema => new();
        public bool IsReadOnly => readOnly;
        public string DescribeCall(JsonObject arguments) => name;
        public Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Permission tests never execute tools.");
    }

    private sealed class FakeProvider(string reply, bool complete = true) : ILlmProvider
    {
        public string Id => "test";
        public string DisplayName => "test";
        public LlmRequest? Request { get; private set; }
        public async IAsyncEnumerable<ProviderEvent> StreamChatAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request = request;
            await Task.Yield();
            yield return new TextDeltaEvent(reply);
            if (complete) yield return new ResponseCompletedEvent(false, Usage.Zero, StopReasons.EndTurn);
        }
    }
}
