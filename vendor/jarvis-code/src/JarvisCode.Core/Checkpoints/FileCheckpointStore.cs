using System.Text.Json;

namespace JarvisCode.Core.Checkpoints;

/// <summary>
/// Persists per-turn file snapshots under {directory}/{sessionId}/turn-NNNN.json
/// so an edit made by the agent can be undone with /rewind. Shell-made changes
/// are invisible here and cannot be restored.
/// </summary>
public sealed class FileCheckpointStore(string directory)
{
    private const long MaxSnapshotBytes = 5 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public int NextTurnNumber(string sessionId)
    {
        var sessionDirectory = SessionDirectory(sessionId);
        if (!Directory.Exists(sessionDirectory))
            return 1;
        int max = 0;
        foreach (var file in Directory.GetFiles(sessionDirectory, "turn-*.json"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file).AsSpan("turn-".Length), out int turn))
                max = Math.Max(max, turn);
        }
        return max + 1;
    }

    public TurnCheckpoint BeginTurn(string sessionId, int turnNumber, string userPrompt, int messageIndexAtTurnStart)
    {
        var record = new CheckpointRecord
        {
            TurnNumber = turnNumber,
            UserPrompt = userPrompt.Length > 200 ? userPrompt[..200] : userPrompt,
            MessageIndexAtTurnStart = messageIndexAtTurnStart,
        };
        return new TurnCheckpoint(PathFor(sessionId, turnNumber), record);
    }

    public async Task<IReadOnlyList<CheckpointSummary>> ListAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var sessionDirectory = SessionDirectory(sessionId);
        if (!Directory.Exists(sessionDirectory))
            return [];
        var summaries = new List<CheckpointSummary>();
        foreach (var file in Directory.GetFiles(sessionDirectory, "turn-*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await TryReadAsync(file, cancellationToken);
            if (record is not null)
                summaries.Add(new CheckpointSummary(record.TurnNumber, record.UserPrompt, record.CreatedAt, record.Files.Count)
                { MessageIndexAtTurnStart = record.MessageIndexAtTurnStart });
        }
        return [.. summaries.OrderBy(s => s.TurnNumber)];
    }

    /// <summary>
    /// Restores every file captured at turn <paramref name="toTurnNumber"/> and later,
    /// newest first, then removes those checkpoints. Files that cannot be written are
    /// reported in <see cref="RewindResult.Skipped"/> rather than aborting the rest.
    /// </summary>
    public async Task<RewindResult?> RewindAsync(string sessionId, int toTurnNumber, CancellationToken cancellationToken = default)
    {
        var sessionDirectory = SessionDirectory(sessionId);
        if (!Directory.Exists(sessionDirectory))
            return null;

        var records = new List<CheckpointRecord>();
        foreach (var file in Directory.GetFiles(sessionDirectory, "turn-*.json"))
        {
            var record = await TryReadAsync(file, cancellationToken);
            if (record is not null && record.TurnNumber >= toTurnNumber)
                records.Add(record);
        }
        var target = records.FirstOrDefault(r => r.TurnNumber == toTurnNumber);
        if (target is null)
            return null;

        var restored = new List<string>();
        var skipped = new List<string>();
        foreach (var record in records.OrderByDescending(r => r.TurnNumber))
        {
            foreach (var snapshot in record.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (snapshot.Existed && snapshot.OriginalContent is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);
                        await File.WriteAllTextAsync(snapshot.Path, snapshot.OriginalContent, cancellationToken);
                        restored.Add(snapshot.Path);
                    }
                    else if (!snapshot.Existed)
                    {
                        if (File.Exists(snapshot.Path))
                            File.Delete(snapshot.Path);
                        restored.Add(snapshot.Path + " (deleted)");
                    }
                    else
                    {
                        skipped.Add(snapshot.Path + " (snapshot was too large to keep)");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add($"{snapshot.Path} ({ex.Message})");
                }
            }
        }

        foreach (var record in records)
        {
            var path = PathFor(sessionId, record.TurnNumber);
            if (File.Exists(path))
                File.Delete(path);
        }

        return new RewindResult(toTurnNumber, target.MessageIndexAtTurnStart, target.UserPrompt, restored, skipped);
    }

    /// <summary>Reads checkpoint contents for review without restoring or consuming them.</summary>
    public Task<CheckpointRecord?> ReadAsync(string sessionId, int turnNumber, CancellationToken cancellationToken = default) =>
        TryReadAsync(PathFor(sessionId, turnNumber), cancellationToken);

    private string SessionDirectory(string sessionId) => Path.Combine(directory, sessionId);

    private string PathFor(string sessionId, int turnNumber) =>
        Path.Combine(SessionDirectory(sessionId), $"turn-{turnNumber:D4}.json");

    private static async Task<CheckpointRecord?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return JsonSerializer.Deserialize<CheckpointRecord>(
                await File.ReadAllTextAsync(path, cancellationToken), SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Recorder for one turn; persists after each newly captured file.</summary>
    public sealed class TurnCheckpoint(string recordPath, CheckpointRecord record) : ICheckpointRecorder
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>True once at least one file was captured (an empty turn writes nothing).</summary>
        public bool HasCaptures { get; private set; }

        /// <summary>The turn this checkpoint belongs to, which is what a rewind names.</summary>
        public int TurnNumber => record.TurnNumber;

        public async Task RecordBeforeChangeAsync(string filePath, CancellationToken cancellationToken)
        {
            var fullPath = Path.GetFullPath(filePath);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (record.Files.Any(f => string.Equals(f.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
                    return;

                CheckpointFile snapshot;
                if (!File.Exists(fullPath))
                {
                    snapshot = new CheckpointFile(fullPath, Existed: false, OriginalContent: null);
                }
                else if (new FileInfo(fullPath).Length > MaxSnapshotBytes)
                {
                    snapshot = new CheckpointFile(fullPath, Existed: true, OriginalContent: null);
                }
                else
                {
                    snapshot = new CheckpointFile(
                        fullPath, Existed: true, await File.ReadAllTextAsync(fullPath, cancellationToken));
                }
                record.Files.Add(snapshot);
                HasCaptures = true;

                Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
                await File.WriteAllTextAsync(
                    recordPath, JsonSerializer.Serialize(record, SerializerOptions), cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
