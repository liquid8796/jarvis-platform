using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ExecutionSettingsTests
{
    [Fact]
    public void Defaults_are_five_and_empty_workspace_remains_valid()
    {
        var options = new AgentOptions("https://example.test", Guid.NewGuid().ToString(), "");
        var property = typeof(AgentOptions).GetProperty("ExecutionSettings");
        Assert.NotNull(property);
        dynamic settings = property.GetValue(options)!;
        Assert.Equal(5, (int)settings.MaxConcurrentCalls);
        Assert.Equal(5, (int)settings.MaxProcessJobs);
        Assert.Equal(5, (int)settings.MaxDurableTasks);
        settings.Validate();
        Assert.Equal("wss", options.ValidateAndGetWebSocketUri().Scheme);
    }

    [Fact]
    public void Limits_are_not_silently_clamped_to_four_or_five_and_invalid_values_fail()
    {
        var type = typeof(AgentSessionRules).Assembly.GetType("Jarvis.Protocol.AgentExecutionSettings");
        Assert.NotNull(type);
        dynamic large = JsonSerializer.Deserialize("{\"maxConcurrentCalls\":10000,\"maxProcessJobs\":200,\"maxDurableTasks\":100}", type, WireJson.Options)!;
        large.Validate();
        Assert.Equal(10000, (int)large.MaxConcurrentCalls);
        dynamic zero = JsonSerializer.Deserialize("{\"maxConcurrentCalls\":0}", type, WireJson.Options)!;
        Assert.Throws<ArgumentException>(() => { zero.Validate(); });
        dynamic negative = JsonSerializer.Deserialize("{\"maxQueuedCalls\":-1}", type, WireJson.Options)!;
        Assert.Throws<ArgumentException>(() => { negative.Validate(); });
    }
}
