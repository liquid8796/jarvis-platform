using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Protocol;
using Microsoft.Data.Sqlite;

namespace Jarvis.Agent.Core.Artifacts;

public sealed record ArtifactRuntimeRecord(
    string ArtifactId,
    string OwnerId,
    string DeviceId,
    string SessionId,
    string Title,
    string Kind,
    string? Content,
    string MetadataJson,
    long Revision,
    string ContentSha256,
    bool Deleted,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string Uri => $"jarvis-artifact://{ArtifactId}?revision={Revision}";
}

public sealed record ArtifactRuntimeLimits(
    int MaxArtifactsPerSession = 128,
    int MaxTitleChars = 240,
    int MaxContentChars = 2_000_000,
    int MaxMetadataChars = 64_000,
    int MaxListItems = 200);

/// <summary>
/// Durable, session-owned artifact documents with optimistic revisions. The store never trusts an
/// artifact ID by itself: every read and write also matches owner, enrolled device and Jarvis session.
/// </summary>
public sealed class SqliteArtifactRuntimeStore : IDisposable
{
    public static readonly IReadOnlySet<string> SupportedKinds =
        new HashSet<string>(["html", "markdown", "svg", "json", "text"], StringComparer.Ordinal);

    private readonly string _connectionString;
    private readonly ArtifactRuntimeLimits _limits;
    private int _disposed;

    public SqliteArtifactRuntimeStore(string databasePath, ArtifactRuntimeLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _limits = limits ?? new ArtifactRuntimeLimits();
        if (_limits.MaxArtifactsPerSession < 1 || _limits.MaxTitleChars < 1 ||
            _limits.MaxContentChars < 1 || _limits.MaxMetadataChars < 2 || _limits.MaxListItems < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public ArtifactRuntimeRecord Create(AgentSessionIdentity identity, string title, string kind,
        string content, string? metadataJson)
    {
        ThrowIfDisposed();
        title = ValidateTitle(title);
        kind = ValidateKind(kind);
        content = ValidateContent(content);
        ValidateFormat(kind, content);
        var metadata = ValidateMetadata(metadataJson);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM artifacts WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND deleted=0;";
            AddIdentity(count, identity);
            var active = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (active >= _limits.MaxArtifactsPerSession)
                throw new AgentRequestException("ARTIFACT_LIMIT",
                    $"This session already has {_limits.MaxArtifactsPerSession} active artifacts.");
        }
        var id = "artifact_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var hash = Hash(content);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO artifacts(artifact_id,owner_id,device_id,session_id,title,kind,content,metadata_json,revision,content_sha256,deleted,created_utc,updated_utc)
                VALUES($id,$owner,$device,$session,$title,$kind,$content,$metadata,1,$hash,0,$now,$now);
                """;
            command.Parameters.AddWithValue("$id", id);
            AddIdentity(command, identity);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$metadata", metadata);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.ExecuteNonQuery();
        }
        AppendEvent(connection, transaction, id, 1, "artifact.created", JsonSerializer.Serialize(new
        {
            title, kind, contentSha256 = hash
        }, WireJson.Options), now);
        transaction.Commit();
        return new(id, identity.OwnerId, identity.DeviceId, identity.SessionId, title, kind, content,
            metadata, 1, hash, false, now, now);
    }

    public ArtifactRuntimeRecord Update(AgentSessionIdentity identity, string artifactId, long expectedRevision,
        string? title, string? kind, string? content, string? metadataJson)
    {
        ThrowIfDisposed();
        ValidateArtifactId(artifactId);
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (title is null && kind is null && content is null && metadataJson is null)
            throw new ArgumentException("At least one artifact field must be supplied.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = Read(connection, transaction, identity, artifactId, includeContent: true, includeDeleted: false);
        if (current.Revision != expectedRevision)
            throw new AgentRequestException("ARTIFACT_REVISION_CONFLICT",
                $"Artifact revision changed from {expectedRevision} to {current.Revision}. Read it again and reconcile the update.");
        var nextTitle = title is null ? current.Title : ValidateTitle(title);
        var nextKind = kind is null ? current.Kind : ValidateKind(kind);
        var nextContent = content is null ? current.Content! : ValidateContent(content);
        ValidateFormat(nextKind, nextContent);
        var nextMetadata = metadataJson is null ? current.MetadataJson : ValidateMetadata(metadataJson);
        var revision = current.Revision + 1;
        var now = DateTimeOffset.UtcNow;
        var hash = Hash(nextContent);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE artifacts SET title=$title,kind=$kind,content=$content,metadata_json=$metadata,
                    revision=$next,content_sha256=$hash,updated_utc=$now
                WHERE artifact_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session
                    AND revision=$expected AND deleted=0;
                """;
            command.Parameters.AddWithValue("$title", nextTitle);
            command.Parameters.AddWithValue("$kind", nextKind);
            command.Parameters.AddWithValue("$content", nextContent);
            command.Parameters.AddWithValue("$metadata", nextMetadata);
            command.Parameters.AddWithValue("$next", revision);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$id", artifactId);
            AddIdentity(command, identity);
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (command.ExecuteNonQuery() != 1)
                throw new AgentRequestException("ARTIFACT_REVISION_CONFLICT",
                    "Artifact changed concurrently. Read it again and reconcile the update.");
        }
        AppendEvent(connection, transaction, artifactId, revision, "artifact.updated", JsonSerializer.Serialize(new
        {
            previousRevision = expectedRevision,
            titleChanged = title is not null,
            kindChanged = kind is not null,
            contentChanged = content is not null,
            metadataChanged = metadataJson is not null,
            contentSha256 = hash
        }, WireJson.Options), now);
        transaction.Commit();
        return current with
        {
            Title = nextTitle, Kind = nextKind, Content = nextContent, MetadataJson = nextMetadata,
            Revision = revision, ContentSha256 = hash, UpdatedAt = now
        };
    }

    public ArtifactRuntimeRecord Get(AgentSessionIdentity identity, string artifactId,
        bool includeContent = true, bool includeDeleted = false)
    {
        ThrowIfDisposed();
        ValidateArtifactId(artifactId);
        using var connection = Open();
        return Read(connection, null, identity, artifactId, includeContent, includeDeleted);
    }

    public IReadOnlyList<ArtifactRuntimeRecord> List(AgentSessionIdentity identity, bool includeDeleted, int limit)
    {
        ThrowIfDisposed();
        if (limit is < 1 || limit > _limits.MaxListItems) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = includeDeleted
            ? "SELECT artifact_id,owner_id,device_id,session_id,title,kind,NULL,metadata_json,revision,content_sha256,deleted,created_utc,updated_utc FROM artifacts WHERE owner_id=$owner AND device_id=$device AND session_id=$session ORDER BY updated_utc DESC LIMIT $limit;"
            : "SELECT artifact_id,owner_id,device_id,session_id,title,kind,NULL,metadata_json,revision,content_sha256,deleted,created_utc,updated_utc FROM artifacts WHERE owner_id=$owner AND device_id=$device AND session_id=$session AND deleted=0 ORDER BY updated_utc DESC LIMIT $limit;";
        AddIdentity(command, identity);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<ArtifactRuntimeRecord>();
        while (reader.Read()) result.Add(ReadRecord(reader));
        return result;
    }

    public ArtifactRuntimeRecord Delete(AgentSessionIdentity identity, string artifactId, long expectedRevision)
    {
        ThrowIfDisposed();
        ValidateArtifactId(artifactId);
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = Read(connection, transaction, identity, artifactId, includeContent: false, includeDeleted: false);
        if (current.Revision != expectedRevision)
            throw new AgentRequestException("ARTIFACT_REVISION_CONFLICT",
                $"Artifact revision changed from {expectedRevision} to {current.Revision}.");
        var revision = expectedRevision + 1;
        var now = DateTimeOffset.UtcNow;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE artifacts SET deleted=1,content='',metadata_json='{}',revision=$next,
                    content_sha256=$hash,updated_utc=$now
                WHERE artifact_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session
                    AND revision=$expected AND deleted=0;
                """;
            command.Parameters.AddWithValue("$next", revision);
            command.Parameters.AddWithValue("$hash", Hash(string.Empty));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$id", artifactId);
            AddIdentity(command, identity);
            command.Parameters.AddWithValue("$expected", expectedRevision);
            if (command.ExecuteNonQuery() != 1)
                throw new AgentRequestException("ARTIFACT_REVISION_CONFLICT",
                    "Artifact changed concurrently. Read it again before deleting it.");
        }
        AppendEvent(connection, transaction, artifactId, revision, "artifact.deleted", "{}", now);
        transaction.Commit();
        return current with
        {
            Content = null, MetadataJson = "{}", Revision = revision, ContentSha256 = Hash(string.Empty),
            Deleted = true, UpdatedAt = now
        };
    }

    private void Initialize()
    {
        using var connection = OpenCore();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS artifacts(
                artifact_id TEXT PRIMARY KEY,
                owner_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                title TEXT NOT NULL,
                kind TEXT NOT NULL,
                content TEXT NOT NULL,
                metadata_json TEXT NOT NULL,
                revision INTEGER NOT NULL,
                content_sha256 TEXT NOT NULL,
                deleted INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS artifact_events(
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                artifact_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                kind TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY(artifact_id) REFERENCES artifacts(artifact_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_artifacts_scope_updated
                ON artifacts(owner_id,device_id,session_id,updated_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_artifact_events_artifact_revision
                ON artifact_events(artifact_id,revision,event_id);
            """;
        command.ExecuteNonQuery();
    }

    private ArtifactRuntimeRecord Read(SqliteConnection connection, SqliteTransaction? transaction,
        AgentSessionIdentity identity, string artifactId, bool includeContent, bool includeDeleted)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = includeDeleted
            ? $"SELECT artifact_id,owner_id,device_id,session_id,title,kind,{(includeContent ? "content" : "NULL")},metadata_json,revision,content_sha256,deleted,created_utc,updated_utc FROM artifacts WHERE artifact_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session LIMIT 1;"
            : $"SELECT artifact_id,owner_id,device_id,session_id,title,kind,{(includeContent ? "content" : "NULL")},metadata_json,revision,content_sha256,deleted,created_utc,updated_utc FROM artifacts WHERE artifact_id=$id AND owner_id=$owner AND device_id=$device AND session_id=$session AND deleted=0 LIMIT 1;";
        command.Parameters.AddWithValue("$id", artifactId);
        AddIdentity(command, identity);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Artifact was not found in this session.");
        return ReadRecord(reader);
    }

    private static ArtifactRuntimeRecord ReadRecord(SqliteDataReader reader)
    {
        var deleted = reader.GetBoolean(10);
        return new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), deleted || reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7), reader.GetInt64(8), reader.GetString(9), deleted,
            ParseTime(reader.GetString(11)), ParseTime(reader.GetString(12)));
    }

    private static void AppendEvent(SqliteConnection connection, SqliteTransaction transaction,
        string artifactId, long revision, string kind, string payload, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO artifact_events(artifact_id,revision,kind,payload_json,created_utc) VALUES($id,$revision,$kind,$payload,$now);";
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private string ValidateTitle(string title)
    {
        title = title?.Trim() ?? string.Empty;
        if (title.Length is < 1 || title.Length > _limits.MaxTitleChars)
            throw new ArgumentException($"Artifact title must contain 1..{_limits.MaxTitleChars} characters.");
        return title;
    }

    private string ValidateContent(string content)
    {
        if (content is null || content.Length > _limits.MaxContentChars)
            throw new ArgumentException($"Artifact content must contain at most {_limits.MaxContentChars} characters.");
        return content;
    }

    private static string ValidateKind(string kind)
    {
        kind = kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!SupportedKinds.Contains(kind))
            throw new ArgumentException("Artifact kind must be html, markdown, svg, json, or text.");
        return kind;
    }

    private static void ValidateFormat(string kind, string content)
    {
        if (kind != "json") return;
        try { using var _ = JsonDocument.Parse(content); }
        catch (JsonException ex) { throw new ArgumentException("JSON artifact content must be valid JSON.", ex); }
    }

    private string ValidateMetadata(string? metadata)
    {
        metadata = string.IsNullOrWhiteSpace(metadata) ? "{}" : metadata;
        if (metadata.Length > _limits.MaxMetadataChars)
            throw new ArgumentException($"Artifact metadata must contain at most {_limits.MaxMetadataChars} characters.");
        try
        {
            using var document = JsonDocument.Parse(metadata);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Artifact metadata must be a JSON object.");
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex) { throw new ArgumentException("Artifact metadata must be valid JSON.", ex); }
    }

    private static void ValidateArtifactId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^artifact_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Artifact ID is invalid.");
    }

    private static void AddIdentity(SqliteCommand command, AgentSessionIdentity identity)
    {
        command.Parameters.AddWithValue("$owner", identity.OwnerId);
        command.Parameters.AddWithValue("$device", identity.DeviceId);
        command.Parameters.AddWithValue("$session", identity.SessionId);
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private SqliteConnection Open() { ThrowIfDisposed(); return OpenCore(); }
    private SqliteConnection OpenCore()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

public static class ArtifactRenderer
{
    private const string Csp = "default-src 'none'; img-src data: blob:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'none'; media-src data: blob:; font-src data:; frame-src 'none'; child-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
    private static readonly Regex BaseTag = new("<\\s*base\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RefreshMeta = new("<\\s*meta\\b[^>]*http-equiv\\s*=\\s*['\"]?refresh['\"]?[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static WidgetArtifact Render(ArtifactRuntimeRecord artifact)
    {
        if (artifact.Deleted || artifact.Content is null) throw new InvalidOperationException("Deleted artifact cannot be rendered.");
        var body = artifact.Kind switch
        {
            "html" => NormalizeHtml(artifact.Content, artifact.Title),
            "markdown" => Document(artifact.Title, Markdown(artifact.Content)),
            "svg" => Document(artifact.Title, SvgImage(artifact.Content)),
            "json" => Document(artifact.Title, Pre(PrettyJson(artifact.Content))),
            _ => Document(artifact.Title, Pre(artifact.Content))
        };
        return new WidgetArtifact(artifact.Title, body);
    }

    private static string NormalizeHtml(string html, string title)
    {
        html = BaseTag.Replace(html, string.Empty);
        html = RefreshMeta.Replace(html, string.Empty);
        var meta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{WebUtility.HtmlEncode(Csp)}\"><meta name=\"referrer\" content=\"no-referrer\">";
        if (Regex.IsMatch(html, "<\\s*head\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return Regex.Replace(html, "(<\\s*head\\b[^>]*>)", "$1" + meta, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (Regex.IsMatch(html, "<\\s*html\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return Regex.Replace(html, "(<\\s*html\\b[^>]*>)", "$1<head>" + meta + "<title>" + WebUtility.HtmlEncode(title) + "</title></head>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return Document(title, html);
    }

    private static string Document(string title, string body) => "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"" +
        WebUtility.HtmlEncode(Csp) + "\"><meta name=\"referrer\" content=\"no-referrer\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" +
        WebUtility.HtmlEncode(title) + "</title><style>body{font:14px/1.55 system-ui,sans-serif;margin:24px;color:#171717;background:#fff}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f5f5f5;padding:16px;border-radius:8px}code{font-family:ui-monospace,monospace}h1,h2,h3{line-height:1.2}img{max-width:100%;height:auto}</style></head><body>" + body + "</body></html>";

    private static string Pre(string value) => "<pre><code>" + WebUtility.HtmlEncode(value) + "</code></pre>";

    private static string SvgImage(string svg) => "<img alt=\"SVG artifact\" src=\"data:image/svg+xml;base64," +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)) + "\">";

    private static string PrettyJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException ex) { throw new ArgumentException("JSON artifact content must be valid JSON.", ex); }
    }

    private static string Markdown(string markdown)
    {
        var output = new StringBuilder();
        var paragraph = new List<string>();
        var inCode = false;
        var code = new StringBuilder();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            output.Append("<p>").Append(WebUtility.HtmlEncode(string.Join(" ", paragraph))).Append("</p>");
            paragraph.Clear();
        }
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode) { output.Append(Pre(code.ToString())); code.Clear(); inCode = false; }
                else { FlushParagraph(); inCode = true; }
                continue;
            }
            if (inCode) { if (code.Length > 0) code.AppendLine(); code.Append(line); continue; }
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }
            var hashes = line.TakeWhile(ch => ch == '#').Count();
            if (hashes is >= 1 and <= 3 && line.Length > hashes && line[hashes] == ' ')
            {
                FlushParagraph();
                output.Append("<h").Append(hashes).Append('>').Append(WebUtility.HtmlEncode(line[(hashes + 1)..]))
                    .Append("</h").Append(hashes).Append('>');
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                output.Append("<ul><li>").Append(WebUtility.HtmlEncode(line[2..])).Append("</li></ul>");
            }
            else paragraph.Add(line);
        }
        if (inCode) output.Append(Pre(code.ToString()));
        FlushParagraph();
        return output.ToString();
    }
}

/// <summary>Tool facade for durable artifact creation, optimistic editing and isolated rendering.</summary>
public sealed class ArtifactRuntimeToolSet : IDisposable
{
    private static readonly string[] Operations = ["create", "update", "get", "list", "show", "delete"];
    private readonly SqliteArtifactRuntimeStore _store;
    private readonly Func<WidgetArtifact, CancellationToken, Task>? _show;

    public ArtifactRuntimeToolSet(string databasePath,
        Func<WidgetArtifact, CancellationToken, Task>? show = null,
        ArtifactRuntimeLimits? limits = null)
    {
        _store = new(databasePath, limits);
        _show = show;
    }

    public IEnumerable<IAgentTool> Tools => Operations.Select(operation => (IAgentTool)new Tool(this, operation));
    public static IReadOnlyList<ToolDescriptor> Descriptors => Operations.Select(DescriptorFor).ToArray();

    public void Dispose() => _store.Dispose();

    private sealed class Tool(ArtifactRuntimeToolSet owner, string operation) : IAgentTool
    {
        public ToolDescriptor Descriptor { get; } = DescriptorFor(operation);

        public async Task<ToolReply> ExecuteAsync(JsonElement args, AgentExecutionContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var identity = context.RequireSessionIdentity();
                return operation switch
                {
                    "create" => await Create(owner, identity, args, ct).ConfigureAwait(false),
                    "update" => await Update(owner, identity, args, ct).ConfigureAwait(false),
                    "get" => Json(owner._store.Get(identity, Required(args, "artifactId", 80),
                        !args.TryGetProperty("includeContent", out var include) || include.GetBoolean(),
                        args.TryGetProperty("includeDeleted", out var deleted) && deleted.GetBoolean())),
                    "list" => Json(owner._store.List(identity,
                        args.TryGetProperty("includeDeleted", out var all) && all.GetBoolean(),
                        args.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 50)),
                    "show" => await Show(owner, identity, args, ct).ConfigureAwait(false),
                    _ => Delete(owner, identity, args)
                };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or
                                       AgentRequestException or SqliteException or JsonException)
            {
                return ToolReply.Error(ex.Message);
            }
        }

        private static async Task<ToolReply> Create(ArtifactRuntimeToolSet owner, AgentSessionIdentity identity,
            JsonElement args, CancellationToken ct)
        {
            var artifact = owner._store.Create(identity, Required(args, "title", 240), Required(args, "kind", 32),
                Required(args, "content", 2_000_000, allowEmpty: true), Optional(args, "metadataJson", 64_000));
            if (!args.TryGetProperty("show", out var show) || !show.GetBoolean()) return Json(artifact);
            return await Render(owner, artifact, ct).ConfigureAwait(false);
        }

        private static async Task<ToolReply> Update(ArtifactRuntimeToolSet owner, AgentSessionIdentity identity,
            JsonElement args, CancellationToken ct)
        {
            var artifact = owner._store.Update(identity, Required(args, "artifactId", 80),
                args.GetProperty("expectedRevision").GetInt64(), Optional(args, "title", 240),
                Optional(args, "kind", 32), Optional(args, "content", 2_000_000, allowEmpty: true),
                Optional(args, "metadataJson", 64_000));
            if (!args.TryGetProperty("show", out var show) || !show.GetBoolean()) return Json(artifact);
            return await Render(owner, artifact, ct).ConfigureAwait(false);
        }

        private static async Task<ToolReply> Show(ArtifactRuntimeToolSet owner, AgentSessionIdentity identity,
            JsonElement args, CancellationToken ct)
        {
            var artifact = owner._store.Get(identity, Required(args, "artifactId", 80));
            if (args.TryGetProperty("expectedRevision", out var expected) && artifact.Revision != expected.GetInt64())
                throw new AgentRequestException("ARTIFACT_REVISION_CONFLICT",
                    $"Artifact revision changed to {artifact.Revision}. Read it again before showing stale content.");
            return await Render(owner, artifact, ct).ConfigureAwait(false);
        }

        private static async Task<ToolReply> Render(ArtifactRuntimeToolSet owner, ArtifactRuntimeRecord artifact,
            CancellationToken ct)
        {
            var widget = ArtifactRenderer.Render(artifact);
            if (owner._show is not null) await owner._show(widget, ct).ConfigureAwait(false);
            var envelope = new
            {
                artifact = WithoutContent(artifact),
                displayed = owner._show is not null,
                message = "Artifact rendered with a network-denying content security policy."
            };
            return new ToolReply(JsonSerializer.Serialize(envelope, WireJson.Options), Widget: widget);
        }

        private static ToolReply Delete(ArtifactRuntimeToolSet owner, AgentSessionIdentity identity, JsonElement args) =>
            Json(owner._store.Delete(identity, Required(args, "artifactId", 80),
                args.GetProperty("expectedRevision").GetInt64()));

        private static object WithoutContent(ArtifactRuntimeRecord record) => new
        {
            record.ArtifactId, record.Title, record.Kind, record.MetadataJson, record.Revision,
            record.ContentSha256, record.Deleted, record.CreatedAt, record.UpdatedAt, record.Uri
        };

        private static ToolReply Json(object value) => new(JsonSerializer.Serialize(value, WireJson.Options));

        private static string Required(JsonElement args, string name, int max, bool allowEmpty = false)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new ArgumentException(name + " is required.");
            var text = value.GetString() ?? string.Empty;
            if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > max)
                throw new ArgumentException(name + " is required and bounded.");
            return text;
        }

        private static string? Optional(JsonElement args, string name, int max, bool allowEmpty = false)
        {
            if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " must be a string.");
            var text = value.GetString() ?? string.Empty;
            if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > max)
                throw new ArgumentException(name + " exceeds bounded size.");
            return text;
        }

        internal static JsonElement Schema(string op)
        {
            var id = new { type = "string", pattern = "^artifact_[a-f0-9]{32}$" };
            var common = new Dictionary<string, object>
            {
                ["artifactId"] = id,
                ["title"] = new { type = "string", minLength = 1, maxLength = 240 },
                ["kind"] = new { type = "string", @enum = new[] { "html", "markdown", "svg", "json", "text" } },
                ["content"] = new { type = "string", maxLength = 2_000_000 },
                ["metadataJson"] = new { type = "string", maxLength = 64_000 },
                ["expectedRevision"] = new { type = "integer", minimum = 1 },
                ["show"] = new { type = "boolean", @default = false }
            };
            object schema = op switch
            {
                "create" => new { type = "object", properties = common, required = new[] { "title", "kind", "content" }, additionalProperties = false },
                "update" => new { type = "object", properties = common, required = new[] { "artifactId", "expectedRevision" }, additionalProperties = false },
                "get" => new { type = "object", properties = new Dictionary<string, object> { ["artifactId"] = id, ["includeContent"] = new { type = "boolean", @default = true }, ["includeDeleted"] = new { type = "boolean", @default = false } }, required = new[] { "artifactId" }, additionalProperties = false },
                "list" => new { type = "object", properties = new Dictionary<string, object> { ["includeDeleted"] = new { type = "boolean", @default = false }, ["limit"] = new { type = "integer", minimum = 1, maximum = 200, @default = 50 } }, additionalProperties = false },
                "show" => new { type = "object", properties = new Dictionary<string, object> { ["artifactId"] = id, ["expectedRevision"] = new { type = "integer", minimum = 1 } }, required = new[] { "artifactId" }, additionalProperties = false },
                _ => new { type = "object", properties = new Dictionary<string, object> { ["artifactId"] = id, ["expectedRevision"] = new { type = "integer", minimum = 1 } }, required = new[] { "artifactId", "expectedRevision" }, additionalProperties = false }
            };
            return WireJson.Element(schema);
        }
    }

    private static ToolDescriptor DescriptorFor(string operation) => new("artifact." + operation, "artifact_" + operation, "artifact",
            operation switch
            {
                "create" => "Create a durable session-owned HTML, Markdown, SVG, JSON, or text artifact, optionally showing it immediately.",
                "update" => "Update a durable artifact using an expected revision so concurrent edits cannot be overwritten silently.",
                "get" => "Read one durable artifact owned by this Jarvis session.",
                "list" => "List bounded artifact metadata owned by this Jarvis session.",
                "show" => "Render and display the current revision of a durable artifact in the local artifact host and return the widget.",
                _ => "Soft-delete a durable artifact using an expected revision."
            }, Tool.Schema(operation), operation is "get" or "list" or "show",
            operation is "create" or "update" or "show" or "delete");
}
