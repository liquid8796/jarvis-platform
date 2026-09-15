using Jarvis.Protocol;

namespace Jarvis.Agent.Core.RemoteTasks;

public sealed record RemoteTaskLineage(string? ParentTaskId, string RootTaskId, int Depth);

public static class RemoteTaskDelegation
{
    public const int MaxDepth = 3;
    public const int MaxChildren = 8;

    public static RemoteTaskLineage Root(string taskId) =>
        new(null, RemoteTaskRules.TaskId(taskId), 0);

    internal static RemoteTaskLineage Child(StoredRemoteTask parent, RemoteTaskPlan child, string resolvedProject)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        if (!StringComparer.Ordinal.Equals(parent.Snapshot.Project, resolvedProject))
            throw new ArgumentException("Child task must inherit the parent project.");
        if (Rank(child.ExecutionMode) > Rank(parent.Plan.ExecutionMode))
            throw new ArgumentException("Child executionMode cannot be broader than the parent task.");
        var depth = checked(parent.Snapshot.Depth + 1);
        if (depth > MaxDepth) throw new ArgumentException($"Child task depth cannot exceed {MaxDepth}.");
        var root = parent.Snapshot.RootTaskId ?? parent.Snapshot.TaskId;
        return new(parent.Snapshot.TaskId, root, depth);
    }

    public static async Task<IReadOnlyDictionary<string, RemoteTaskSnapshot>> JoinAsync(
        IEnumerable<string> childTaskIds,
        Func<string, CancellationToken, Task<RemoteTaskSnapshot?>> fetch,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(childTaskIds);
        ArgumentNullException.ThrowIfNull(fetch);
        if (pollInterval < TimeSpan.Zero || pollInterval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        var ids = childTaskIds.Select(RemoteTaskRules.TaskId).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length is < 1 or > MaxChildren)
            throw new ArgumentException($"Delegation join must contain 1..{MaxChildren} unique child tasks.", nameof(childTaskIds));

        var terminal = new Dictionary<string, RemoteTaskSnapshot>(StringComparer.Ordinal);
        while (terminal.Count < ids.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var id in ids)
            {
                if (terminal.ContainsKey(id)) continue;
                var snapshot = await fetch(id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Delegated child task was not found: " + id);
                if (RemoteTaskRules.IsTerminal(snapshot.Status)) terminal[id] = snapshot;
            }
            if (terminal.Count == ids.Length) break;
            if (pollInterval > TimeSpan.Zero)
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            else
                await Task.Yield();
        }
        return terminal;
    }

    private static int Rank(string mode) => mode switch
    {
        "READ_ONLY" => 0,
        "NORMAL" => 1,
        "AUTONOMOUS" => 2,
        _ => throw new ArgumentException("Unknown execution mode: " + mode)
    };
}
