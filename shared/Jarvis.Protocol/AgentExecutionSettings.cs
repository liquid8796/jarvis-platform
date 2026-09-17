namespace Jarvis.Protocol;

/// <summary>Locally chosen budgets. Tool concurrency is not a session count, OS sandbox or permission grant.</summary>
public sealed record AgentExecutionSettings
{
    public const string Capability = "execution-settings-v1";
    public int MaxConcurrentCalls { get; init; } = 5;
    public int MaxProcessJobs { get; init; } = 5;
    public int MaxDurableTasks { get; init; } = 5;
    public int MaxQueuedCalls { get; init; } = 100;
    public int QueueTimeoutSeconds { get; init; } = 60;
    public long Revision { get; init; } = 1;

    public void Validate()
    {
        if (MaxConcurrentCalls < 1 || MaxProcessJobs < 1 || MaxDurableTasks < 1)
            throw new ArgumentException("Concurrent calls, process jobs and durable tasks must be positive whole numbers.");
        if (MaxQueuedCalls is < 0 or > 100000)
            throw new ArgumentException("Queue capacity must be between 0 and 100000. Zero disables waiting.");
        if (QueueTimeoutSeconds is < 1 or > 240)
            throw new ArgumentException("Queue timeout must be between 1 and 240 seconds and remains bounded by the request deadline.");
        if (Revision < 1) throw new ArgumentException("Execution settings revision must be positive.");
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public long AdmissionCapacity => (long)MaxConcurrentCalls + MaxQueuedCalls;
}
