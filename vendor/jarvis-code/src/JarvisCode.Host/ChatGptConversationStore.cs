using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JarvisCode.Core.Settings;

namespace JarvisCode.Host;

/// <summary>
/// Browser runtime state has its own files so independent desktop and CLI processes cannot
/// overwrite each other's settings or conversation mappings. Hold a scope lease while reading,
/// sending, invalidating and committing one conversation.
/// </summary>
public sealed class ChatGptConversationStore(string rootDirectory)
{
    private readonly string _root = Path.GetFullPath(rootDirectory);

    public async ValueTask<IAsyncDisposable?> AcquireScopeAsync(string scopeKey, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var path = ScopePath(scopeKey, ".lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                // A sharing/lock violation is another process holding the scope. Other filesystem
                // failures are real errors and must not turn into an infinite retry loop.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ChatGptChatEntry? Read(string scopeKey, IReadOnlyList<ChatGptChatEntry>? legacy = null)
    {
        var path = ScopePath(scopeKey, ".json");
        if (!File.Exists(path))
        {
            // Only the versioned mappings carry enough ownership information to migrate. Older
            // history-only keys are deliberately ignored and the local transcript is replayed.
            return legacy?.Where(entry => entry.ScopeKey == scopeKey && entry.Key.Length > 0 &&
                    entry.ToolContractHash.Length > 0)
                .OrderByDescending(entry => entry.UpdatedAt).FirstOrDefault();
        }

        try
        {
            var state = JsonSerializer.Deserialize<ConversationState>(File.ReadAllText(path));
            return state is { Version: 1, Chat: { } chat } && chat.ScopeKey == scopeKey ? chat : null;
        }
        catch (JsonException)
        {
            // A damaged state file is a fresh context, never a reason to reuse a stale legacy row.
            return null;
        }
    }

    public void Save(string scopeKey, ChatGptChatEntry? chat)
    {
        if (chat is not null && chat.ScopeKey != scopeKey)
            throw new ArgumentException("The conversation does not belong to this scope.", nameof(chat));
        // Keep a tombstone instead of deleting the file: another process may still hold legacy
        // settings from before invalidation, and must not migrate that old mapping a second time.
        WriteAtomically(ScopePath(scopeKey, ".json"), new ConversationState(1, chat));
    }

    public string ReadProjectId(string projectScopeKey)
    {
        var path = ScopePath(projectScopeKey, ".project.json");
        if (!File.Exists(path)) return "";
        try
        {
            // Readers do not take the cross-process scope lease on the verified in-memory fast
            // path. Share replacement/deletion so an atomic writer can still swap the target while
            // this handle keeps reading the old, complete file.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.SequentialScan);
            return JsonSerializer.Deserialize<ProjectState>(stream) is { Version: 1 } state
                ? state.ProjectId : "";
        }
        catch (JsonException) { return ""; }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
    }

    public void SaveProjectId(string projectScopeKey, string projectId) =>
        WriteAtomically(ScopePath(projectScopeKey, ".project.json"), new ProjectState(1, projectId));

    private string ScopePath(string scopeKey, string suffix) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey))).ToLowerInvariant() + suffix);

    private void WriteAtomically<T>(string path, T state)
    {
        Directory.CreateDirectory(_root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record ConversationState(int Version, ChatGptChatEntry? Chat);
    private sealed record ProjectState(int Version, string ProjectId);
}
