using Jarvis.Protocol;

namespace Jarvis.Agent.Core;

public sealed partial class AgentConnection
{
    private UserPromptContext? _userPromptContext;
    private volatile bool _promptContextProtocol;
    public bool ServerSupportsPromptContext => _promptContextProtocol;

    /// <summary>Replace optional context only. Does not alter the gate, registry, permissions or running tasks.</summary>
    public void ConfigurePromptContext(UserPromptContext? context)
    {
        UserPromptContext? snapshot = null;
        if (context is not null)
        {
            context.Validate();
            snapshot = new(context.Revision, Array.AsReadOnly(context.Prompts.ToArray()));
            snapshot.Validate();
        }
        Volatile.Write(ref _userPromptContext, snapshot);
    }

    private UserPromptContext? OutboundPromptContext(bool isError) =>
        isError || !_promptContextProtocol ? null : Volatile.Read(ref _userPromptContext);
}
