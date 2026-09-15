using JarvisCode.Core.Agent;

namespace JarvisCode.App.Services;

/// <summary>
/// The live progress of the workflow runs the Background tasks pane lists,
/// keyed by the background-task id the run was adopted under. The engine feeds
/// each run's phases, agents and log lines here; the pane reads them back as
/// the reference's progress groups.
/// </summary>
public sealed class WorkflowRuns
{
    private readonly Dictionary<string, WorkflowProgressFeed> _feeds = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Starts a feed for a run, replacing any feed left under that id.</summary>
    public WorkflowProgressFeed Open(string taskId)
    {
        var feed = new WorkflowProgressFeed();
        lock (_gate)
        {
            _feeds[taskId] = feed;
        }

        return feed;
    }

    public WorkflowProgressFeed? For(string taskId)
    {
        lock (_gate)
        {
            return _feeds.GetValueOrDefault(taskId);
        }
    }

    /// <summary>Every live feed, for one pass over the pane's rows.</summary>
    public IReadOnlyDictionary<string, WorkflowProgressFeed> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, WorkflowProgressFeed>(_feeds, StringComparer.Ordinal);
        }
    }

    /// <summary>Drops a run's feed — its row was cleared from the pane.</summary>
    public void Forget(string taskId)
    {
        lock (_gate)
        {
            _feeds.Remove(taskId);
        }
    }
}
