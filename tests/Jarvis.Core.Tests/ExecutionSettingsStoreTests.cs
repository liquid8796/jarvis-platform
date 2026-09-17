using Jarvis.Agent.Core;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class ExecutionSettingsStoreTests
{
    [Fact]
    public void Settings_are_persistent_revisioned_and_independent_of_enrollment_secrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            dynamic store = Create(Path.Combine(root, "execution-settings.json"));
            AgentExecutionSettings original = store.Load();
            Assert.Equal(5, original.MaxConcurrentCalls);
            AgentExecutionSettings saved = store.Save(original with { MaxConcurrentCalls = 11, MaxProcessJobs = 7 }, original.Revision);
            Assert.Equal(2, saved.Revision);
            AgentExecutionSettings reloaded = Create(Path.Combine(root, "execution-settings.json")).Load();
            Assert.Equal(saved, reloaded);
            var conflict = Assert.Throws<AgentRequestException>(() => store.Save(original with { MaxConcurrentCalls = 2 }, original.Revision));
            Assert.Equal("SETTINGS_REVISION_CONFLICT", conflict.Code);
            Assert.DoesNotContain("token", File.ReadAllText(Path.Combine(root, "execution-settings.json")), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Invalid_or_corrupt_settings_are_not_silently_replaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "execution-settings.json");
        try
        {
            dynamic store = Create(path);
            Assert.Throws<ArgumentException>(() => store.Save(new AgentExecutionSettings { MaxConcurrentCalls = 0 }, 1L));
            Assert.False(File.Exists(path));
            File.WriteAllText(path, "invalid-json");
            Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Throws<InvalidDataException>(() => store.Save(new AgentExecutionSettings(), 1L));
            Assert.Equal("invalid-json", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private static dynamic Create(string path)
    {
        var type = typeof(AgentConnection).Assembly.GetType("Jarvis.Agent.Core.Execution.ExecutionSettingsStore");
        Assert.NotNull(type);
        return Activator.CreateInstance(type, path)!;
    }
}
