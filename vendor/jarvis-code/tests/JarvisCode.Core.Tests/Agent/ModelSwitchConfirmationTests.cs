using System.Text.Json.Nodes;
using JarvisCode.Core.Hooks;

namespace JarvisCode.Core.Tests.Agent;

public sealed class ModelSwitchConfirmationTests
{
    private static ModelSwitch Change => new("old", "new", "new", ModelSwitchSource.Picker, 100, true);
    private static HookDefinition Hook(string id) => new(HookEvent.PreModelSwitch, null, "", 10,
        HookKind.Callback, CallbackId: id);

    [Fact]
    public async Task Confirmation_applies_to_that_hook_only_and_does_not_skip_a_later_denial()
    {
        var calls = new List<string>();
        var runner = new HookRunner([], Path.GetTempPath()).WithSdkCallbacks([Hook("ask"), Hook("deny")],
            (hook, _, _, _) =>
            {
                calls.Add(hook.CallbackId!);
                return Task.FromResult<JsonObject?>(new JsonObject { ["permissionDecision"] = hook.CallbackId,
                    ["permissionDecisionReason"] = hook.CallbackId + " reason" });
            });
        var prompts = 0;
        var result = await runner.RunPreModelSwitchAsync(Change, default, (reason, _) =>
        { Assert.Equal("ask reason", reason); prompts++; return Task.FromResult(true); });
        Assert.False(result.Allowed);
        Assert.Equal("deny reason", result.BlockReason);
        Assert.Equal(1, prompts);
        Assert.Equal(new[] { "ask", "deny" }, calls);
    }

    [Fact]
    public async Task An_ask_is_allowed_only_after_explicit_confirmation()
    {
        var runner = new HookRunner([], Path.GetTempPath()).WithSdkCallbacks([Hook("ask")],
            (_, _, _, _) => Task.FromResult<JsonObject?>(new JsonObject { ["permissionDecision"] = "ask" }));
        Assert.False((await runner.RunPreModelSwitchAsync(Change, default)).Allowed);
        Assert.False((await runner.RunPreModelSwitchAsync(Change, default, (_, _) => Task.FromResult(false))).Allowed);
        Assert.True((await runner.RunPreModelSwitchAsync(Change, default, (_, _) => Task.FromResult(true))).Allowed);
    }
}
