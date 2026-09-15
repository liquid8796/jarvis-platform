using System.Text.Json;

namespace Jarvis.Agent.Core.Autonomous.Planning;

public sealed record AdaptiveAction(
    string Id,
    string ToolId,
    JsonElement Arguments,
    IReadOnlyList<string> DependsOn,
    string Stage = "EXECUTE");

public sealed record AdaptivePlan(IReadOnlyList<AdaptiveAction> Actions, int MaxRepairs = 2)
{
    private static readonly string[] Stages = ["EXECUTE", "BUILD", "TEST", "PACKAGE", "VERIFY"];

    public AdaptivePlan Validate()
    {
        if (Actions is null || Actions.Count is < 1 or > 32) throw new ArgumentException("Adaptive plan must contain 1..32 actions.");
        if (MaxRepairs is < 0 or > 3) throw new ArgumentException("Adaptive repair budget must be 0..3.");
        var map = new Dictionary<string, AdaptiveAction>(StringComparer.Ordinal);
        foreach (var action in Actions)
        {
            if (action is null || string.IsNullOrWhiteSpace(action.Id) || action.Id.Length > 100 ||
                string.IsNullOrWhiteSpace(action.ToolId) || action.ToolId.Length > 100 || action.Arguments.ValueKind != JsonValueKind.Object ||
                Array.IndexOf(Stages, action.Stage) < 0)
                throw new ArgumentException("Adaptive action ID, tool, arguments or stage is invalid.");
            if (!map.TryAdd(action.Id, action)) throw new ArgumentException("Adaptive plan contains duplicate action IDs.");
        }
        foreach (var action in Actions)
            foreach (var dependency in action.DependsOn ?? [])
                if (!map.ContainsKey(dependency) || StringComparer.Ordinal.Equals(dependency, action.Id))
                    throw new ArgumentException("Adaptive plan contains a missing or self dependency.");

        var remaining = map.Keys.ToHashSet(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => (map[id].DependsOn ?? []).All(completed.Contains)).ToArray();
            if (ready.Length == 0) throw new ArgumentException("Adaptive plan contains a dependency cycle.");
            foreach (var id in ready) { remaining.Remove(id); completed.Add(id); }
        }
        return this;
    }
}
