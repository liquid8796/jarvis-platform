using System.Text.RegularExpressions;

namespace JarvisCode.Core.Security;

/// <summary>Outcome of a workspace scope check.</summary>
public sealed record PathScopeDecision(bool Allowed, string Reason, string? Candidate = null, string? Resolved = null);

/// <summary>
/// Validates tool/shell path operands against explicit workspace roots (ported from
/// claw-code's path_scope). Deliberately conservative: any candidate that resolves
/// outside the configured roots — including through <c>..</c>, symlinks, glob
/// expansion, environment variables, or shell redirection targets — is reported as
/// out of scope. Decisions inform permission prompts and auto-approval policy; they
/// are not a sandbox by themselves.
/// </summary>
public sealed class WorkspacePathScope
{
    private static readonly char[] GlobMeta = ['*', '?', '['];
    private static readonly char[] Separators = ['/', '\\'];
    private static readonly Regex WindowsDriveRegex = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
    private static readonly Regex WindowsUncRegex = new(@"^(?:\\\\|//)[^\\/]+[\\/][^\\/]+", RegexOptions.Compiled);
    private static readonly Regex EnvAssignmentRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);
    private static readonly Regex RedirectionRegex = new(@"^\d*(?:<>|>>?|<)(.+)$|^&>>?(.+)$", RegexOptions.Compiled);
    private static readonly Regex DollarVarRegex = new(@"\$\{([^}]+)\}|\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>Symlink chains longer than this stop being followed (cycle guard).</summary>
    private const int MaxLinkHops = 40;

    /// <summary>Glob expansion stops enumerating past this many filesystem entries.</summary>
    private const int MaxGlobMatches = 4096;

    private readonly string[] _roots;

    private WorkspacePathScope(string[] roots) => _roots = roots;

    /// <summary>Fully resolved (symlink-free) root directories, in configuration order.</summary>
    public IReadOnlyList<string> Roots => _roots;

    public static WorkspacePathScope FromRoot(string root) => FromRoots([root]);

    public static WorkspacePathScope FromRoots(IEnumerable<string> roots)
    {
        var resolved = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => ResolveRealPath(Path.GetFullPath(ExpandUser(root.Trim()))))
            .ToArray();
        if (resolved.Length == 0)
            throw new ArgumentException("At least one workspace root is required.", nameof(roots));
        return new WorkspacePathScope(resolved);
    }

    /// <summary>
    /// Validates every path-like operand in a shell/tool payload. The cwd itself is
    /// validated first — a shell that already escaped the workspace fails closed.
    /// </summary>
    public PathScopeDecision ValidatePayload(string payload, string? cwd = null)
    {
        string cwdPath;
        try
        {
            cwdPath = cwd is null ? _roots[0] : ResolveRealPath(Path.GetFullPath(ExpandUser(cwd)));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return new PathScopeDecision(false, $"cwd could not be resolved: {cwd}", cwd);
        }

        var cwdDecision = ValidatePath(cwdPath);
        if (!cwdDecision.Allowed)
            return new PathScopeDecision(false, $"cwd outside workspace scope: {cwdPath}", cwdPath, cwdDecision.Resolved);

        foreach (var candidate in ExtractPathCandidates(payload))
        {
            var decision = ValidatePath(candidate, cwdPath);
            if (!decision.Allowed)
                return decision;
        }
        return new PathScopeDecision(true, "all path candidates are inside workspace scope");
    }

    /// <summary>Validates one path (absolute or relative to <paramref name="cwd"/> / the first root).</summary>
    public PathScopeDecision ValidatePath(string candidate, string? cwd = null)
    {
        var raw = ExpandEnvironment(ExpandUser(candidate));
        var basePath = cwd ?? _roots[0];

        string full;
        try
        {
            full = Path.GetFullPath(raw, basePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // Unparseable operands fail closed: we cannot prove they stay inside.
            return new PathScopeDecision(false, "path could not be parsed", candidate);
        }

        var expanded = ExpandGlob(full);
        foreach (var expandedPath in expanded)
        {
            var resolved = ResolveRealPath(expandedPath);
            if (!_roots.Any(root => IsWithin(resolved, root)))
                return new PathScopeDecision(false, "path resolves outside workspace scope", candidate, resolved);
        }
        return new PathScopeDecision(true, "path is inside workspace scope", candidate, ResolveRealPath(expanded[0]));
    }

    /// <summary>
    /// Conservative path-like operands from a shell payload. The payload is tokenized
    /// twice — POSIX-quote-aware and naive whitespace — and the union is checked, so a
    /// quoting trick that defeats one tokenizer is still caught by the other.
    /// </summary>
    public static IReadOnlyList<string> ExtractPathCandidates(string payload)
    {
        var naive = payload.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tokens = PosixSplit(payload) ?? [.. naive];
        var candidates = new List<string>();
        foreach (var original in tokens.Concat(naive))
        {
            if (original.Length == 0 || original[0] == '-' || EnvAssignmentRegex.IsMatch(original))
                continue;
            var token = StripRedirectionOperator(original);
            var expanded = ExpandEnvironment(ExpandUser(token));
            if (!LooksLikePath(token) && !LooksLikePath(expanded))
                continue;
            var candidate = LooksLikePath(expanded) ? expanded : token;
            if (!candidates.Contains(candidate))
                candidates.Add(candidate);
        }
        return candidates;
    }

    // ---------------- Candidate heuristics ----------------

    private static bool LooksLikePath(string token) =>
        token is "." or ".."
        || token.StartsWith("./", StringComparison.Ordinal)
        || token.StartsWith("../", StringComparison.Ordinal)
        || token.StartsWith('/')
        || token.StartsWith("~/", StringComparison.Ordinal)
        || token.Contains('/')
        || token.Contains('\\')
        || token.IndexOfAny(GlobMeta) >= 0
        || IsWindowsAbsolute(token);

    private static bool IsWindowsAbsolute(string value) =>
        WindowsDriveRegex.IsMatch(value) || WindowsUncRegex.IsMatch(value);

    private static string StripRedirectionOperator(string token)
    {
        var match = RedirectionRegex.Match(token);
        if (!match.Success)
            return token;
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static string ExpandUser(string value)
    {
        if (value == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith(@"~\", StringComparison.Ordinal))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..];
        return value;
    }

    /// <summary>Expands both %VAR% (Windows) and $VAR / ${VAR} (POSIX shells); unknown variables stay literal.</summary>
    private static string ExpandEnvironment(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        return DollarVarRegex.Replace(expanded, match =>
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return Environment.GetEnvironmentVariable(name) ?? match.Value;
        });
    }

    /// <summary>
    /// POSIX-shell-style tokenization honoring quotes and backslash escapes.
    /// Returns null on an unbalanced quote so the caller falls back to naive splitting.
    /// </summary>
    private static List<string>? PosixSplit(string payload)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inToken = false;
        for (int i = 0; i < payload.Length; i++)
        {
            char c = payload[i];
            if (c == '\'')
            {
                inToken = true;
                int end = payload.IndexOf('\'', i + 1);
                if (end < 0)
                    return null;
                current.Append(payload, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                inToken = true;
                i++;
                bool closed = false;
                for (; i < payload.Length; i++)
                {
                    char inner = payload[i];
                    if (inner == '\\' && i + 1 < payload.Length && payload[i + 1] is '"' or '\\')
                        current.Append(payload[++i]);
                    else if (inner == '"')
                    {
                        closed = true;
                        break;
                    }
                    else
                        current.Append(inner);
                }
                if (!closed)
                    return null;
            }
            else if (c == '\\' && i + 1 < payload.Length)
            {
                inToken = true;
                current.Append(payload[++i]);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                inToken = true;
                current.Append(c);
            }
        }
        if (inToken)
            tokens.Add(current.ToString());
        return tokens;
    }

    // ---------------- Resolution ----------------

    /// <summary>
    /// Normalizes and resolves symlinks component by component; a non-existent tail is
    /// appended verbatim (Python resolve(strict=False) semantics). UNC paths are only
    /// normalized — probing an unreachable server would block on network I/O.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            return full;

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;
        var parts = full[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        int hops = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            try
            {
                FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current)
                    : null;
                if (info is null)
                {
                    for (int j = i + 1; j < parts.Length; j++)
                        current = Path.Combine(current, parts[j]);
                    return current;
                }
                if (info.LinkTarget is not null && hops++ < MaxLinkHops)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is not null)
                        current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: keep the unresolved component and continue normalizing.
            }
        }
        return current;
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
            return true;
        if (Path.IsPathRooted(relative))
            return false;
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Expands a glob against the real filesystem so every match is validated. An
    /// unmatched (or capped) glob contributes its stable non-glob parent prefix, so
    /// "../secrets/*.txt" is still checked even when nothing matches.
    /// </summary>
    private static string[] ExpandGlob(string fullPath)
    {
        if (fullPath.IndexOfAny(GlobMeta) < 0)
            return [fullPath];

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.IndexOfAny(GlobMeta) >= 0)
            return [fullPath];

        var segments = fullPath[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var stablePrefix = StableGlobPrefix(root, segments);

        IEnumerable<string> bases = [Path.TrimEndingDirectorySeparator(root)];
        bool capped = false;
        foreach (var segment in segments)
        {
            if (segment == "**")
            {
                var descendants = new List<string>();
                foreach (var baseDir in bases.Where(Directory.Exists))
                {
                    descendants.Add(baseDir);
                    try
                    {
                        foreach (var dir in Directory.EnumerateDirectories(baseDir, "*", SearchOption.AllDirectories))
                        {
                            descendants.Add(dir);
                            if (descendants.Count >= MaxGlobMatches)
                            {
                                capped = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        capped = true;
                    }
                    if (capped)
                        break;
                }
                bases = descendants;
            }
            else if (segment.IndexOfAny(GlobMeta) >= 0)
            {
                var regex = GlobSegmentToRegex(segment);
                var next = new List<string>();
                foreach (var baseDir in bases.Where(Directory.Exists))
                {
                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(baseDir))
                        {
                            if (!regex.IsMatch(Path.GetFileName(entry)))
                                continue;
                            next.Add(entry);
                            if (next.Count >= MaxGlobMatches)
                            {
                                capped = true;
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        capped = true;
                    }
                    if (capped)
                        break;
                }
                bases = next;
            }
            else
            {
                bases = bases.Select(baseDir => Path.Combine(baseDir, segment)).ToList();
            }
            if (!bases.Any())
                break;
        }

        var matches = bases.Where(p => File.Exists(p) || Directory.Exists(p)).Take(MaxGlobMatches).ToList();
        if (matches.Count == 0)
            return [stablePrefix];
        if (capped && !matches.Contains(stablePrefix))
            matches.Add(stablePrefix);
        return [.. matches];
    }

    private static string StableGlobPrefix(string root, string[] segments)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(GlobMeta) >= 0)
                break;
            prefix = Path.Combine(prefix, segment);
        }
        return prefix;
    }

    private static Regex GlobSegmentToRegex(string segment)
    {
        var pattern = new System.Text.StringBuilder("^");
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            switch (c)
            {
                case '*':
                    pattern.Append(".*");
                    break;
                case '?':
                    pattern.Append('.');
                    break;
                case '[':
                    int close = segment.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        var body = segment[(i + 1)..close];
                        if (body.StartsWith('!'))
                            body = "^" + body[1..];
                        pattern.Append('[').Append(body).Append(']');
                        i = close;
                    }
                    else
                        pattern.Append(Regex.Escape("["));
                    break;
                default:
                    pattern.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        pattern.Append('$');
        var options = OperatingSystem.IsWindows()
            ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant;
        return new Regex(pattern.ToString(), options);
    }
}
