namespace Jarvis.Agent.Core.Autonomous.Runtime;

public enum AgentState
{
    Created,
    Planning,
    Executing,
    WaitingTool,
    Verifying,
    Recovering,
    Completed,
    Failed
}

public sealed class AgentStateMachine
{
    public AgentState State { get; private set; } = AgentState.Created;

    public void MoveTo(AgentState next)
    {
        State = next;
    }
}
