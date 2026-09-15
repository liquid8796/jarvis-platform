namespace Jarvis.Agent.Core.Autonomous.Planning;

public sealed record TaskNode(string Id, string Description);

public sealed class TaskGraph
{
    private readonly List<TaskNode> _nodes = [];

    public IReadOnlyCollection<TaskNode> Nodes => _nodes;

    public void Add(TaskNode node) => _nodes.Add(node);
}

public interface ITaskPlanner
{
    TaskGraph Plan(string goal);
}

public sealed class TaskPlanner : ITaskPlanner
{
    public TaskGraph Plan(string goal)
    {
        var graph = new TaskGraph();
        graph.Add(new TaskNode("analyze", $"Analyze: {goal}"));
        graph.Add(new TaskNode("execute", "Execute required changes"));
        graph.Add(new TaskNode("verify", "Verify result"));
        return graph;
    }
}
