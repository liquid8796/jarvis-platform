using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Protocol;

namespace Jarvis.Agent.Core.Auditing;

public sealed record AgentAuditRecord(
    DateTimeOffset TimestampUtc,
    string Code,
    string Message,
    string? ToolId = null,
    string? SessionId = null,
    string? CallId = null,
    int? ProcessId = null,
    string? Category = null);

public interface IAgentAuditSink
{
    void Write(AgentAuditRecord record);
}

public static class AgentAuditSinkExtensions
{
    public static void Write(this IAgentAuditSink sink, string code, string category, string message,
        string? toolId = null, string? sessionId = null, string? callId = null, int? processId = null) =>
        sink.Write(new AgentAuditRecord(DateTimeOffset.UtcNow, code, message, toolId, sessionId, callId,
            processId, category));
}

public interface IAgentAuditReader
{
    IReadOnlyList<AgentAuditRecord> Query(int limit, string? code = null, string? toolId = null, string? sessionId = null);
}

public sealed class NullAgentAuditSink : IAgentAuditSink
{
    public static NullAgentAuditSink Instance { get; } = new();
    private NullAgentAuditSink() { }
    public void Write(AgentAuditRecord record) { }
}

public sealed class FileAgentAuditLog : IAgentAuditSink, IAgentAuditReader, IDisposable
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxFilesPerDay = 10;
    private const int RetentionDays = 14;
    private readonly string _directory;
    private readonly object _sync = new();
    private bool _disposed;

    public FileAgentAuditLog(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        PruneExpiredFiles();
    }

    public void Write(AgentAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var safe = Redact(record);
        lock (_sync)
        {
            if (_disposed) return;
            try
            {
                Directory.CreateDirectory(_directory);
                var path = CurrentPath(safe.TimestampUtc);
                if (path is null) return;
                File.AppendAllText(path, JsonSerializer.Serialize(safe, WireJson.Options) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public IReadOnlyList<AgentAuditRecord> Query(int limit, string? code = null, string? toolId = null, string? sessionId = null)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = new List<AgentAuditRecord>();
            var sessionKey = SessionKey(sessionId);
            foreach (var file in Directory.EnumerateFiles(_directory, "audit-*.jsonl").OrderByDescending(path => path, StringComparer.Ordinal))
            {
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                for (var index = lines.Length - 1; index >= 0 && result.Count < limit; index--)
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<AgentAuditRecord>(lines[index], WireJson.Options);
                        if (record is null ||
                            code is not null && !StringComparer.OrdinalIgnoreCase.Equals(record.Code, code) ||
                            toolId is not null && !StringComparer.Ordinal.Equals(record.ToolId, toolId) ||
                            sessionKey is not null && !StringComparer.Ordinal.Equals(record.SessionId, sessionKey)) continue;
                        result.Add(record);
                    }
                    catch (JsonException) { }
                }
                if (result.Count >= limit) break;
            }
            return result;
        }
    }

    public void Dispose() { lock (_sync) _disposed = true; }

    private static AgentAuditRecord Redact(AgentAuditRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Code) || record.Code.Length > 80 ||
            record.Code.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("Invalid audit code.");
        var message = (record.Message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length > 1000) message = message[..1000];
        return record with
        {
            TimestampUtc = record.TimestampUtc.ToUniversalTime(),
            Message = message,
            ToolId = Bound(record.ToolId, 256),
            SessionId = SessionKey(record.SessionId),
            CallId = Bound(record.CallId, 128),
            Category = Bound(record.Category, 80)
        };
    }

    private static string? Bound(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : value.Length <= maximum ? value : value[..maximum];

    private void PruneExpiredFiles()
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "audit-*.jsonl"))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string? CurrentPath(DateTimeOffset timestamp)
    {
        var date = timestamp.UtcDateTime.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var basePath = Path.Combine(_directory, $"audit-{date}.jsonl");
        if (!File.Exists(basePath) || new FileInfo(basePath).Length < MaxFileBytes) return basePath;
        for (var index = 1; index < MaxFilesPerDay; index++)
        {
            var candidate = Path.Combine(_directory, $"audit-{date}-{index:00}.jsonl");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < MaxFileBytes) return candidate;
        }
        return null;
    }

    private static string? SessionKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
