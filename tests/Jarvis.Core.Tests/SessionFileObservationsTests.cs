using Jarvis.Agent.Core.Execution;
using Jarvis.Protocol;

namespace Jarvis.Core.Tests;

public sealed class SessionFileObservationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jarvis-file-revisions-" + Guid.NewGuid().ToString("N"));
    private readonly AgentSessionIdentity _a = new("owner", "device", AgentSessionRules.NewSessionId());
    private readonly SessionFileObservations _observations = new();
    public SessionFileObservationsTests() => Directory.CreateDirectory(_root);
    [Fact]
    public async Task A_stale_read_cannot_overwrite_B_even_if_the_target_text_still_exists()
    {
        var path = Path.Combine(_root, "file.txt"); await File.WriteAllTextAsync(path,"same target\nold value");
        await _observations.RememberAsync(_a, path, default);
        await File.WriteAllTextAsync(path,"same target\nB changed another line");
        var ex=await Assert.ThrowsAsync<AgentRequestException>(() => _observations.ValidateWriteAsync(_a,path,default));
        Assert.Equal("FILE_CHANGED", ex.Code);
        Assert.Contains("B changed",await File.ReadAllTextAsync(path));
        await _observations.RememberAsync(_a,path,default);
        await _observations.ValidateWriteAsync(_a,path,default);
    }
    [Fact]
    public async Task Existing_file_requires_this_sessions_own_read_not_another_sessions_observation()
    {
        var path=Path.Combine(_root,"file.txt"); await File.WriteAllTextAsync(path,"existing");
        await _observations.RememberAsync(_a,path,default);
        var ex=await Assert.ThrowsAsync<AgentRequestException>(()=>_observations.ValidateWriteAsync(_a with {SessionId=AgentSessionRules.NewSessionId()},path,default));
        Assert.Equal("FILE_READ_REQUIRED",ex.Code);
    }
    [Fact]
    public async Task Creation_is_allowed_but_delete_after_read_is_not_silent_recreation()
    {
        var path=Path.Combine(_root,"new.txt"); await _observations.ValidateWriteAsync(_a,path,default);
        await File.WriteAllTextAsync(path,"created"); await _observations.RememberAsync(_a,path,default);
        File.Delete(path);
        var ex=await Assert.ThrowsAsync<AgentRequestException>(()=>_observations.ValidateWriteAsync(_a,path,default));
        Assert.Equal("FILE_CHANGED",ex.Code);
    }
    [Fact]
    public async Task Forget_discards_only_the_closed_sessions_observations()
    {
        var path=Path.Combine(_root,"file.txt"); await File.WriteAllTextAsync(path,"existing");
        var b=_a with {SessionId=AgentSessionRules.NewSessionId()};
        await _observations.RememberAsync(_a,path,default); await _observations.RememberAsync(b,path,default);
        _observations.Forget(_a);
        await Assert.ThrowsAsync<AgentRequestException>(()=>_observations.ValidateWriteAsync(_a,path,default));
        await _observations.ValidateWriteAsync(b,path,default);
    }
    public void Dispose() => Directory.Delete(_root,true);
}
