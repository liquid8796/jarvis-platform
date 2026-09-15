using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JarvisCode.Core.Models;
using JarvisCode.Core.Utilities;

namespace JarvisCode.Core.Agent;

/// <summary>Why a stored summary was not taken up (the reference's own reason set).</summary>
public enum SidecarRejection
{
    /// <summary>Nothing was stored. Not a rejection: the common case.</summary>
    Absent,

    TooLarge,
    ParseError,
    Version,
    SessionMismatch,
    ModelMismatch,
    BadTimestamp,
    TooOld,
    BoundaryMissing,
    GrewTooMuch,
    ShrankTooMuch,
    PreserveMissing,
}

/// <summary>One message, identified well enough to know it is still the same one.</summary>
public readonly record struct MessageFingerprint(int Index, string Hash);

/// <summary>What is written beside a session so a later process can reuse its summary.</summary>
public sealed record PrecompactPayload(
    int Version,
    string SessionId,
    string Model,
    string CliVersion,
    string CreatedAt,
    long PreCompactTokens,
    long ReadyDurationMs,
    string ContinuationText,
    string SummaryText,
    long SummaryInputTokens,
    long SummaryOutputTokens,
    MessageFingerprint Boundary,
    IReadOnlyList<MessageFingerprint> Preserve);

/// <summary>The outcome of writing one, with the size the reference reports beside it.</summary>
public sealed record SidecarWrite(bool Ok, long Bytes, string? Reason = null, string? Detail = null);

/// <summary>
/// The reference's precompact sidecar: a summary written ahead of time is
/// stored next to its session, so a process that starts later can compact
/// without paying for the summarization again. Read once per session, taken up
/// only while it still describes this conversation, and deleted the moment it
/// does not.
///
/// The reference identifies the messages it must still find by their uuids,
/// which this engine's <see cref="ChatMessage"/> does not carry. The invariant
/// is the same and is re-derived here from position plus a content hash: a
/// message that moved, changed or vanished no longer matches, which is exactly
/// what a missing uuid told the reference.
/// </summary>
public sealed class PrecompactSidecar(
    string sessionsDirectory, string sessionId, string modelId, string clientVersion)
{
    /// <summary>Numbering this file's own shape; the reference's 2 numbers its uuid-keyed one.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Largest sidecar that will be written or read (the reference's <c>O$</c>).</summary>
    public const long MaxBytes = 8_000_000;

    /// <summary>Past this age a stored summary is not taken up (its <c>vWn</c>, seven days).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(604_800_000);

    /// <summary>Growth past which the summary no longer covers enough (its <c>EWn</c>).</summary>
    public const long MaxGrowthTokens = 150_000;

    /// <summary>The directory name keeps sidecars out of the session glob.</summary>
    private const string DirectoryName = "precompact";

    /// <summary>Keeps one block's text from reading as the next one's.</summary>
    private const char Separator = '\u001f';

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public string Path { get; } =
        System.IO.Path.Combine(sessionsDirectory, DirectoryName, sessionId + ".json");

    /// <summary>
    /// A message's identity for this purpose: what it is and what it says. Two
    /// messages with the same content at the same index are the same message as
    /// far as a summary written over them is concerned.
    /// </summary>
    public static string Fingerprint(ChatMessage message)
    {
        var text = new StringBuilder().Append((int)message.Role);
        foreach (var block in message.Content)
        {
            text.Append(Separator).Append(block.GetType().Name).Append(Separator);
            text.Append(block switch
            {
                TextBlock t => t.Text,
                ToolCallBlock c => string.Join(Separator, c.Id, c.Name, c.ArgumentsJson),
                ToolResultBlock r => string.Join(Separator, r.ToolCallId, r.ToolName, r.Content),
                ThinkingBlock k => k.Thinking,
                ImageBlock i => string.Join(Separator, i.MediaType, i.Base64Data.Length),
                RawProviderBlock w => w.RawJson,
                _ => "",
            });
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    /// <summary>
    /// Stores <paramref name="summary"/> beside the session. The write is atomic
    /// — a crash mid-write must not leave a half file that reads as corrupt —
    /// and oversized payloads are refused rather than written.
    /// </summary>
    public SidecarWrite Write(
        PrecomputedSummary summary,
        IReadOnlyList<ChatMessage> snapshot,
        long preCompactTokens,
        long readyDurationMs)
    {
        if (summary.PrefixLength < 1 || summary.PrefixLength > snapshot.Count ||
            summary.Result.Messages.Count == 0)
        {
            return new SidecarWrite(false, 0, "boundary_out_of_range");
        }

        var payload = new PrecompactPayload(
            SchemaVersion,
            sessionId,
            modelId,
            clientVersion,
            DateTimeOffset.UtcNow.ToString("O"),
            preCompactTokens,
            readyDurationMs,
            summary.Result.Messages[0].GetText(),
            summary.Result.Summary,
            summary.Result.UsageSpent.InputTokens,
            summary.Result.UsageSpent.OutputTokens,
            new MessageFingerprint(
                summary.PrefixLength - 1, Fingerprint(snapshot[summary.PrefixLength - 1])),
            [.. snapshot.Skip(summary.PrefixLength)
                .Select((m, i) => new MessageFingerprint(summary.PrefixLength + i, Fingerprint(m)))]);

        string json = JsonSerializer.Serialize(payload, Json);
        long bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxBytes)
            return new SidecarWrite(false, bytes, "too_large");

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, json, Encoding.UTF8);
            RestrictToOwner(temporary);
            File.Move(temporary, Path, overwrite: true);
            return new SidecarWrite(true, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SidecarWrite(false, bytes, "write_error", ex.Message);
        }
    }

    /// <summary>
    /// Reads the stored summary and takes it up only when it still describes
    /// <paramref name="messages"/>; a null rejection means it was taken. Every
    /// refusal names itself, and the caller deletes the file for all of them
    /// except <see cref="SidecarRejection.Absent"/> — as the reference does, so
    /// a sidecar it cannot use never lingers.
    /// </summary>
    public (PrecomputedSummary? Summary, SidecarRejection? Rejection) Read(
        IReadOnlyList<ChatMessage> messages, DateTimeOffset now)
    {
        PrecompactPayload? payload;
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
                return (null, SidecarRejection.Absent);
            if (info.Length > MaxBytes)
                return (null, SidecarRejection.TooLarge);
            payload = JsonSerializer.Deserialize<PrecompactPayload>(File.ReadAllText(Path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, SidecarRejection.Absent);
        }
        catch (JsonException)
        {
            return (null, SidecarRejection.ParseError);
        }

        if (!IsWellFormed(payload))
            return (null, SidecarRejection.ParseError);
        if (payload!.Version != SchemaVersion)
            return (null, SidecarRejection.Version);
        if (payload.SessionId != sessionId)
            return (null, SidecarRejection.SessionMismatch);
        if (payload.Model != modelId)
            return (null, SidecarRejection.ModelMismatch);

        if (!DateTimeOffset.TryParse(
                payload.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var created))
        {
            return (null, SidecarRejection.BadTimestamp);
        }

        // A clock that moved backwards reads as age zero rather than as negative.
        var age = now - created;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age > MaxAge)
            return (null, SidecarRejection.TooOld);

        if (!Matches(messages, payload.Boundary))
            return (null, SidecarRejection.BoundaryMissing);

        long growth = TokenEstimator.Estimate(messages) - payload.PreCompactTokens;
        if (growth > MaxGrowthTokens)
            return (null, SidecarRejection.GrewTooMuch);
        if (growth < -(payload.PreCompactTokens / 2))
            return (null, SidecarRejection.ShrankTooMuch);

        if (payload.Preserve.Any(fingerprint => !Matches(messages, fingerprint)))
            return (null, SidecarRejection.PreserveMissing);

        int prefix = payload.Boundary.Index + 1;
        var restored = new PrecomputedSummary(
            new CompactionResult(
                [ChatMessage.FromUserText(payload.ContinuationText)],
                [],
                payload.SummaryText,
                new Usage(payload.SummaryInputTokens, payload.SummaryOutputTokens)),
            prefix,
            messages[payload.Boundary.Index]);
        return (restored, null);
    }

    /// <summary>Removes the stored summary; a file that is not there is already gone.</summary>
    public void Delete()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing depends on the delete succeeding: a sidecar that survives
            // is refused again on the next read.
        }
    }

    /// <summary>
    /// The reference's <c>yWn</c>: JSON that parses is not yet a payload. Every
    /// field the read path goes on to dereference is checked here, so a
    /// truncated or hand-edited file is refused rather than thrown on.
    /// </summary>
    private static bool IsWellFormed(PrecompactPayload? payload) =>
        payload is not null &&
        !string.IsNullOrEmpty(payload.SessionId) &&
        !string.IsNullOrEmpty(payload.Model) &&
        !string.IsNullOrEmpty(payload.CreatedAt) &&
        !string.IsNullOrEmpty(payload.ContinuationText) &&
        payload.SummaryText is not null &&
        payload.CliVersion is not null &&
        payload.Boundary.Hash is not null &&
        payload.Boundary.Index >= 0 &&
        payload.Preserve is not null &&
        payload.Preserve.All(static f => f.Hash is not null);

    private static bool Matches(IReadOnlyList<ChatMessage> messages, MessageFingerprint fingerprint) =>
        fingerprint.Index >= 0 &&
        fingerprint.Index < messages.Count &&
        Fingerprint(messages[fingerprint.Index]) == fingerprint.Hash;

    /// <summary>
    /// The reference writes the sidecar 0600. Windows has no such mode and the
    /// app-data directory is already per-user, so this is the POSIX half only.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
